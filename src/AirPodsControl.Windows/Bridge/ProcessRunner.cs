using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPodsControl.Windows.Bridge;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string Combined => (StdOut + "\n" + StdErr).Trim();
    public string FirstErrorLine =>
        (StdErr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
         ?? StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {ExitCode}");
}

/// <summary>
/// Runs console tools (usbipd.exe, wsl.exe) without any visible window: UseShellExecute=false + CreateNoWindow=true
/// maps to CREATE_NO_WINDOW. Child processes are put in a kill-on-close job object, so nothing outlives the app.
/// </summary>
public static class ProcessRunner
{
    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    public static ProcessStartInfo CreateStartInfo(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WSL_UTF8"] = "1"; // wsl.exe prints UTF-16 by default
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? stdin = null, Action<string>? onLine = null)
    {
        using var p = new Process { StartInfo = CreateStartInfo(file, args), EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (stderr) stderr.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Win32Exception ex) { return new ProcessResult(-1, "", $"Cannot start {Path.GetFileName(file)}: {ex.Message}", false); }
        AssignToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }
        if (!timedOut) p.WaitForExit(); // flush async readers
        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        return new ProcessResult(timedOut ? -1 : p.ExitCode, o, e2, timedOut);
    }

    /// <summary>Starts a long-running hidden process (e.g. a WSL keep-alive) bound to the app's lifetime.</summary>
    public static Process? StartBackground(string file, IEnumerable<string> args)
    {
        var psi = CreateStartInfo(file, args);
        psi.RedirectStandardOutput = psi.RedirectStandardError = false;
        // encodings may only be set together with redirection (InvalidOperationException otherwise)
        psi.StandardOutputEncoding = null;
        psi.StandardErrorEncoding = null;
        try
        {
            var p = Process.Start(psi);
            if (p is not null) AssignToJob(p);
            return p;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // ---- job object (kill children when the app exits or crashes)
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    internal static void AssignToJob(Process p)
    {
        try { if (Job.Value != IntPtr.Zero) AssignProcessToJobObject(Job.Value, p.Handle); } catch { }
    }
}
