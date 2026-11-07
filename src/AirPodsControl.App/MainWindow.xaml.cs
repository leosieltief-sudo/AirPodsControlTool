using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirPodsControl.App.ViewModels;

namespace AirPodsControl.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _docked;
    private bool _hiding;

    public event Action? SettingsRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += OnVmChanged;
        Deactivated += (_, _) => { if (_docked && IsVisible) AnimatedHide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) AnimatedHide(); };
        ApplyStatusColors(animate: false);
    }

    public static bool ReduceMotion { get; set; }

    /// <summary>Shows the panel anchored above the notification area of the screen under the cursor; hides on focus loss.</summary>
    public void ShowDocked()
    {
        _docked = true;
        Topmost = true;
        ShowAnimated(() =>
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var source = PresentationSource.FromVisual(this);
            double sx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            var wa = screen.WorkingArea;
            Left = wa.Right * sx - ActualWidth + 4;
            Top = wa.Bottom * sy - ActualHeight + 4;
        });
    }

    /// <summary>Shows the panel as a normal, draggable window that stays open.</summary>
    public void ShowPinned()
    {
        _docked = false;
        Topmost = false;
        ShowAnimated(() =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + (wa.Height - ActualHeight) / 2;
        });
    }

    public void Toggle()
    {
        if (IsVisible) AnimatedHide(); else ShowDocked();
    }

    private void ShowAnimated(Action position)
    {
        _hiding = false;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
        }
        position();
        Activate();
        if (ReduceMotion) { BeginAnimation(OpacityProperty, null); Opacity = 1; return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.18)));
        shellSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromSeconds(0.24)) { EasingFunction = ease });
    }

    public void AnimatedHide()
    {
        if (!IsVisible || _hiding) return;
        if (ReduceMotion) { Hide(); return; }
        _hiding = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.14));
        fade.Completed += (_, _) => { if (_hiding) { Hide(); _hiding = false; } };
        BeginAnimation(OpacityProperty, fade);
        shellSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(0, 10, TimeSpan.FromSeconds(0.14)));
    }

    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusKind)) ApplyStatusColors(animate: IsVisible && !ReduceMotion);
    }

    private void ApplyStatusColors(bool animate)
    {
        var fg = _vm.StatusKind switch
        {
            StatusKind.Good => (Color)FindResource("AccentColor"),
            StatusKind.Warn => (Color)ColorConverter.ConvertFromString("#FFFFC857"),
            StatusKind.Bad => (Color)ColorConverter.ConvertFromString("#FFFF6B6B"),
            _ => (Color)ColorConverter.ConvertFromString("#FF8E9AAF"),
        };
        var bg = Color.FromArgb(0x1F, fg.R, fg.G, fg.B);
        if (animate)
        {
            var d = TimeSpan.FromSeconds(0.35);
            var fgBrush = new SolidColorBrush(((statusText.Foreground as SolidColorBrush)?.Color) ?? fg);
            var bgBrush = new SolidColorBrush(((statusPill.Background as SolidColorBrush)?.Color) ?? bg);
            statusText.Foreground = fgBrush; statusDot.Fill = fgBrush; statusPill.Background = bgBrush;
            fgBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(fg, d));
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(bg, d));
            statusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.3)) { AutoReverse = false, RepeatBehavior = new RepeatBehavior(2) });
        }
        else
        {
            var fgBrush = new SolidColorBrush(fg);
            statusText.Foreground = fgBrush; statusDot.Fill = fgBrush; statusPill.Background = new SolidColorBrush(bg);
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _docked = false; // once moved by the user, the panel stays open
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => AnimatedHide();

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var readme = Path.Combine(AppContext.BaseDirectory, "README.md");
        var target = File.Exists(readme) ? readme : "https://magicpods.app/magicaap/";
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
