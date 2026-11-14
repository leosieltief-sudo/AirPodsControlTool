using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;

// airpodsctl — proof-of-concept / diagnostics tool. Every value printed comes from the OS or the AirPods.
// Some launchers (seen with Windows PowerShell 5.1 on long paths) pass the exe path as the first argument; drop it.
string[] argv = args.Length > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]) ? args[1..] : args;
var cmd = argv.Length > 0 ? argv[0].ToLowerInvariant() : "diag";
var log = new ConsoleLog(argv.Contains("-v") ? LogLevel.Debug : LogLevel.Info);

try
{
    return cmd switch
    {
        "diag" or "list" => await Diag(),
        "battery" => await Battery(argv.Length > 1 && int.TryParse(argv[1], out var s) ? s : 20),
        "mode" when argv.Length > 1 => await SetMode(argv[1]),
        "monitor" => await Monitor(argv.Length > 1 && int.TryParse(argv[1], out var m) ? m : 30),
        "bridge-status" => await BridgeStatus(),
        "bridge-selftest" => await BridgeSelfTest(argv.Contains("--keep")),
        "bridge-mode" when argv.Length > 1 => await BridgeMode(argv[1]),
        "bridge-provision" => await BridgeProvision(),
        "bridge-setup" => await BridgeSetup(),
        "bridge-read" => await BridgeRead(),
        "bridge-battery" => await BridgeBattery(),
        "bridge-check" => await BridgeCheck(),
        "bridge-enable-off" => await BridgeHoldCycle(addOff: true),
        "bridge-hold-cycle" => await BridgeHoldCycle(addOff: false),
        "bridge-adapter-test" => await BridgeAdapterTest(),
        "kernel-status" => await KernelStatus(),
        "kernel-install" => await KernelInstall(Opt("--from")),
        "kernel-remove" => await KernelRemove(),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        airpodsctl diag                 Bluetooth state, paired AirPods, control-channel availability
        airpodsctl battery [seconds]    Read battery from BLE advertisements (10 % steps)
        airpodsctl mode <anc|off|transparency|adaptive>   Send a listening-mode command and wait for confirmation
        airpodsctl monitor [seconds]    Open the AAP channel and print every packet the AirPods send
        airpodsctl bridge-status        driverless (WSL2) bridge: config, usbipd, adapter, readiness
        airpodsctl bridge-selftest [--keep]   import a temporary Alpine distro, install BlueZ/Python, check scripts, remove it
        airpodsctl bridge-mode <anc|off|transparency|adaptive>[,mode…] [--trace] [--legacy] [--no-kick] [--allow-input-loss]
                                        switch via the WSL bridge (hands the adapter to WSL!); prints the operation trace;
                                        extra modes are merged into the same hand-over; --legacy = old bridge.sh path
        airpodsctl bridge-battery [--allow-input-loss]   exact battery over AAP (one hand-over, no command sent)
        airpodsctl bridge-hold-cycle --modes off,anc,transparency[,adaptive] [--allow-input-loss] [--trace] [--no-kick]
                                        write the press-and-hold cycle exactly (2+ modes; with off also 'Allow Off' 0x34);
                                        one hand-over; the current listening mode and the stems' Siri/noise-control action are not changed
        airpodsctl bridge-enable-off (--modes anc,transparency[,adaptive] | --keep-current) [--allow-input-loss] [--trace] [--no-kick]
                                        same, Off always added; --keep-current = add Off to the cycle the AirPods report —
                                        if they do not report it, nothing is written
        airpodsctl bridge-check         which adapter would be handed over and which Bluetooth input devices it serves (no hand-over)
                                        a connected Bluetooth mouse/keyboard blocks every hand-over unless --allow-input-loss is given
        airpodsctl bridge-setup [--yes] [--allow-input-loss]  the full setup wizard (one UAC prompt); --yes allows the kernel install only
        airpodsctl bridge-provision     create/provision the 'airpods-bridge' distribution (no adapter, no admin)
        airpodsctl bridge-adapter-test  attach the adapter to WSL, wait for hci0, BlueZ power on, LE scan for AirPods, detach
        airpodsctl kernel-status        MediaTek WSL kernel: bundled files, .wslconfig, running kernel
        airpodsctl kernel-install [--from DIR]   set the MediaTek WSL kernel in .wslconfig (backup) + wsl --shutdown
        airpodsctl kernel-remove        remove only our kernel lines from .wslconfig (backup) + wsl --shutdown
        add -v for packet-level logging
        """);
    return 2;
}

async Task<int> Diag()
{
    using var mgr = new BluetoothDeviceManager(log);
    await mgr.StartAsync();
    Console.WriteLine($"Bluetooth radio        : {mgr.Radio}");
    Console.WriteLine($"MagicAAP driver service: {(AapControlChannel.IsMagicAapInstalled ? "installed" : "not installed")}");
    Console.WriteLine($"Winsock L2CAP socket   : {AapControlChannel.ProbeWinsockL2cap()}");
    var list = BluetoothDeviceManager.FindPairedAppleAudioDevices();
    if (list.Count == 0) { Console.WriteLine("No paired AirPods found."); return 3; }
    foreach (var d in list)
    {
        var (name, connected) = await mgr.TrackAsync(d.Address);
        var probe = AapControlChannel.Probe(d);
        Console.WriteLine();
        Console.WriteLine($"Device      : {name ?? "?"}  [{d.Mac}]");
        Console.WriteLine($"Model       : {d.Model.Name} (PID 0x{d.ProductId:X4}), noise control: {d.Model.NoiseControl}, Off: {d.Model.OffMode}");
        Console.WriteLine($"Connected   : {connected}");
        Console.WriteLine($"AAP node    : {d.AapNodeInstanceId ?? "(none)"}");
        Console.WriteLine($"AAP driver  : {d.AapNodeDriver ?? "(no driver bound)"}");
        Console.WriteLine($"Control     : {probe.Availability} — {probe.Explanation}");
        if (probe.InterfacePath is not null) Console.WriteLine($"Interface   : {probe.InterfacePath}");
    }
    return 0;
}

async Task<int> Battery(int seconds)
{
    var list = BluetoothDeviceManager.FindPairedAppleAudioDevices();
    var filter = new AdvertisementFilter { ExpectedModelId = list.FirstOrDefault()?.ProductId };
    Console.WriteLine(filter.ExpectedModelId is { } pid
        ? $"Listening {seconds}s for proximity advertisements of model 0x{pid:X4} (open the case / take a bud out if nothing arrives)…"
        : $"No paired AirPods; showing all Apple proximity advertisements for {seconds}s…");
    using var ble = new BleAdvertisementSource(log);
    int count = 0;
    ble.Received += (m, rssi) =>
    {
        bool ours = filter.ExpectedModelId is null || filter.Accept(m, rssi);
        if (!ours) return;
        count++;
        static string P(int? v, bool c) => v is null ? "—" : $"{v}%{(c ? "⚡" : "")}";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} model=0x{m.ModelId:X4} rssi={rssi,4}  L {P(m.LeftPercent, m.LeftCharging),-6} R {P(m.RightPercent, m.RightCharging),-6} Case {P(m.CasePercent, m.CaseCharging),-6} inEar L={m.LeftInEar} R={m.RightInEar} lidOpen={m.LidOpen}");
    };
    ble.Start();
    if (ble.LastError is not null) { Console.WriteLine("BLE scan failed: " + ble.LastError); return 4; }
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine(count == 0 ? "No matching advertisements received." : $"{count} advertisements.");
    return count == 0 ? 5 : 0;
}

async Task<(AirPodsProtocolService? proto, PairedAppleDevice? dev)> OpenChannel()
{
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return (null, null); }
    var probe = AapControlChannel.Probe(dev);
    if (probe.Availability != ControlChannelAvailability.Available)
    {
        Console.WriteLine($"Control channel unavailable: {probe.Explanation}");
        return (null, dev);
    }
    var proto = new AirPodsProtocolService(new DriverAapTransport(probe.InterfacePath!, log), log);
    proto.BatteryReceived += b => Console.WriteLine("Battery (AAP): " + string.Join(", ", b.Entries.Select(e => $"{e.Component}={(e.Level is null ? "—" : e.Level + "%")} {e.Status}")));
    proto.EarDetectionReceived += e => Console.WriteLine($"Ear detection: primary={e.Primary} secondary={e.Secondary}");
    proto.MetadataReceived += m => Console.WriteLine($"Metadata: name={m.Name} model={m.ModelNumber} fw={m.FirmwareVersion}");
    proto.ControlReceived += c => Console.WriteLine($"Control 0x{c.Identifier:X2} = {c.Value1:X2} {c.Value2:X2} {c.Value3:X2} {c.Value4:X2}{(c.AsListeningMode is { } lm ? "  (" + lm + ")" : "")}");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await proto.StartAsync(cts.Token);
    return (proto, dev);
}

async Task<int> SetMode(string arg)
{
    ListeningMode? mode = arg.ToLowerInvariant() switch
    {
        "anc" or "nc" or "noise" => ListeningMode.NoiseCancellation,
        "off" => ListeningMode.Off,
        "transparency" or "t" => ListeningMode.Transparency,
        "adaptive" => ListeningMode.Adaptive,
        _ => null,
    };
    if (mode is null) return Usage();
    var (proto, dev) = await OpenChannel();
    if (proto is null) return 6;
    await using (proto)
    {
        var mgr = new NoiseControlManager(log) { Model = dev!.Model };
        mgr.Attach(proto);
        await Task.Delay(1500); // let initial notifications (current mode, allow-off state) arrive
        Console.WriteLine($"Current mode reported by AirPods: {(mgr.CurrentMode?.ToString() ?? "unknown")}");
        var r = await mgr.SetModeAsync(mode.Value);
        Console.WriteLine($"Result: {r.Outcome} — {r.Message}");
        return r.Success ? 0 : 7;
    }
}

async Task<int> Monitor(int seconds)
{
    var (proto, _) = await OpenChannel();
    if (proto is null) return 6;
    await using (proto)
    {
        Console.WriteLine($"Monitoring for {seconds}s…");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }
    return 0;
}

async Task<int> BridgeStatus()
{
    var bridge = new AirPodsControl.Windows.WslBridgeProvider(log);
    var cfg = bridge.Config;
    var wsl = new AirPodsControl.Windows.Bridge.WslClient(log);
    var (ok, msg) = await wsl.CheckAsync(CancellationToken.None);
    Console.WriteLine($"WSL               : {(ok ? "yes" : "no")} — {msg}");
    Console.WriteLine($"usbipd-win        : {AirPodsControl.Windows.Bridge.UsbipdClient.FindExe() ?? "not installed"}");
    foreach (var a in AirPodsControl.Windows.Bridge.BluetoothUsbAdapter.FindAll())
        Console.WriteLine($"USB BT adapter    : {a.Name} ({a.VidPid}){(a.IsMediaTek ? "  [MediaTek: needs the AirPods Control WSL kernel — see kernel-status]" : "")}");
    Console.WriteLine($"Distribution      : {cfg.Distro} {(ok && await wsl.DistroExistsAsync(cfg.Distro, CancellationToken.None) ? "(present)" : "(absent)")}");
    Console.WriteLine($"Provisioned       : {cfg.Provisioned} (scripts v{cfg.ScriptVersion})");
    Console.WriteLine($"Keys copied for   : {(cfg.KeysInstalledFor.Count == 0 ? "-" : string.Join(", ", cfg.KeysInstalledFor))}");
    Console.WriteLine($"Adapter test      : {(cfg.AdapterTestOk is null ? "not run" : cfg.AdapterTestOk.Value ? "ok" : "FAILED")} {cfg.AdapterTestMessage}");
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    bridge.CurrentDevice = dev?.Address;
    Console.WriteLine($"Bridge status     : {(bridge.Status.Ready ? "READY" : "not ready")} — {bridge.Status.Message}");
    return bridge.Status.Ready ? 0 : 8;
}

async Task<int> BridgeSelfTest(bool keep)
{
    const string distro = "airpods-bridge-selftest";
    var bridge = new AirPodsControl.Windows.WslBridgeProvider(log);
    var setup = new AirPodsControl.Windows.Bridge.BridgeSetupService(log, bridge);
    var wsl = new AirPodsControl.Windows.Bridge.WslClient(log);
    Console.WriteLine($"[1] creating + provisioning temporary distribution '{distro}' …");
    var err = await setup.CreateAndProvisionAsync(distro, mediatek: false, CancellationToken.None);
    int rc = 0;
    if (err is not null) { Console.WriteLine("    FAILED: " + err); rc = 9; }
    else
    {
        Console.WriteLine("    ok");
        (string name, string[] cmd)[] checks =
        [
            ("alpine release", ["cat", "/etc/alpine-release"]),
            ("bridge.sh syntax", ["/bin/sh", "-n", "/opt/airpods/bridge.sh"]),
            ("aap_bridge.py selftest", ["python3", "/opt/airpods/aap_bridge.py", "--selftest"]),
            ("python AF_BLUETOOTH", ["python3", "-c", "import socket;print('AF_BLUETOOTH' , hasattr(socket,'AF_BLUETOOTH'))"]),
            ("ctypes libc", ["python3", "-c", "import ctypes,ctypes.util;print('libc', ctypes.util.find_library('c'))"]),
            ("bluez binaries", ["/bin/sh", "-c", "ls /usr/lib/bluetooth/bluetoothd /usr/bin/btmgmt /usr/bin/dbus-daemon"]),
            ("kernel", ["uname", "-r"]),
            ("bt modules", ["/bin/sh", "-c", "ls /lib/modules/$(uname -r)/kernel/drivers/bluetooth/ 2>&1 | head -n 20; grep -c . /proc/modules"]),
            ("install-key validation", ["/bin/sh", "/opt/airpods/bridge.sh", "install-key", "00:00:00:00:00:00", "00:00:00:00:00:01", "nothex"]),
            ("run without adapter", ["/bin/sh", "/opt/airpods/bridge.sh", "run", "--mac", "00:00:00:00:00:01", "--mode", "anc", "--hci-timeout", "1"]),
        ];
        foreach (var (name, cmd) in checks)
        {
            var r = await wsl.ExecAsync(distro, cmd, TimeSpan.FromSeconds(60), CancellationToken.None);
            Console.WriteLine($"[{name}] exit={r.ExitCode}");
            foreach (var l in r.Combined.Split('\n').Take(12)) Console.WriteLine("    " + l.TrimEnd());
        }
    }
    if (!keep)
    {
        var u = await wsl.UnregisterAsync(distro, CancellationToken.None);
        Console.WriteLine($"[cleanup] wsl --unregister {distro}: {(u.Success ? "ok" : u.FirstErrorLine)}");
    }
    return rc;
}

async Task<int> BridgeMode(string arg)
{
    static ListeningMode? ParseMode(string a) => a.Trim().ToLowerInvariant() switch
    {
        "anc" => ListeningMode.NoiseCancellation, "off" => ListeningMode.Off,
        "transparency" => ListeningMode.Transparency, "adaptive" => ListeningMode.Adaptive, _ => null,
    };
    // "anc,transparency": the later modes are merged into the running hand-over exactly like presses during a switch in the app
    var modes = arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseMode).ToList();
    if (modes.Count == 0 || modes.Any(m => m is null)) return Usage();
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return 3; }
    var bridge = NewBridge(dev.Address);
    if (!bridge.Status.Ready) { Console.WriteLine("Bridge not ready: " + bridge.Status.Message); return 8; }
    bridge.Trace = argv.Contains("--trace");
    if (argv.Contains("--legacy")) bridge.Config.UseDaemon = false;   // in memory only: one bridge.sh run per command
    var queue = new Queue<ListeningMode>(modes.Skip(1).Select(m => m!.Value));
    if (argv.Contains("--own")) bridge.ExtraArgs.Add("--own");
    if (argv.Contains("--a2dp")) bridge.ExtraArgs.Add("--a2dp");
    if (Opt("--features") is { } fb) bridge.ExtraArgs.AddRange(["--features", fb]);
    if (Opt("--settle") is { } st) bridge.ExtraArgs.AddRange(["--settle", st]);
    var progress = new SyncProgress(s =>
    {
        Console.WriteLine("  … " + s);
        if (s == "Mode sent." && queue.Count > 0)
            Console.WriteLine("  merge: " + bridge.Coordinator.TryMerge(new ModeSwitchRequest(dev.Address, "AirPods", dev.Model, queue.Dequeue(), true, "cli")));
    });
    var r = await bridge.SetModeAsync(new ModeSwitchRequest(dev.Address, "AirPods", dev.Model, modes[0]!.Value, true, "cli", argv.Contains("--allow-input-loss")),
        progress, CancellationToken.None);
    if (bridge.LastOutput is { } o)
        foreach (var l in o.Split('\n').Where(l => l.StartsWith("PKT ")).Take(60)) Console.WriteLine("  " + l.TrimEnd());
    Console.WriteLine($"Result: {r.Outcome} — {r.Message}");
    if (bridge.LastResult is { } lr) Console.WriteLine("Raw: " + lr.Raw);
    if (bridge.LastOperationDisrupted)
    {
        if (await WaitForAirPodsAsync(dev.Address, TimeSpan.FromSeconds(25), kick: !argv.Contains("--no-kick"), bridge.LastTrace) is { } back)
            Console.WriteLine($"AirPods connected to Windows again {back:0.0} s after the hand-over");
        else Console.WriteLine("AirPods did not reconnect to Windows within 25 s");
    }
    await PrintTraceAsync(bridge);
    return r.Success ? 0 : 7;
}

async Task<int> BridgeBattery()
{
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return 3; }
    var bridge = NewBridge(dev.Address);
    if (!bridge.Status.Ready) { Console.WriteLine("Bridge not ready: " + bridge.Status.Message); return 8; }
    var (r, err) = await bridge.ReadBatteryAsync(dev.Address, "cli", new SyncProgress(s => Console.WriteLine("  … " + s)), CancellationToken.None,
        argv.Contains("--allow-input-loss"));
    Console.WriteLine(err is not null ? "FAILED: " + err : "Battery: " + string.Join(", ", r!.Battery.Select(b => $"{b.Component} {b.Level?.ToString() ?? "—"}%{(b.Charging ? " charging" : "")}")));
    if (bridge.LastOperationDisrupted && await WaitForAirPodsAsync(dev.Address, TimeSpan.FromSeconds(25), kick: true, bridge.LastTrace) is { } back)
        Console.WriteLine($"AirPods connected to Windows again {back:0.0} s after the hand-over");
    await PrintTraceAsync(bridge);
    return err is null ? 0 : 13;
}

async Task<int> BridgeCheck()
{
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    var bridge = NewBridge(dev?.Address);
    var (adapter, input) = await bridge.CheckAsync(dev?.Address, CancellationToken.None);
    Console.WriteLine(adapter.Ok ? "Adapter : " + adapter.Target!.Description : "Adapter : NOT RESOLVED — " + adapter.Error);
    if (input is not null)
    {
        Console.WriteLine($"Input   : {input.Status} — {input.Explain()}");
        foreach (var d in input.Devices)
            Console.WriteLine($"          {d.Name} [{AirPodsControl.Core.Bridge.BtAddress.Format(d.Address)}] {d.Transport} connected={d.Connected?.ToString() ?? "unknown"} on_adapter={d.OnTargetAdapter?.ToString() ?? "unknown"} — {d.Evidence}");
    }
    Console.WriteLine("Nothing was switched off (check only).");
    return adapter.Ok && input?.Status == AirPodsControl.Core.Bridge.InputGuardStatus.Clear ? 0 : 14;
}

async Task<int> BridgeHoldCycle(bool addOff)
{
    // the set must be chosen explicitly: no default, no guess
    var modesArg = Opt("--modes");
    bool keep = addOff && argv.Contains("--keep-current");
    if ((modesArg is null) == !keep)
    {
        Console.WriteLine(addOff ? "Choose exactly one: --modes <list> (e.g. --modes anc,transparency) or --keep-current."
                                 : "Give the cycle with --modes (e.g. --modes off,anc,transparency).");
        return 2;
    }
    CycleModes? modes = null;
    if (modesArg is not null)
    {
        modes = CycleModeSet.Parse(modesArg, out var perr);
        if (modes is null) { Console.WriteLine("Invalid --modes: " + perr); return 2; }
        if (addOff) modes |= CycleModes.Off;
    }
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return 3; }
    var request = new OffCycleRequest(dev.Address, modes, "cli", argv.Contains("--allow-input-loss"));
    if (OffCyclePlanner.ValidateRequest(request, dev.Model) is { } verr) { Console.WriteLine("Not started: " + verr); return 2; }
    var bridge = NewBridge(dev.Address);
    if (!bridge.Status.Ready) { Console.WriteLine("Bridge not ready: " + bridge.Status.Message); return 8; }
    bridge.Trace = argv.Contains("--trace");
    Console.WriteLine(modes is { } m
        ? $"Press-and-hold cycle to write: {CycleModeSet.Describe(m)}{(m.HasFlag(CycleModes.Off) ? " ('Allow Off' first)" : "")}."
        : "Keeping the cycle the AirPods report and adding Off (nothing is written if they do not report it).");
    Console.WriteLine("Windows Bluetooth pauses for about 15 s while the adapter is in WSL. The current listening mode is not changed.");
    var r = await bridge.ApplyHoldCycleAsync(request, dev.Model, new SyncProgress(s => Console.WriteLine("  … " + s)), CancellationToken.None);
    if (bridge.LastOutput is { } o)
        foreach (var l in o.Split('\n').Where(l => l.StartsWith("PKT ")).Take(60)) Console.WriteLine("  " + l.TrimEnd());
    Console.WriteLine($"Result: {r.Status} — {r.Message}");
    foreach (var line in r.StatusLines()) Console.WriteLine("  " + line);
    if (bridge.LastOperationDisrupted)
    {
        if (await WaitForAirPodsAsync(dev.Address, TimeSpan.FromSeconds(25), kick: !argv.Contains("--no-kick"), bridge.LastTrace) is { } back)
            Console.WriteLine($"AirPods connected to Windows again {back:0.0} s after the hand-over");
        else Console.WriteLine("AirPods did not reconnect to Windows within 25 s");
    }
    await PrintTraceAsync(bridge);
    return r.Status switch
    {
        OffCycleStatus.Sent or OffCycleStatus.Confirmed or OffCycleStatus.AlreadySet => 0,
        OffCycleStatus.NotStarted => 16,
        OffCycleStatus.NothingSent => 15,
        _ => 7,
    };
}

AirPodsControl.Windows.WslBridgeProvider NewBridge(ulong? device)
{
    var bridge = new AirPodsControl.Windows.WslBridgeProvider(log) { CurrentDevice = device };
    // no dialog in the CLI: only the dedicated --allow-input-loss flag (never --yes) lets a hand-over cut off a mouse/keyboard
    bridge.ConfirmInputLoss = (report, op) =>
    {
        Console.WriteLine($"  input guard: {report.Status} — {report.Explain()}");
        Console.WriteLine("  → blocked. Pass --allow-input-loss to hand the adapter over anyway.");
        return Task.FromResult(false);
    };
    return bridge;
}

async Task PrintTraceAsync(AirPodsControl.Windows.WslBridgeProvider bridge)
{
    if (bridge.LastProbe is { } probe) await Task.WhenAny(probe.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
    if (bridge.LastTrace is not { } op) return;
    Console.WriteLine($"Trace {op.Header} (also in {AirPodsControl.Windows.WslBridgeProvider.TraceFile}):");
    foreach (var e in op.Events.Where(e => argv.Contains("--trace") || e.Origin != AirPodsControl.Core.Bridge.TraceOrigin.Aap || e.Name != "tx"))
        Console.WriteLine("  " + AirPodsControl.Core.Bridge.OperationTrace.Format(op, e));
    Console.WriteLine("Summary: " + op.Summary());
}

// Same idea as AirPodsController.WithAdapterHandoffAsync: optionally page the AirPods from Windows, then wait for the link.
async Task<double?> WaitForAirPodsAsync(ulong address, TimeSpan timeout, bool kick, AirPodsControl.Core.Bridge.OperationTrace? lastTrace = null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mgr = new BluetoothDeviceManager(log);
    Task<string>? k = null;
    while (sw.Elapsed < timeout)
    {
        try
        {
            using var d = await global::Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address);
            if (d?.ConnectionStatus == global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
            {
                if (k is not null) Console.WriteLine("  connection kick: " + (k.IsCompleted ? k.Result : "still running"));
                return sw.Elapsed.TotalSeconds;
            }
            if (kick && k is null && d is not null)
            {
                lastTrace?.App("reconnect_kick", "uncached SDP query from Windows");
                k = mgr.KickConnectionAsync(address, TimeSpan.FromSeconds(8));
                _ = k.ContinueWith(t => lastTrace?.App("reconnect_kick_done", t.IsCompletedSuccessfully ? t.Result : "failed"));
            }
        }
        catch { /* radio still coming back */ }
        await Task.Delay(250);
    }
    return null;
}

async Task<int> BridgeProvision()
{
    var bridge = new AirPodsControl.Windows.WslBridgeProvider(log);
    var setup = new AirPodsControl.Windows.Bridge.BridgeSetupService(log, bridge);
    var cfg = bridge.Config;
    var adapter = AirPodsControl.Windows.Bridge.BluetoothUsbAdapter.FindAll().FirstOrDefault();
    Console.WriteLine($"Provisioning '{cfg.Distro}' …");
    var err = await setup.CreateAndProvisionAsync(cfg.Distro, mediatek: false, CancellationToken.None);
    if (err is not null) { Console.WriteLine("FAILED: " + err); return 9; }
    cfg.Provisioned = true; cfg.ScriptVersion = AirPodsControl.Windows.Bridge.BridgeConfig.CurrentScriptVersion;
    if (adapter is not null)
    {
        cfg.AdapterVidPid = adapter.VidPid;
        if (adapter.IsMediaTek && cfg.HciTimeoutSeconds < 10) cfg.HciTimeoutSeconds = 10;
    }
    cfg.Save();
    Console.WriteLine($"ok (adapter {adapter?.VidPid ?? "none"}, hci timeout {cfg.HciTimeoutSeconds}s)");
    return 0;
}

async Task<int> BridgeSetup()
{
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return 3; }
    var bridge = NewBridge(dev.Address);
    var setup = new AirPodsControl.Windows.Bridge.BridgeSetupService(log, bridge);
    bool yes = argv.Contains("--yes");
    // --yes covers the kernel question only; losing a Bluetooth mouse/keyboard needs its own flag
    if (argv.Contains("--allow-input-loss")) bridge.ConfirmInputLoss = (report, op) => Task.FromResult(true);
    setup.Confirm = (title, msg) =>
    {
        Console.WriteLine($"\n[{title}]\n{msg}\n→ {(yes ? "yes (--yes)" : "no (pass --yes to allow)")}");
        return Task.FromResult(yes);
    };
    var last = new Dictionary<string, string>();
    setup.Changed += () =>
    {
        foreach (var s in setup.Steps)
        {
            var line = $"{s.State,-8} {s.Title}{(s.Detail.Length > 0 ? " — " + s.Detail : "")}";
            if (last.TryGetValue(s.Id, out var prev) && prev == line) continue;
            last[s.Id] = line;
            if (s.State != AirPodsControl.Windows.Bridge.SetupStepState.Pending) Console.WriteLine("  " + line);
        }
    };
    Console.WriteLine($"Driverless setup for {dev.Model?.Name ?? "AirPods"} [{AirPodsControl.Core.Bridge.BtAddress.Format(dev.Address)}]");
    var ok = await setup.RunAllAsync(dev.Address, "AirPods Pro", CancellationToken.None);
    Console.WriteLine(ok ? "SETUP OK" : "SETUP FAILED");
    bridge.ReloadConfig();
    Console.WriteLine("Bridge status: " + bridge.Status.Message);
    return ok ? 0 : 12;
}

async Task<int> BridgeRead()
{
    var dev = BluetoothDeviceManager.FindPairedAppleAudioDevices().FirstOrDefault();
    if (dev is null) { Console.WriteLine("No paired AirPods."); return 3; }
    var bridge = NewBridge(dev.Address);
    double settle = double.TryParse(Opt("--settle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3;
    var (r, err) = await bridge.ReadStatusAsync(dev.Address, new Progress<string>(m => Console.WriteLine("  … " + m)), CancellationToken.None,
        settle, trace: argv.Contains("--trace"));
    if (bridge.LastOutput is { } o)
        foreach (var l in o.Split('\n').Where(l => l.StartsWith("PKT ")).Take(60)) Console.WriteLine("  " + l.TrimEnd());
    Console.WriteLine(err is not null ? "FAILED: " + err : "Raw: " + r?.Raw);
    return r?.Ok == true ? 0 : 13;
}

async Task<int> BridgeAdapterTest()
{
    var bridge = NewBridge(null);
    var (r, err) = await bridge.TestAdapterAsync(new Progress<string>(s => Console.WriteLine("  … " + s)), CancellationToken.None, scanSeconds: 8);
    Console.WriteLine(err is not null ? "FAILED: " + err : $"Result: ok={r?.Ok} outcome={r?.Outcome}");
    if (r is not null) Console.WriteLine("Raw: " + r.Raw);
    var cfg = AirPodsControl.Windows.Bridge.BridgeConfig.Load();
    cfg.AdapterTestAt = DateTimeOffset.Now; cfg.AdapterTestOk = r?.Ok == true;
    cfg.AdapterTestMessage = r?.Ok == true ? $"hci0 up ({r.Address})" : err ?? r?.Error ?? r?.Outcome;
    cfg.Save();
    return r?.Ok == true ? 0 : 11;
}

string? Opt(string name) { var i = Array.IndexOf(argv, name); return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null; }

AirPodsControl.Windows.Bridge.WslKernelManager KernelManager(string? from) =>
    new(log, new AirPodsControl.Windows.Bridge.WslClient(log)) { SourceDir = from ?? AirPodsControl.Windows.Bridge.WslKernelManager.BundleDir };

async Task<int> KernelStatus()
{
    var k = KernelManager(Opt("--from"));
    var wsl = new AirPodsControl.Windows.Bridge.WslClient(log);
    var cfg = AirPodsControl.Windows.Bridge.BridgeConfig.Load();
    Console.WriteLine($"Bundle            : {k.SourceDir} {(k.BundleAvailable ? "(present)" : "(missing)")}");
    if (k.BundleInfo() is { } info) foreach (var l in info.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("    " + l.Trim());
    var st = k.Configured(out var path);
    Console.WriteLine($".wslconfig kernel : {st} {path}");
    Console.WriteLine($"Install dir       : {AirPodsControl.Windows.Bridge.WslKernelManager.InstallDir}");
    var distro = await wsl.DistroExistsAsync(cfg.Distro, CancellationToken.None) ? cfg.Distro : null;
    if (distro is null) { Console.WriteLine("Running kernel    : (distribution absent, not started)"); return 0; }
    var rel = await wsl.KernelReleaseAsync(distro, CancellationToken.None);
    Console.WriteLine($"Running kernel    : {rel} {(AirPodsControl.Windows.Bridge.WslKernelManager.IsOurRelease(rel) ? "[AirPods Control MediaTek kernel]" : "")}");
    return 0;
}

async Task<int> KernelInstall(string? from)
{
    var k = KernelManager(from);
    Console.WriteLine($"Installing kernel from {k.SourceDir} (WSL will be restarted: wsl --shutdown) …");
    var err = await k.InstallAsync(CancellationToken.None);
    Console.WriteLine(err ?? "ok — .wslconfig updated (a previous .wslconfig, if any, is backed up next to it as .wslconfig.airpodscontrol-*.bak), WSL stopped; the next start uses the new kernel.");
    return err is null ? 0 : 10;
}

async Task<int> KernelRemove()
{
    var err = await KernelManager(null).RemoveAsync(CancellationToken.None);
    Console.WriteLine(err ?? "ok — AirPods Control kernel removed from .wslconfig, WSL stopped.");
    return err is null ? 0 : 10;
}

/// <summary>Reports synchronously on the calling thread (the merge test needs "sent" before the daemon's reply is handled).</summary>
sealed class SyncProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
