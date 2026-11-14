using System.Text;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Cross-process lock for adapter hand-overs: the tray app and airpodsctl must never hand the adapter over (or run
/// recovery and detach it) at the same time. An exclusively opened file — released by Windows if the process dies.
/// </summary>
public static class BridgeOperationLock
{
    private static string PathOf => Path.Combine(BridgeConfig.Dir, "operation.lock");

    public static IDisposable? TryAcquire(string description, out string? holder)
    {
        holder = null;
        Directory.CreateDirectory(BridgeConfig.Dir);
        try
        {
            var fs = new FileStream(PathOf, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.None);
            fs.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes($"{description} · pid {Environment.ProcessId} · {Path.GetFileName(Environment.ProcessPath)} · {DateTimeOffset.Now:HH:mm:ss}");
            fs.Write(bytes);
            fs.Flush();
            return fs;
        }
        catch (IOException)
        {
            try
            {
                using var r = new FileStream(PathOf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(r, Encoding.UTF8);
                holder = sr.ReadToEnd().Trim();
            }
            catch { holder = "another process"; }
            return null;
        }
    }
}
