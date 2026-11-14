using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;

namespace AirPodsControl.App.Controls;

/// <summary>
/// Picture of the paired model. AirPods Pro use layered artwork (open lid, case body, two buds, front overlay that hides the
/// stems); other models use the vector drawing (standard: long stems; Max: over-ear).
/// Animations: buds float with different phases, rise out of the case when the AirPods connect and sink back when they
/// disconnect; a soft glow pulses in the colour of the new listening mode. Everything is decorative, runs only while the control
/// is visible and motion is allowed (WPF animations are clock-driven, nothing is rendered while the window is hidden).
/// </summary>
public partial class AirPodsVisual : UserControl
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(ModelShape), typeof(AirPodsVisual),
        new PropertyMetadata(ModelShape.Pro, (d, _) => ((AirPodsVisual)d).ApplyShape()));
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(AirPodsVisual),
        new PropertyMetadata(false, (d, e) => ((AirPodsVisual)d).OnConnectedChanged((bool)e.NewValue)));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(ListeningMode?), typeof(AirPodsVisual),
        new PropertyMetadata(null, (d, e) => ((AirPodsVisual)d).OnModeChanged((ListeningMode?)e.NewValue)));

    public ModelShape Shape { get => (ModelShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }
    public ListeningMode? Mode { get => (ListeningMode?)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public static bool ReduceMotion { get; set; }

    /// <summary>How far the buds sink into the case while disconnected (the front overlay hides them).</summary>
    private const double Docked = 34;

    private bool UsePhoto => Shape == ModelShape.Pro;

    public AirPodsVisual()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdateFloat();
        Loaded += (_, _) => { ApplyShape(); OnConnectedChanged(IsConnected, initial: true); };
    }

    private void ApplyShape()
    {
        photo.Visibility = UsePhoto ? Visibility.Visible : Visibility.Collapsed;
        vector.Visibility = UsePhoto ? Visibility.Collapsed : Visibility.Visible;
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
        bool animate = !initial && !ReduceMotion && IsLoaded;

        // Pro artwork: buds rise out of the case on connect, sink in on disconnect.
        photoLeftY.BeginAnimation(TranslateTransform.YProperty, null);
        photoRightY.BeginAnimation(TranslateTransform.YProperty, null);
        double target = connected ? 0 : Docked;
        if (animate && UsePhoto)
        {
            Slide(photoLeftY, target, TimeSpan.Zero, connected);
            Slide(photoRightY, target, TimeSpan.FromMilliseconds(120), connected);
        }
        else photoLeftY.Y = photoRightY.Y = target;

        if (connected && animate)
        {
            // "pop in" when the AirPods connect
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromSeconds(0.55)) { EasingFunction = ease });
            stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromSeconds(0.55)) { EasingFunction = ease });
            stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)));
        }
        else stage.BeginAnimation(OpacityProperty, null);
        if (!animate) UpdateFloat();
    }

    private void Slide(TranslateTransform t, double to, TimeSpan begin, bool rising)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromSeconds(rising ? 0.7 : 0.45))
        {
            BeginTime = begin,
            EasingFunction = rising ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } : new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        // Keep the final value, then hand over to the idle float.
        a.Completed += (_, _) => { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = to; UpdateFloat(); };
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void OnModeChanged(ListeningMode? mode)
    {
        if (mode is null) return;
        glowColor.Color = mode switch
        {
            ListeningMode.NoiseCancellation => Color.FromRgb(0x4D, 0x8D, 0xFF),
            ListeningMode.Transparency => Color.FromRgb(0x35, 0xE6, 0xE0),
            ListeningMode.Adaptive => Color.FromRgb(0xB0, 0x7C, 0xFF),
            _ => Color.FromRgb(0xA0, 0xA8, 0xB8),
        };
        if (ReduceMotion || !IsVisible) { glow.BeginAnimation(OpacityProperty, null); glow.Opacity = 0; return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.4) };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25)), ease));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4)), new SineEase { EasingMode = EasingMode.EaseIn }));
        glow.BeginAnimation(OpacityProperty, fade);
        glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1.15, TimeSpan.FromSeconds(1.4)) { EasingFunction = ease });
        glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1.15, TimeSpan.FromSeconds(1.4)) { EasingFunction = ease });
    }

    private void UpdateFloat()
    {
        bool run = IsVisible && IsConnected && !ReduceMotion;
        Animate(leftFloat, run && !UsePhoto, 0, -5, TimeSpan.Zero, 5.2);
        Animate(rightFloat, run && !UsePhoto, 0, -5, TimeSpan.FromSeconds(1.3), 5.2);
        Animate(maxFloat, run && Shape == ModelShape.Max, 0, -4, TimeSpan.Zero, 5.2);
        if (IsConnected)
        {
            Animate(photoLeftY, run && UsePhoto, 0, -4, TimeSpan.Zero, 3.2);
            Animate(photoRightY, run && UsePhoto, 0, -4, TimeSpan.FromSeconds(1.1), 3.2);
        }
    }

    private static void Animate(TranslateTransform t, bool run, double from, double to, TimeSpan begin, double seconds)
    {
        if (!run) { t.BeginAnimation(TranslateTransform.YProperty, null); return; }
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(a, 30); // a slow float does not need 60 fps
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }
}
