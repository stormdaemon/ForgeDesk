using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ForgeDesk.App.Controls;

/// <summary>
/// A small colored dot for a <see cref="StatusKind"/> (project list, tabs, run history).
/// <see cref="StatusKind.Running"/> pulses gently, unless Windows animations are turned off.
/// </summary>
public partial class StatusDot : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(StatusKind), typeof(StatusDot), new PropertyMetadata(StatusKind.Neutral, OnStatusChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(StatusDot), new PropertyMetadata(8.0));

    public StatusDot()
    {
        InitializeComponent();
        ApplyStatus();
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
        IsVisibleChanged += (_, _) => UpdatePulse();
    }

    public StatusKind Status
    {
        get => (StatusKind)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Diameter in pixels (default 8).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dot = (StatusDot)d;
        dot.ApplyStatus();
        dot.UpdatePulse();
    }

    private void ApplyStatus()
    {
        var key = StatusResources.BrushKey(Status);
        Dot.SetResourceReference(Shape.FillProperty, key);
        Halo.SetResourceReference(Shape.FillProperty, key);
        AutomationProperties.SetName(this, Status.ToString());
    }

    private void UpdatePulse()
    {
        if (Status == StatusKind.Running && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            var duration = new Duration(TimeSpan.FromSeconds(1.2));
            var grow = new DoubleAnimation(1, 2.4, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.55, 0, duration) { RepeatBehavior = RepeatBehavior.Forever };
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Halo.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            StopPulse();
        }
    }

    private void StopPulse()
    {
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Halo.BeginAnimation(OpacityProperty, null);
        Halo.Opacity = 0;
    }
}
