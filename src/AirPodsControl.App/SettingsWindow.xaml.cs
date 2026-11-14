using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AirPodsControl.App.Services;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;

namespace AirPodsControl.App;

public partial class SettingsWindow : Window
{
    private sealed record DeviceOption(ulong? Address, string Label);

    private readonly AppSettings _original;
    private readonly AirPodsController? _controller;
    private readonly Action<CycleModes>? _persistDesired;
    private CycleModes? _savedDesired;
    private bool _applying;
    public AppSettings? Result { get; private set; }

    /// <summary>Raised when the user wants the driverless setup wizard.</summary>
    public event Action? BridgeSetupRequested;

    /// <param name="controller">Needed for "Apply to AirPods"; null (snapshots) disables it.</param>
    /// <param name="persistDesired">Stores the desired press-and-hold set right away when the user applies it.</param>
    public SettingsWindow(AppSettings current, IReadOnlyList<PairedAppleDevice> devices, IEnumerable<HotkeyRegistration> hotkeyState,
        string backendStatusText = "", AirPodsController? controller = null, Action<CycleModes>? persistDesired = null)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 16;
        _original = current;
        _controller = controller;
        _persistDesired = persistDesired;
        autostart.IsChecked = current.StartWithWindows;
        startHidden.IsChecked = current.StartHidden;
        notifications.IsChecked = current.ShowNotifications;
        reduceMotion.IsChecked = current.ReduceMotion;
        autoOff.IsChecked = current.AutoEnableOffOption;
        hotkeysOn.IsChecked = current.HotkeysEnabled;
        hkAnc.Text = current.HotkeyNoiseCancellation;
        hkOff.Text = current.HotkeyOff;
        hkTr.Text = current.HotkeyTransparency;
        foreach (ComboBoxItem item in backendBox.Items)
            if ((string)item.Tag == current.ControlBackend.ToString()) backendBox.SelectedItem = item;
        backendStatus.Text = backendStatusText;

        var options = new List<DeviceOption> { new(null, "Automatic (first connected AirPods)") };
        options.AddRange(devices.Select(d => new DeviceOption(d.Address, $"{d.Model.Name} — {d.Mac}")));
        deviceBox.ItemsSource = options;
        deviceBox.SelectedItem = options.FirstOrDefault(o => o.Address == current.PreferredDevice) ?? options[0];

        var lines = hotkeyState.Select(h => h.Success ? $"✓ {h.Name}: {h.Gesture}" : $"✗ {h.Name}: {h.Gesture} — {h.Error}").ToList();
        hotkeyStatus.Text = lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);

        // ---- press-and-hold cycle: checkboxes are the user's desired set (saved in the app), never the AirPods' state
        var model = controller?.Model;
        if (model?.Adaptive == Support.No) { cyAd.IsEnabled = false; cyAd.Content = "Adaptive (not on this model)"; }
        if (model?.OffMode == OffModeRequirement.NotApplicable) { cyOff.IsEnabled = false; cyOff.Content = "Off (not on this model)"; }
        _savedDesired = current.HoldCycleModes is { } hm ? (CycleModes)hm : null;
        if (_savedDesired is { } d0)
        {
            cyOff.IsChecked = d0.HasFlag(CycleModes.Off) && cyOff.IsEnabled;
            cyAnc.IsChecked = d0.HasFlag(CycleModes.NoiseCancellation);
            cyTr.IsChecked = d0.HasFlag(CycleModes.Transparency);
            cyAd.IsChecked = d0.HasFlag(CycleModes.Adaptive) && cyAd.IsEnabled;
        }
        foreach (var cb in new[] { cyOff, cyAnc, cyTr, cyAd }) { cb.Checked += (_, _) => UpdateCycleUi(); cb.Unchecked += (_, _) => UpdateCycleUi(); }
        if (controller is not null)
        {
            controller.StateChanged += OnControllerState;
            Closed += (_, _) => controller.StateChanged -= OnControllerState;
        }
        Closing += (_, e) => { if (_applying) e.Cancel = true; };   // the adapter is in WSL: wait until it is back
        UpdateCycleUi();
    }

    private CycleModes SelectedCycle =>
        (cyOff.IsChecked == true ? CycleModes.Off : 0) | (cyAnc.IsChecked == true ? CycleModes.NoiseCancellation : 0) |
        (cyTr.IsChecked == true ? CycleModes.Transparency : 0) | (cyAd.IsChecked == true ? CycleModes.Adaptive : 0);

    private void UpdateCycleUi()
    {
        var sel = SelectedCycle;
        var err = CycleModeSet.Validate(sel, _controller?.Model);
        cycleDesired.Text = err is not null
            ? "Desired set: " + err
            : $"Desired set: {CycleModeSet.Describe(sel)} — " + (_savedDesired == sel ? "saved in this app." : "not saved yet (Apply or Save stores it).");
        cycleDesired.Foreground = (Brush)FindResource(err is null ? "SubtleTextBrush" : "WarnBrush");
        cycleAirPods.Text = AirPodsStateText();
        foreach (var cb in new[] { cyOff, cyAnc, cyTr, cyAd }) cb.IsHitTestVisible = cb.Focusable = !_applying;
        applyCycle.IsEnabled = !_applying && err is null && _controller is not null;
        saveBtn.IsEnabled = cancelBtn.IsEnabled = closeX.IsEnabled = !_applying;
    }

    /// <summary>What the AirPods themselves reported or confirmed (bridge records for this device) — kept apart from the checkboxes.</summary>
    private string AirPodsStateText()
    {
        var cfg = _controller?.Bridge.Config;
        var dev = _controller?.Device?.Address.ToString("X12");
        if (cfg is null || dev is null) return "Current AirPods set: unknown.";
        (CycleModes, DateTimeOffset)? reported = cfg.HoldCycleReported is { } r && r.Device == dev ? ((CycleModes)r.Modes, r.At) : null;
        (CycleModes, DateTimeOffset, OffCycleStatus)? applied = cfg.LastOffCycle is { } l && l.Device == dev && Enum.TryParse<OffCycleStatus>(l.Status, out var st)
            ? ((CycleModes)l.Modes, l.At, st) : null;
        return HoldCycleStatus.Describe(reported, applied);
    }

    private void OnControllerState() => Dispatcher.BeginInvoke(() =>
    {
        if (_applying && _controller?.SwitchProgress is { } p) cycleResult.Text = p;
    });

    private async void ApplyCycle_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || _controller is null) return;
        var modes = SelectedCycle;
        if (CycleModeSet.Validate(modes, _controller.Model) is { } err) { cycleDesired.Text = "Desired set: " + err; return; }
        // explicit, keyboard-accessible warning before anything is switched off (default: No)
        var go = MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine,
                $"Write this press-and-hold cycle to the AirPods: {CycleModeSet.Describe(modes)}?" +
                    (modes.HasFlag(CycleModes.Off) ? " 'Allow Off' is sent as well." : ""),
                "Windows hands the Bluetooth adapter to WSL for about 15 seconds: audio stops and the AirPods reconnect afterwards. " +
                    "A connected Bluetooth mouse or keyboard on this adapter stops the operation unless you confirm that separately.",
                "The current listening mode and the stems' Siri / noise-control assignment are not changed. " +
                    "Whether the AirPods keep the setting after reconnecting has not been verified yet."),
            "Apply to AirPods — Bluetooth pauses", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!go) return;

        _savedDesired = modes;
        _persistDesired?.Invoke(modes);
        _applying = true;
        cycleResult.Foreground = (Brush)FindResource("TextBrush");
        cycleResult.Text = "Starting…";
        UpdateCycleUi();
        OffCycleResult r;
        try { r = await _controller.ApplyHoldCycleAsync(modes, "settings"); }
        catch (Exception ex) { r = OffCycleResult.Failed(OffCycleState.Unknown, modes, "Error: " + ex.Message); }
        finally { _applying = false; }
        cycleResult.Text = r.Message + Environment.NewLine + string.Join(Environment.NewLine, r.StatusLines().Select(l => "· " + l));
        cycleResult.Foreground = (Brush)FindResource(r.Status switch
        {
            OffCycleStatus.Confirmed or OffCycleStatus.AlreadySet => "AccentBrush",
            OffCycleStatus.Sent or OffCycleStatus.NothingSent or OffCycleStatus.NotStarted => "WarnBrush",
            _ => "BadBrush",
        });
        UpdateCycleUi();
        applyCycle.Focus();
    }

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { ((TextBox)sender).Text = ""; return; }
        var g = HotkeyService.Format(Keyboard.Modifiers, key);
        if (g is not null && Keyboard.Modifiers != ModifierKeys.None) ((TextBox)sender).Text = g;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        foreach (var tb in new[] { hkAnc, hkOff, hkTr })
            if (tb.Text.Length > 0 && !HotkeyService.TryParse(tb.Text, out _, out _, out var err))
            { hotkeyStatus.Text = $"Invalid hotkey '{tb.Text}': {err}"; return; }
        var dups = new[] { hkAnc.Text, hkOff.Text, hkTr.Text }.Where(t => t.Length > 0).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dups.Count > 0) { hotkeyStatus.Text = $"'{dups[0].Key}' is assigned twice."; return; }

        var cycle = SelectedCycle;
        Result = new AppSettings
        {
            StartWithWindows = autostart.IsChecked == true,
            StartHidden = startHidden.IsChecked == true,
            ShowNotifications = notifications.IsChecked == true,
            ReduceMotion = reduceMotion.IsChecked == true,
            AutoEnableOffOption = autoOff.IsChecked == true,
            HotkeysEnabled = hotkeysOn.IsChecked == true,
            HotkeyNoiseCancellation = hkAnc.Text,
            HotkeyOff = hkOff.Text,
            HotkeyTransparency = hkTr.Text,
            PreferredDevice = (deviceBox.SelectedItem as DeviceOption)?.Address,
            ControlBackend = Enum.TryParse<ControlBackend>((backendBox.SelectedItem as ComboBoxItem)?.Tag as string, out var cb) ? cb : ControlBackend.Auto,
            // desired set only (Save never writes to the AirPods); an empty selection keeps the previous choice
            HoldCycleModes = cycle != CycleModes.None ? (byte)cycle : (_savedDesired is { } sd ? (byte)sd : _original.HoldCycleModes),
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_applying) DialogResult = false; }

    private void Bridge_Click(object sender, RoutedEventArgs e) => BridgeSetupRequested?.Invoke();

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsService.LogDir);
        try { Process.Start(new ProcessStartInfo(SettingsService.LogDir) { UseShellExecute = true }); } catch { }
    }

    private void Drag(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch (InvalidOperationException) { } }
}
