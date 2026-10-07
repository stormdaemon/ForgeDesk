using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>What the views of the GitHub tab share: the project, the services, the journal and navigation between views.</summary>
public sealed class GitHubSectionContext
{
    internal GitHubSectionContext(ProjectContext context, WorkspaceServices services, IGitHubService gitHub, Func<GitHubNavigation, Task> navigate)
    {
        Context = context;
        Services = services;
        GitHub = gitHub;
        Navigate = navigate;
        Activity = new GitHubActivity(services.Activity, context, services.Time);
    }

    public ProjectContext Context { get; }

    public WorkspaceServices Services { get; }

    public IGitHubService GitHub { get; }

    /// <summary>Shows another view of the GitHub tab (with its argument).</summary>
    public Func<GitHubNavigation, Task> Navigate { get; }

    internal GitHubActivity Activity { get; }

    /// <summary>The linked repository, or null when the project has no GitHub remote.</summary>
    public GitHubRepoRef? Repo => Context.Project.GitHub;

    public CancellationToken Lifetime => Context.Lifetime;

    /// <summary>The checked-out branch, or null when HEAD is detached or the folder is not a repository.</summary>
    public string? CurrentBranch => Context.GitStatus is { IsDetached: false, Branch: { Length: > 0 } branch } ? branch : null;

    public IDialogService Dialogs => Services.Dialogs;

    public INotificationService Notifications => Services.Notifications;

    public IShellIntegration Shell => Services.Shell;

    /// <summary>The linked repository; throws a readable error when the project is not linked.</summary>
    public GitHubRepoRef RequireRepo() => Repo ?? throw new ForgeException(ErrorKind.NotFound,
        "This project isn't linked to a GitHub repository.", "Re-detect the GitHub remote from the GitHub tab.");
}

/// <summary>
/// One view of the GitHub tab (Overview, Pull requests, Issues, Actions). Loads on first activation,
/// reloads when invalidated while visible, and only remembers that it is stale while hidden.
/// </summary>
public abstract partial class GitHubSubViewModel : ViewModelBase, IDisposable
{
    private bool _stale;
    private Task? _invalidationLoad;

    protected GitHubSubViewModel(GitHubSectionContext section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Section = section;
    }

    public abstract GitHubView View { get; }

    protected GitHubSectionContext Section { get; }

    protected ProjectContext Context => Section.Context;

    protected IGitHubService GitHub => Section.GitHub;

    /// <summary>True while this view is on screen.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True once the first load was started.</summary>
    public bool HasLoaded { get; private set; }

    /// <summary>First load in progress, nothing to show yet: the view shows skeleton rows.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; protected set; }

    protected bool IsDisposed { get; private set; }

    internal async Task ActivateAsync()
    {
        IsActive = true;
        if (!HasLoaded || _stale)
        {
            _stale = false;
            HasLoaded = true;
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            OnReactivated();
        }
    }

    internal void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>The data changed: reload now when visible, on the next activation otherwise.</summary>
    internal void Invalidate()
    {
        if (!HasLoaded || IsDisposed)
        {
            return;
        }

        if (IsActive)
        {
            if (_invalidationLoad is { IsCompleted: false })
            {
                return;
            }

            _invalidationLoad = LoadAsync();
        }
        else
        {
            _stale = true;
        }
    }

    /// <summary>Forgets everything loaded (another repository was linked); the next activation loads from scratch.</summary>
    internal void Reset()
    {
        HasLoaded = false;
        _stale = false;
        Error = null;
        OnReset();
    }

    /// <summary>Loads (or reloads) the view's data. Never throws: failures go to <see cref="ViewModelBase.Error"/>.</summary>
    public abstract Task LoadAsync();

    /// <summary>Retry of the error panel and "Refresh" menu entries.</summary>
    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    /// <summary>A link clicked in rendered Markdown (descriptions, comments).</summary>
    [RelayCommand]
    private void OpenLink(string? url) => OpenUrl(url);

    /// <summary>Takes the filters of a deep link before the view is shown, so it loads once with them.</summary>
    internal virtual void PrepareNavigation(GitHubNavigation navigation)
    {
    }

    /// <summary>Applies a deep link once the view is selected and loaded (select an item, open a dialog…).</summary>
    internal virtual Task NavigateAsync(GitHubNavigation navigation) => Task.CompletedTask;

    protected virtual void OnReactivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    protected virtual void OnReset()
    {
    }

    /// <summary>Runs an action whose failure is reported as a notification (never inline).</summary>
    protected Task<bool> RunActionAsync(Func<Task> work, string errorTitle) =>
        RunAsync(work, errorTitle: errorTitle, errorMode: ErrorMode.Toast, notifications: Section.Notifications);

    protected void Notify(string title, string? message = null, NotificationSeverity severity = NotificationSeverity.Success, NotificationAction? action = null) =>
        Section.Notifications.Show(title, message, severity, action);

    protected void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Section.Shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            Section.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    /// <summary>Copies text to the clipboard and confirms it.</summary>
    protected void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Section.Shell.CopyToClipboard(text);
            Notify($"{what} copied", text.Length > 120 ? text[..117] + "…" : text, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            Section.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels the view's pending work (called once).</summary>
    protected virtual void OnDisposed()
    {
    }
}

/// <summary>Journals the GitHub tab's operations (issues, comments, pull requests, workflow runs) in the activity log.</summary>
internal sealed class GitHubActivity
{
    private readonly IActivityLog _log;
    private readonly ProjectContext _context;
    private readonly TimeProvider _time;

    public GitHubActivity(IActivityLog log, ProjectContext context, TimeProvider time)
    {
        _log = log;
        _context = context;
        _time = time;
    }

    /// <summary>An entry pointing to a GitHub page (issue, pull request, run).</summary>
    public Task SucceededAsync(ActivityKind kind, string title, string? detail = null, string? url = null) =>
        RecordAsync(kind, ActivityOutcome.Success, title, detail, url);

    public Task FailedAsync(ActivityKind kind, string title, ErrorInfo error, string? url = null) =>
        RecordAsync(kind, ActivityOutcome.Failure, title, error.Hint is null ? error.Message : $"{error.Message} {error.Hint}", url);

    private async Task RecordAsync(ActivityKind kind, ActivityOutcome outcome, string title, string? detail, string? url)
    {
        try
        {
            await _log.RecordAsync(new ActivityEntry
            {
                ProjectId = _context.ProjectId,
                At = _time.GetLocalNow(),
                Kind = kind,
                Outcome = outcome,
                Title = title,
                Detail = detail,
                RefKind = url is null ? null : "url",
                RefValue = url,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Journaling must never turn a successful operation into a failure.
            System.Diagnostics.Trace.TraceWarning($"Could not record activity '{title}': {ex.Message}");
        }
    }
}
