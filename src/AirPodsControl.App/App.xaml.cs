using System.Windows;
using System.Windows.Threading;
using AirPodsControl.App.Controls;
using AirPodsControl.App.Services;
using AirPodsControl.App.ViewModels;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows;
using WinForms = System.Windows.Forms;

namespace AirPodsControl.App;

public partial class App : Application
{
    private const string MutexName = @"Local\AirPodsControl.SingleInstance";
    private const string ShowEventName = @"Local\AirPodsControl.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
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
        if (!first)
        {
            // Another instance is running: ask it to show its panel, then exit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowPinned()), null, -1, false);

        _log = new FileLog(SettingsService.LogDir);
        _log.Info($"AirPods Control {typeof(App).Assembly.GetName().Version} starting");
        DispatcherUnhandledException += (_, ex) => { _log.Error("UI exception", ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { _log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        _settings.Load();
        var s = _settings.Current;

        _controller = new AirPodsController(_log) { PreferredAddress = s.PreferredDevice };
        _controller.NoiseControl.AutoEnableOffOption = s.AutoEnableOffOption;
        _vm = new MainViewModel(_controller, Dispatcher);
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
    }

    private void ApplyMotion(bool reduce)
    {
        AirPodsControl.App.MainWindow.ReduceMotion = reduce; AirPodsVisual.ReduceMotion = reduce; BatteryCard.ReduceMotion = reduce;
    }

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        icon = res is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);

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

    private void TraySetMode(ListeningMode m) => _ = _vm.SetModeAsync(m);

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
        if (!_vm.ControlsEnabled)
        {
            if (_settings.Current.ShowNotifications && _tray is not null)
                _tray.ShowBalloonTip(4000, "AirPods Control", _vm.ChannelText, WinForms.ToolTipIcon.Info);
            return;
        }
        _ = _vm.SetModeAsync(mode);
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings.Current, _controller.KnownDevices, _hotkeyState);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        try { _settings.Save(dlg.Result); }
        catch (Exception ex) { _log.Error("Saving settings failed", ex); MessageBox.Show("Could not save settings: " + ex.Message, "AirPods Control"); }
        var s = _settings.Current;
        ApplyMotion(s.ReduceMotion);
        _controller.NoiseControl.AutoEnableOffOption = s.AutoEnableOffOption;
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
