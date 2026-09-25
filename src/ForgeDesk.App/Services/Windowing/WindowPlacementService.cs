using System.ComponentModel;
using System.Windows;
using ForgeDesk.Core.Settings;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Services.Windowing;

/// <summary>
/// Restores the main window's size, position and maximized state from settings (validated
/// against the current monitors) and remembers them when the window closes.
/// </summary>
internal sealed class WindowPlacementService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowPlacementService> _logger;
    private WindowState _lastVisibleState = WindowState.Normal;
    private WindowPlacement? _captured;

    public WindowPlacementService(ISettingsService settings, ILogger<WindowPlacementService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Applies the saved placement. Call before the window is shown.</summary>
    public void Restore(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var virtualScreen = new ScreenRect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var workArea = SystemParameters.WorkArea;
        var placement = WindowPlacementCalculator.Compute(_settings.Current.MainWindowPlacement, virtualScreen,
            new ScreenRect(workArea.Left, workArea.Top, workArea.Width, workArea.Height));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;
        window.WindowState = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
        _lastVisibleState = window.WindowState;
    }

    /// <summary>Follows the window so its placement can be saved when it closes.</summary>
    public void Track(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized)
            {
                _lastVisibleState = window.WindowState;
            }
        };
        window.Closing += (_, e) => OnClosing(window, e);
    }

    /// <summary>Persists the placement captured when the window closed (no-op otherwise).</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_captured is not { } placement)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { MainWindowPlacement = placement }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the window placement");
        }
    }

    private void OnClosing(Window window, CancelEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }

        // RestoreBounds holds the normal-state rectangle while maximized or minimized.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || bounds.Width <= 0)
        {
            return;
        }

        _captured = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            _lastVisibleState == WindowState.Maximized);
    }
}
