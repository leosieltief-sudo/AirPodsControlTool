using System.Windows;
using System.Windows.Threading;
using AirPodsControl.App.Controls;
using AirPodsControl.App.Services;
using AirPodsControl.App.ViewModels;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;
using AirPodsControl.Windows.Bridge;
using WinForms = System.Windows.Forms;

namespace AirPodsControl.App;

public partial class App : Application
{
    private const string MutexName = @"Local\AirPodsControl.SingleInstance";
    private const string ShowEventName = @"Local\AirPodsControl.Show";
    private const string ExitEventName = @"Local\AirPodsControl.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent, _exitEvent;
    private FileLog _log = null!;
    private readonly SettingsService _settings = new();
    private AirPodsController _controller = null!;
    private MainViewModel _vm = null!;
    private MainWindow _window = null!;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _miAnc, _miOff, _miTr;
    private HotkeyService? _hotkeys;
    private List<HotkeyRegistration> _hotkeyState = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool first);
        bool exitRequest = e.Args.Contains("--exit");
        if (!first || exitRequest)
        {
            // Another instance is running: ask it to show its panel (or, with --exit, to quit cleanly), then exit.
            try { EventWaitHandle.OpenExisting(exitRequest ? ExitEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowPinned()), null, -1, false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            // never quit with the adapter in WSL: wait for a running hand-over to give it back (at most 90 s)
            for (int i = 0; i < 360 && _controller?.Bridge.IsBusy == true; i++) await Task.Delay(250);
            _log.Info("exit requested by another process (--exit)");
            ExitApp();
        }), null, -1, true);

        _log = new FileLog(SettingsService.LogDir);
        _log.Info($"AirPods Control {typeof(App).Assembly.GetName().Version} starting");
        DispatcherUnhandledException += (_, ex) => { _log.Error("UI exception", ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { _log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        _settings.Load();
        var s = _settings.Current;

        _controller = new AirPodsController(_log) { PreferredAddress = s.PreferredDevice };
        _controller.NoiseControl.AutoEnableOffOption = s.AutoEnableOffOption;
        _controller.BackendPreference = s.ControlBackend;
        // a Bluetooth mouse/keyboard on the adapter blocks the hand-over unless the user explicitly continues
        _controller.Bridge.ConfirmInputLoss = (report, op) => Dispatcher.InvokeAsync(() => ConfirmInputLoss(report, op)).Task;
        _vm = new MainViewModel(_controller, Dispatcher) { AutoEnableOffOption = s.AutoEnableOffOption };
        _vm.ExactBatteryRequested += OnExactBatteryRequested;
        _vm.SetupRequested += () => OpenBridgeSetup(null);
        _vm.ModeResult += OnModeResult;
        _vm.PropertyChanged += (_, _) => UpdateTrayMenu();
        _window = new MainWindow(_vm);
        _window.SettingsRequested += OpenSettings;
        ApplyMotion(s.ReduceMotion);

        CreateTray();
        _hotkeys = new HotkeyService();
        RegisterHotkeys();

        bool background = e.Args.Contains("--background") || s.StartHidden;
        if (!background) _window.ShowPinned();

        await _controller.StartAsync();

        // Diagnostic: --snapshot <file.png> renders the panel to an image after the first data arrived, then exits.
        int si = Array.IndexOf(e.Args, "--snapshot");
        if (si >= 0 && si + 1 < e.Args.Length)
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (!_window.IsVisible) _window.ShowPinned();
            await Task.Delay(TimeSpan.FromSeconds(1));
            SnapshotWriter.Save(_window, e.Args[si + 1]);
            ExitApp();
        }
        int wi = Array.IndexOf(e.Args, "--snapshot-setup");
        if (wi >= 0 && wi + 1 < e.Args.Length)
        {
            var w = new BridgeSetupWindow(new BridgeSetupService(_log, _controller.Bridge), _controller.Device?.Address, _controller.DeviceName ?? "AirPods");
            w.Show();
            await Task.Delay(TimeSpan.FromSeconds(1));
            SnapshotWriter.Save(w, e.Args[wi + 1]);
            w.Close();
            ExitApp();
        }
        // Diagnostic: renders the settings window (nothing is written to the AirPods), then exits.
        int ei = Array.IndexOf(e.Args, "--snapshot-settings");
        if (ei >= 0 && ei + 1 < e.Args.Length)
        {
            var sw = new SettingsWindow(_settings.Current, _controller.KnownDevices, _hotkeyState, "snapshot", _controller);
            sw.Show();
            await Task.Delay(TimeSpan.FromSeconds(1));
            SnapshotWriter.Save(sw, e.Args[ei + 1]);
            sw.Close();
            ExitApp();
        }
    }

    private void ApplyMotion(bool reduce)
    {
        AirPodsControl.App.MainWindow.ReduceMotion = reduce; AirPodsVisual.ReduceMotion = reduce; BatteryCard.ReduceMotion = reduce;
    }

    private void CreateTray()
    {
        var icon = LoadTrayIcon();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_tray is null) return;
                var old = _tray.Icon; _tray.Icon = LoadTrayIcon(); old?.Dispose();
            });
        };

        var menu = new WinForms.ContextMenuStrip();
        _miAnc = new WinForms.ToolStripMenuItem("Noise Cancellation", null, (_, _) => TraySetMode(ListeningMode.NoiseCancellation));
        _miOff = new WinForms.ToolStripMenuItem("Off", null, (_, _) => TraySetMode(ListeningMode.Off));
        _miTr = new WinForms.ToolStripMenuItem("Transparency", null, (_, _) => TraySetMode(ListeningMode.Transparency));
        menu.Items.AddRange([_miAnc, _miOff, _miTr, new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Open main window", null, (_, _) => _window.ShowPinned()),
            new WinForms.ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()),
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitApp())]);
        menu.Opening += (_, _) => UpdateTrayMenu();

        _tray = new WinForms.NotifyIcon { Icon = icon, Text = "AirPods Control", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Dispatcher.BeginInvoke(_window.Toggle); };
        UpdateTrayMenu();
    }

    /// <summary>White AirPods silhouette on a dark taskbar, dark silhouette on a light one (SystemUsesLightTheme).</summary>
    private System.Drawing.Icon LoadTrayIcon()
    {
        bool lightTaskbar = false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { }
        var res = GetResourceStream(new Uri($"pack://application:,,,/Assets/{(lightTaskbar ? "tray-dark" : "tray-light")}.ico"));
        return res is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);
    }

    private void UpdateTrayMenu()
    {
        if (_tray is null || _miAnc is null || _miOff is null || _miTr is null) return;
        bool en = _vm.ControlsEnabled;
        _miAnc.Enabled = _miOff.Enabled = _miTr.Enabled = en;
        _miAnc.Checked = _vm.IsAnc; _miOff.Checked = _vm.IsOff; _miTr.Checked = _vm.IsTransparency;
        var batt = $"L {_vm.Left.Text} · R {_vm.Right.Text} · Case {_vm.Case.Text}";
        var text = $"{_vm.DeviceName}\n{_vm.StatusText}\n{batt}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void TraySetMode(ListeningMode m) => _ = _vm.SetModeAsync(m, "tray");

    /// <summary>Keyboard-accessible Yes/No (default No) in front of everything, also when the app sits in the tray.</summary>
    private static bool AskFrontmost(string text, string title)
    {
        var host = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, ShowActivated = true, Opacity = 0 };
        host.Show();
        host.Activate();
        try { return MessageBox.Show(host, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes; }
        finally { host.Close(); }
    }

    private bool ConfirmInputLoss(AirPodsControl.Core.Bridge.InputGuardReport report, AirPodsControl.Core.Bridge.OperationTrace op)
    {
        _log.Warn($"input guard ({op.Header}): {report.Status} — {report.Explain()}");
        return AskFrontmost(string.Join(Environment.NewLine + Environment.NewLine,
                report.Explain(),
                "The WSL bridge needs this Bluetooth adapter for about 10 seconds. Nothing has been switched off yet.",
                "Continue anyway? (No keeps Bluetooth as it is.)"),
            report.Status == AirPodsControl.Core.Bridge.InputGuardStatus.Unknown ? "Bluetooth input devices: state unknown" : "Bluetooth mouse/keyboard would disconnect");
    }

    private void OnExactBatteryRequested()
    {
        bool go = AskFrontmost(string.Join(Environment.NewLine + Environment.NewLine,
                "Exact battery (1 %) is read through the WSL bridge: Windows hands the Bluetooth adapter to WSL for about 10 seconds.",
                "During that time Windows Bluetooth is off — audio stops and any Bluetooth mouse or keyboard disconnects; the AirPods reconnect afterwards.",
                "'Refresh battery' (BLE) needs no interruption, but it is only ±10 % and depends on the AirPods advertising.",
                "Read exact battery now?"),
            "Exact battery interrupts Bluetooth");
        if (go) _ = _vm.ReadExactBatteryAsync("battery-button");
    }

    private void OnModeResult(NoiseControlResult r)
    {
        if (r.Success || !_settings.Current.ShowNotifications || _window.IsVisible || _tray is null) return;
        _tray.ShowBalloonTip(4000, $"{NoiseControlManager.Describe(r.Requested)} failed", r.Message, WinForms.ToolTipIcon.Warning);
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        _hotkeyState = new();
        var s = _settings.Current;
        if (!s.HotkeysEnabled) return;
        void Reg(string name, string gesture, ListeningMode mode)
        {
            var r = _hotkeys.Register(name, gesture, () => Dispatcher.BeginInvoke(() => TrySetFromHotkey(mode)));
            _hotkeyState.Add(r);
            if (!r.Success) _log.Warn($"Hotkey {name} ({gesture}) not registered: {r.Error}");
        }
        Reg("Noise Cancellation", s.HotkeyNoiseCancellation, ListeningMode.NoiseCancellation);
        Reg("Off", s.HotkeyOff, ListeningMode.Off);
        Reg("Transparency", s.HotkeyTransparency, ListeningMode.Transparency);
    }

    private void TrySetFromHotkey(ListeningMode mode)
    {
        // during a running switch: merged into it (latest wins) instead of being dropped
        if (_vm.IsSwitching) { _ = _vm.SetModeAsync(mode, "hotkey"); return; }
        if (!_vm.ControlsEnabled)
        {
            if (_settings.Current.ShowNotifications && _tray is not null)
                _tray.ShowBalloonTip(4000, "AirPods Control", _vm.ChannelText, WinForms.ToolTipIcon.Info);
            return;
        }
        _ = _vm.SetModeAsync(mode, "hotkey");
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings.Current, _controller.KnownDevices, _hotkeyState,
            "Now: " + (_controller.ActiveProvider?.DisplayName ?? "none ready") + " — " + _controller.BackendExplanation,
            _controller, PersistDesiredHoldCycle);
        dlg.BridgeSetupRequested += () => OpenBridgeSetup(dlg);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try { _settings.Save(dlg.Result); }
        catch (Exception ex) { _log.Error("Saving settings failed", ex); MessageBox.Show("Could not save settings: " + ex.Message, "AirPods Control"); }
        var s = _settings.Current;
        ApplyMotion(s.ReduceMotion);
        _controller.NoiseControl.AutoEnableOffOption = s.AutoEnableOffOption;
        _controller.BackendPreference = s.ControlBackend;
        _vm.AutoEnableOffOption = s.AutoEnableOffOption;
        _ = _controller.RefreshAsync();
        if (_controller.PreferredAddress != s.PreferredDevice)
        {
            _controller.PreferredAddress = s.PreferredDevice;
            _ = _controller.RefreshAsync();
        }
        RegisterHotkeys();
        var failed = _hotkeyState.Where(h => !h.Success && h.Gesture.Length > 0).ToList();
        if (failed.Count > 0)
            MessageBox.Show(string.Join("\n", failed.Select(f => $"{f.Name}: {f.Gesture} — {f.Error}")), "Some hotkeys could not be registered",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>"Apply to AirPods" stores the desired press-and-hold set at once (even if the settings dialog is then cancelled).</summary>
    private void PersistDesiredHoldCycle(CycleModes modes)
    {
        try { _settings.Current.HoldCycleModes = (byte)modes; _settings.Save(_settings.Current); }
        catch (Exception ex) { _log.Warn("Could not save the desired press-and-hold set", ex); }
    }

    private void OpenBridgeSetup(Window? owner)
    {
        var svc = new BridgeSetupService(_log, _controller.Bridge)
        {
            // adapter tests go through the controller so the expected Bluetooth drop does not confuse the UI
            AdapterOperation = async op => (await _controller.WithAdapterHandoffAsync("Testing WSL bridge…", op)).result,
        };
        var w = new BridgeSetupWindow(svc, _controller.Device?.Address, _controller.DeviceName ?? "AirPods");
        if (owner is not null) w.Owner = owner;
        w.ShowDialog();
        _controller.Bridge.ReloadConfig();
        _ = _controller.RefreshAsync();
    }

    private async void ExitApp()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _hotkeys?.Dispose();
        try { await _controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _log.Info("Exit");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
