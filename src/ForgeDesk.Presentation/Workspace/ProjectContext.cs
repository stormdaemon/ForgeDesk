using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// Shared, observable state of an open project: the project record, its detected profile and
/// its live git status. Every workspace section reads from here instead of querying git on its
/// own, so the header, the Git tab and the file tree always agree.
/// </summary>
public sealed partial class ProjectContext : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly IProjectDetector _detector;
    private readonly IProjectRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly Debouncer _statusDebouncer = new(TimeSpan.FromMilliseconds(400));
    private bool _disposed;

    public ProjectContext(Project project, IGitService git, IProjectDetector detector, IProjectRegistry registry, IUiDispatcher dispatcher)
    {
        Project = project;
        _git = git;
        _detector = detector;
        _registry = registry;
        _dispatcher = dispatcher;
        Lifetime = _lifetime.Token;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Root))]
    public partial Project Project { get; set; }

    public string Root => Project.Path;

    public string ProjectId => Project.Id;

    [ObservableProperty]
    public partial ProjectProfile? Profile { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitRepository))]
    public partial GitStatus? GitStatus { get; private set; }

    /// <summary>Set when git status could not be read (git missing, not a repo, repo locked…).</summary>
    [ObservableProperty]
    public partial ErrorInfo? GitError { get; private set; }

    [ObservableProperty]
    public partial bool IsRepositoryChecked { get; private set; }

    public bool IsGitRepository => GitStatus is not null;

    public bool FolderExists => Directory.Exists(Root);

    /// <summary>
    /// Cancelled when the project is closed. Captured once so work that outlives the context still
    /// observes cancellation instead of an ObjectDisposedException.
    /// </summary>
    public CancellationToken Lifetime { get; }

    /// <summary>Raised on the UI thread after <see cref="GitStatus"/> is refreshed.</summary>
    public event EventHandler? GitStatusChanged;

    /// <summary>Raised on the UI thread when files in the working tree changed.</summary>
    public event EventHandler? FilesChanged;

    /// <summary>Raised on the UI thread when refs changed (commit, checkout, fetch…): history/branches must reload.</summary>
    public event EventHandler? RepositoryChanged;

    /// <summary>Raised when a section asks the workspace to switch tabs.</summary>
    public event EventHandler<WorkspaceNavigationRequest>? NavigationRequested;

    public void RequestNavigation(WorkspaceSection section, object? argument = null) =>
        NavigationRequested?.Invoke(this, new WorkspaceNavigationRequest(section, argument));

    /// <summary>Loads cached profile then re-detects in the background.</summary>
    public async Task InitializeAsync()
    {
        var cached = await _registry.GetCachedProfileAsync(ProjectId, Lifetime).ConfigureAwait(true);
        if (cached is not null)
        {
            Profile = cached;
        }

        await RefreshGitStatusAsync().ConfigureAwait(true);
        _ = RefreshProfileAsync();
    }

    public async Task RefreshProfileAsync()
    {
        if (!FolderExists)
        {
            return;
        }

        try
        {
            var profile = await _detector.DetectAsync(Root, Lifetime).ConfigureAwait(false);
            await _registry.SaveProfileAsync(ProjectId, profile, Lifetime).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => Profile = profile).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Profile detection failed for {Root}: {ex.Message}");
        }
    }

    /// <summary>Refreshes git status now (serialized; concurrent callers share the result).</summary>
    public async Task RefreshGitStatusAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _statusGate.WaitAsync(Lifetime).ConfigureAwait(false);
        try
        {
            GitStatus? status = null;
            ErrorInfo? error = null;
            try
            {
                if (!FolderExists)
                {
                    error = new ErrorInfo(ErrorKind.PathNotFound, "Folder not found",
                        $"'{Root}' no longer exists.", "Locate the folder again or remove the project from ForgeDesk.");
                }
                else
                {
                    status = await _git.GetStatusAsync(Root, Lifetime).ConfigureAwait(false);
                }
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.NotARepository)
            {
                // Not an error: plenty of projects are not under version control.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ErrorInfo.From(ex, "Could not read Git status");
            }

            var previousHead = GitStatus?.HeadSha;
            var previousBranch = GitStatus?.Branch;
            await _dispatcher.InvokeAsync(() =>
            {
                GitStatus = status;
                GitError = error;
                IsRepositoryChecked = true;
                GitStatusChanged?.Invoke(this, EventArgs.Empty);
                if (status is not null && (status.HeadSha != previousHead || status.Branch != previousBranch))
                {
                    RepositoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _statusGate.Release();
        }
    }

    /// <summary>Debounced refresh, for bursts of file-system events.</summary>
    public void ScheduleGitStatusRefresh() => _statusDebouncer.Trigger(RefreshGitStatusAsync);

    /// <summary>Called by the watcher (any thread).</summary>
    public void NotifyFilesChanged(bool gitMetadataChanged)
    {
        _dispatcher.Post(() =>
        {
            FilesChanged?.Invoke(this, EventArgs.Empty);
            if (gitMetadataChanged)
            {
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
        ScheduleGitStatusRefresh();
    }

    /// <summary>Signals that refs changed through ForgeDesk (commit, checkout, pull…).</summary>
    public async Task NotifyRepositoryChangedAsync()
    {
        await RefreshGitStatusAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() => RepositoryChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _statusDebouncer.Dispose();
        _lifetime.Dispose();
    }
}
