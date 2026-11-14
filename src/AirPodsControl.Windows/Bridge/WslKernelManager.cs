using System.Security.Cryptography;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Installs / removes the AirPods Control WSL kernel (stock Microsoft WSL kernel + MediaTek Bluetooth, see kernel/).
/// The kernel is global for WSL: every distribution, including Docker Desktop, runs on it after <c>wsl --shutdown</c>.
/// No administrator rights are needed (.wslconfig lives in the user profile).
/// </summary>
public sealed class WslKernelManager(ILog log, WslClient wsl)
{
    public const string ReleaseSuffix = "-btmtk";
    private static readonly string[] Files = ["bzImage", "modules.vhdx"];

    /// <summary>Kernel files shipped next to the exe (release zip / installer).</summary>
    public static string BundleDir => Path.Combine(AppContext.BaseDirectory, "wsl-kernel");
    /// <summary>Installed copy that .wslconfig points to (outside the app folder so updates/uninstall don't break WSL).</summary>
    public static string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsControl", "kernel");
    public static string WslConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    public string SourceDir { get; set; } = BundleDir;
    public bool BundleAvailable => Files.All(f => File.Exists(Path.Combine(SourceDir, f)));

    public static bool IsOurRelease(string? release) => release?.Contains(ReleaseSuffix, StringComparison.Ordinal) == true;

    public WslKernelSetting Configured(out string? path)
    {
        string? text = null;
        try { if (File.Exists(WslConfigPath)) text = File.ReadAllText(WslConfigPath); } catch (IOException) { }
        return WslConfigFile.GetKernel(text, InstallDir, out path);
    }

    public string? BundleInfo()
    {
        var p = Path.Combine(SourceDir, "BUILDINFO");
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    /// <summary>Copies the kernel, edits .wslconfig (backup first) and restarts WSL. Returns an error or null.</summary>
    public async Task<string?> InstallAsync(CancellationToken ct)
    {
        if (!BundleAvailable) return $"The MediaTek WSL kernel is not included in this build ({SourceDir}).";
        if (Configured(out var foreign) == WslKernelSetting.Foreign)
            return $".wslconfig already uses another custom kernel ({foreign}). AirPods Control does not replace it; that kernel needs CONFIG_BT_HCIBTUSB_MTK=y.";

        var bad = VerifyChecksums(SourceDir);
        if (bad is not null) return bad;

        // modules.vhdx is locked while the VM runs on our kernel: stop WSL before replacing a different copy.
        Directory.CreateDirectory(InstallDir);
        bool needCopy = Files.Any(f => !SameFile(Path.Combine(SourceDir, f), Path.Combine(InstallDir, f)));
        if (needCopy)
        {
            if (Files.Any(f => File.Exists(Path.Combine(InstallDir, f)))) await wsl.ShutdownAsync(ct).ConfigureAwait(false);
            foreach (var f in Files.Append("BUILDINFO").Append("SHA256SUMS").Append("config"))
            {
                var src = Path.Combine(SourceDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(InstallDir, f), overwrite: true);
            }
            var badCopy = VerifyChecksums(InstallDir);
            if (badCopy is not null) return badCopy;
        }
        // The modules VHD is opened by the VM worker process (virtual machine account), not by the user:
        // without read access for "NT VIRTUAL MACHINE\Virtual Machines" WSL fails with HCS/E_ACCESSDENIED (seen on the test PC).
        GrantVmRead(Path.Combine(InstallDir, "modules.vhdx"));

        var existing = File.Exists(WslConfigPath) ? await File.ReadAllTextAsync(WslConfigPath, ct).ConfigureAwait(false) : null;
        var updated = WslConfigFile.SetKernel(existing, Path.Combine(InstallDir, "bzImage"), Path.Combine(InstallDir, "modules.vhdx"), InstallDir);
        if (updated != existing)
        {
            Backup(existing);
            await WriteAtomicAsync(updated, ct).ConfigureAwait(false);
            log.Info(".wslconfig: kernel set to " + InstallDir);
        }
        var r = await wsl.ShutdownAsync(ct).ConfigureAwait(false);
        return r.Success ? null : "wsl --shutdown failed: " + r.FirstErrorLine;
    }

    /// <summary>Removes only our lines from .wslconfig (backup first), restarts WSL on the stock kernel, deletes the copy.</summary>
    public async Task<string?> RemoveAsync(CancellationToken ct)
    {
        if (Configured(out _) == WslKernelSetting.Ours)
        {
            var text = await File.ReadAllTextAsync(WslConfigPath, ct).ConfigureAwait(false);
            Backup(text);
            var updated = WslConfigFile.RemoveKernel(text, InstallDir);
            if (updated.Trim().Length == 0) File.Delete(WslConfigPath);
            else await WriteAtomicAsync(updated, ct).ConfigureAwait(false);
            log.Info(".wslconfig: AirPods Control kernel removed");
            var r = await wsl.ShutdownAsync(ct).ConfigureAwait(false);
            if (!r.Success) return "wsl --shutdown failed: " + r.FirstErrorLine;
        }
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); }
        catch (IOException ex) { return "Kernel files could not be deleted yet (WSL still running?): " + ex.Message; }
        return null;
    }

    private void GrantVmRead(string file)
    {
        var vms = new System.Security.Principal.SecurityIdentifier("S-1-5-83-0"); // NT VIRTUAL MACHINE\Virtual Machines
        var fi = new FileInfo(file);
        var acl = fi.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(vms,
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        fi.SetAccessControl(acl);
        log.Info("granted VM read access: " + file);
    }

    private void Backup(string? text)
    {
        if (text is null) return;
        var b = WslConfigPath + ".airpodscontrol-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.WriteAllText(b, text);
        log.Info(".wslconfig backup: " + b);
    }

    private static async Task WriteAtomicAsync(string text, CancellationToken ct)
    {
        var tmp = WslConfigPath + ".tmp";
        await File.WriteAllTextAsync(tmp, text, ct).ConfigureAwait(false);
        File.Move(tmp, WslConfigPath, overwrite: true);
    }

    /// <summary>Checks SHA256SUMS ("hash  name" lines) when present. Returns an error or null.</summary>
    private static string? VerifyChecksums(string dir)
    {
        var sums = Path.Combine(dir, "SHA256SUMS");
        if (!File.Exists(sums)) return null;
        foreach (var line in File.ReadAllLines(sums))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Files.Contains(parts[1].TrimStart('*'))) continue;
            var file = Path.Combine(dir, parts[1].TrimStart('*'));
            if (!File.Exists(file)) return $"Kernel file missing: {file}";
            if (!Hash(file).Equals(parts[0], StringComparison.OrdinalIgnoreCase)) return $"Checksum mismatch for {file} — kernel files are damaged.";
        }
        return null;
    }

    private static string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }

    private static bool SameFile(string a, string b) =>
        File.Exists(a) && File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && Hash(a) == Hash(b);
}
