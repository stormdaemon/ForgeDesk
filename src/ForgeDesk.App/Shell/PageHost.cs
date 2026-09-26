using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ForgeDesk.App.Shell;

/// <summary>
/// Hosts the current page (resolved by implicit DataTemplate) and plays a short fade-and-rise when
/// it changes (150 ms, skipped when Windows animations are off).
/// </summary>
public sealed class PageHost : ContentControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(150));
    private const double RiseDistance = 8;

    private readonly TranslateTransform _offset = new();

    public PageHost()
    {
        RenderTransform = _offset;
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is null || !SystemParameters.ClientAreaAnimation || !IsLoaded)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var rise = new DoubleAnimation(RiseDistance, 0, TransitionDuration) { EasingFunction = ease };

        // Hosted HWNDs (the terminal) follow layout, not render transforms: re-arrange once the
        // page has settled so they end up exactly where the page is.
        rise.Completed += (_, _) =>
        {
            InvalidateArrange();
            UpdateLayout();
        };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TransitionDuration) { EasingFunction = ease });
        _offset.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
