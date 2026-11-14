using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Windows;

public enum LinkState { Starting, NoAdapter, BluetoothOff, NoAirPodsPaired, Disconnected, Connected }
public enum ChannelState { Unavailable, Connecting, Ready, Failed }

/// <summary>
/// Orchestrates discovery → connection tracking → control channel → battery/noise-control state.
/// All public events may fire on background threads.
/// </summary>
public sealed class AirPodsController : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(3)];

    private readonly ILog _log;
    private readonly BluetoothDeviceManager _devices;
    private readonly BleAdvertisementSource _ble;
    private readonly AdvertisementFilter _filter = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Timer _tick;
    private Timer? _retry;
    private int _failures;
    private AirPodsProtocolService? _protocol;
    private bool _disposed;

    public AirPodsController(ILog log)
    {
        _log = log;
        _devices = new BluetoothDeviceManager(log);
        _ble = new BleAdvertisementSource(log);
        NoiseControl = new NoiseControlManager(log);
        Battery = new BatteryMonitor();
        _devices.RadioChanged += () => _ = RefreshAsync();
        _devices.PairedDevicesChanged += () => _ = RefreshAsync();
        _devices.TrackedConnectionChanged += connected => _ = RefreshAsync();
        _ble.Received += OnAdvertisement;
        Battery.Changed += _ => Raise();
        NoiseControl.ModeChanged += _ => Raise();
        _tick = new Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        Bridge = new WslBridgeProvider(log);
        MagicAap = new MagicAapProvider(NoiseControl, () => Channel == ChannelState.Ready
            ? new ProviderStatus(true, "channel open")
            : ProviderStatus.NotReady(AapControlChannel.IsMagicAapInstalled ? (ChannelMessage.Length > 0 ? ChannelMessage : "channel not open") : "driver not installed"));
    }

    // ------------------------------------------------------------------ mode switching backends (Strategy)

    public MagicAapProvider MagicAap { get; }
    public WslBridgeProvider Bridge { get; }
    public IReadOnlyList<IModeSwitchProvider> Providers => [MagicAap, Bridge];
    public ControlBackend BackendPreference { get; set; } = ControlBackend.Auto;
    public IModeSwitchProvider? ActiveProvider => BackendSelector.Select(BackendPreference, Providers);
    public string BackendExplanation => BackendSelector.Explain(BackendPreference, Providers);

    /// <summary>True while a WSL bridge hand-over is in progress (Windows Bluetooth is temporarily gone).</summary>
    public bool IsSwitching { get; private set; }
    public string? SwitchProgress { get; private set; }
    private volatile bool _handoff;
    private ListeningMode? _bridgeMode;

    /// <summary>
    /// Last listening mode reported by the AirPods: live over MagicAAP, or the value returned by the last WSL bridge
    /// session (<see cref="CurrentModeAt"/>; it can be changed later by a stem press without the app knowing).
    /// </summary>
    public ListeningMode? CurrentMode => Channel == ChannelState.Ready ? NoiseControl.CurrentMode : _bridgeMode;
    public bool CurrentModeIsLive => Channel == ChannelState.Ready;
    public DateTimeOffset? CurrentModeAt { get; private set; }
    /// <summary>False when <see cref="CurrentMode"/> is the last mode sent over the bridge without an echo.</summary>
    public bool CurrentModeConfirmed { get; private set; } = true;

    /// <summary>Seconds until the AirPods were connected to Windows again after the last hand-over (null = not back).</summary>
    public double? LastReconnectSeconds { get; private set; }

    /// <summary>
    /// A mode chosen while a WSL mode switch is running: merged into that hand-over while the adapter is still in WSL
    /// (latest wins, repeats ignored). Never starts a second hand-over and never queues anything for later.
    /// </summary>
    public MergeResult TryMergeMode(ListeningMode mode, bool autoEnableOffOption, string initiator)
    {
        if (Device is not { } dev || ActiveProvider is not WslBridgeProvider) return MergeResult.NotAccepting;
        if (Model is { } m && !AirPodsCatalog.SupportsMode(m, mode)) return MergeResult.NotAccepting;
        var r = Bridge.Coordinator.TryMerge(new ModeSwitchRequest(dev.Address, DeviceName ?? "AirPods", Model, mode, autoEnableOffOption, initiator));
        _log.Info($"mode {mode} from {initiator} while switching: {r}");
        return r;
    }

    public async Task<NoiseControlResult> SetModeAsync(ListeningMode mode, bool autoEnableOffOption, CancellationToken ct = default, string initiator = "app")
    {
        if (Device is not { } dev || Link != LinkState.Connected)
            return new(NoiseControlOutcome.NoControlChannel, mode, CurrentMode, "AirPods are not connected to Windows.");
        if (Model is { } m && !AirPodsCatalog.SupportsMode(m, mode))
            return new(NoiseControlOutcome.NotSupportedByModel, mode, CurrentMode, $"{m.Name} does not support {NoiseControlManager.Describe(mode)}.");
        var provider = ActiveProvider;
        if (provider is null)
            return new(NoiseControlOutcome.NoControlChannel, mode, CurrentMode, "No mode-switching backend is ready. " + BackendExplanation);

        var request = new ModeSwitchRequest(dev.Address, DeviceName ?? "AirPods", Model, mode, autoEnableOffOption, initiator);
        if (!provider.InterruptsAudio) return await provider.SetModeAsync(request, null, ct).ConfigureAwait(false);
        // one bridge operation at a time: reject instead of touching the UI state of the running one
        if (Bridge.IsBusy || IsSwitching)
            return new(NoiseControlOutcome.NotStarted, mode, CurrentMode, "Another WSL bridge operation is running" +
                (Bridge.Coordinator.Current is { } cur ? $" ({cur.Kind} by {cur.Initiator})." : "."));

        // WSL bridge: Windows loses the adapter for the duration of the command.
        var (result, back) = await WithAdapterHandoffAsync("Switching mode via WSL bridge…",
            p => provider.SetModeAsync(request, p, ct)).ConfigureAwait(false);

        if (result.Outcome is NoiseControlOutcome.Confirmed or NoiseControlOutcome.DeviceReportedDifferentMode && result.Reported is { } rep)
        {
            _bridgeMode = rep; CurrentModeAt = DateTimeOffset.Now; CurrentModeConfirmed = true;
            Raise();
        }
        else if (result.Outcome == NoiseControlOutcome.Sent)
        {
            // last command we delivered; the AirPods do not report it back (see NoiseControlOutcome.Sent).
            // Requested, not `mode`: a newer queued request may have been applied in the same hand-over.
            _bridgeMode = result.Requested; CurrentModeAt = DateTimeOffset.Now; CurrentModeConfirmed = false;
            Raise();
        }
        // no automatic repeat of anything: a missing confirmation is reported as "sent", not retried
        return back ? result : result with
        {
            Message = result.Message + " The AirPods did not reconnect to Windows by themselves — open the case or reconnect them in Bluetooth settings.",
        };
    }

    /// <summary>Exact (1 %) battery over AAP through the WSL bridge: one hand-over, explicitly requested by the user.</summary>
    public async Task<(bool ok, string message)> ReadExactBatteryAsync(string initiator, CancellationToken ct = default)
    {
        if (Device is not { } dev || Link != LinkState.Connected) return (false, "AirPods are not connected to Windows.");
        if (Bridge.Status is { Ready: false } st) return (false, "WSL bridge not ready: " + st.Message);
        if (Bridge.IsBusy || IsSwitching) return (false, "Another WSL bridge operation is running.");
        var ((res, err), back) = await WithAdapterHandoffAsync("Reading exact battery via WSL bridge…",
            p => Bridge.ReadBatteryAsync(dev.Address, initiator, p, ct)).ConfigureAwait(false);
        if (res is { Battery.Count: > 0 } && err is null)
            return (true, $"Exact battery received ({DateTime.Now:HH:mm:ss})." + (back ? "" : " The AirPods have not reconnected to Windows yet."));
        return (false, err ?? (res is null ? "No answer from the WSL bridge." : "The AirPods sent no battery report (" + res.Outcome + ")."));
    }

    /// <summary>
    /// Writes the press-and-hold cycle (and "Allow Off" when Off is in it) in one WSL-bridge hand-over.
    /// Only on an explicit user action (never at start-up, on connect or when a checkbox changes). Does not change the current listening mode.
    /// </summary>
    /// <param name="modes">The exact cycle chosen by the user; null = keep what the AirPods report and add Off, or write nothing.</param>
    public async Task<OffCycleResult> ApplyHoldCycleAsync(CycleModes? modes, string initiator, bool allowInputLoss = false, CancellationToken ct = default)
    {
        if (Device is not { } dev || Link != LinkState.Connected) return OffCycleResult.NotStarted("AirPods are not connected to Windows.");
        if (Bridge.Status is { Ready: false } st) return OffCycleResult.NotStarted("Writing the press-and-hold cycle uses the WSL bridge, which is not ready: " + st.Message);
        if (Bridge.IsBusy || IsSwitching)
            return OffCycleResult.NotStarted("Another WSL bridge operation is running" + (Bridge.Coordinator.Current is { } cur ? $" ({cur.Kind} by {cur.Initiator})." : "."));
        var request = new OffCycleRequest(dev.Address, modes, initiator, allowInputLoss);
        if (OffCyclePlanner.ValidateRequest(request, Model) is { } err) return OffCycleResult.NotStarted(err);
        var (result, back) = await WithAdapterHandoffAsync("Writing the press-and-hold cycle via WSL bridge…",
            p => Bridge.ApplyHoldCycleAsync(request, Model, p, ct)).ConfigureAwait(false);
        _log.Info($"press-and-hold cycle: {result.Status} — {result.Message}");
        return back || !Bridge.LastOperationDisrupted ? result : result with
        {
            Message = result.Message + " The AirPods did not reconnect to Windows by themselves — open the case or reconnect them in Bluetooth settings.",
        };
    }

    /// <summary>
    /// BLE battery: no hand-over. BLE cannot ask the AirPods for a value — this only listens for their next advertisement.
    /// Returns the time of a new one, or null if none arrived within <paramref name="wait"/> (old values stay, marked stale).
    /// </summary>
    public async Task<DateTimeOffset?> WaitForBleBatteryAsync(TimeSpan wait, CancellationToken ct = default)
    {
        if (IsSwitching || Radio is not RadioStatus.On) return null;
        var before = Battery.LastBleAt;
        _ble.Start();
        var end = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < end)
        {
            if (Battery.LastBleAt is { } t && t != before) return t;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Runs an operation that hands the USB Bluetooth adapter to WSL: suppresses the expected "Bluetooth off /
    /// disconnected" churn, then re-acquires the radio, restarts BLE, applies exact battery from the bridge session
    /// and waits up to 20 s for the AirPods to reconnect. Returns the operation result and whether they came back.
    /// </summary>
    public async Task<(T result, bool reconnected)> WithAdapterHandoffAsync<T>(string status, Func<IProgress<string>, Task<T>> operation)
    {
        // only one hand-over UI at a time; a second caller is rejected by the bridge coordinator anyway
        if (Interlocked.CompareExchange(ref _handoffUi, 1, 0) != 0)
            return (await operation(new Progress<string>(_ => { })).ConfigureAwait(false), Link == LinkState.Connected);
        try { return await WithAdapterHandoffCoreAsync(status, operation).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _handoffUi, 0); }
    }

    private int _handoffUi;

    private async Task<(T result, bool reconnected)> WithAdapterHandoffCoreAsync<T>(string status, Func<IProgress<string>, Task<T>> operation)
    {
        IsSwitching = true; SwitchProgress = status; _handoff = true;
        _ble.Stop();
        Raise();
        T result;
        try
        {
            result = await operation(new Progress<string>(s => { SwitchProgress = s; Raise(); })).ConfigureAwait(false);
        }
        finally
        {
            if (Bridge.LastOperationDisrupted)
            {
                SwitchProgress = "Reconnecting AirPods to Windows…"; Raise();
                try { await _devices.ResetAfterAdapterChangeAsync().ConfigureAwait(false); } catch (Exception ex) { _log.Warn("Adapter reset failed", ex); }
            }
            _handoff = false;
            await RefreshAsync().ConfigureAwait(false);
        }
        if (!Bridge.LastOperationDisrupted)
        {
            // stopped before Bluetooth was touched (busy, input devices, adapter unclear): nothing to reconnect
            IsSwitching = false; SwitchProgress = null; Raise();
            return (result, Link == LinkState.Connected);
        }
        if (Bridge.LastResult is { Battery.Count: > 0 } br)
            Battery.OnAapEntries(br.Battery
                .Where(b => b.Component is "left" or "right" or "case")
                .Select(b => new BatteryEntry(b.Component switch { "left" => BatteryComponent.Left, "right" => BatteryComponent.Right, _ => BatteryComponent.Case },
                    b.Level, b.Charging ? BatteryStatus.Charging : BatteryStatus.NotCharging)));
        if (Bridge.LastResult?.CurrentMode is { } cm && Bridge.LastResult.Ok) { _bridgeMode = cm; CurrentModeAt = DateTimeOffset.Now; CurrentModeConfirmed = true; }
        var linkTimer = System.Diagnostics.Stopwatch.StartNew();
        // Link may still say Connected from before the hand-over (live test: kick skipped, 5 s instead of 2 s); the kick
        // itself checks the device's real connection status
        Task<string>? kick = Bridge.Config.ReconnectKick && Device is { } kd ? KickAfterHandoffAsync(kd.Address) : null;
        bool back = await WaitForLinkAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        LastReconnectSeconds = back ? linkTimer.Elapsed.TotalSeconds : null;
        _log.Info($"AirPods {(back ? "back on Windows" : "NOT back on Windows")} {linkTimer.Elapsed.TotalSeconds:0.0}s after the hand-over; " +
                  $"connection kick: {(kick is null ? "off" : kick.IsCompleted ? kick.Result : "still running")}");
        IsSwitching = false; SwitchProgress = null;
        Raise();
        return (result, back);
    }

    /// <summary>Once the radio is back on, asks Windows to connect to the AirPods instead of waiting for them.</summary>
    private async Task<string> KickAfterHandoffAsync(ulong address)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (Radio is not RadioStatus.On && DateTime.UtcNow < until) await Task.Delay(250).ConfigureAwait(false);
        var op = Bridge.LastTrace;
        op?.App("reconnect_kick", "uncached SDP query from Windows");
        var r = await _devices.KickConnectionAsync(address, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        op?.App("reconnect_kick_done", r);
        return r;
    }

    private async Task<bool> WaitForLinkAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (Link == LinkState.Connected) return true;
            await Task.Delay(1000).ConfigureAwait(false);
            // The adapter re-enumerates after the hand-back; pick up the new radio once it exists.
            if (Radio is not RadioStatus.On) await _devices.ResetAfterAdapterChangeAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        return Link == LinkState.Connected;
    }

    public BatteryMonitor Battery { get; }
    public NoiseControlManager NoiseControl { get; }
    public PairedAppleDevice? Device { get; private set; }
    public string? DeviceName { get; private set; }
    public AirPodsModelInfo? Model { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public LinkState Link { get; private set; } = LinkState.Starting;
    public RadioStatus Radio => _devices.Radio;
    public ChannelState Channel { get; private set; } = ChannelState.Unavailable;
    public ControlChannelAvailability? ChannelAvailability { get; private set; }
    public string ChannelMessage { get; private set; } = "";
    public bool? LidOpen { get; private set; }
    public bool LeftInEar { get; private set; }
    public bool RightInEar { get; private set; }
    public string? BleError => _ble.LastError;

    /// <summary>User-selected device (Settings). Null = automatic.</summary>
    public ulong? PreferredAddress { get; set; }

    public IReadOnlyList<PairedAppleDevice> KnownDevices { get; private set; } = [];

    public event Action? StateChanged;
    private void Raise() { if (!_disposed) StateChanged?.Invoke(); }

    public async Task StartAsync()
    {
        await Bridge.RecoverAsync(CancellationToken.None).ConfigureAwait(false); // adapter left in WSL by a crash?
        await _devices.StartAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        _tick.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void OnTick()
    {
        if (_handoff) return;
        Battery.Tick();
        // Picks up a driver installed while the app runs, devices paired elsewhere, etc. (device-tree query only)
        if (Link == LinkState.Connected && Channel is ChannelState.Unavailable) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _handoff) return; // adapter is in WSL: "Bluetooth off"/"disconnected" events are expected
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try { await RefreshCoreAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Error("Refresh failed", ex); }
        finally { _refreshGate.Release(); Raise(); }
    }

    private async Task RefreshCoreAsync()
    {
        switch (_devices.Radio)
        {
            case RadioStatus.NoAdapter:
                Link = LinkState.NoAdapter; _ble.Stop(); await CloseChannelAsync("No Bluetooth adapter."); return;
            case RadioStatus.Off:
                Link = LinkState.BluetoothOff; _ble.Stop(); await CloseChannelAsync("Bluetooth is off."); Battery.MarkAllStale(); return;
        }
        _ble.Start();

        var list = BluetoothDeviceManager.FindPairedAppleAudioDevices();
        KnownDevices = list;
        if (list.Count == 0)
        {
            Device = null; Model = null; DeviceName = null; _filter.ExpectedModelId = null;
            Link = LinkState.NoAirPodsPaired;
            await CloseChannelAsync("No paired AirPods.");
            return;
        }

        PairedAppleDevice chosen = list.FirstOrDefault(d => d.Address == PreferredAddress) ?? list[0];
        string? name; bool connected;
        if (PreferredAddress is null && list.Count > 1)
        {
            // automatic: first connected one wins
            (name, connected) = (null, false);
            foreach (var d in list)
            {
                var r = await _devices.TrackAsync(d.Address).ConfigureAwait(false);
                chosen = d; (name, connected) = r;
                if (r.Connected) break;
            }
        }
        else (name, connected) = await _devices.TrackAsync(chosen.Address).ConfigureAwait(false);

        if (Device?.Address != chosen.Address)
        {
            Battery.Reset(); _filter.Reset(); FirmwareVersion = null;
            await CloseChannelAsync("Device changed.");
        }
        Device = chosen;
        Bridge.CurrentDevice = chosen.Address;
        DeviceName = name ?? chosen.Model.Name;
        if (Model is null || Model.ProductId != chosen.ProductId) Model = chosen.Model;
        NoiseControl.Model = Model;
        _filter.ExpectedModelId = chosen.ProductId;
        Link = connected ? LinkState.Connected : LinkState.Disconnected;

        if (!connected) { await CloseChannelAsync("AirPods are not connected."); return; }
        await EnsureChannelAsync(chosen).ConfigureAwait(false);
    }

    private async Task EnsureChannelAsync(PairedAppleDevice device)
    {
        if (_protocol is { IsOpen: true, HandshakeDone: true }) return;
        if (_retry is not null) return; // a retry is already scheduled


        var probe = AapControlChannel.Probe(device);
        ChannelAvailability = probe.Availability;
        if (probe.Availability != ControlChannelAvailability.Available)
        {
            Channel = ChannelState.Unavailable; ChannelMessage = probe.Explanation;
            return;
        }

        Channel = ChannelState.Connecting; ChannelMessage = "Opening control channel…"; Raise();
        var transport = new DriverAapTransport(probe.InterfacePath!, _log);
        var proto = new AirPodsProtocolService(transport, _log);
        proto.BatteryReceived += Battery.OnAapBattery;
        proto.MetadataReceived += OnMetadata;
        proto.Closed += ex => _ = OnChannelClosedAsync(proto, ex);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await proto.StartAsync(cts.Token).ConfigureAwait(false);
            _protocol = proto;
            NoiseControl.Attach(proto);
            Channel = ChannelState.Ready; ChannelMessage = probe.Explanation;
            _failures = 0;
        }
        catch (Exception ex)
        {
            _log.Warn("Control channel failed to open", ex);
            await proto.DisposeAsync().ConfigureAwait(false);
            Channel = ChannelState.Failed;
            ChannelMessage = "Control channel error: " + ex.Message;
            ScheduleRetry();
        }
    }

    private async Task OnChannelClosedAsync(AirPodsProtocolService proto, Exception? ex)
    {
        if (!ReferenceEquals(proto, _protocol)) return;
        _protocol = null;
        NoiseControl.Attach(null);
        try { await proto.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_disposed) return;
        Channel = ChannelState.Failed;
        ChannelMessage = ex is null ? "Control channel closed." : "Control channel lost: " + ex.Message;
        Raise();
        if (Link == LinkState.Connected) ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        if (_disposed || _retry is not null) return;
        var delay = Backoff[Math.Min(_failures, Backoff.Length - 1)];
        _failures++;
        _log.Info($"Retrying control channel in {delay.TotalSeconds:0}s");
        _retry = new Timer(_ => { _retry?.Dispose(); _retry = null; _ = RefreshAsync(); }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>User-triggered reconnect: skips the backoff.</summary>
    public Task ReconnectNowAsync()
    {
        _retry?.Dispose(); _retry = null; _failures = 0;
        return RefreshAsync();
    }

    private async Task CloseChannelAsync(string reason)
    {
        _retry?.Dispose(); _retry = null;
        var p = _protocol; _protocol = null;
        NoiseControl.Attach(null);
        if (p is not null) { try { await p.DisposeAsync().ConfigureAwait(false); } catch { } }
        Channel = ChannelState.Unavailable;
        ChannelMessage = reason;
    }

    private void OnMetadata(MetadataMessage m)
    {
        FirmwareVersion = m.FirmwareVersion;
        if (AirPodsCatalog.ByModelNumber(m.ModelNumber) is { } refined && refined != Model)
        {
            _log.Info($"Model refined from metadata: {m.ModelNumber} → {refined.Name}");
            Model = refined; NoiseControl.Model = refined;
        }
        Raise();
    }

    private void OnAdvertisement(ProximityPairingMessage msg, short rssi)
    {
        if (!_filter.Accept(msg, rssi)) return;
        LidOpen = msg.BothInCase ? msg.LidOpen : null;
        LeftInEar = msg.LeftInEar; RightInEar = msg.RightInEar;
        Battery.OnBleAdvertisement(msg);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _tick.Dispose();
        _retry?.Dispose();
        _ble.Dispose();
        await CloseChannelAsync("Exiting").ConfigureAwait(false);
        _devices.Dispose();
    }
}
