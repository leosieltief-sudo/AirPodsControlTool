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
        await _devices.StartAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        _tick.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void OnTick()
    {
        Battery.Tick();
        // Picks up a driver installed while the app runs, devices paired elsewhere, etc. (device-tree query only)
        if (Link == LinkState.Connected && Channel is ChannelState.Unavailable) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
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
                Link = LinkState.BluetoothOff; _ble.Stop(); await CloseChannelAsync("Bluetooth is off."); Battery.Reset(); return;
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

    public Task<NoiseControlResult> SetModeAsync(ListeningMode mode, CancellationToken ct = default) => NoiseControl.SetModeAsync(mode, ct);

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
