using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// Fetch / Pull / Push buttons of the workspace header, plus the silent periodic fetch that keeps
/// ahead/behind counts current while the project is open.
/// </summary>
public sealed partial class GitSyncViewModel : ObservableObject, IDisposable
{
    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly WorkspaceActivity _activity;
    private readonly Func<WorkspaceSection, Task> _showSection;
    private readonly ILogger _logger;
    private CancellationTokenSource? _autoFetchLoop;
    private (bool Enabled, int Minutes) _autoFetchSettings;
    private bool _disposed;

    public GitSyncViewModel(ProjectContext context, WorkspaceServices services, Func<WorkspaceSection, Task> showSection)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        _context = context;
        _services = services;
        _showSection = showSection;
        _activity = new WorkspaceActivity(services.Activity, context, services.Time);
        _logger = services.LoggerFactory.CreateLogger<GitSyncViewModel>();
        _context.GitStatusChanged += OnGitStatusChanged;
        UpdateFromStatus();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncing))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand))]
    public partial bool IsFetching { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncing))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand))]
    public partial bool IsPulling { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncing))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand))]
    public partial bool IsPushing { get; private set; }

    /// <summary>True while the periodic background fetch runs (the fetch icon spins, nothing else changes).</summary>
    [ObservableProperty]
    public partial bool IsAutoFetching { get; private set; }

    public bool IsSyncing => IsFetching || IsPulling || IsPushing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand))]
    public partial bool IsRepository { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPublish), nameof(PushToolTip))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand), nameof(PushCommand))]
    public partial string? BranchName { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpstream), nameof(NeedsPublish), nameof(PullToolTip), nameof(PushToolTip))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    public partial string? Upstream { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PushToolTip))]
    public partial int Ahead { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullToolTip))]
    public partial int Behind { get; private set; }

    public bool HasUpstream => Upstream is not null;

    /// <summary>The branch exists only locally: Push publishes it and sets its upstream.</summary>
    public bool NeedsPublish => BranchName is not null && Upstream is null;

    [ObservableProperty]
    public partial DateTimeOffset? LastFetchedAt { get; private set; }

    /// <summary>Short status of the automatic fetch for the status bar ("Offline"), null when healthy.</summary>
    [ObservableProperty]
    public partial string? AutoFetchStatus { get; private set; }

    /// <summary>Why the last automatic fetch failed (tooltip of <see cref="AutoFetchStatus"/>).</summary>
    [ObservableProperty]
    public partial ErrorInfo? AutoFetchError { get; private set; }

    public string PullToolTip => Upstream is null
        ? "Pull (this branch has no upstream yet)"
        : Behind > 0 ? $"Pull {Format.Count(Behind, "commit")} from {Upstream}" : $"Pull from {Upstream}";

    public string PushToolTip => NeedsPublish
        ? $"Publish {BranchName} to the remote"
        : Ahead > 0 ? $"Push {Format.Count(Ahead, "commit")} to {Upstream}" : $"Push to {Upstream ?? "the remote"}";

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        IsFetching = true;
        try
        {
            await _services.Git.FetchAsync(_context.Root, null, _context.Lifetime).ConfigureAwait(true);
            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            MarkFetched();

            var status = _context.GitStatus;
            var remote = RemoteOf(status?.Upstream);
            var behind = status?.Behind ?? 0;
            if (status?.Upstream is { } upstream && behind > 0)
            {
                var news = $"{Format.Count(behind, "new commit")} on {upstream}";
                await _activity.SucceededAsync(ActivityKind.GitFetch, $"Fetched from {remote}", news, status.Branch).ConfigureAwait(true);
                _services.Notifications.Show(news, $"Pull to bring them into {status.Branch}.", NotificationSeverity.Info,
                    new NotificationAction("Pull now", () => RunIfAllowed(PullCommand)));
            }
            else
            {
                await _activity.SucceededAsync(ActivityKind.GitFetch, $"Fetched from {remote}", null, status?.Branch).ConfigureAwait(true);
                _services.Notifications.Show($"Fetched from {remote}",
                    status?.Upstream is { } tracked ? $"{status.Branch} is up to date with {tracked}." : null, NotificationSeverity.Success);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while the operation ran.
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(ActivityKind.GitFetch, "Fetch failed", ex).ConfigureAwait(true);
        }
        finally
        {
            IsFetching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPull))]
    private async Task PullAsync()
    {
        IsPulling = true;
        try
        {
            var before = _context.GitStatus;
            await _services.Git.PullAsync(_context.Root, null, _context.Lifetime).ConfigureAwait(true);
            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            MarkFetched();

            var after = _context.GitStatus;
            var upstream = after?.Upstream ?? before?.Upstream ?? "the remote";
            var branch = after?.Branch ?? before?.Branch;
            var pulled = await CountNewCommitsAsync(before?.HeadSha, after?.HeadSha).ConfigureAwait(true);
            if (pulled > 0)
            {
                var title = $"Pulled {Format.Count(pulled, "commit")} from {upstream}";
                await _activity.SucceededAsync(ActivityKind.GitPull, title, null, branch).ConfigureAwait(true);
                _services.Notifications.Show(title, null, NotificationSeverity.Success);
            }
            else
            {
                await _activity.SucceededAsync(ActivityKind.GitPull, $"Pulled from {upstream}", "Already up to date.", branch).ConfigureAwait(true);
                _services.Notifications.Show("Already up to date", $"{branch} matches {upstream}.", NotificationSeverity.Success);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while the operation ran.
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(ActivityKind.GitPull, "Pull failed", ex).ConfigureAwait(true);
        }
        finally
        {
            IsPulling = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPush))]
    private async Task PushAsync()
    {
        IsPushing = true;
        try
        {
            var before = _context.GitStatus;
            var branch = before?.Branch ?? throw new ForgeException(ErrorKind.DetachedHead,
                "You are not on a branch, so there is nothing to push.", "Switch to a branch, or create one from the current commit.");
            var publish = before.Upstream is null;
            var ahead = before.Ahead;

            await _services.Git.PushAsync(_context.Root, new GitPushOptions(SetUpstream: true, Branch: publish ? branch : null), null, _context.Lifetime)
                .ConfigureAwait(true);
            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);

            var upstream = _context.GitStatus?.Upstream ?? before.Upstream;
            string title;
            string? message = null;
            if (publish)
            {
                title = $"Published {branch} to {RemoteOf(upstream)}";
                message = upstream is null ? null : $"{branch} now tracks {upstream}.";
            }
            else if (ahead > 0)
            {
                title = $"Pushed {Format.Count(ahead, "commit")} to {upstream}";
            }
            else
            {
                title = $"{upstream} is up to date";
                message = "There were no new commits to push.";
            }

            await _activity.SucceededAsync(ActivityKind.GitPush, title, message, branch).ConfigureAwait(true);
            _services.Notifications.Show(title, message, NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while the operation ran.
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(ActivityKind.GitPush, "Push failed", ex).ConfigureAwait(true);
        }
        finally
        {
            IsPushing = false;
        }
    }

    private bool CanFetch() => IsRepository && !IsSyncing;

    private bool CanPull() => IsRepository && BranchName is not null && Upstream is not null && !IsSyncing;

    private bool CanPush() => IsRepository && BranchName is not null && !IsSyncing;

    /// <summary>(Re)starts the periodic fetch according to the AutoFetch settings. Idempotent.</summary>
    public void StartAutoFetch()
    {
        if (_disposed)
        {
            return;
        }

        _services.Settings.Changed -= OnSettingsChanged;
        _services.Settings.Changed += OnSettingsChanged;
        RestartAutoFetch(_services.Settings.Current, force: true);
    }

    /// <summary>One silent fetch: never shows a notification, only updates <see cref="AutoFetchStatus"/>.</summary>
    internal async Task AutoFetchOnceAsync()
    {
        if (_disposed || !_context.IsGitRepository || IsSyncing || IsAutoFetching || !_context.FolderExists)
        {
            return;
        }

        IsAutoFetching = true;
        try
        {
            await _services.Git.FetchAsync(_context.Root, null, _context.Lifetime).ConfigureAwait(true);
            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            MarkFetched();
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, "Automatic fetch failed");
            AutoFetchError = error;
            AutoFetchStatus = error.Kind switch
            {
                ErrorKind.NetworkUnavailable or ErrorKind.Timeout => "Offline",
                ErrorKind.AuthenticationRequired or ErrorKind.AuthenticationFailed => "Fetch needs sign-in",
                _ => "Fetch failed",
            };
            _logger.LogDebug(ex, "Automatic fetch failed for {Root}", _context.Root);
        }
        finally
        {
            IsAutoFetching = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.GitStatusChanged -= OnGitStatusChanged;
        _services.Settings.Changed -= OnSettingsChanged;
        StopAutoFetch();
    }

    private void OnGitStatusChanged(object? sender, EventArgs e) => UpdateFromStatus();

    private void UpdateFromStatus()
    {
        var status = _context.GitStatus;
        IsRepository = status is not null;
        BranchName = status?.Branch;
        Upstream = status?.Upstream;
        Ahead = status?.Ahead ?? 0;
        Behind = status?.Behind ?? 0;
    }

    private void MarkFetched()
    {
        LastFetchedAt = _services.Time.GetLocalNow();
        AutoFetchStatus = null;
        AutoFetchError = null;
    }

    private async Task<int> CountNewCommitsAsync(string? previousHead, string? currentHead)
    {
        if (previousHead is null || currentHead is null || string.Equals(previousHead, currentHead, StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            return await _services.Git.CountCommitsAsync(_context.Root, $"{previousHead}..{currentHead}", null, _context.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "Could not count pulled commits");
            return 0;
        }
    }

    private async Task ReportFailureAsync(ActivityKind kind, string title, Exception exception)
    {
        // Known failures keep their specific title ("Remote has new changes"); the journal says what failed.
        var error = ErrorInfo.From(exception, exception is ForgeException { Kind: not ErrorKind.Unknown } ? null : title);
        await _activity.FailedAsync(kind, title, error, _context.GitStatus?.Branch).ConfigureAwait(true);
        _services.Notifications.ShowError(error, RecoveryFor(error.Kind));
    }

    private NotificationAction? RecoveryFor(ErrorKind kind) => kind switch
    {
        ErrorKind.NonFastForward => new NotificationAction("Pull now", () => RunIfAllowed(PullCommand)),
        ErrorKind.NoUpstream => new NotificationAction("Publish branch", () => RunIfAllowed(PushCommand)),
        ErrorKind.MergeConflict => new NotificationAction("Resolve conflicts", () => _showSection(WorkspaceSection.Git)),
        ErrorKind.DirtyWorkingTree => new NotificationAction("Review changes", () => _showSection(WorkspaceSection.Git)),
        ErrorKind.AuthenticationRequired or ErrorKind.AuthenticationFailed => new NotificationAction("Sign in", () =>
        {
            _services.Navigation.OpenSettings("GitHub");
            return Task.CompletedTask;
        }),
        _ => null,
    };

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        _services.Dispatcher.Post(() => RestartAutoFetch(settings, force: false));

    private void RestartAutoFetch(AppSettings settings, bool force)
    {
        if (_disposed)
        {
            return;
        }

        var wanted = (settings.AutoFetch, Math.Max(1, settings.AutoFetchIntervalMinutes));
        if (!force && wanted == _autoFetchSettings)
        {
            return;
        }

        _autoFetchSettings = wanted;
        StopAutoFetch();
        if (!settings.AutoFetch)
        {
            return;
        }

        var loop = new CancellationTokenSource();
        _autoFetchLoop = loop;
        _ = RunAutoFetchLoopAsync(TimeSpan.FromMinutes(wanted.Item2), loop.Token);
    }

    private async Task RunAutoFetchLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            var delay = _services.InitialAutoFetchDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, _services.Time, cancellationToken).ConfigureAwait(true);
                var fetch = await _services.Dispatcher.InvokeAsync(AutoFetchOnceAsync).ConfigureAwait(true);
                await fetch.ConfigureAwait(true);
                delay = interval;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopAutoFetch()
    {
        _autoFetchLoop?.Cancel();
        _autoFetchLoop?.Dispose();
        _autoFetchLoop = null;
    }

    private static Task RunIfAllowed(IAsyncRelayCommand command) =>
        command.CanExecute(null) ? command.ExecuteAsync(null) : Task.CompletedTask;

    internal static string RemoteOf(string? upstream)
    {
        if (string.IsNullOrEmpty(upstream))
        {
            return "origin";
        }

        var slash = upstream.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? upstream[..slash] : upstream;
    }
}
