using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AirPodsControl.App.ViewModels;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Windows.Bridge;

namespace AirPodsControl.App;

public sealed class StepViewModel(SetupStep step) : ObservableObject
{
    public string Title => step.Title;
    public string Detail => step.Detail;
    public Visibility HasDetail => string.IsNullOrEmpty(step.Detail) ? Visibility.Collapsed : Visibility.Visible;
    public string Glyph => step.State switch
    {
        SetupStepState.Done => "",     // check
        SetupStepState.Failed => "",   // cross
        SetupStepState.Warning => "",  // warning
        SetupStepState.Running => "",  // sync
        SetupStepState.Skipped => "",
        _ => "",                        // dot
    };
    public Brush GlyphBrush => new SolidColorBrush(step.State switch
    {
        SetupStepState.Done => Color.FromRgb(0x7E, 0xE0, 0xA0),
        SetupStepState.Failed => Color.FromRgb(0xFF, 0x6B, 0x6B),
        SetupStepState.Warning => Color.FromRgb(0xFF, 0xC8, 0x57),
        SetupStepState.Running => Color.FromRgb(0x6F, 0xA8, 0xFF),
        _ => Color.FromRgb(0x5A, 0x64, 0x78),
    });
    public void Refresh() { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(HasDetail)); OnPropertyChanged(nameof(Glyph)); OnPropertyChanged(nameof(GlyphBrush)); }
}

/// <summary>One-click setup of the driverless WSL2 bridge. All long-running work is async; UI stays responsive.</summary>
public partial class BridgeSetupWindow : Window
{
    private readonly BridgeSetupService _svc;
    private readonly ulong? _device;
    private readonly string _deviceName;
    private readonly List<StepViewModel> _steps;
    private CancellationTokenSource? _cts;

    public BridgeSetupWindow(BridgeSetupService svc, ulong? device, string deviceName)
    {
        InitializeComponent();
        _svc = svc; _device = device; _deviceName = deviceName;
        _steps = svc.Steps.Select(s => new StepViewModel(s)).ToList();
        stepsList.ItemsSource = _steps;
        svc.Changed += OnChanged;
        svc.Confirm = (title, message) => Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes).Task;
        Closed += (_, _) => { svc.Changed -= OnChanged; svc.Confirm = (_, _) => Task.FromResult(false); _cts?.Cancel(); };
        deviceText.Text = device is { } d ? $"AirPods: {deviceName}  [{BtAddress.Format(d)}]" : "Connect your AirPods to Windows first — the pairing key is copied per device.";
        runBtn.IsEnabled = device is not null;
        testBtn.IsEnabled = device is not null;
    }

    /// <summary>True once setup or a test succeeded end-to-end (the main window then re-evaluates the backends).</summary>
    public bool Changed { get; private set; }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { foreach (var s in _steps) s.Refresh(); });

    private async Task RunGuarded(Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Driverless setup", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); Changed = true; }
    }

    private void SetBusy(bool busy)
    {
        busyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        runBtn.IsEnabled = testBtn.IsEnabled = !busy && _device is not null;
        removeBtn.IsEnabled = !busy;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_device is not { } d) return;
        await RunGuarded(ct => _svc.RunAllAsync(d, _deviceName, ct));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_device is not { } d) return;
        await RunGuarded(async ct => { if (await _svc.TestAdapterAsync(ct)) await _svc.TestAirPodsAsync(d, ct); });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var kernelNote = _svc.Kernel.Configured(out _) == WslKernelSetting.Ours
            ? "\n\nThe AirPods Control MediaTek kernel line is removed from .wslconfig (backup kept) and WSL restarts on the stock kernel (wsl --shutdown stops running distributions, including Docker Desktop)."
            : "";
        if (MessageBox.Show(this, "Delete the 'airpods-bridge' WSL distribution (including the copied pairing key) and stop sharing the adapter with usbipd?" + kernelNote + "\n\nusbipd-win itself stays installed.",
                "Remove driverless mode", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunGuarded(async ct =>
        {
            var err = await _svc.RemoveAsync(ct);
            MessageBox.Show(this, err ?? "Driverless mode removed.", "Driverless setup", MessageBoxButton.OK, err is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Drag(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch (InvalidOperationException) { } }
}
