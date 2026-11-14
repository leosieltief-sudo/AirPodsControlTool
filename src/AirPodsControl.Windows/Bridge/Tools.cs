using System.Diagnostics;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Windows.Bridge;

/// <summary>usbipd-win 5.x wrapper. bind/unbind need admin (done by the setup wizard once); state/attach/detach do not.</summary>
public sealed class UsbipdClient(ILog log)
{
    public static string? FindExe()
    {
        var onPath = ProcessRunner.FindOnPath("usbipd.exe");
        if (onPath is not null) return onPath;
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        return File.Exists(pf) ? pf : null;
    }

    public bool IsInstalled => FindExe() is not null;

    public async Task<IReadOnlyList<UsbipdDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var exe = FindExe() ?? throw new InvalidOperationException("usbipd-win is not installed.");
        var st = await ProcessRunner.RunAsync(exe, ["state"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (st.Success)
        {
            try { return UsbipdParser.ParseState(st.StdOut); }
            catch (Exception ex) { log.Warn("usbipd state output not understood, falling back to list", ex); }
        }
        var ls = await ProcessRunner.RunAsync(exe, ["list"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!ls.Success) throw new InvalidOperationException("usbipd list failed: " + ls.FirstErrorLine);
        return UsbipdParser.ParseList(ls.StdOut);
    }

    public async Task<UsbipdDevice?> FindAsync(string vidPid, CancellationToken ct) =>
        (await GetDevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Matches(vidPid) && d.BusId is not null);

    public Task<ProcessResult> AttachAsync(string busId, string distro, CancellationToken ct) =>
        Run(["attach", "--wsl", distro, "--busid", busId], TimeSpan.FromSeconds(30), ct);

    public Task<ProcessResult> DetachAsync(string busId, CancellationToken ct) =>
        Run(["detach", "--busid", busId], TimeSpan.FromSeconds(20), ct);

    public Task<ProcessResult> DetachByHardwareIdAsync(string vidPid, CancellationToken ct) =>
        Run(["detach", "--hardware-id", vidPid], TimeSpan.FromSeconds(20), ct);

    private async Task<ProcessResult> Run(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var exe = FindExe() ?? throw new InvalidOperationException("usbipd-win is not installed.");
        var r = await ProcessRunner.RunAsync(exe, args, timeout, ct).ConfigureAwait(false);
        log.Info($"usbipd {string.Join(' ', args)} → {(r.Success ? "ok" : r.FirstErrorLine)}");
        return r;
    }
}

/// <summary>wsl.exe wrapper (always hidden, UTF-8 output).</summary>
public sealed class WslClient(ILog log)
{
    public static string Exe => Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public async Task<(bool ok, string message)> CheckAsync(CancellationToken ct)
    {
        if (!File.Exists(Exe)) return (false, "WSL is not installed (wsl.exe not found).");
        var r = await ProcessRunner.RunAsync(Exe, ["--version"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!r.Success) return (false, "WSL is not set up: " + r.FirstErrorLine + " (run 'wsl --install' as administrator).");
        var first = r.StdOut.Replace("\0", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "WSL";
        return (true, first);
    }

    public async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(Exe, ["--list", "--quiet"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return r.StdOut.Replace("\0", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    public async Task<bool> DistroExistsAsync(string name, CancellationToken ct) =>
        (await ListDistrosAsync(ct).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase);

    public Task<ProcessResult> ImportAsync(string name, string installDir, string tarPath, CancellationToken ct) =>
        Logged(["--import", name, installDir, tarPath, "--version", "2"], TimeSpan.FromMinutes(3), ct);

    public Task<ProcessResult> UnregisterAsync(string name, CancellationToken ct) =>
        Logged(["--unregister", name], TimeSpan.FromMinutes(1), ct);

    /// <summary>Stops the WSL VM (all distributions, including Docker Desktop's). Needed for .wslconfig changes.</summary>
    public Task<ProcessResult> ShutdownAsync(CancellationToken ct) =>
        Logged(["--shutdown"], TimeSpan.FromMinutes(1), ct);

    /// <summary>Kernel release the WSL VM runs (starts the VM if needed), e.g. "6.6.114.1-microsoft-standard-WSL2".</summary>
    public async Task<string?> KernelReleaseAsync(string distro, CancellationToken ct)
    {
        var r = await ExecAsync(distro, ["uname", "-r"], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return r.Success ? r.StdOut.Trim() : null;
    }

    /// <summary>Runs a command as root inside the distribution (no shell on the Windows side, arguments passed verbatim).</summary>
    public Task<ProcessResult> ExecAsync(string distro, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? stdin = null, Action<string>? onLine = null) =>
        ProcessRunner.RunAsync(Exe, new[] { "--distribution", distro, "--user", "root", "--exec" }.Concat(args), timeout, ct, stdin, onLine);

    /// <summary>Keeps the WSL VM and distribution running while a USB device is attached.</summary>
    public Process? StartKeepAlive(string distro, int seconds) =>
        ProcessRunner.StartBackground(Exe, ["--distribution", distro, "--user", "root", "--exec", "sleep", seconds.ToString()]);

    private async Task<ProcessResult> Logged(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(Exe, args, timeout, ct).ConfigureAwait(false);
        log.Info($"wsl {args[0]} → {(r.Success ? "ok" : r.FirstErrorLine)}");
        return r;
    }
}
