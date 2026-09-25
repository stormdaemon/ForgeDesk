using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Shows a loading indicator only when the wait lasts longer than a short delay, then fades it
/// in, so fast loads never flash a spinner (DESIGN.md: show only after ~250 ms).
/// </summary>
internal sealed class DelayedReveal
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(150));

    private readonly FrameworkElement _host;
    private readonly UIElement _target;
    private readonly Func<bool> _isActive;
    private readonly Action<bool>? _onRevealChanged;
    private readonly DispatcherTimer _timer;
    private bool _revealed;

    public DelayedReveal(FrameworkElement host, UIElement target, Func<bool> isActive, Action<bool>? onRevealChanged = null)
    {
        _host = host;
        _target = target;
        _isActive = isActive;
        _onRevealChanged = onRevealChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, host.Dispatcher);
        _timer.Tick += (_, _) => Reveal();
        Hide();

        host.IsVisibleChanged += (_, _) => Update();
        host.Loaded += (_, _) => Update();
        host.Unloaded += (_, _) => Update();
    }

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Re-evaluates whether the indicator should be pending, visible or hidden.</summary>
    public void Update()
    {
        var shouldShow = _host.IsLoaded && _host.IsVisible && _isActive();
        if (!shouldShow)
        {
            _timer.Stop();
            Hide();
            return;
        }

        if (_revealed || _timer.IsEnabled)
        {
            return;
        }

        if (Delay <= TimeSpan.Zero)
        {
            Reveal();
            return;
        }

        _timer.Interval = Delay;
        _timer.Start();
    }

    private void Reveal()
    {
        _timer.Stop();
        if (_revealed)
        {
            return;
        }

        _revealed = true;
        _target.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            _target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
        }
        else
        {
            _target.BeginAnimation(UIElement.OpacityProperty, null);
            _target.Opacity = 1;
        }

        _onRevealChanged?.Invoke(true);
    }

    private void Hide()
    {
        var wasRevealed = _revealed;
        _revealed = false;
        _target.BeginAnimation(UIElement.OpacityProperty, null);
        _target.Opacity = 0;
        _target.Visibility = Visibility.Collapsed;
        if (wasRevealed)
        {
            _onRevealChanged?.Invoke(false);
        }
    }
}
