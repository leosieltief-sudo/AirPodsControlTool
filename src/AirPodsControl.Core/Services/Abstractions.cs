namespace AirPodsControl.Core.Services;

/// <summary>
/// A packet-oriented byte channel to the AirPods AAP endpoint (L2CAP PSM 0x1001).
/// One <see cref="PacketReceived"/> call == one L2CAP SDU.
/// </summary>
public interface IAapTransport : IAsyncDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    event Action<byte[]>? PacketReceived;
    /// <summary>Raised once when the channel closes; argument is the failure, or null for a clean close.</summary>
    event Action<Exception?>? Closed;
    Task OpenAsync(CancellationToken ct);
    Task SendAsync(byte[] packet, CancellationToken ct);
}

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog l, string m) => l.Write(LogLevel.Debug, m);
    public static void Info(this ILog l, string m) => l.Write(LogLevel.Info, m);
    public static void Warn(this ILog l, string m, Exception? e = null) => l.Write(LogLevel.Warn, m, e);
    public static void Error(this ILog l, string m, Exception? e = null) => l.Write(LogLevel.Error, m, e);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { }
}

/// <summary>Thread-safe rolling file log in %LOCALAPPDATA%\AirPodsControl\logs. Contains no personal data beyond the device MAC.</summary>
public sealed class FileLog : ILog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _min;
    public event Action<LogLevel, string>? Written;

    public FileLog(string directory, LogLevel min = LogLevel.Info)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"airpodscontrol-{DateTime.Now:yyyyMMdd}.log");
        _min = min;
        // keep last 7 log files
        foreach (var old in new DirectoryInfo(directory).GetFiles("airpodscontrol-*.log").OrderByDescending(f => f.Name).Skip(7))
            try { old.Delete(); } catch { }
    }

    public string FilePath => _path;

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        Written?.Invoke(level, message);
        if (level < _min) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : " :: " + ex.GetType().Name + ": " + ex.Message)}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }
}

public sealed class ConsoleLog(LogLevel min = LogLevel.Debug) : ILog
{
    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (level < min) return;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : " :: " + ex.Message)}");
    }
}
