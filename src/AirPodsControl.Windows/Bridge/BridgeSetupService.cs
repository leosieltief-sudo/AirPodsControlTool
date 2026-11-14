using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Windows.Bridge;

public enum SetupStepState { Pending, Running, Done, Warning, Failed, Skipped }

public sealed class SetupStep(string id, string title)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public SetupStepState State { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>
/// One-click setup for the driverless WSL2 bridge. Nothing here touches Test Mode, Secure Boot or Code Integrity.
/// Only <see cref="RunElevatedAsync"/> needs administrator rights (one UAC prompt).
/// </summary>
public sealed class BridgeSetupService
{
    private const string AlpineBase = "https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/x86_64/";
    private const string AlpineFallbackFile = "alpine-minirootfs-3.24.4-x86_64.tar.gz";

    private readonly ILog _log;
    private readonly WslClient _wsl;
    private readonly UsbipdClient _usbipd;
    private readonly WslBridgeProvider _provider;

    public BridgeSetupService(ILog log, WslBridgeProvider provider)
    {
        _log = log; _provider = provider;
        _wsl = new WslClient(log); _usbipd = new UsbipdClient(log);
        Kernel = new WslKernelManager(log, _wsl);
        Steps =
        [
            new("wsl", "WSL 2 is installed"),
            new("adapter", "USB Bluetooth adapter found"),
            new("distro", "Alpine Linux distribution 'airpods-bridge' (~4 MB download)"),
            new("packages", "BlueZ + Python installed in the distribution, bridge scripts copied"),
            new("kernel", "WSL kernel with MediaTek Bluetooth support (MediaTek adapters only)"),
            new("admin", "One administrator step: usbipd-win, share adapter, copy AirPods pairing key"),
            new("adapter-test", "Adapter starts inside WSL (Bluetooth audio pauses briefly)"),
            new("airpods-test", "AirPods answer over L2CAP 0x1001 (read-only, audio pauses briefly)"),
        ];
    }

    public IReadOnlyList<SetupStep> Steps { get; }

    /// <summary>Wraps operations that take the adapter away from Windows (the controller suppresses the expected
    /// disconnect events and restores everything afterwards). Default: run directly.</summary>
    public Func<Func<IProgress<string>, Task<(BridgeResult?, string?)>>, Task<(BridgeResult?, string?)>> AdapterOperation { get; set; } = op => op(new Progress<string>());
    public event Action? Changed;
    public BluetoothUsbAdapter? Adapter { get; private set; }
    public WslKernelManager Kernel { get; }

    /// <summary>Asks the user (title, message) → true to proceed. Used before global changes (WSL kernel). Default: no.</summary>
    public Func<string, string, Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);

    private bool _kernelOffered;

    private SetupStep S(string id) => Steps.First(s => s.Id == id);
    private void Set(string id, SetupStepState st, string detail = "") { var s = S(id); s.State = st; s.Detail = detail; Changed?.Invoke(); _log.Info($"setup[{id}] {st}: {detail}"); }

    /// <summary>Runs every step in order; stops at the first hard failure. <paramref name="device"/> = selected AirPods.</summary>
    public async Task<bool> RunAllAsync(ulong device, string deviceName, CancellationToken ct)
    {
        foreach (var s in Steps) { s.State = SetupStepState.Pending; s.Detail = ""; }
        Changed?.Invoke();
        _kernelOffered = false;
        var cfg = _provider.Config;

        // 1. WSL
        Set("wsl", SetupStepState.Running);
        var (wslOk, wslMsg) = await _wsl.CheckAsync(ct);
        if (!wslOk) { Set("wsl", SetupStepState.Failed, wslMsg); return false; }
        Set("wsl", SetupStepState.Done, wslMsg + CustomKernelNote());

        // 2. adapter
        Set("adapter", SetupStepState.Running);
        var adapters = BluetoothUsbAdapter.FindAll();
        Adapter = adapters.FirstOrDefault();
        if (Adapter is null) { Set("adapter", SetupStepState.Failed, "No USB Bluetooth adapter found. usbipd can only hand over USB devices."); return false; }
        cfg.AdapterVidPid = Adapter.VidPid;
        Set("adapter", SetupStepState.Done, $"{Adapter.Name} ({Adapter.VidPid})" + (Adapter.IsMediaTek ? " · MediaTek" : ""));

        // 3. distro
        Set("distro", SetupStepState.Running);
        if (await _wsl.DistroExistsAsync(cfg.Distro, ct))
            Set("distro", SetupStepState.Done, "already present");
        else
        {
            var err = await CreateDistroAsync(cfg.Distro, ct);
            if (err is not null) { Set("distro", SetupStepState.Failed, err); return false; }
            Set("distro", SetupStepState.Done, "imported");
        }

        // 4. packages + scripts (MediaTek firmware is built into our kernel, no firmware package needed)
        Set("packages", SetupStepState.Running, "apk add bluez bluez-btmgmt dbus python3 …");
        var perr = await ProvisionAsync(cfg.Distro, mediatek: false, ct);
        if (perr is not null) { Set("packages", SetupStepState.Failed, perr); return false; }
        cfg.Provisioned = true; cfg.ScriptVersion = BridgeConfig.CurrentScriptVersion; cfg.Save();
        Set("packages", SetupStepState.Done, "ready");

        // 5. kernel (MediaTek only; otherwise the stock kernel already has the driver)
        if (Adapter.IsMediaTek)
        {
            // MediaTek firmware download takes a few seconds before hci0 appears
            if (cfg.HciTimeoutSeconds < 10) { cfg.HciTimeoutSeconds = 10; cfg.Save(); }
            await EnsureKernelAsync("This adapter is a MediaTek chip.", ct);
        }
        else Set("kernel", SetupStepState.Skipped, "not needed: the stock WSL kernel includes the driver for this adapter");

        // 6. elevated
        Set("admin", SetupStepState.Running, "waiting for the UAC prompt…");
        var aerr = await RunElevatedAsync(Adapter.VidPid, [device], unbind: false, ct);
        if (aerr is not null) { Set("admin", SetupStepState.Failed, aerr); return false; }
        var kerr = await InstallKeysAsync(cfg, device, deviceName, ct);
        if (kerr is not null) { Set("admin", SetupStepState.Failed, kerr); return false; }
        cfg.Save();
        Set("admin", SetupStepState.Done, "usbipd ready, adapter shared, pairing key copied into the distribution");

        // 6. adapter test; if the kernel could not start the adapter, offer our kernel once and retry
        if (!await TestAdapterAsync(ct))
        {
            if (_kernelOffered || cfg.AdapterTestMessage?.Contains("hci0") != true) return false;
            if (!await EnsureKernelAsync("The WSL kernel could not start this adapter (adapter_not_ready).", ct)) return false;
            if (!await TestAdapterAsync(ct)) return false;
        }

        // 7. AirPods test
        return await TestAirPodsAsync(device, ct);
    }

    /// <summary>Creates (if needed) and provisions a distribution without touching Bluetooth — used by the CLI self-test.</summary>
    public async Task<string?> CreateAndProvisionAsync(string distro, bool mediatek, CancellationToken ct)
    {
        if (!await _wsl.DistroExistsAsync(distro, ct))
        {
            var err = await CreateDistroAsync(distro, ct);
            if (err is not null) return err;
        }
        return await ProvisionAsync(distro, mediatek, ct);
    }

    public async Task<bool> TestAdapterAsync(CancellationToken ct)
    {
        var cfg = _provider.Config;
        Set("adapter-test", SetupStepState.Running, "attaching adapter to WSL…");
        var (r, err) = await AdapterOperation(p => _provider.TestAdapterAsync(new Progress<string>(m => { Set("adapter-test", SetupStepState.Running, m); p.Report(m); }), ct, scanSeconds: 6));
        if (r is null && !_provider.LastOperationDisrupted)
        {
            // stopped by a check before anything was switched off (input devices, ambiguous adapter, busy):
            // not a verdict on the adapter, so the stored test result stays as it was
            Set("adapter-test", SetupStepState.Failed, "Not started: " + err);
            return false;
        }
        cfg.AdapterTestAt = DateTimeOffset.Now;
        cfg.AdapterTestOk = r?.Ok == true;
        cfg.AdapterTestMessage = r?.Ok == true ? $"hci0 up ({r.Address}){DescribeScan(r.Raw)}" : err ?? r?.Error ?? r?.Outcome ?? "unknown";
        cfg.Save();
        if (r?.Ok == true) { Set("adapter-test", SetupStepState.Done, cfg.AdapterTestMessage); return true; }
        var release = await _wsl.KernelReleaseAsync(cfg.Distro, ct);
        var hint = cfg.AdapterTestMessage?.Contains("hci0") == true
            ? WslKernelManager.IsOurRelease(release)
                ? $" — WSL runs the MediaTek kernel ({release}) but the adapter still did not start; see the log (dmesg lines) and docs/WSL-BRIDGE.md."
                : $" — the WSL kernel ({release ?? "?"}) could not start this adapter."
            : "";
        Set("adapter-test", SetupStepState.Failed, cfg.AdapterTestMessage + hint);
        return false;
    }

    /// <summary>", LE scan: 14 advertisers, AirPods Pro 3 (0x2027) seen" from the adapter-test RESULT.</summary>
    private static string DescribeScan(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("scan", out var s) || s.ValueKind != JsonValueKind.Object) return "";
            if (s.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return $", LE scan failed: {(s.TryGetProperty("error", out var e) ? e.GetString() : "?")}";
            int n = s.TryGetProperty("advertisers", out var a) ? a.GetInt32() : 0;
            var models = s.TryGetProperty("apple_proximity", out var ap)
                ? ap.EnumerateArray().Select(x => x.GetProperty("model").GetString() ?? "").Distinct().ToList() : [];
            var names = models.Select(m => ushort.TryParse(m.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var id)
                ? $"{Core.Models.AirPodsCatalog.ByProductId(id)?.Name ?? "Apple device"} ({m})" : m);
            return $", LE scan: {n} advertisers" + (models.Count > 0 ? ", seen: " + string.Join(", ", names) : ", no AirPods advertisement (open the case near the PC)");
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>Makes sure WSL runs the AirPods Control MediaTek kernel (asks first: it is a global WSL change).
    /// Returns true when the kernel is active afterwards.</summary>
    public async Task<bool> EnsureKernelAsync(string reason, CancellationToken ct)
    {
        _kernelOffered = true;
        var cfg = _provider.Config;
        Set("kernel", SetupStepState.Running, "checking the running WSL kernel…");
        var running = await _wsl.KernelReleaseAsync(cfg.Distro, ct);
        if (WslKernelManager.IsOurRelease(running)) { Set("kernel", SetupStepState.Done, $"active: {running}"); return true; }

        var setting = Kernel.Configured(out var path);
        if (setting == WslKernelSetting.Foreign)
        {
            Set("kernel", SetupStepState.Warning, $".wslconfig already uses another custom kernel ({path}); it is left unchanged. It must be built with CONFIG_BT_HCIBTUSB_MTK=y.");
            return false;
        }
        if (!Kernel.BundleAvailable)
        {
            Set("kernel", SetupStepState.Warning, "This build does not include the MediaTek WSL kernel. Build it with kernel/build-kernel.sh (docs/WSL-BRIDGE.md).");
            return false;
        }
        var ok = await Confirm("Install WSL kernel with MediaTek support",
            reason + " The stock WSL kernel has no MediaTek Bluetooth driver.\n\n" +
            "AirPods Control can switch WSL to its own kernel: Microsoft's WSL kernel (same version) with MediaTek Bluetooth enabled and the MT7921/MT7922/MT7925 firmware built in.\n\n" +
            "• This applies to ALL WSL distributions, including Docker Desktop.\n" +
            "• WSL will be restarted now (wsl --shutdown): running Linux programs and Docker containers stop.\n" +
            $"• Your .wslconfig is backed up; files go to {WslKernelManager.InstallDir}.\n" +
            "• 'Remove driverless mode' restores the stock kernel.\n\nInstall the kernel?");
        if (!ok) { Set("kernel", SetupStepState.Warning, "not installed (declined). The adapter test will most likely fail."); return false; }

        Set("kernel", SetupStepState.Running, "installing kernel, restarting WSL…");
        var err = await Kernel.InstallAsync(ct);
        if (err is not null) { Set("kernel", SetupStepState.Failed, err); return false; }
        running = await _wsl.KernelReleaseAsync(cfg.Distro, ct);   // starts the VM on the new kernel
        if (!WslKernelManager.IsOurRelease(running))
        {
            Set("kernel", SetupStepState.Failed, $"installed, but WSL still runs {running ?? "?"} — check .wslconfig and the WSL version (kernelModules needs WSL 2.x).");
            return false;
        }
        Set("kernel", SetupStepState.Done, $"active: {running}");
        return true;
    }

    public async Task<bool> TestAirPodsAsync(ulong device, CancellationToken ct)
    {
        Set("airpods-test", SetupStepState.Running, "connecting…");
        var (r, err) = await AdapterOperation(p => _provider.ReadStatusAsync(device, new Progress<string>(m => { Set("airpods-test", SetupStepState.Running, m); p.Report(m); }), ct));
        if (r?.Ok == true)
        {
            var batt = string.Join(", ", r.Battery.Select(b => $"{b.Component} {(b.Level is null ? "—" : b.Level + "%")}"));
            if (r.CurrentMode is { } m)
            {
                Set("airpods-test", SetupStepState.Done, $"AirPods answered. Mode: {NoiseControlManager.Describe(m)}. Battery: {(batt.Length > 0 ? batt : "—")}");
                return true;
            }
            // AirPods Pro 3 (14.11.2026): without an audio connection they send battery/ear data but no listening-mode
            // packets; commands are applied without an echo (verified by ear). AAP data received = channel works.
            if (r.Battery.Count > 0)
            {
                Set("airpods-test", SetupStepState.Done,
                    $"AirPods answered over AAP. Battery: {batt}. These AirPods do not report their mode to the bridge: switches are shown as \"sent\", not \"confirmed\".");
                return true;
            }
            Set("airpods-test", SetupStepState.Failed, "The AAP channel opened, but the AirPods sent no data (take them out of the case and test again).");
            return false;
        }
        Set("airpods-test", SetupStepState.Failed, err ?? r?.Error ?? r?.Outcome ?? "no answer");
        return false;
    }

    // ------------------------------------------------------------------ steps

    private async Task<string?> CreateDistroAsync(string name, CancellationToken ct)
    {
        Directory.CreateDirectory(BridgeConfig.Dir);
        var tar = Path.Combine(BridgeConfig.Dir, "alpine-minirootfs.tar.gz");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AirPodsControl/0.2");
            var (file, sha) = await ResolveAlpineAsync(http, ct);
            Set("distro", SetupStepState.Running, $"downloading {file}…");
            var bytes = await http.GetByteArrayAsync(AlpineBase + file, ct);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                return $"SHA-256 mismatch for {file} (expected {sha}, got {actual}) — download rejected.";
            await File.WriteAllBytesAsync(tar, bytes, ct);
            Set("distro", SetupStepState.Running, $"importing into WSL ({bytes.Length / 1024} KB, SHA-256 verified)…");
            var r = await _wsl.ImportAsync(name, BridgeConfig.DistroDir, tar, ct);
            return r.Success ? null : "wsl --import failed: " + r.FirstErrorLine;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return "Download/import failed: " + ex.Message;
        }
        finally { try { File.Delete(tar); } catch { } }
    }

    /// <summary>Reads latest-releases.yaml (file + sha256 of the minirootfs flavour); falls back to the .sha256 file.</summary>
    private static async Task<(string file, string sha)> ResolveAlpineAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var yaml = await http.GetStringAsync(AlpineBase + "latest-releases.yaml", ct);
            foreach (var block in Regex.Split(yaml, @"^-\s*$", RegexOptions.Multiline))
            {
                if (!Regex.IsMatch(block, @"^\s*flavor:\s*alpine-minirootfs\s*$", RegexOptions.Multiline)) continue;
                var f = Regex.Match(block, @"^\s*file:\s*(\S+)\s*$", RegexOptions.Multiline);
                var h = Regex.Match(block, @"^\s*sha256:\s*([0-9a-fA-F]{64})\s*$", RegexOptions.Multiline);
                if (f.Success && h.Success) return (f.Groups[1].Value, h.Groups[1].Value);
            }
        }
        catch (HttpRequestException) { }
        var shaFile = await http.GetStringAsync(AlpineBase + AlpineFallbackFile + ".sha256", ct);
        return (AlpineFallbackFile, shaFile.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
    }

    private async Task<string?> ProvisionAsync(string distro, bool mediatek, CancellationToken ct)
    {
        if (await BridgeScripts.CopyAsync(_wsl, distro, ct) is { } cerr) return cerr;
        _provider.StopDaemon(); // a running daemon would keep the old code
        var args = new List<string> { "/bin/sh", "/opt/airpods/bridge.sh", "provision" };
        if (mediatek) args.Add("--mediatek");
        var r = await _wsl.ExecAsync(distro, args, TimeSpan.FromMinutes(10), ct);
        var res2 = BridgeResultParser.Parse(r.StdOut);
        return res2?.Ok == true ? null : "Package installation failed: " + (res2?.Error ?? r.FirstErrorLine);
    }

    /// <summary>Starts the elevated helper (one UAC prompt) and waits for it. Returns an error message or null.</summary>
    public async Task<string?> RunElevatedAsync(string vidPid, IEnumerable<ulong> devices, bool unbind, CancellationToken ct)
    {
        Directory.CreateDirectory(BridgeConfig.Dir);
        var script = Path.Combine(BridgeConfig.Dir, "elevated-setup.ps1");
        var status = Path.Combine(BridgeConfig.Dir, "elevated-status.json");
        var keys = Path.Combine(BridgeConfig.Dir, "keys.reg");
        await File.WriteAllTextAsync(script, ReadResource("bridge/elevated-setup.ps1"), ct);
        try { File.Delete(status); } catch { }

        var macs = string.Join(",", devices.Select(d => d.ToString("X12")));
        var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -VidPid {vidPid} -OutFile \"{keys}\" -StatusFile \"{status}\"";
        if (unbind) args += " -Unbind";
        else
        {
            if (!_usbipd.IsInstalled) args += " -InstallUsbipd";
            if (macs.Length > 0) args += $" -DeviceMacs {macs}";
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is null) return "Could not start the administrator step.";
            await p.WaitForExitAsync(ct);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt was cancelled.";
        }
        finally { try { File.Delete(script); } catch { } }

        if (!File.Exists(status)) return "The administrator step did not report a result.";
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(status, ct));
        File.Delete(status);
        var e = doc.RootElement.GetProperty("error");
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        _log.Info("elevated step: " + doc.RootElement.GetRawText());
        return null;
    }

    private async Task<string?> InstallKeysAsync(BridgeConfig cfg, ulong device, string deviceName, CancellationToken ct)
    {
        var keyFile = Path.Combine(BridgeConfig.Dir, "keys.reg");
        try
        {
            if (!File.Exists(keyFile)) return "No pairing key was exported.";
            var entries = WindowsLinkKeyParser.Parse(await File.ReadAllTextAsync(keyFile, ct));
            var e = entries.FirstOrDefault(k => k.DeviceAddress == device);
            if (e is null) return $"Windows has no classic pairing key for {BtAddress.Format(device)} (re-pair the AirPods with Windows).";
            var safeName = Regex.Replace(deviceName, @"[^\w \-\(\)]", "");
            var r = await _wsl.ExecAsync(cfg.Distro, ["/bin/sh", "/opt/airpods/bridge.sh", "install-key", e.AdapterMac, e.DeviceMac, "-", safeName],
                TimeSpan.FromSeconds(30), ct, stdin: e.KeyHex + "\n");
            if (BridgeResultParser.Parse(r.StdOut)?.Ok != true) return "Installing the key in WSL failed: " + r.FirstErrorLine;
            cfg.AdapterMac = e.AdapterMac;
            var id = device.ToString("X12");
            if (!cfg.KeysInstalledFor.Contains(id)) cfg.KeysInstalledFor.Add(id);
            return null;
        }
        finally
        {
            // the exported key must not stay on the Windows side
            try { if (File.Exists(keyFile)) { File.WriteAllBytes(keyFile, new byte[new FileInfo(keyFile).Length]); File.Delete(keyFile); } } catch { }
        }
    }

    /// <summary>Removes the distribution (with the copied key) and un-shares the adapter (one UAC prompt).</summary>
    public async Task<string?> RemoveAsync(CancellationToken ct)
    {
        var cfg = _provider.Config;
        string? err = null;
        if (await _wsl.DistroExistsAsync(cfg.Distro, ct))
        {
            var r = await _wsl.UnregisterAsync(cfg.Distro, ct);
            if (!r.Success) err = "wsl --unregister failed: " + r.FirstErrorLine;
        }
        if (cfg.AdapterVidPid is { } vp && _usbipd.IsInstalled)
            err = await RunElevatedAsync(vp, [], unbind: true, ct) ?? err;
        // our kernel line in .wslconfig (only ours; a foreign kernel= is never touched) + kernel files
        err = await Kernel.RemoveAsync(ct) ?? err;
        BridgeConfig.Delete();
        _provider.ReloadConfig();
        return err;
    }

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private string CustomKernelNote() => Kernel.Configured(out var p) switch
    {
        WslKernelSetting.Ours => " · AirPods Control MediaTek kernel configured",
        WslKernelSetting.Foreign => $" · custom kernel configured in .wslconfig ({p})",
        _ => " · stock WSL kernel",
    };
}
