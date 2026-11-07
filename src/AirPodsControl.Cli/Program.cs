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
