using System.Globalization;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using OctokitNewRelease = Octokit.NewRelease;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// GitHub REST API access through Octokit. List calls and CI summaries go through a short TTL
/// cache (shared in-flight requests); every write invalidates the repository's cached data.
/// </summary>
internal sealed class GitHubService : IGitHubService, IDisposable
{
    internal const int MaxRepositories = 1000;
    internal static readonly TimeSpan ListTimeToLive = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CiTimeToLive = TimeSpan.FromSeconds(30);

    private const int PageSize = 100;
    private const int MaxListItems = 1000;
    private const int MaxIssuePages = 10;
    private const int CiSummaryRunWindow = 50;

    private readonly GitHubClientProvider _clients;
    private readonly GitHubSession _session;
    private readonly GitHubResponseCache _cache;
    private readonly ReleaseAssetUploader _uploader;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(
        GitHubClientProvider clients,
        GitHubSession session,
        GitHubResponseCache cache,
        ReleaseAssetUploader uploader,
        ILogger<GitHubService>? logger = null)
    {
        _clients = clients;
        _session = session;
        _cache = cache;
        _uploader = uploader;
        _logger = logger ?? NullLogger<GitHubService>.Instance;

        // Another account sees other repositories: never serve its predecessor's data.
        _session.Changed += OnSessionChanged;
    }

    public bool IsSignedIn => _session.Account is not null;

    public void InvalidateCache(GitHubRepoRef? repo = null)
    {
        if (repo is null)
        {
            _cache.Clear();
        }
        else
        {
            _cache.Invalidate(Scope(repo));
        }
    }

    public async Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn)
        {
            return null;
        }

        try
        {
            // /rate_limit doesn't count against the limit.
            var limits = await CallAsync("GitHub's rate limit", c => c.RateLimit.GetRateLimits(), cancellationToken).ConfigureAwait(false);
            var core = limits.Resources?.Core ?? limits.Rate;
            return core is null ? null : GitHubMapper.ToRateLimit(core);
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.NetworkUnavailable && _clients.GetLastApiInfo()?.RateLimit is { } last)
        {
            return GitHubMapper.ToRateLimit(last);
        }
    }

    public Task<GitHubRepository> GetRepositoryAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return CachedAsync(Scope(repo), "repository", ListTimeToLive, RepositorySubject(repo),
            async c => GitHubMapper.ToRepository(await c.Repository.Get(repo.Owner, repo.Name).ConfigureAwait(false)),
            cancellationToken);
    }

    public Task<IReadOnlyList<GitHubRepository>> GetMyRepositoriesAsync(CancellationToken cancellationToken = default) =>
        CachedAsync(GitHubResponseCache.AccountScope, "repositories", ListTimeToLive, "Your repository list",
            async c =>
            {
                var request = new RepositoryRequest
                {
                    Affiliation = RepositoryAffiliation.All,
                    Sort = RepositorySort.Pushed,
                    Direction = SortDirection.Descending,
                };
                var repositories = await c.Repository.GetAllForCurrent(request, Pages(MaxRepositories)).ConfigureAwait(false);
                return (IReadOnlyList<GitHubRepository>)repositories
                    .Take(MaxRepositories)
                    .Select(GitHubMapper.ToRepository)
                    .OrderByDescending(r => r.PushedAt ?? r.UpdatedAt ?? DateTimeOffset.MinValue)
                    .ToList();
            },
            cancellationToken);

    public Task<IReadOnlyList<string>> GetBranchesAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return CachedAsync(Scope(repo), "branches", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                var branches = await c.Repository.Branch.GetAll(repo.Owner, repo.Name, Pages(MaxListItems)).ConfigureAwait(false);
                return (IReadOnlyList<string>)branches.Select(b => b.Name).ToList();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<GitHubTag>> GetTagsAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return CachedAsync(Scope(repo), "tags", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                var tags = await c.Repository.GetAllTags(repo.Owner, repo.Name, Pages(500)).ConfigureAwait(false);
                return (IReadOnlyList<GitHubTag>)tags.Select(GitHubMapper.ToTag).ToList();
            },
            cancellationToken);
    }

    // ---------------------------------------------------------------- Issues

    public Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(GitHubRepoRef repo, IssueStateFilter state, int max = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var limit = ClampMax(max);
        return CachedAsync(Scope(repo), $"issues:{state}:{limit}", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                // The issues endpoint also returns pull requests; keep paging until enough real issues are found.
                var request = new RepositoryIssueRequest { State = ToItemState(state) };
                var issues = new List<GitHubIssue>(Math.Min(limit, PageSize));
                var maxPages = Math.Min(MaxIssuePages, (limit / PageSize) + 3);
                for (var page = 1; page <= maxPages && issues.Count < limit; page++)
                {
                    var options = new ApiOptions { PageSize = PageSize, PageCount = 1, StartPage = page };
                    var batch = await c.Issue.GetAllForRepository(repo.Owner, repo.Name, request, options).ConfigureAwait(false);
                    issues.AddRange(batch.Where(i => i.PullRequest is null).Select(GitHubMapper.ToIssue));
                    if (batch.Count < PageSize)
                    {
                        break;
                    }
                }

                return (IReadOnlyList<GitHubIssue>)issues.Take(limit).ToList();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<GitHubComment>> GetIssueCommentsAsync(GitHubRepoRef repo, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return CachedAsync(Scope(repo), $"comments:{number}", ListTimeToLive, IssueSubject(repo, number),
            async c =>
            {
                var comments = await c.Issue.Comment.GetAllForIssue(repo.Owner, repo.Name, number, Pages(500)).ConfigureAwait(false);
                return (IReadOnlyList<GitHubComment>)comments.Select(GitHubMapper.ToComment).ToList();
            },
            cancellationToken);
    }

    public async Task<GitHubIssue> CreateIssueAsync(GitHubRepoRef repo, string title, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var trimmedTitle = Require(title, "An issue needs a title.");
        var issue = await WriteAsync(repo, RepositorySubject(repo),
            c => c.Issue.Create(repo.Owner, repo.Name, new NewIssue(trimmedTitle) { Body = body ?? string.Empty }),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToIssue(issue);
    }

    public async Task<GitHubComment> AddIssueCommentAsync(GitHubRepoRef repo, int number, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var text = Require(body, "Write a comment before sending it.");
        var comment = await WriteAsync(repo, IssueSubject(repo, number),
            c => c.Issue.Comment.Create(repo.Owner, repo.Name, number, text),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToComment(comment);
    }

    public async Task<GitHubIssue> SetIssueOpenAsync(GitHubRepoRef repo, int number, bool open, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // Octokit's IssueUpdate always serializes "milestone": null, which would detach the
        // issue from its milestone; send only the state change.
        var changes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["state"] = open ? "open" : "closed",
        };
        if (!open)
        {
            changes["state_reason"] = "completed";
        }

        var uri = new Uri($"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/issues/{number.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative);
        var response = await WriteAsync(repo, IssueSubject(repo, number),
            c => c.Connection.Patch<Issue>(uri, changes),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToIssue(response.Body);
    }

    // ---------------------------------------------------------------- Pull requests

    public Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(GitHubRepoRef repo, IssueStateFilter state, int max = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var limit = ClampMax(max);
        return CachedAsync(Scope(repo), $"pulls:{state}:{limit}", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                var request = new PullRequestRequest { State = ToItemState(state) };
                var pulls = await c.PullRequest.GetAllForRepository(repo.Owner, repo.Name, request, Pages(limit)).ConfigureAwait(false);
                return (IReadOnlyList<GitHubPullRequest>)pulls.Take(limit).Select(p => GitHubMapper.ToPullRequest(p)).ToList();
            },
            cancellationToken);
    }

    public async Task<GitHubPullRequest> GetPullRequestAsync(GitHubRepoRef repo, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var pr = await CallAsync(PullRequestSubject(repo, number), c => c.PullRequest.Get(repo.Owner, repo.Name, number), cancellationToken).ConfigureAwait(false);
        var checks = string.IsNullOrEmpty(pr.Head?.Sha)
            ? CiState.Unknown
            : await GetCombinedChecksAsync(repo, pr.Head.Sha, cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToPullRequest(pr, checks);
    }

    public async Task<GitHubPullRequest> CreatePullRequestAsync(GitHubRepoRef repo, string title, string body, string head, string baseBranch, bool draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var trimmedTitle = Require(title, "A pull request needs a title.");
        var headRef = Require(head, "Choose the branch that contains your changes.");
        var baseRef = Require(baseBranch, "Choose the branch to merge into.");
        if (string.Equals(headRef, baseRef, StringComparison.Ordinal))
        {
            throw ForgeException.InvalidInput("A pull request needs two different branches.");
        }

        var pr = await WriteAsync(repo, RepositorySubject(repo),
            c => c.PullRequest.Create(repo.Owner, repo.Name, new NewPullRequest(trimmedTitle, headRef, baseRef) { Body = body ?? string.Empty, Draft = draft }),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToPullRequest(pr);
    }

    // ---------------------------------------------------------------- Actions

    public Task<IReadOnlyList<WorkflowInfo>> GetWorkflowsAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return CachedAsync(Scope(repo), "workflows", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                var response = await c.Actions.Workflows.List(repo.Owner, repo.Name, Pages(PageSize)).ConfigureAwait(false);
                return (IReadOnlyList<WorkflowInfo>)(response.Workflows ?? []).Select(GitHubMapper.ToWorkflow).ToList();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowRunInfo>> GetWorkflowRunsAsync(GitHubRepoRef repo, string? branch = null, long? workflowId = null, int max = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var limit = ClampMax(max);
        var key = $"runs:{branch}:{workflowId?.ToString(CultureInfo.InvariantCulture)}:{limit}";
        return CachedAsync(Scope(repo), key, CiTimeToLive, RepositorySubject(repo),
            async c => (IReadOnlyList<WorkflowRunInfo>)(await ListRunsAsync(c, repo, branch, workflowId, limit).ConfigureAwait(false))
                .Take(limit)
                .Select(GitHubMapper.ToWorkflowRun)
                .ToList(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowJobInfo>> GetWorkflowJobsAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // Not cached: jobs of a running workflow change every few seconds.
        var response = await CallAsync(RunSubject(repo, runId),
            c => c.Actions.Workflows.Jobs.List(repo.Owner, repo.Name, runId, new WorkflowRunJobsRequest(), Pages(300)),
            cancellationToken).ConfigureAwait(false);
        return (response.Jobs ?? []).Select(GitHubMapper.ToWorkflowJob).ToList();
    }

    public Task RerunWorkflowAsync(GitHubRepoRef repo, long runId, bool failedJobsOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(repo, RunSubject(repo, runId),
            async c =>
            {
                if (failedJobsOnly)
                {
                    await c.Actions.Workflows.Runs.RerunFailedJobs(repo.Owner, repo.Name, runId).ConfigureAwait(false);
                }
                else
                {
                    await c.Actions.Workflows.Runs.Rerun(repo.Owner, repo.Name, runId).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    public Task CancelWorkflowRunAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(repo, RunSubject(repo, runId),
            async c =>
            {
                await c.Actions.Workflows.Runs.Cancel(repo.Owner, repo.Name, runId).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<CiSummary> GetCiSummaryAsync(GitHubRepoRef repo, string? branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        EnsureSignedIn();
        return _cache.GetOrAddAsync(Scope(repo), $"ci:{branch ?? "\0default"}", CiTimeToLive, async () =>
        {
            var resolvedBranch = string.IsNullOrWhiteSpace(branch)
                ? (await GetRepositoryAsync(repo, CancellationToken.None).ConfigureAwait(false)).DefaultBranch
                : branch;
            var runs = await CallAsync(RepositorySubject(repo),
                c => ListRunsAsync(c, repo, resolvedBranch, workflowId: null, CiSummaryRunWindow),
                CancellationToken.None).ConfigureAwait(false);
            return CiStates.Summarize(resolvedBranch, runs.Select(GitHubMapper.ToWorkflowRun));
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- Releases

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(GitHubRepoRef repo, int max = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var limit = ClampMax(max);
        return CachedAsync(Scope(repo), $"releases:{limit}", ListTimeToLive, RepositorySubject(repo),
            async c =>
            {
                var releases = await c.Repository.Release.GetAll(repo.Owner, repo.Name, Pages(limit)).ConfigureAwait(false);
                return (IReadOnlyList<GitHubRelease>)releases.Take(limit).Select(GitHubMapper.ToRelease).ToList();
            },
            cancellationToken);
    }

    public async Task<GitHubRelease> CreateReleaseAsync(GitHubRepoRef repo, NewRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(release);
        var tag = Require(release.TagName, "A release needs a tag name (for example v1.2.0).");
        var data = new OctokitNewRelease(tag)
        {
            Name = string.IsNullOrWhiteSpace(release.Name) ? tag : release.Name.Trim(),
            Body = release.Body ?? string.Empty,
            Draft = release.Draft,
            Prerelease = release.Prerelease,
            TargetCommitish = string.IsNullOrWhiteSpace(release.TargetCommitish) ? null : release.TargetCommitish.Trim(),

            // GitHub never marks a prerelease as latest.
            MakeLatest = release.MakeLatest && !release.Prerelease ? MakeLatestQualifier.True : MakeLatestQualifier.False,
        };

        var created = await WriteAsync(repo, RepositorySubject(repo),
            c => c.Repository.Release.Create(repo.Owner, repo.Name, data),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToRelease(created);
    }

    public async Task<GitHubRelease> PublishReleaseAsync(GitHubRepoRef repo, long releaseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var published = await WriteAsync(repo, ReleaseSubject(repo),
            c => c.Repository.Release.Edit(repo.Owner, repo.Name, releaseId, new ReleaseUpdate { Draft = false }),
            cancellationToken).ConfigureAwait(false);
        return GitHubMapper.ToRelease(published);
    }

    public Task DeleteReleaseAsync(GitHubRepoRef repo, long releaseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(repo, ReleaseSubject(repo),
            async c =>
            {
                await c.Repository.Release.Delete(repo.Owner, repo.Name, releaseId).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<GitHubReleaseAsset> UploadReleaseAssetAsync(GitHubRepoRef repo, long releaseId, string filePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ReleaseAssetUploader.ValidateFile(filePath);
        var subject = ReleaseSubject(repo);
        var release = await CallAsync(subject, c => c.Repository.Release.Get(repo.Owner, repo.Name, releaseId), cancellationToken).ConfigureAwait(false);
        var (_, token) = _clients.GetClientAndToken();
        try
        {
            return await _uploader.UploadAsync(release.UploadUrl, token, filePath, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GitHubErrorTranslator.TryTranslate(ex, subject, cancellationToken, out var translated) && !ReferenceEquals(ex, translated))
        {
            throw translated;
        }
        finally
        {
            _cache.Invalidate(Scope(repo));
        }
    }

    public async Task<string> GenerateReleaseNotesAsync(GitHubRepoRef repo, string tagName, string? previousTag, string? target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var tag = Require(tagName, "Choose the tag of the release first.");
        var request = new GenerateReleaseNotesRequest(tag)
        {
            PreviousTagName = string.IsNullOrWhiteSpace(previousTag) ? null : previousTag.Trim(),
            TargetCommitish = string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
        };
        var notes = await CallAsync(RepositorySubject(repo),
            c => c.Repository.Release.GenerateReleaseNotes(repo.Owner, repo.Name, request),
            cancellationToken).ConfigureAwait(false);
        return notes.Body ?? string.Empty;
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;

    // ---------------------------------------------------------------- Plumbing

    private void OnSessionChanged(object? sender, EventArgs e) => _cache.Clear();

    private void EnsureSignedIn() => _clients.GetClient();

    /// <summary>Runs an API call, translating failures; caller cancellation propagates as is.</summary>
    private async Task<T> CallAsync<T>(string subject, Func<GitHubClient, Task<T>> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _clients.GetClient();
        try
        {
            return await call(client).WaitOrAbandonAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GitHubErrorTranslator.TryTranslate(ex, subject, cancellationToken, out var translated) && !ReferenceEquals(ex, translated))
        {
            _logger.LogDebug(ex, "GitHub call failed: {Subject}", subject);
            throw translated;
        }
    }

    private Task<T> CachedAsync<T>(string scope, string key, TimeSpan timeToLive, string subject, Func<GitHubClient, Task<T>> call, CancellationToken cancellationToken)
    {
        EnsureSignedIn();

        // The shared request must not be cancelled by whichever caller started it.
        return _cache.GetOrAddAsync(scope, key, timeToLive, () => CallAsync(subject, call, CancellationToken.None), cancellationToken);
    }

    private async Task<T> WriteAsync<T>(GitHubRepoRef repo, string subject, Func<GitHubClient, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(subject, call, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also after a failure or cancellation: the write may have reached GitHub.
            _cache.Invalidate(Scope(repo));
        }
    }

    private async Task<CiState> GetCombinedChecksAsync(GitHubRepoRef repo, string sha, CancellationToken cancellationToken)
    {
        var client = _clients.GetClient();
        var checkRuns = TryGetAsync(() => client.Check.Run.GetAllForReference(repo.Owner, repo.Name, sha, new CheckRunRequest(), Pages(PageSize)));
        var statuses = TryGetAsync(() => client.Repository.Status.GetCombined(repo.Owner, repo.Name, sha));
        await Task.WhenAll(checkRuns, statuses).WaitAsync(cancellationToken).ConfigureAwait(false);

        var runs = await checkRuns.ConfigureAwait(false);
        var combined = await statuses.ConfigureAwait(false);
        if (runs is null && combined is null)
        {
            return CiState.Unknown;
        }

        var states = (runs?.CheckRuns ?? [])
            .Select(r => CiStates.FromStatus(r.Status.StringValue, r.Conclusion?.StringValue))
            .Concat((combined?.Statuses ?? []).Select(s => CiStates.FromCommitStatus(s.State.StringValue)));
        return CiStates.CombineChecks(states);
    }

    /// <summary>
    /// Checks are secondary information on a pull request: a token without the checks
    /// permission (fine-grained) or a transient failure leaves them Unknown instead of failing.
    /// </summary>
    private async Task<T?> TryGetAsync<T>(Func<Task<T>> call)
        where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException || GitHubErrorTranslator.TryTranslate(ex, null, CancellationToken.None, out _))
        {
            _logger.LogDebug(ex, "Could not read checks from GitHub");
            return null;
        }
    }

    private static async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(GitHubClient client, GitHubRepoRef repo, string? branch, long? workflowId, int max)
    {
        var request = new WorkflowRunsRequest { ExcludePullRequests = true };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            request.Branch = branch;
        }

        var options = Pages(max);
        var response = workflowId is { } id
            ? await client.Actions.Workflows.Runs.ListByWorkflow(repo.Owner, repo.Name, id, request, options).ConfigureAwait(false)
            : await client.Actions.Workflows.Runs.List(repo.Owner, repo.Name, request, options).ConfigureAwait(false);
        return response.WorkflowRuns ?? [];
    }

    private static ApiOptions Pages(int max)
    {
        var size = Math.Clamp(max, 1, PageSize);
        return new ApiOptions { PageSize = size, PageCount = (int)Math.Ceiling(Math.Max(1, max) / (double)size) };
    }

    private static int ClampMax(int max) => Math.Clamp(max, 1, MaxListItems);

    private static ItemStateFilter ToItemState(IssueStateFilter state) => state switch
    {
        IssueStateFilter.Closed => ItemStateFilter.Closed,
        IssueStateFilter.All => ItemStateFilter.All,
        _ => ItemStateFilter.Open,
    };

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw ForgeException.InvalidInput(message) : value.Trim();

    private static string Scope(GitHubRepoRef repo) => repo.FullName;

    private static string RepositorySubject(GitHubRepoRef repo) => $"The repository '{repo.FullName}'";

    private static string IssueSubject(GitHubRepoRef repo, int number) => $"Issue #{number} in '{repo.FullName}'";

    private static string PullRequestSubject(GitHubRepoRef repo, int number) => $"Pull request #{number} in '{repo.FullName}'";

    private static string RunSubject(GitHubRepoRef repo, long runId) => $"Workflow run {runId} in '{repo.FullName}'";

    private static string ReleaseSubject(GitHubRepoRef repo) => $"This release of '{repo.FullName}'";
}
