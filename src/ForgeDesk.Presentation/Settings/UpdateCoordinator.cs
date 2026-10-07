using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

public enum UpdateStatus
{
    /// <summary>Never checked in this session.</summary>
    Idle,

    /// <summary>This copy can't update itself (portable or development build).</summary>
    Unsupported,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// Drives application updates for the whole app: the automatic check shortly after startup
/// (silent on failure), manual checks from Settings, the status bar badge and the
/// "Restart to update" notification.
/// </summary>
public sealed partial class UpdateCoordinator : ObservableObject, IDisposable
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly StatusBarViewModel _statusBar;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<UpdateCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed;

    public UpdateCoordinator(
        IUpdateService updates,
        ISettingsService settings,
        StatusBarViewModel statusBar,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<UpdateCoordinator> logger)
        : this(updates, settings, statusBar, notifications, dispatcher, logger, TimeProvider.System)
    {
    }

    internal UpdateCoordinator(
        IUpdateService updates,
        ISettingsService settings,
        StatusBarViewModel statusBar,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<UpdateCoordinator> logger,
        TimeProvider time)
    {
        _updates = updates;
        _settings = settings;
        _statusBar = statusBar;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _time = time;
        _lifetimeToken = _lifetime.Token;
        Status = updates.IsSupported ? UpdateStatus.Idle : UpdateStatus.Unsupported;
    }

    public bool IsSupported => _updates.IsSupported;

    public string CurrentVersion => _updates.CurrentVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusTone), nameof(IsChecking), nameof(IsDownloading), nameof(IsBusy), nameof(HasUpdate))]
    public partial UpdateStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasUpdate), nameof(AvailableVersion), nameof(ReleaseNotes), nameof(HasReleaseNotes))]
    public partial UpdateCheckResult? AvailableUpdate { get; private set; }

    /// <summary>Download progress, 0..1.</summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial ErrorInfo? LastError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    public partial DateTimeOffset? LastCheckedAt { get; private set; }

    public bool IsChecking => Status == UpdateStatus.Checking;

    public bool IsDownloading => Status == UpdateStatus.Downloading;

    public bool IsBusy => IsChecking || IsDownloading;

    public bool HasUpdate => AvailableUpdate is { Available: true } && Status is UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.Failed;

    public string? AvailableVersion => AvailableUpdate?.Version;

    public string? ReleaseNotes => string.IsNullOrWhiteSpace(AvailableUpdate?.Notes) ? null : AvailableUpdate.Notes.Trim();

    public bool HasReleaseNotes => ReleaseNotes is not null;

    public string? LastCheckedText => LastCheckedAt is { } at ? $"Last checked {Format.RelativeTime(at, _time.GetLocalNow())}" : null;

    public string StatusText => Status switch
    {
        UpdateStatus.Unsupported => "Updates are installed by ForgeDesk Setup. This copy (portable or development build) doesn't update itself.",
        UpdateStatus.Checking => "Checking for updates…",
        UpdateStatus.UpToDate => "You're up to date.",
        UpdateStatus.Available => $"ForgeDesk {AvailableVersion} is available.",
        UpdateStatus.Downloading => $"Downloading ForgeDesk {AvailableVersion}…",
        UpdateStatus.Failed => LastError?.Message ?? "The update check failed.",
        _ => "ForgeDesk checks GitHub releases for new versions.",
    };

    public StatusTone StatusTone => Status switch
    {
        UpdateStatus.UpToDate => StatusTone.Success,
        UpdateStatus.Available => StatusTone.Info,
        UpdateStatus.Downloading or UpdateStatus.Checking => StatusTone.Running,
        UpdateStatus.Failed => StatusTone.Danger,
        _ => StatusTone.Neutral,
    };

    /// <summary>
    /// Checks for an update. Failures end in <see cref="UpdateStatus.Failed"/> with
    /// <see cref="LastError"/> (never thrown). <paramref name="notify"/> announces an available
    /// update with a "Restart to update" notification.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAsync(bool notify = false, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        if (!IsSupported)
        {
            Status = UpdateStatus.Unsupported;
            return null;
        }

        if (IsBusy)
        {
            return AvailableUpdate;
        }

        var previous = Status;
        Status = UpdateStatus.Checking;
        LastError = null;
        try
        {
            var result = await _updates.CheckAsync(cancellationToken).ConfigureAwait(true);
            LastCheckedAt = _time.GetLocalNow();
            if (result.Available)
            {
                AvailableUpdate = result;
                Status = UpdateStatus.Available;
                _statusBar.UpdateAvailableVersion = result.Version;
                _logger.LogInformation("ForgeDesk {Version} is available", result.Version);
                if (notify)
                {
                    Announce(result);
                }
            }
            else
            {
                AvailableUpdate = null;
                Status = UpdateStatus.UpToDate;
                _statusBar.UpdateAvailableVersion = null;
            }

            return result;
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            Status = previous == UpdateStatus.Checking ? UpdateStatus.Idle : previous;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The update check failed");
            LastError = ErrorInfo.From(ex, "Could not check for updates");
            Status = UpdateStatus.Failed;
            return null;
        }
    }

    /// <summary>The startup check: only when enabled in Settings; failures are logged, never shown.</summary>
    public async Task CheckAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || !_settings.Current.CheckForUpdatesAutomatically)
        {
            return;
        }

        await CheckAsync(notify: true, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Downloads the available update, then restarts ForgeDesk to apply it.</summary>
    public async Task DownloadAndRestartAsync()
    {
        if (!IsSupported || IsBusy || _disposed)
        {
            return;
        }

        if (AvailableUpdate is not { Available: true })
        {
            var result = await CheckAsync().ConfigureAwait(true);
            if (result is not { Available: true })
            {
                if (Status == UpdateStatus.UpToDate)
                {
                    _notifications.Show("ForgeDesk is up to date", $"You have the latest version ({CurrentVersion}).", NotificationSeverity.Success);
                }

                return;
            }
        }

        Status = UpdateStatus.Downloading;
        DownloadProgress = 0;
        LastError = null;
        try
        {
            await _updates.DownloadAndApplyAsync(new UiProgress<double>(_dispatcher, p => DownloadProgress = Math.Clamp(p, 0, 1)), _lifetimeToken)
                .ConfigureAwait(true);

            // Only reached when the updater could not restart ForgeDesk.
            Status = UpdateStatus.Available;
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            Status = UpdateStatus.Available;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloading the update failed");
            LastError = ErrorInfo.From(ex, "Could not install the update");
            Status = UpdateStatus.Failed;
            _notifications.ShowError(LastError);
        }
    }

    private void Announce(UpdateCheckResult result) =>
        _notifications.Show(
            $"ForgeDesk {result.Version} is available",
            "Restart ForgeDesk to install it. Running commands will be stopped.",
            NotificationSeverity.Info,
            new NotificationAction("Restart to update", DownloadAndRestartAsync));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
