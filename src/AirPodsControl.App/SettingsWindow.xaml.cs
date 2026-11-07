using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirPodsControl.App.Services;
using AirPodsControl.Windows;

namespace AirPodsControl.App;

public partial class SettingsWindow : Window
{
    private sealed record DeviceOption(ulong? Address, string Label);

    private readonly AppSettings _original;
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current, IReadOnlyList<PairedAppleDevice> devices, IEnumerable<HotkeyRegistration> hotkeyState)
    {
        InitializeComponent();
        _original = current;
        autostart.IsChecked = current.StartWithWindows;
        startHidden.IsChecked = current.StartHidden;
        notifications.IsChecked = current.ShowNotifications;
        reduceMotion.IsChecked = current.ReduceMotion;
        autoOff.IsChecked = current.AutoEnableOffOption;
        hotkeysOn.IsChecked = current.HotkeysEnabled;
        hkAnc.Text = current.HotkeyNoiseCancellation;
        hkOff.Text = current.HotkeyOff;
        hkTr.Text = current.HotkeyTransparency;

        var options = new List<DeviceOption> { new(null, "Automatic (first connected AirPods)") };
        options.AddRange(devices.Select(d => new DeviceOption(d.Address, $"{d.Model.Name} — {d.Mac}")));
        deviceBox.ItemsSource = options;
        deviceBox.SelectedItem = options.FirstOrDefault(o => o.Address == current.PreferredDevice) ?? options[0];

        var lines = hotkeyState.Select(h => h.Success ? $"✓ {h.Name}: {h.Gesture}" : $"✗ {h.Name}: {h.Gesture} — {h.Error}").ToList();
        hotkeyStatus.Text = lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
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
        foreach (var tb in new[] { hkAnc, hkOff, hkTr })
            if (tb.Text.Length > 0 && !HotkeyService.TryParse(tb.Text, out _, out _, out var err))
            { hotkeyStatus.Text = $"Invalid hotkey '{tb.Text}': {err}"; return; }
        var dups = new[] { hkAnc.Text, hkOff.Text, hkTr.Text }.Where(t => t.Length > 0).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dups.Count > 0) { hotkeyStatus.Text = $"'{dups[0].Key}' is assigned twice."; return; }

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
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsService.LogDir);
        try { Process.Start(new ProcessStartInfo(SettingsService.LogDir) { UseShellExecute = true }); } catch { }
    }

    private void Drag(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch (InvalidOperationException) { } }
}
