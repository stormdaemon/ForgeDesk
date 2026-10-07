using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Sign-in state and credentials. The token is stored in the OS credential vault via
/// <see cref="Security.ISecretStore"/>, never in the database or logs.
/// </summary>
public interface IGitHubAccountService
{
    event EventHandler<GitHubAccount?>? AccountChanged;

    GitHubAccount? Current { get; }

    /// <summary>Restores the saved session (validating the token); null when signed out.</summary>
    Task<GitHubAccount?> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores a personal access token.</summary>
    Task<GitHubAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Git Credential Manager for github.com credentials (it opens the browser sign-in
    /// if needed) and uses the resulting OAuth token.
    /// </summary>
    Task<GitHubAccount> SignInWithGitCredentialManagerAsync(CancellationToken cancellationToken = default);

    /// <summary>Reuses the token of an authenticated GitHub CLI (<c>gh auth token</c>).</summary>
    Task<GitHubAccount> SignInWithGitHubCliAsync(CancellationToken cancellationToken = default);

    Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>The raw token for git HTTPS operations, or null when signed out.</summary>
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read/write access to the GitHub REST API for the signed-in account. Throws
/// ForgeException (AuthenticationRequired, NetworkUnavailable, RateLimited, NotFound…).
/// </summary>
public interface IGitHubService
{
    bool IsSignedIn { get; }

    /// <summary>
    /// Drops cached responses (list calls and CI summaries are cached for up to a minute to
    /// protect the rate limit) for one repository, or everything when <paramref name="repo"/>
    /// is null. Call it for an explicit user "Refresh".
    /// </summary>
    void InvalidateCache(GitHubRepoRef? repo = null)
    {
    }

    Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken cancellationToken = default);

    Task<GitHubRepository> GetRepositoryAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default);

    /// <summary>Repositories the user owns, collaborates on or can access through organizations.</summary>
    Task<IReadOnlyList<GitHubRepository>> GetMyRepositoriesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetBranchesAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubTag>> GetTagsAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default);

    // Issues
    Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(GitHubRepoRef repo, IssueStateFilter state, int max = 100, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubComment>> GetIssueCommentsAsync(GitHubRepoRef repo, int number, CancellationToken cancellationToken = default);

    Task<GitHubIssue> CreateIssueAsync(GitHubRepoRef repo, string title, string body, CancellationToken cancellationToken = default);

    Task<GitHubComment> AddIssueCommentAsync(GitHubRepoRef repo, int number, string body, CancellationToken cancellationToken = default);

    Task<GitHubIssue> SetIssueOpenAsync(GitHubRepoRef repo, int number, bool open, CancellationToken cancellationToken = default);

    // Pull requests
    Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(GitHubRepoRef repo, IssueStateFilter state, int max = 100, CancellationToken cancellationToken = default);

    /// <summary>Full PR details including diff stats and combined check state.</summary>
    Task<GitHubPullRequest> GetPullRequestAsync(GitHubRepoRef repo, int number, CancellationToken cancellationToken = default);

    Task<GitHubPullRequest> CreatePullRequestAsync(GitHubRepoRef repo, string title, string body, string head, string baseBranch, bool draft, CancellationToken cancellationToken = default);

    // Actions
    Task<IReadOnlyList<WorkflowInfo>> GetWorkflowsAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRunInfo>> GetWorkflowRunsAsync(GitHubRepoRef repo, string? branch = null, long? workflowId = null, int max = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowJobInfo>> GetWorkflowJobsAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default);

    Task RerunWorkflowAsync(GitHubRepoRef repo, long runId, bool failedJobsOnly, CancellationToken cancellationToken = default);

    Task CancelWorkflowRunAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default);

    Task<CiSummary> GetCiSummaryAsync(GitHubRepoRef repo, string? branch, CancellationToken cancellationToken = default);

    // Releases
    Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(GitHubRepoRef repo, int max = 50, CancellationToken cancellationToken = default);

    Task<GitHubRelease> CreateReleaseAsync(GitHubRepoRef repo, NewRelease release, CancellationToken cancellationToken = default);

    Task<GitHubRelease> PublishReleaseAsync(GitHubRepoRef repo, long releaseId, CancellationToken cancellationToken = default);

    /// <summary>Edits a release's metadata (title, notes, target, prerelease) without changing its draft state.</summary>
    Task<GitHubRelease> UpdateReleaseAsync(GitHubRepoRef repo, long releaseId, ReleaseChanges changes, CancellationToken cancellationToken = default);

    Task DeleteReleaseAssetAsync(GitHubRepoRef repo, long assetId, CancellationToken cancellationToken = default);

    Task DeleteReleaseAsync(GitHubRepoRef repo, long releaseId, CancellationToken cancellationToken = default);

    Task<GitHubReleaseAsset> UploadReleaseAssetAsync(GitHubRepoRef repo, long releaseId, string filePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Asks GitHub to generate release notes between the previous tag and <paramref name="tagName"/>.</summary>
    Task<string> GenerateReleaseNotesAsync(GitHubRepoRef repo, string tagName, string? previousTag, string? target, CancellationToken cancellationToken = default);
}
