using ForgeDesk.Core.Settings;

namespace ForgeDesk.App.Services.Windowing;

/// <summary>A rectangle in device-independent pixels.</summary>
internal readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsUsable => double.IsFinite(Left) && double.IsFinite(Top) && Width > 0 && Height > 0
        && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>
/// Decides where the main window opens: the saved placement when its title bar is still
/// reachable on the current monitors, otherwise a 1400×900 window centered on the work area.
/// </summary>
internal static class WindowPlacementCalculator
{
    public const double DefaultWidth = 1400;
    public const double DefaultHeight = 900;
    public const double MinimumWidth = 960;
    public const double MinimumHeight = 600;

    // How much of the title bar must stay on screen for the user to grab it.
    private const double GrabbableWidth = 120;
    private const double GrabbableHeight = 24;
    private const double TitleBarHeight = 40;

    public static WindowPlacement Compute(WindowPlacement? saved, ScreenRect virtualScreen, ScreenRect workArea)
    {
        if (saved is not null && virtualScreen.IsUsable && IsValid(saved))
        {
            var width = Math.Clamp(saved.Width, Math.Min(MinimumWidth, virtualScreen.Width), virtualScreen.Width);
            var height = Math.Clamp(saved.Height, Math.Min(MinimumHeight, virtualScreen.Height), virtualScreen.Height);
            var candidate = new WindowPlacement(saved.Left, saved.Top, width, height, saved.Maximized);
            if (IsTitleBarReachable(candidate, virtualScreen))
            {
                return candidate;
            }
        }

        return Centered(workArea, saved?.Maximized ?? false);
    }

    public static WindowPlacement Centered(ScreenRect workArea, bool maximized = false)
    {
        if (!workArea.IsUsable)
        {
            return new WindowPlacement(0, 0, DefaultWidth, DefaultHeight, maximized);
        }

        var width = Math.Min(DefaultWidth, workArea.Width);
        var height = Math.Min(DefaultHeight, workArea.Height);
        return new WindowPlacement(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height,
            maximized);
    }

    private static bool IsValid(WindowPlacement placement) =>
        double.IsFinite(placement.Left) && double.IsFinite(placement.Top)
        && double.IsFinite(placement.Width) && double.IsFinite(placement.Height)
        && placement.Width >= 200 && placement.Height >= 150;

    private static bool IsTitleBarReachable(WindowPlacement placement, ScreenRect screen)
    {
        var overlapWidth = Math.Min(placement.Left + placement.Width, screen.Right) - Math.Max(placement.Left, screen.Left);
        var overlapHeight = Math.Min(placement.Top + TitleBarHeight, screen.Bottom) - Math.Max(placement.Top, screen.Top);
        return overlapWidth >= Math.Min(GrabbableWidth, placement.Width) && overlapHeight >= GrabbableHeight;
    }
}
