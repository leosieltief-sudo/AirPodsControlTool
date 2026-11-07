using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirPodsControl.Core.Models;

namespace AirPodsControl.App.Controls;

/// <summary>
/// Vector rendering of the paired model (Pro: short stems + ear tips; standard: long stems; Max: over-ear).
/// Decorative animations (gentle float) run only while the control is visible, connected and motion is allowed;
/// WPF animations are clock-driven, so nothing is rendered while the window is hidden.
/// </summary>
public partial class AirPodsVisual : UserControl
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(ModelShape), typeof(AirPodsVisual),
        new PropertyMetadata(ModelShape.Pro, (d, _) => ((AirPodsVisual)d).ApplyShape()));
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(AirPodsVisual),
        new PropertyMetadata(false, (d, e) => ((AirPodsVisual)d).OnConnectedChanged((bool)e.NewValue)));

    public ModelShape Shape { get => (ModelShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }

    public static bool ReduceMotion { get; set; }

    public AirPodsVisual()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdateFloat();
        Loaded += (_, _) => { ApplyShape(); OnConnectedChanged(IsConnected, initial: true); };
    }

    private void ApplyShape()
    {
        bool max = Shape == ModelShape.Max;
        earbuds.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        headphones.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        bool pro = Shape is ModelShape.Pro or ModelShape.Beats;
        foreach (var e in new UIElement[] { leftTip, leftMesh, rightTip, rightMesh }) e.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in new UIElement[] { leftGrille, rightGrille }) e.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        leftStem.Height = rightStem.Height = pro ? 58 : 74;
        UpdateFloat();
    }

    private void OnConnectedChanged(bool connected, bool initial = false)
    {
        stage.Opacity = connected ? 1.0 : 0.45;
        led.Visibility = connected ? Visibility.Visible : Visibility.Hidden;
        if (connected && !initial && !ReduceMotion)
        {
            // "pop in" when the AirPods connect
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromSeconds(0.55)) { EasingFunction = ease });
            stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromSeconds(0.55)) { EasingFunction = ease });
            stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)));
        }
        else stage.BeginAnimation(OpacityProperty, null);
        UpdateFloat();
    }

    private void UpdateFloat()
    {
        bool run = IsVisible && IsConnected && !ReduceMotion;
        Animate(leftFloat, run, -5, TimeSpan.Zero);
        Animate(rightFloat, run, -5, TimeSpan.FromSeconds(1.3));
        Animate(maxFloat, run && Shape == ModelShape.Max, -4, TimeSpan.Zero);
    }

    private static void Animate(TranslateTransform t, bool run, double to, TimeSpan begin)
    {
        if (!run) { t.BeginAnimation(TranslateTransform.YProperty, null); return; }
        var a = new DoubleAnimation(0, to, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(a, 30); // a slow float does not need 60 fps
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }
}
