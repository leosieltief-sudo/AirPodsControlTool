using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;

namespace AirPodsControl.App.ViewModels;

public enum StatusKind { Neutral, Good, Warn, Bad }

public sealed class BatteryItemViewModel(string label) : ObservableObject
{
    private double _fraction; private string _text = "—"; private bool _charging, _low, _critical, _known, _stale; private string _source = "";
    public string Label { get; } = label;
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsCharging { get => _charging; set => Set(ref _charging, value); }
    public bool IsLow { get => _low; set => Set(ref _low, value); }
    public bool IsCritical { get => _critical; set => Set(ref _critical, value); }
    public bool IsKnown { get => _known; set => Set(ref _known, value); }
    /// <summary>Last value received, but not fresh: shown dimmed with its time (never as 0, never as current).</summary>
    public bool IsStale { get => _stale; set => Set(ref _stale, value); }
    public string SourceHint { get => _source; set => Set(ref _source, value); }

    public void Update(ComponentBattery b)
    {
        IsKnown = b.Level is not null;
        IsStale = b.IsKnown && b.Stale;
        Fraction = (b.Level ?? 0) / 100.0;
        Text = b.Level is { } l ? $"{l}%" : "—";
        IsCharging = b.IsKnown && b.Charging && !b.Stale;
        IsCritical = b.Level is <= 10;
        IsLow = b.Level is > 10 and <= 20;
        var src = b.Source switch
        {
            BatterySource.Aap => "exact (AAP)",
            BatterySource.Ble => "±10 % (BLE)",
            _ => "not available",
        };
        SourceHint = b.UpdatedAt is { } t
            ? $"{src} · received {t:HH:mm:ss}" + (b.Stale ? " · no fresh data since then" : "")
            : src;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AirPodsController _c;
    private readonly Dispatcher _ui;
    private int _refreshQueued;
    private CancellationTokenSource? _errorCts;

    public MainViewModel(AirPodsController controller, Dispatcher ui)
    {
        _c = controller;
        _ui = ui;
        _c.StateChanged += QueueRefresh;
        SetModeCommand = new RelayCommand(p => { if (p is ListeningMode m) _ = SetModeAsync(m, "panel"); else if (p is string s && Enum.TryParse<ListeningMode>(s, out var mm)) _ = SetModeAsync(mm, "panel"); },
                                          _ => ControlsEnabled && PendingMode is null);
        ReconnectCommand = new RelayCommand(_ => _ = _c.ReconnectNowAsync());
        RefreshBatteryCommand = new RelayCommand(_ => _ = RefreshBatteryAsync(), _ => !_batteryBusy && !IsSwitching && IsConnected);
        ExactBatteryCommand = new RelayCommand(_ => ExactBatteryRequested?.Invoke(), _ => !_batteryBusy && !IsSwitching && IsConnected && ExactBatteryAvailable);
        SetupCommand = new RelayCommand(_ => SetupRequested?.Invoke(), _ => !IsSwitching);
        Refresh();
    }

    public BatteryItemViewModel Left { get; } = new("Left");
    public BatteryItemViewModel Right { get; } = new("Right");
    public BatteryItemViewModel Case { get; } = new("Case");

    public ICommand SetModeCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand RefreshBatteryCommand { get; }
    public ICommand ExactBatteryCommand { get; }
    public event Action? SetupRequested;
    /// <summary>The user asked for exact battery; the view confirms the Bluetooth interruption first.</summary>
    public event Action? ExactBatteryRequested;
    private bool _batteryBusy, _exactAvailable;
    private string _batteryStatus = "";
    /// <summary>Freshness of the battery values / result of the last refresh.</summary>
    public string BatteryStatusText { get => _batteryStatus; private set => Set(ref _batteryStatus, value); }
    /// <summary>Exact (AAP) battery is possible: the WSL bridge is ready (it interrupts Bluetooth).</summary>
    public bool ExactBatteryAvailable { get => _exactAvailable; private set => Set(ref _exactAvailable, value); }
    /// <summary>Mirrors the "Allow 'Off' automatically" setting.</summary>
    public bool AutoEnableOffOption { get; set; } = true;

    private string _deviceName = "AirPods Control", _modelName = "", _status = "Starting…", _channelText = "", _error = "", _modeCaption = "";
    private bool _connected, _controls, _showAdaptive, _channelProblem, _hasDevice, _offNeedsOption, _switching, _bridgeOffer;
    private string _backendName = "";
    private StatusKind _statusKind;
    private ListeningMode? _active, _pending;
    private ModelShape _shape = ModelShape.Pro;
    private Support _ncSupport = Support.No;

    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public bool IsConnected { get => _connected; private set => Set(ref _connected, value); }
    public bool HasDevice { get => _hasDevice; private set => Set(ref _hasDevice, value); }
    public ModelShape Shape { get => _shape; private set => Set(ref _shape, value); }
    public bool ControlsEnabled { get => _controls; private set => Set(ref _controls, value); }
    public bool ShowAdaptive { get => _showAdaptive; private set => Set(ref _showAdaptive, value); }
    public string ChannelText { get => _channelText; private set => Set(ref _channelText, value); }
    public bool ChannelProblem { get => _channelProblem; private set => Set(ref _channelProblem, value); }
    public string ErrorText { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string ModeCaption { get => _modeCaption; private set => Set(ref _modeCaption, value); }
    public bool OffNeedsOption { get => _offNeedsOption; private set => Set(ref _offNeedsOption, value); }
    public Support NoiseControlSupport { get => _ncSupport; private set => Set(ref _ncSupport, value); }
    /// <summary>A WSL bridge hand-over is running (Windows Bluetooth audio is paused).</summary>
    public bool IsSwitching { get => _switching; private set { if (Set(ref _switching, value)) CommandManager.InvalidateRequerySuggested(); } }
    /// <summary>Show the "Set up driverless mode" action.</summary>
    public bool OfferBridgeSetup { get => _bridgeOffer; private set => Set(ref _bridgeOffer, value); }
    public string BackendName { get => _backendName; private set => Set(ref _backendName, value); }

    /// <summary>Mode reported by the AirPods, or (WSL bridge) the last mode delivered without echo — the caption says which.</summary>
    public ListeningMode? ActiveMode
    {
        get => _active;
        private set { if (Set(ref _active, value)) { OnPropertyChanged(nameof(IsAnc)); OnPropertyChanged(nameof(IsOff)); OnPropertyChanged(nameof(IsTransparency)); OnPropertyChanged(nameof(IsAdaptive)); } }
    }
    public ListeningMode? PendingMode
    {
        get => _pending;
        private set { if (Set(ref _pending, value)) { OnPropertyChanged(nameof(PendingAnc)); OnPropertyChanged(nameof(PendingOff)); OnPropertyChanged(nameof(PendingTransparency)); OnPropertyChanged(nameof(PendingAdaptive)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsAnc => _active == ListeningMode.NoiseCancellation;
    public bool IsOff => _active == ListeningMode.Off;
    public bool IsTransparency => _active == ListeningMode.Transparency;
    public bool IsAdaptive => _active == ListeningMode.Adaptive;
    public bool PendingAnc => _pending == ListeningMode.NoiseCancellation;
    public bool PendingOff => _pending == ListeningMode.Off;
    public bool PendingTransparency => _pending == ListeningMode.Transparency;
    public bool PendingAdaptive => _pending == ListeningMode.Adaptive;

    /// <summary>Raised after a user-initiated mode change finishes (for tray notifications).</summary>
    public event Action<NoiseControlResult>? ModeResult;

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = 0; Refresh(); });
    }

    private void Refresh()
    {
        var model = _c.Model;
        HasDevice = _c.Device is not null;
        DeviceName = _c.DeviceName ?? "AirPods Control";
        ModelName = model is null ? "" : model.Name + (_c.FirmwareVersion is { } fw ? $" · fw {fw}" : "");
        Shape = model?.Shape ?? ModelShape.Pro;
        NoiseControlSupport = model?.NoiseControl ?? Support.No;
        ShowAdaptive = model is not null && model.Adaptive != Support.No;
        OffNeedsOption = model?.OffMode == OffModeRequirement.RequiresAllowOffOption;

        (StatusText, StatusKind) = _c.Link switch
        {
            LinkState.Starting => ("Starting…", StatusKind.Neutral),
            LinkState.NoAdapter => ("No Bluetooth adapter", StatusKind.Bad),
            LinkState.BluetoothOff => ("Bluetooth off", StatusKind.Bad),
            LinkState.NoAirPodsPaired => ("No AirPods paired", StatusKind.Warn),
            LinkState.Disconnected => ("Disconnected", StatusKind.Warn),
            LinkState.Connected => ("Connected", StatusKind.Good),
            _ => ("", StatusKind.Neutral),
        };
        IsConnected = _c.Link == LinkState.Connected;
        IsSwitching = _c.IsSwitching;
        if (IsSwitching) (StatusText, StatusKind) = ("Switching…", StatusKind.Warn);

        var snap = _c.Battery.Current;
        Left.Update(snap.Left); Right.Update(snap.Right); Case.Update(snap.Case);
        ExactBatteryAvailable = _c.Bridge.Status.Ready && _c.Channel != ChannelState.Ready;
        if (!_batteryBusy && _batteryStatusUntil < DateTime.UtcNow) BatteryStatusText = DescribeFreshness(snap);

        var provider = _c.ActiveProvider;
        BackendName = provider?.DisplayName ?? "";
        ControlsEnabled = (IsConnected || IsSwitching) && provider is not null && model is { HasNoiseControl: true } && !IsSwitching;
        ActiveMode = _c.CurrentMode;

        if (model is { NoiseControl: Support.No })
        {
            ChannelText = $"{model.Name} has no noise control.";
            ChannelProblem = false;
        }
        else
        {
            ChannelText = IsSwitching ? (_c.SwitchProgress ?? "Switching mode via WSL bridge…") + "  Bluetooth audio is paused for a few seconds."
            : _c.Link switch
            {
                LinkState.Connected => provider switch
                {
                    { InterruptsAudio: false } => "Control channel ready (MagicAAP, L2CAP 0x1001)",
                    { InterruptsAudio: true } => "Driverless mode: WSL2 bridge ready. Switching pauses Bluetooth audio for ~10–20 s.",
                    null when _c.Channel == ChannelState.Connecting => "Opening control channel…",
                    _ => "Mode switching is not set up. " + _c.BackendExplanation,
                },
                LinkState.Disconnected => "Connect your AirPods to control noise modes.",
                LinkState.NoAirPodsPaired => "Pair AirPods in Windows Settings → Bluetooth & devices.",
                LinkState.BluetoothOff => "Turn Bluetooth on to use AirPods Control.",
                _ => _c.ChannelMessage,
            };
            ChannelProblem = _c.Link == LinkState.Connected && provider is null && !IsSwitching;
        }

        OfferBridgeSetup = ChannelProblem && model is not { NoiseControl: Support.No };
        ModeCaption = ActiveMode is { } m
            ? NoiseControlManager.Describe(m) + (_c.CurrentModeIsLive || _c.CurrentModeAt is null ? ""
                : _c.CurrentModeConfirmed ? $" · confirmed {_c.CurrentModeAt:HH:mm}" : $" · sent {_c.CurrentModeAt:HH:mm} (not confirmed by AirPods)")
            : ControlsEnabled ? "Mode not reported yet" : "Mode unavailable";
    }

    public async Task<NoiseControlResult?> SetModeAsync(ListeningMode mode, string initiator = "panel")
    {
        if (PendingMode is not null || IsSwitching || _c.IsSwitching)
        {
            // a switch is running: merge into it while the adapter is still in WSL (latest wins, repeats ignored);
            // never start a second hand-over and never keep the request for later
            switch (_c.TryMergeMode(mode, AutoEnableOffOption, initiator))
            {
                case MergeResult.Merged: PendingMode = mode; break;
                case MergeResult.DuplicateIgnored: break;
                default: ShowError("A Bluetooth hand-over is finishing — choose the mode again in a few seconds."); break;
            }
            return null;
        }
        PendingMode = mode;
        ErrorText = "";
        try
        {
            var r = await _c.SetModeAsync(mode, AutoEnableOffOption, default, initiator);
            if (!r.Success) ShowError(r.Message);
            ModeResult?.Invoke(r);
            return r;
        }
        finally
        {
            PendingMode = null;
            Refresh();
        }
    }

    private DateTime _batteryStatusUntil;

    private void ShowBatteryStatus(string text)
    {
        BatteryStatusText = text;
        _batteryStatusUntil = DateTime.UtcNow + TimeSpan.FromSeconds(12);
    }

    private static string DescribeFreshness(BatterySnapshot s)
    {
        var known = new[] { s.Left, s.Right, s.Case }.Where(c => c.IsKnown).ToList();
        if (known.Count == 0) return "No battery data received yet.";
        var newest = known.Max(c => c.UpdatedAt);
        var src = known.Any(c => c.Source == BatterySource.Aap && !c.Stale) ? "exact (AAP)" : known.Any(c => !c.Stale) ? "BLE" : "last values";
        return known.All(c => c.Stale)
            ? $"No fresh battery data — showing values from {newest:HH:mm}."
            : $"Battery: {src}, updated {newest:HH:mm:ss}.";
    }

    /// <summary>BLE only: waits for the AirPods' next advertisement. Does not interrupt Bluetooth.</summary>
    public async Task RefreshBatteryAsync()
    {
        if (_batteryBusy) return;
        _batteryBusy = true; CommandManager.InvalidateRequerySuggested();
        BatteryStatusText = "Waiting for a BLE advertisement from the AirPods…";
        try
        {
            var at = await _c.WaitForBleBatteryAsync(TimeSpan.FromSeconds(8));
            ShowBatteryStatus(at is { } t
                ? $"New BLE battery data at {t:HH:mm:ss} (±10 %)."
                : "No new BLE advertisement in 8 s — the values shown are from " +
                  (_c.Battery.LastBleAt is { } last ? $"{last:HH:mm:ss}" : "earlier sources") + ". BLE cannot request a fresh value.");
        }
        finally { _batteryBusy = false; CommandManager.InvalidateRequerySuggested(); Refresh(); }
    }

    /// <summary>Exact (1 %) battery through the WSL bridge — call only after the user confirmed the interruption.</summary>
    public async Task ReadExactBatteryAsync(string initiator = "battery-button")
    {
        if (_batteryBusy) return;
        _batteryBusy = true; CommandManager.InvalidateRequerySuggested();
        BatteryStatusText = "Reading exact battery (Bluetooth is paused)…";
        try
        {
            var (ok, msg) = await _c.ReadExactBatteryAsync(initiator);
            ShowBatteryStatus(msg);
            if (!ok) ShowError(msg);
        }
        finally { _batteryBusy = false; CommandManager.InvalidateRequerySuggested(); Refresh(); }
    }

    private async void ShowError(string message)
    {
        _errorCts?.Cancel();
        var cts = _errorCts = new CancellationTokenSource();
        ErrorText = message;
        try { await Task.Delay(TimeSpan.FromSeconds(8), cts.Token); ErrorText = ""; } catch (TaskCanceledException) { }
    }
}
