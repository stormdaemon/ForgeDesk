using System.Windows;
using System.Windows.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Panel loading state: a ProgressRing and an optional <see cref="Message"/>, revealed only after
/// <see cref="Delay"/> (250 ms) so quick loads do not flicker. Bind <c>Visibility</c> or
/// <see cref="IsActive"/> to the view model's busy flag.
/// </summary>
public partial class LoadingState : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(LoadingState), new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingState), new PropertyMetadata(true, OnRevealInputChanged));

    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(
        nameof(Delay), typeof(TimeSpan), typeof(LoadingState), new PropertyMetadata(TimeSpan.FromMilliseconds(250), OnRevealInputChanged));

    private readonly DelayedReveal _reveal;

    public LoadingState()
    {
        InitializeComponent();
        _reveal = new DelayedReveal(this, Body, () => IsActive) { Delay = Delay };
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public TimeSpan Delay
    {
        get => (TimeSpan)GetValue(DelayProperty);
        set => SetValue(DelayProperty, value);
    }

    private static void OnRevealInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LoadingState)d;
        if (control._reveal is { } reveal)
        {
            reveal.Delay = control.Delay;
            reveal.Update();
        }
    }
}
