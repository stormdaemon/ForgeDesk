using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>CI health of one branch: overall state and the latest run of each workflow.</summary>
public sealed class CiCardViewModel
{
    public CiCardViewModel(string title, CiSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Title = title;
        Branch = summary.Branch;
        State = summary.State;
        UpdatedAt = summary.UpdatedAt;
        Message = summary.Message;
        Runs = summary.LatestRuns.Select(r => new WorkflowRunRowViewModel(r)).ToList();
    }

    /// <summary>"Current branch" or "Default branch".</summary>
    public string Title { get; }

    public string? Branch { get; }

    public CiState State { get; }

    public DateTimeOffset? UpdatedAt { get; }

    public string? Message { get; }

    public IReadOnlyList<WorkflowRunRowViewModel> Runs { get; }

    public bool HasRuns => Runs.Count > 0;

    public string Headline => State switch
    {
        CiState.Success => "All checks passing",
        CiState.Failure => Runs.Count(r => r.IsFailed) is var failed and > 1 ? $"{failed} workflows failing" : "A workflow is failing",
        CiState.Running => "Checks running",
        CiState.Queued => "Checks queued",
        CiState.Cancelled => "Latest run cancelled",
        CiState.None => "No workflow runs",
        _ => "CI status unavailable",
    };

    public string Description => Message
        ?? (State == CiState.None
            ? $"No GitHub Actions workflow ran on {Branch ?? "this branch"} yet."
            : $"Latest run of each workflow on {Branch ?? "this branch"}.");
}

/// <summary>
/// Overview of the GitHub tab: the repository card, CI health of the current and default branches,
/// counts of open pull requests and issues, and the remaining API rate limit.
/// </summary>
public sealed partial class GitHubOverviewViewModel : GitHubSubViewModel
{
    /// <summary>How many open pull requests / issues are counted before showing "100+".</summary>
    public const int CountCap = 100;

    private CancellationTokenSource? _load;

    internal GitHubOverviewViewModel(GitHubSectionContext section)
        : base(section)
    {
    }

    public override GitHubView View => GitHubView.Overview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(VisibilityText), nameof(Description), nameof(HasDescription))]
    public partial GitHubRepository? Repository { get; private set; }

    public bool HasRepository => Repository is not null;

    public string VisibilityText => Repository?.IsPrivate == true ? "Private" : "Public";

    public string Description => Repository?.Description is { Length: > 0 } description ? description : "No description provided.";

    public bool HasDescription => Repository?.Description is { Length: > 0 };

    [ObservableProperty]
    public partial CiCardViewModel? CurrentBranchCi { get; private set; }

    /// <summary>CI of the default branch; null when the current branch is the default branch (one card is enough).</summary>
    [ObservableProperty]
    public partial CiCardViewModel? DefaultBranchCi { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenPullRequestsText))]
    public partial int? OpenPullRequests { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenIssuesText))]
    public partial int? OpenIssues { get; private set; }

    public string OpenPullRequestsText => OpenPullRequests is { } count ? GitHubText.CappedCount(count, CountCap) : "—";

    public string OpenIssuesText => OpenIssues is { } count ? GitHubText.CappedCount(count, CountCap) : "—";

    /// <summary>"4,812 of 5,000 API requests left · resets at 14:05", or null when unknown.</summary>
    [ObservableProperty]
    public partial string? RateLimitText { get; private set; }

    [ObservableProperty]
    public partial bool IsRateLimitLow { get; private set; }

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
        IsLoading = Repository is null;
        try
        {
            await RunAsync(async () =>
            {
                var repo = Section.RequireRepo();
                var repositoryTask = GitHub.GetRepositoryAsync(repo, token);
                var pullsTask = GitHub.GetPullRequestsAsync(repo, IssueStateFilter.Open, CountCap, token);
                var issuesTask = GitHub.GetIssuesAsync(repo, IssueStateFilter.Open, CountCap, token);
                var rateTask = SafeRateLimitAsync(token);

                var repository = await repositoryTask.ConfigureAwait(true);
                var current = Section.CurrentBranch;
                var defaultBranch = repository.DefaultBranch;
                var currentCiTask = GitHub.GetCiSummaryAsync(repo, current ?? defaultBranch, token);
                var defaultCiTask = current is not null && defaultBranch is not null && !string.Equals(current, defaultBranch, StringComparison.Ordinal)
                    ? OptionalCiAsync(repo, defaultBranch, token)
                    : Task.FromResult<CiSummary?>(null);

                await Task.WhenAll(pullsTask, issuesTask, currentCiTask, defaultCiTask).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                Repository = repository;
                OpenPullRequests = pullsTask.Result.Count;
                OpenIssues = issuesTask.Result.Count;
                CurrentBranchCi = new CiCardViewModel(current is null || current == defaultBranch ? "Default branch" : "Current branch", currentCiTask.Result);
                DefaultBranchCi = defaultCiTask.Result is { } defaultCi ? new CiCardViewModel("Default branch", defaultCi) : null;
                ApplyRateLimit(await rateTask.ConfigureAwait(true));
            }, errorTitle: "Could not load the repository").ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private void OpenRepository() => OpenUrl(Repository?.HtmlUrl ?? Section.Repo?.HtmlUrl);

    [RelayCommand]
    private void OpenRun(WorkflowRunRowViewModel? run)
    {
        if (run is not null)
        {
            _ = Section.Navigate(GitHubNavigation.Run(run.Id));
        }
    }

    [RelayCommand]
    private Task ShowPullRequestsAsync() => Section.Navigate(GitHubNavigation.PullRequests());

    [RelayCommand]
    private Task ShowIssuesAsync() => Section.Navigate(GitHubNavigation.Issues());

    [RelayCommand]
    private Task ShowActionsAsync() => Section.Navigate(GitHubNavigation.Actions());

    protected override void OnReset()
    {
        _load?.Cancel();
        Repository = null;
        CurrentBranchCi = null;
        DefaultBranchCi = null;
        OpenPullRequests = null;
        OpenIssues = null;
        RateLimitText = null;
    }

    protected override void OnDisposed()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
    }

    private async Task<CiSummary?> OptionalCiAsync(Core.Projects.GitHubRepoRef repo, string branch, CancellationToken token) =>
        await GitHub.GetCiSummaryAsync(repo, branch, token).ConfigureAwait(true);

    private async Task<RateLimitInfo?> SafeRateLimitAsync(CancellationToken token)
    {
        try
        {
            return await GitHub.GetRateLimitAsync(token).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // The rate limit is a footnote: never fail the overview because of it.
            return null;
        }
    }

    private void ApplyRateLimit(RateLimitInfo? rate)
    {
        if (rate is null)
        {
            RateLimitText = null;
            IsRateLimitLow = false;
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var reset = rate.ResetsAt.ToLocalTime().ToString("t", culture);
        RateLimitText = $"{rate.Remaining.ToString("N0", culture)} of {rate.Limit.ToString("N0", culture)} API requests left · resets at {reset}";
        IsRateLimitLow = rate.Limit > 0 && rate.Remaining < rate.Limit / 10;
    }
}
