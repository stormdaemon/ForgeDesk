using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Projects;

/// <summary>
/// Computes dashboard snapshots. A snapshot is a best-effort summary: every source (git, GitHub,
/// tasks, runs) may fail on its own without failing the snapshot. Concurrent refreshes of the same
/// project share one computation.
/// </summary>
internal sealed class ProjectStatusService : IProjectStatusService
{
    public const int MaxTechnologies = 6;
    private const int RecentRunsConsidered = 5;
    private const int MaxRelocationRetries = 2;

    // Most telling first: "React · Vite" says more than "npm · ESLint".
    private static readonly TechnologyKind[] TechnologyPriority =
    [
        TechnologyKind.Framework, TechnologyKind.Runtime, TechnologyKind.Library, TechnologyKind.Language,
        TechnologyKind.BuildTool, TechnologyKind.TestFramework, TechnologyKind.Tooling, TechnologyKind.PackageManager,
        TechnologyKind.Infrastructure,
    ];

    private readonly IProjectRegistry _registry;
    private readonly IGitService _git;
    private readonly IGitHubService _gitHub;
    private readonly IWorkItemService _workItems;
    private readonly IRunService _runs;
    private readonly IClock _clock;
    private readonly ILogger<ProjectStatusService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InFlightRefresh> _inFlight = new(StringComparer.Ordinal);

    public ProjectStatusService(
        IProjectRegistry registry,
        IGitService git,
        IGitHubService gitHub,
        IWorkItemService workItems,
        IRunService runs,
        IClock clock,
        ILogger<ProjectStatusService>? logger = null)
    {
        _registry = registry;
        _git = git;
        _gitHub = gitHub;
        _workItems = workItems;
        _runs = runs;
        _clock = clock;
        _logger = logger ?? NullLogger<ProjectStatusService>.Instance;
    }

    public event EventHandler<ProjectSnapshot>? SnapshotUpdated;

    public Task<ProjectSnapshot?> GetCachedAsync(string projectId, CancellationToken cancellationToken = default) =>
        _registry.GetCachedSnapshotAsync(projectId, cancellationToken);

    public async Task<ProjectSnapshot> RefreshAsync(Project project, bool includeRemote = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InFlightRefresh? joined = null;
            Task? runningLocalRefresh = null;
            var isOwner = false;
            lock (_gate)
            {
                if (_inFlight.TryGetValue(project.Id, out var current))
                {
                    if (current.IncludeRemote || !includeRemote)
                    {
                        current.Waiters++;
                        joined = current;
                    }
                    else
                    {
                        runningLocalRefresh = current.Completion.Task;
                    }
                }
                else
                {
                    joined = new InFlightRefresh(includeRemote) { Waiters = 1 };
                    _inFlight[project.Id] = joined;
                    isOwner = true;
                }
            }

            if (joined is null)
            {
                // A local-only refresh cannot answer a request for remote data: let it finish, then start ours.
                await WaitIgnoringFailuresAsync(runningLocalRefresh!, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (isOwner)
            {
                _ = Task.Run(() => RunAsync(project, joined), CancellationToken.None);
            }

            try
            {
                return await joined.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Leave(project.Id, joined);
            }
        }
    }

    private async Task RunAsync(Project project, InFlightRefresh refresh)
    {
        try
        {
            ProjectSnapshot snapshot;
            for (var attempt = 0; ; attempt++)
            {
                snapshot = await ComputeAsync(project, refresh.IncludeRemote, refresh.Cancellation.Token).ConfigureAwait(false);
                try
                {
                    await _registry.SaveSnapshotAsync(snapshot, refresh.Cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The fresh snapshot is still worth showing even if it could not be cached.
                    _logger.LogWarning(ex, "Could not cache the snapshot of project {ProjectId}", project.Id);
                }

                // A relocation committed while computing (or a caller holding an outdated Project) would
                // leave a snapshot of the old folder cached over the one RelocateAsync cleared. Checked
                // after saving: a relocation committed after this check clears the cache itself.
                var current = await TryGetRegisteredAsync(project.Id, refresh.Cancellation.Token).ConfigureAwait(false);
                if (current is null || string.Equals(current.Path, project.Path, StringComparison.Ordinal) || attempt >= MaxRelocationRetries)
                {
                    break;
                }

                project = current;
            }

            Forget(project.Id, refresh);

            // Raised before completing so awaiting callers observe the event first.
            SafeEvent.Raise(SnapshotUpdated, this, snapshot);
            refresh.Completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException ex)
        {
            Forget(project.Id, refresh);
            refresh.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            Forget(project.Id, refresh);
            refresh.Completion.TrySetException(ex);
        }
    }

    private async Task<Project?> TryGetRegisteredAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            return await _registry.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not re-read project {ProjectId} before caching its snapshot", projectId);
            return null;
        }
    }

    private async Task<ProjectSnapshot> ComputeAsync(Project project, bool includeRemote, CancellationToken cancellationToken)
    {
        var previous = await TryAsync(() => _registry.GetCachedSnapshotAsync(project.Id, cancellationToken), null, "read the cached snapshot", cancellationToken).ConfigureAwait(false);
        var folderExists = Directory.Exists(project.Path);

        GitStatus? status = null;
        GitCommit? lastCommit = null;
        string? problem = null;
        var hasRemotes = false;
        if (folderExists)
        {
            try
            {
                status = await _git.GetStatusAsync(project.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.NotARepository)
            {
                // Not an error: plenty of projects are not under version control.
            }
            catch (ForgeException ex)
            {
                _logger.LogInformation(ex, "Git status failed for {Path}", project.Path);
                problem = ex.Message;
            }

            if (status is { IsUnborn: false })
            {
                lastCommit = await TryAsync(async () => (await _git.GetLogAsync(project.Path, new GitLogQuery { Take = 1 }, cancellationToken).ConfigureAwait(false)).FirstOrDefault(),
                    null, "read the last commit", cancellationToken).ConfigureAwait(false);
            }

            if (status is { Upstream: null, IsDetached: false, IsUnborn: false })
            {
                hasRemotes = await TryAsync(async () => (await _git.GetRemotesAsync(project.Path, cancellationToken).ConfigureAwait(false)).Count > 0,
                    false, "read the remotes", cancellationToken).ConfigureAwait(false);
            }
        }

        var profile = await TryAsync(() => _registry.GetCachedProfileAsync(project.Id, cancellationToken), null, "read the cached profile", cancellationToken).ConfigureAwait(false);
        var ci = includeRemote
            ? await GetCiAsync(project, status?.Branch, cancellationToken).ConfigureAwait(false)
            : KeepPreviousCi(previous, status?.Branch);
        var openWorkItems = await TryAsync(() => _workItems.CountOpenAsync(project.Id, cancellationToken), 0, "count open tasks", cancellationToken).ConfigureAwait(false);
        var lastRun = await TryAsync(() => GetLastRunAsync(project.Id, cancellationToken), null, "read the run history", cancellationToken).ConfigureAwait(false);

        var snapshot = new ProjectSnapshot
        {
            ProjectId = project.Id,
            CapturedAt = _clock.Now,
            FolderExists = folderExists,
            IsGitRepository = status is not null,
            Branch = status?.Branch,
            IsDetachedHead = status?.IsDetached ?? false,
            Upstream = status?.Upstream,
            Ahead = status?.Ahead ?? 0,
            Behind = status?.Behind ?? 0,
            ChangedFiles = status?.Entries.Count ?? 0,
            StagedFiles = status?.Staged.Count() ?? 0,
            UntrackedFiles = status?.Entries.Count(e => e.IsUntracked) ?? 0,
            ConflictedFiles = status?.Conflicted.Count() ?? 0,
            StashCount = status?.StashCount ?? 0,
            LastCommitAt = lastCommit is null ? null : lastCommit.Committer?.When ?? lastCommit.Author.When,
            LastCommitSubject = lastCommit?.Subject,
            PrimaryLanguage = profile?.PrimaryLanguage,
            Technologies = SummarizeTechnologies(profile),
            Ci = ci,
            OpenWorkItems = openWorkItems,
            RunningCommands = CountRunningCommands(project.Id),
            LastRun = lastRun,
            Problem = problem,
        };

        return snapshot with { Attention = AttentionRules.Evaluate(snapshot, status, hasRemotes) };
    }

    private async Task<CiSummary?> GetCiAsync(Project project, string? branch, CancellationToken cancellationToken)
    {
        if (project.GitHub is not { } repo || !_gitHub.IsSignedIn)
        {
            return null;
        }

        try
        {
            return await _gitHub.GetCiSummaryAsync(repo, branch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, rate limited, token revoked…: CI is simply unknown for now.
            _logger.LogInformation(ex, "CI status unavailable for {Repository}", repo.FullName);
            return new CiSummary { State = CiState.Unknown, Branch = branch, Message = ex.Message };
        }
    }

    /// <summary>A local refresh has no remote data: the last known CI state stays valid while the branch is the same.</summary>
    private static CiSummary? KeepPreviousCi(ProjectSnapshot? previous, string? branch) =>
        previous?.Ci is { } ci && string.Equals(previous.Branch, branch, StringComparison.Ordinal) ? ci : null;

    private async Task<RunOutcomeSummary?> GetLastRunAsync(string projectId, CancellationToken cancellationToken)
    {
        // Running, cancelled and interrupted runs say nothing about success: report the latest definite outcome.
        var history = await _runs.GetHistoryAsync(projectId, RecentRunsConsidered, cancellationToken).ConfigureAwait(false);
        var last = history
            .Where(r => r.Status is RunStatus.Succeeded or RunStatus.Failed)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();
        return last is null ? null : new RunOutcomeSummary(last.Label, last.Status == RunStatus.Succeeded, last.EndedAt ?? last.StartedAt);
    }

    private int CountRunningCommands(string projectId)
    {
        try
        {
            return _runs.ActiveRuns.Count(r => r.Request.ProjectId == projectId && r.Status is RunStatus.Queued or RunStatus.Running);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count the running commands of project {ProjectId}", projectId);
            return 0;
        }
    }

    internal static IReadOnlyList<string> SummarizeTechnologies(ProjectProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        return profile.Technologies
            .Where(t => !string.Equals(t.Name, profile.PrimaryLanguage, StringComparison.OrdinalIgnoreCase))
            .Select((t, index) => (Technology: t, Index: index))
            .OrderBy(t => Array.IndexOf(TechnologyPriority, t.Technology.Kind) is var rank and >= 0 ? rank : TechnologyPriority.Length)
            .ThenBy(t => t.Index)
            .Select(t => t.Technology.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTechnologies)
            .ToList();
    }

    private async Task<T> TryAsync<T>(Func<Task<T>> read, T fallback, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not {What} for a project snapshot", what);
            return fallback;
        }
    }

    private static async Task WaitIgnoringFailuresAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Its outcome belongs to its own callers.
        }
    }

    /// <summary>A caller stops waiting; when nobody waits any more the computation is cancelled.</summary>
    private void Leave(string projectId, InFlightRefresh refresh)
    {
        var abandon = false;
        lock (_gate)
        {
            refresh.Waiters--;
            if (refresh.Waiters == 0 && !refresh.Completion.Task.IsCompleted)
            {
                abandon = true;
                if (_inFlight.TryGetValue(projectId, out var current) && ReferenceEquals(current, refresh))
                {
                    _inFlight.Remove(projectId);
                }
            }
        }

        if (abandon)
        {
            refresh.Cancellation.Cancel();
        }
    }

    /// <summary>Removes a finished refresh so the next caller starts a new one.</summary>
    private void Forget(string projectId, InFlightRefresh refresh)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(projectId, out var current) && ReferenceEquals(current, refresh))
            {
                _inFlight.Remove(projectId);
            }
        }
    }

    private sealed class InFlightRefresh(bool includeRemote)
    {
        public bool IncludeRemote { get; } = includeRemote;

        public TaskCompletionSource<ProjectSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never disposed on purpose: a late Cancel() from the last leaving caller must not race a Dispose().
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Guarded by the service's lock.</summary>
        public int Waiters { get; set; }
    }
}
