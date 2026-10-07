using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>Which runs the Actions view lists.</summary>
public enum RunBranchScope
{
    CurrentBranch,
    All,
}

/// <summary>An entry of the "Current branch · All branches" filter.</summary>
public sealed record BranchScopeOption(RunBranchScope Value, string Title)
{
    public static IReadOnlyList<BranchScopeOption> All { get; } =
    [
        new(RunBranchScope.CurrentBranch, "Current branch"),
        new(RunBranchScope.All, "All branches"),
    ];

    public static BranchScopeOption For(RunBranchScope value) => All.First(o => o.Value == value);

    public string AutomationId => $"GitHub.RunScope.{Value}";
}

/// <summary>The selected workflow run with its jobs and steps.</summary>
public sealed partial class RunDetailViewModel : ObservableObject
{
    public RunDetailViewModel(WorkflowRunRowViewModel run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Run = run;
    }

    public WorkflowRunRowViewModel Run { get; }

    public ObservableCollection<WorkflowJobViewModel> Jobs { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingJobs { get; internal set; }

    [ObservableProperty]
    public partial ErrorInfo? JobsError { get; internal set; }

    [ObservableProperty]
    public partial bool HasNoJobs { get; internal set; }

    [ObservableProperty]
    public partial bool IsWorking { get; internal set; }

    internal void SetJobs(IReadOnlyList<WorkflowJobInfo> jobs)
    {
        // Keep the user's expanded/collapsed choice of jobs that are still there.
        var expanded = Jobs.ToDictionary(j => j.Id, j => j.IsExpanded);
        Jobs.Clear();
        foreach (var job in jobs)
        {
            var row = new WorkflowJobViewModel(job);
            if (expanded.TryGetValue(job.Id, out var wasExpanded) && job.State is not CiState.Failure and not CiState.Running)
            {
                row.IsExpanded = wasExpanded;
            }

            Jobs.Add(row);
        }

        HasNoJobs = Jobs.Count == 0;
    }
}

/// <summary>
/// GitHub Actions of the linked repository: workflows on the left ("All workflows" first), runs of the
/// selection (current branch or all branches) on the right, and the selected run's jobs and steps with
/// Re-run, Re-run failed jobs, Cancel and Open on GitHub. While visible, the list refreshes every 15 s
/// as long as a run is queued or in progress.
/// </summary>
public sealed partial class ActionsViewModel : GitHubSubViewModel
{
    public const int MaxRuns = 50;

    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);

    private CancellationTokenSource? _load;
    private CancellationTokenSource? _jobs;
    private CancellationTokenSource? _autoRefresh;
    private long? _pendingRun;
    private bool _suppressReload;

    internal ActionsViewModel(GitHubSectionContext section)
        : base(section)
    {
        SelectedScope = BranchScopeOption.For(section.CurrentBranch is null ? RunBranchScope.All : RunBranchScope.CurrentBranch);
        Delay = (delay, token) => Task.Delay(delay, Section.Services.Time, token);
    }

    public override GitHubView View => GitHubView.Actions;

    /// <summary>Waits between automatic refreshes (replaced by tests).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; }

    public ObservableCollection<WorkflowFilterViewModel> Workflows { get; } = [];

    [ObservableProperty]
    public partial WorkflowFilterViewModel? SelectedWorkflow { get; set; }

    public IReadOnlyList<BranchScopeOption> ScopeOptions => BranchScopeOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunsCaption))]
    public partial BranchScopeOption SelectedScope { get; set; }

    public ObservableCollection<WorkflowRunRowViewModel> Runs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial WorkflowRunRowViewModel? SelectedRun { get; set; }

    public bool IsDetailOpen => SelectedRun is not null;

    [ObservableProperty]
    public partial RunDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The repository has no workflow at all.</summary>
    [ObservableProperty]
    public partial bool HasNoWorkflows { get; private set; }

    /// <summary>A run is queued or in progress: the list refreshes itself.</summary>
    [ObservableProperty]
    public partial bool IsAutoRefreshing { get; private set; }

    /// <summary>The branch used by the "Current branch" scope.</summary>
    public string? CurrentBranch => Section.CurrentBranch;

    /// <summary>"Runs of CI on main", "Runs of every workflow on all branches".</summary>
    public string RunsCaption
    {
        get
        {
            var workflow = SelectedWorkflow is { IsAll: false } w ? w.Name : "every workflow";
            var branch = EffectiveBranch is { } b ? $"on {b}" : "on all branches";
            return $"Runs of {workflow} {branch}";
        }
    }

    public string EmptyTitle => EffectiveBranch is { } branch ? $"No runs on {branch}" : "No workflow runs yet";

    public string EmptyDescription => EffectiveBranch is not null
        ? "No workflow ran on this branch yet. Push it, or look at the runs of every branch."
        : "Workflows run when their trigger happens (a push, a pull request, a schedule).";

    private string? EffectiveBranch => SelectedScope.Value == RunBranchScope.CurrentBranch ? CurrentBranch : null;

    public override async Task LoadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _load = cts;
        var token = cts.Token;
        IsLoading = Runs.Count == 0 && Workflows.Count == 0;
        try
        {
            var loaded = await RunAsync(async () =>
            {
                var repo = Section.RequireRepo();
                var workflows = await GitHub.GetWorkflowsAsync(repo, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ApplyWorkflows(workflows);
                var runs = await GitHub.GetWorkflowRunsAsync(repo, EffectiveBranch, SelectedWorkflow?.Id, MaxRuns, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ApplyRuns(runs);
            }, errorTitle: "Could not load workflow runs").ConfigureAwait(true);

            if (loaded && ReferenceEquals(_load, cts))
            {
                AfterRunsLoaded();
            }
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                IsLoading = false;
                UpdateEmptyStates();
            }
        }
    }

    [RelayCommand]
    private void CloseDetail() => SelectedRun = null;

    [RelayCommand]
    private Task RetryJobsAsync() => SelectedRun is { } run ? LoadJobsAsync(run) : Task.CompletedTask;

    [RelayCommand]
    private void ShowAllBranches() => SelectedScope = BranchScopeOption.For(RunBranchScope.All);

    [RelayCommand]
    private void OpenOnGitHub(WorkflowRunRowViewModel? run) => OpenUrl((run ?? SelectedRun)?.HtmlUrl);

    [RelayCommand]
    private void OpenJob(WorkflowJobViewModel? job) => OpenUrl(job?.HtmlUrl);

    [RelayCommand]
    private void CopyLink(WorkflowRunRowViewModel? run) => Copy((run ?? SelectedRun)?.HtmlUrl, "Link");

    [RelayCommand]
    private void OpenWorkflows() => OpenUrl(Section.Repo is { } repo ? repo.HtmlUrl + "/actions" : null);

    [RelayCommand]
    private Task RerunAsync(WorkflowRunRowViewModel? run) => RerunCoreAsync(run ?? SelectedRun, failedJobsOnly: false);

    [RelayCommand]
    private Task RerunFailedAsync(WorkflowRunRowViewModel? run) => RerunCoreAsync(run ?? SelectedRun, failedJobsOnly: true);

    /// <summary>Cancels a queued or running run after confirmation.</summary>
    [RelayCommand]
    private async Task CancelRunAsync(WorkflowRunRowViewModel? run)
    {
        run ??= SelectedRun;
        if (run is not { CanCancel: true } || Section.Repo is not { } repo)
        {
            return;
        }

        if (!await Section.Dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = $"Cancel {run.Name} {run.RunNumberText}?",
            Message = "The jobs still running stop where they are and the run is marked as cancelled. You can re-run it afterwards.",
            ConfirmText = "Cancel run",
            CancelText = "Keep running",
            IsDestructive = true,
        }).ConfigureAwait(true))
        {
            return;
        }

        await WithDetailWorkingAsync(run, () => RunActionAsync(async () =>
        {
            await GitHub.CancelWorkflowRunAsync(repo, run.Id, Section.Lifetime).ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.WorkflowCancelled, $"Cancelled {run.Name} {run.RunNumberText}", run.CommitMessage, run.HtmlUrl)
                .ConfigureAwait(true);
            Notify($"Cancelling {run.Name} {run.RunNumberText}", "GitHub stops the remaining jobs in a few seconds.", NotificationSeverity.Info);
            await RefreshNowAsync(repo).ConfigureAwait(true);
        }, "Could not cancel the run")).ConfigureAwait(true);
    }

    internal override void PrepareNavigation(GitHubNavigation navigation)
    {
        if (navigation.RunId is { } id)
        {
            _pendingRun = id;
            if (HasLoaded)
            {
                ApplyPendingRun();
            }
        }
    }

    protected override void OnReactivated()
    {
        if (Runs.Any(r => r.IsInProgress))
        {
            _ = RefreshQuietlyAsync();
        }

        UpdateAutoRefresh();
    }

    protected override void OnDeactivated() => StopAutoRefresh();

    protected override void OnReset()
    {
        _load?.Cancel();
        _jobs?.Cancel();
        StopAutoRefresh();
        _suppressReload = true;
        try
        {
            Workflows.Clear();
            Runs.Clear();
            SelectedWorkflow = null;
            SelectedRun = null;
        }
        finally
        {
            _suppressReload = false;
        }

        IsEmpty = false;
        HasNoWorkflows = false;
    }

    protected override void OnDisposed()
    {
        StopAutoRefresh();
        _load?.Cancel();
        _load?.Dispose();
        _jobs?.Cancel();
        _jobs?.Dispose();
    }

    /// <summary>The current branch changed (checkout): the "Current branch" scope lists other runs.</summary>
    internal void OnBranchChanged()
    {
        OnPropertyChanged(nameof(CurrentBranch));
        OnPropertyChanged(nameof(RunsCaption));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        if (SelectedScope.Value == RunBranchScope.CurrentBranch)
        {
            Invalidate();
        }
    }

    partial void OnSelectedWorkflowChanged(WorkflowFilterViewModel? value)
    {
        OnPropertyChanged(nameof(RunsCaption));
        ReloadForFilter();
    }

    partial void OnSelectedScopeChanged(BranchScopeOption value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        ReloadForFilter();
    }

    partial void OnSelectedRunChanged(WorkflowRunRowViewModel? value)
    {
        _jobs?.Cancel();
        if (value is null)
        {
            Detail = null;
            return;
        }

        if (!ReferenceEquals(Detail?.Run, value))
        {
            Detail = new RunDetailViewModel(value);
        }

        _ = LoadJobsAsync(value);
    }

    private void ReloadForFilter()
    {
        if (_suppressReload || !HasLoaded || IsDisposed)
        {
            return;
        }

        Runs.Clear();
        _ = LoadAsync();
    }

    private async Task RerunCoreAsync(WorkflowRunRowViewModel? run, bool failedJobsOnly)
    {
        if (run is null || Section.Repo is not { } repo || (failedJobsOnly ? !run.CanRerunFailed : !run.CanRerun))
        {
            return;
        }

        await WithDetailWorkingAsync(run, () => RunActionAsync(async () =>
        {
            await GitHub.RerunWorkflowAsync(repo, run.Id, failedJobsOnly, Section.Lifetime).ConfigureAwait(true);
            var what = failedJobsOnly ? "failed jobs of " : string.Empty;
            await Section.Activity.SucceededAsync(ActivityKind.WorkflowRerun, $"Re-ran {what}{run.Name} {run.RunNumberText}", run.CommitMessage, run.HtmlUrl)
                .ConfigureAwait(true);
            Notify($"Re-running {what}{run.Name}", $"{run.RunNumberText} is queued again on GitHub.");
            await RefreshNowAsync(repo).ConfigureAwait(true);
        }, failedJobsOnly ? "Could not re-run the failed jobs" : "Could not re-run the workflow")).ConfigureAwait(true);
    }

    private async Task WithDetailWorkingAsync(WorkflowRunRowViewModel run, Func<Task> work)
    {
        var detail = Detail is { } d && ReferenceEquals(d.Run, run) ? d : null;
        if (detail is not null)
        {
            detail.IsWorking = true;
        }

        try
        {
            await work().ConfigureAwait(true);
        }
        finally
        {
            if (detail is not null)
            {
                detail.IsWorking = false;
            }
        }
    }

    /// <summary>After an action, GitHub's state changed: bypass the cache and reload the runs (and the selected run's jobs).</summary>
    private async Task RefreshNowAsync(Core.Projects.GitHubRepoRef repo)
    {
        GitHub.InvalidateCache(repo);
        await LoadAsync().ConfigureAwait(true);
    }

    private void ApplyWorkflows(IReadOnlyList<WorkflowInfo> workflows)
    {
        var selectedId = SelectedWorkflow?.Id;
        var desired = new List<WorkflowFilterViewModel> { Workflows.FirstOrDefault(w => w.IsAll) ?? new WorkflowFilterViewModel(null, "All workflows", null, null) };
        var existing = Workflows.Where(w => !w.IsAll).ToDictionary(w => w.Id!.Value);
        foreach (var workflow in workflows.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var active = string.Equals(workflow.State, "active", StringComparison.OrdinalIgnoreCase);
            desired.Add(existing.TryGetValue(workflow.Id, out var current) && current.Name == workflow.Name && current.IsActive == active
                ? current
                : new WorkflowFilterViewModel(workflow.Id, workflow.Name, workflow.Path, workflow.HtmlUrl, active));
        }

        _suppressReload = true;
        try
        {
            Workflows.SyncWith(desired);
            SelectedWorkflow = Workflows.FirstOrDefault(w => w.Id == selectedId) ?? Workflows[0];
        }
        finally
        {
            _suppressReload = false;
        }

        HasNoWorkflows = workflows.Count == 0;
    }

    private void ApplyRuns(IReadOnlyList<WorkflowRunInfo> runs)
    {
        var existing = Runs.ToDictionary(r => r.Id);
        var desired = runs.Select(run =>
        {
            if (existing.TryGetValue(run.Id, out var row))
            {
                row.Update(run);
                return row;
            }

            return new WorkflowRunRowViewModel(run);
        }).ToList();
        Runs.SyncWith(desired);
    }

    private void AfterRunsLoaded()
    {
        var previous = SelectedRun;
        ApplyPendingRun();
        if (previous is not null && ReferenceEquals(SelectedRun, previous) && Runs.Contains(previous))
        {
            _ = LoadJobsAsync(previous);
        }

        UpdateAutoRefresh();
    }

    private void ApplyPendingRun()
    {
        if (_pendingRun is not { } id)
        {
            return;
        }

        var run = Runs.FirstOrDefault(r => r.Id == id);
        if (run is not null)
        {
            _pendingRun = null;
            SelectedRun = run;
        }
        else if (SelectedScope.Value != RunBranchScope.All || SelectedWorkflow is { IsAll: false })
        {
            // The run may be on another branch or of another workflow: look everywhere.
            _suppressReload = true;
            try
            {
                SelectedScope = BranchScopeOption.For(RunBranchScope.All);
                SelectedWorkflow = Workflows.FirstOrDefault(w => w.IsAll);
            }
            finally
            {
                _suppressReload = false;
            }

            Runs.Clear();
            _ = LoadAsync();
        }
        else
        {
            _pendingRun = null;
            Notify("Run not found", $"It isn't among the latest {MaxRuns} runs of {Section.Repo?.FullName}.", NotificationSeverity.Warning);
        }
    }

    private async Task LoadJobsAsync(WorkflowRunRowViewModel run)
    {
        _jobs?.Cancel();
        _jobs?.Dispose();
        _jobs = null;
        if (Detail is not { } detail || !ReferenceEquals(detail.Run, run) || Section.Repo is not { } repo || IsDisposed)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _jobs = cts;
        detail.JobsError = null;
        detail.IsLoadingJobs = detail.Jobs.Count == 0;
        try
        {
            var jobs = await GitHub.GetWorkflowJobsAsync(repo, run.Id, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                detail.SetJobs(jobs);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                detail.JobsError = ErrorInfo.From(ex, "Could not load the jobs of this run");
            }
        }
        finally
        {
            if (ReferenceEquals(_jobs, cts))
            {
                detail.IsLoadingJobs = false;
            }
        }
    }

    private void UpdateEmptyStates()
    {
        var ready = HasLoaded && !IsLoading && Error is null;
        IsEmpty = ready && Runs.Count == 0;
    }

    private bool ShouldAutoRefresh => IsActive && !IsDisposed && Runs.Any(r => r.IsInProgress);

    /// <summary>Starts the 15 s refresh loop while a run is in progress and the view is visible; stops it otherwise.</summary>
    private void UpdateAutoRefresh()
    {
        if (!ShouldAutoRefresh)
        {
            StopAutoRefresh();
            return;
        }

        if (_autoRefresh is not null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _autoRefresh = cts;
        IsAutoRefreshing = true;
        _ = AutoRefreshLoopAsync(cts);
    }

    private void StopAutoRefresh()
    {
        var cts = _autoRefresh;
        _autoRefresh = null;
        IsAutoRefreshing = false;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task AutoRefreshLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Delay(AutoRefreshInterval, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !ShouldAutoRefresh)
                {
                    break;
                }

                if (!await RefreshQuietlyAsync().ConfigureAwait(true) || !ShouldAutoRefresh)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        finally
        {
            if (ReferenceEquals(_autoRefresh, cts))
            {
                StopAutoRefresh();
            }
        }

        // The loop ended because no run is in progress any more (or the refresh failed): it starts
        // again when a refresh finds a queued or running run.
    }

    /// <summary>Reloads runs and the selected run's jobs without the loading and error states. Returns false on failure.</summary>
    private async Task<bool> RefreshQuietlyAsync()
    {
        if (Section.Repo is not { } repo || IsDisposed)
        {
            return false;
        }

        try
        {
            GitHub.InvalidateCache(repo);
            var token = Section.Lifetime;
            var runs = await GitHub.GetWorkflowRunsAsync(repo, EffectiveBranch, SelectedWorkflow?.Id, MaxRuns, token).ConfigureAwait(true);
            if (IsDisposed)
            {
                return false;
            }

            ApplyRuns(runs);
            UpdateEmptyStates();
            if (SelectedRun is { } selected && Runs.Contains(selected))
            {
                await LoadJobsAsync(selected).ConfigureAwait(true);
            }

            return true;
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            return false;
        }
        catch (Exception ex)
        {
            // Automatic refreshes stay quiet: the list keeps the last known state and F5 shows the error.
            System.Diagnostics.Trace.TraceWarning($"Automatic refresh of workflow runs failed: {ex.Message}");
            return false;
        }
    }
}
