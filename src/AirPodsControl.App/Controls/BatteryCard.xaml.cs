using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirPodsControl.App.ViewModels;

namespace AirPodsControl.App.Controls;

/// <summary>Battery tile. Bar width and colour animate on change; the charging bolt pulses only while charging and visible.</summary>
public partial class BatteryCard : UserControl
{
    private BatteryItemViewModel? _vm;
    private readonly DoubleAnimation _pulse = new(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

    public BatteryCard()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVm;
            _vm = e.NewValue as BatteryItemViewModel;
            if (_vm is not null) { _vm.PropertyChanged += OnVm; Apply(animate: false); }
        };
        IsVisibleChanged += (_, _) => UpdatePulse();
    }

    public static bool ReduceMotion { get; set; }

    private void OnVm(object? s, PropertyChangedEventArgs e) => Apply(animate: true);

    private void Apply(bool animate)
    {
        if (_vm is null) return;
        var target = _vm.IsKnown ? Math.Clamp(_vm.Fraction, 0, 1) : 0;
        if (animate && !ReduceMotion)
            barScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(target, TimeSpan.FromSeconds(0.7)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); barScale.ScaleX = target; }

        var color = _vm.IsCritical ? (Color)ColorConverter.ConvertFromString("#FFFF6B6B")
                  : _vm.IsLow ? (Color)ColorConverter.ConvertFromString("#FFFFC857")
                  : (Color)FindResource("AccentColor");
        if (bar.Background is SolidColorBrush b && !b.IsFrozen && animate && !ReduceMotion)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromSeconds(0.4)));
        else bar.Background = new SolidColorBrush(color);

        pct.Foreground = _vm.IsKnown ? (Brush)FindResource("TextBrush") : (Brush)FindResource("SubtleTextBrush");
        bolt.Visibility = _vm.IsCharging ? Visibility.Visible : Visibility.Collapsed;
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        bool run = _vm?.IsCharging == true && IsVisible && !ReduceMotion;
        bolt.BeginAnimation(OpacityProperty, run ? _pulse : null);
    }
}
