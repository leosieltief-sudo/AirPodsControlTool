using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;

namespace AirPodsControl.App.ViewModels;

public enum StatusKind { Neutral, Good, Warn, Bad }

public sealed class BatteryItemViewModel(string label) : ObservableObject
{
    private double _fraction; private string _text = "—"; private bool _charging, _low, _critical, _known; private string _source = "";
    public string Label { get; } = label;
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsCharging { get => _charging; set => Set(ref _charging, value); }
    public bool IsLow { get => _low; set => Set(ref _low, value); }
    public bool IsCritical { get => _critical; set => Set(ref _critical, value); }
    public bool IsKnown { get => _known; set => Set(ref _known, value); }
    public string SourceHint { get => _source; set => Set(ref _source, value); }

    public void Update(ComponentBattery b)
    {
        IsKnown = b.Level is not null;
        Fraction = (b.Level ?? 0) / 100.0;
        Text = b.Level is { } l ? $"{l}%" : "—";
        IsCharging = b.IsKnown && b.Charging;
        IsCritical = b.Level is <= 10;
        IsLow = b.Level is > 10 and <= 20;
        SourceHint = b.Source switch
        {
            BatterySource.Aap => "exact (AAP)",
            BatterySource.Ble => "±10 % (BLE)",
            _ => "not available",
        };
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
        SetModeCommand = new RelayCommand(p => { if (p is ListeningMode m) _ = SetModeAsync(m); else if (p is string s && Enum.TryParse<ListeningMode>(s, out var mm)) _ = SetModeAsync(mm); },
                                          _ => ControlsEnabled && PendingMode is null);
        ReconnectCommand = new RelayCommand(_ => _ = _c.ReconnectNowAsync());
        Refresh();
    }

    public BatteryItemViewModel Left { get; } = new("Left");
    public BatteryItemViewModel Right { get; } = new("Right");
    public BatteryItemViewModel Case { get; } = new("Case");

    public ICommand SetModeCommand { get; }
    public ICommand ReconnectCommand { get; }

    private string _deviceName = "AirPods Control", _modelName = "", _status = "Starting…", _channelText = "", _error = "", _modeCaption = "";
    private bool _connected, _controls, _showAdaptive, _channelProblem, _hasDevice, _offNeedsOption;
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

    /// <summary>Mode confirmed by the AirPods. Never set optimistically.</summary>
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

        var snap = _c.Battery.Current;
        Left.Update(snap.Left); Right.Update(snap.Right); Case.Update(snap.Case);

        bool channelReady = _c.Channel == ChannelState.Ready;
        ControlsEnabled = IsConnected && channelReady && model is { HasNoiseControl: true };
        ActiveMode = channelReady ? _c.NoiseControl.CurrentMode : null;

        if (model is { NoiseControl: Support.No })
        {
            ChannelText = $"{model.Name} has no noise control.";
            ChannelProblem = false;
        }
        else
        {
            ChannelText = _c.Link switch
            {
                LinkState.Connected => _c.Channel switch
                {
                    ChannelState.Ready => "Control channel ready (L2CAP 0x1001)",
                    ChannelState.Connecting => "Opening control channel…",
                    _ => _c.ChannelMessage,
                },
                LinkState.Disconnected => "Connect your AirPods to control noise modes.",
                LinkState.NoAirPodsPaired => "Pair AirPods in Windows Settings → Bluetooth & devices.",
                LinkState.BluetoothOff => "Turn Bluetooth on to use AirPods Control.",
                _ => _c.ChannelMessage,
            };
            ChannelProblem = _c.Link == LinkState.Connected && !channelReady;
        }

        ModeCaption = ActiveMode is { } m ? NoiseControlManager.Describe(m)
            : ControlsEnabled ? "Mode not reported yet" : "Mode unavailable";
    }

    public async Task<NoiseControlResult?> SetModeAsync(ListeningMode mode)
    {
        if (PendingMode is not null) return null;
        PendingMode = mode;
        ErrorText = "";
        try
        {
            var r = await _c.SetModeAsync(mode);
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

    private async void ShowError(string message)
    {
        _errorCts?.Cancel();
        var cts = _errorCts = new CancellationTokenSource();
        ErrorText = message;
        try { await Task.Delay(TimeSpan.FromSeconds(8), cts.Token); ErrorText = ""; } catch (TaskCanceledException) { }
    }
}
