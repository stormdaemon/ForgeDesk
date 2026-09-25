namespace ForgeDesk.Core.Git;

/// <summary>
/// Git operations for a repository, implemented on top of the git CLI. Every method throws
/// <see cref="Common.ForgeException"/> with a translated, user-readable message on failure
/// (GitNotFound, NotARepository, NonFastForward, MergeConflict, AuthenticationFailed…).
/// Paths are repository-relative with forward slashes.
/// </summary>
public interface IGitService
{
    // Environment
    Task<GitInstallation?> FindGitAsync(CancellationToken cancellationToken = default);

    Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Top-level directory of the repository containing <paramref name="path"/>, or null.</summary>
    Task<string?> GetRepositoryRootAsync(string path, CancellationToken cancellationToken = default);

    Task InitAsync(string path, CancellationToken cancellationToken = default);

    Task CloneAsync(string url, string targetDirectory, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<GitIdentity> GetIdentityAsync(string repoPath, CancellationToken cancellationToken = default);

    Task SetGlobalIdentityAsync(string name, string email, CancellationToken cancellationToken = default);

    // Working tree
    Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<FileDiff> GetFileDiffAsync(string repoPath, string path, DiffTarget target, CancellationToken cancellationToken = default);

    Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task StageAllAsync(string repoPath, CancellationToken cancellationToken = default);

    Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Stages (or unstages when <paramref name="reverse"/>) a single hunk of a file diff.</summary>
    Task ApplyHunkAsync(string repoPath, FileDiff diff, DiffHunk hunk, bool reverse, CancellationToken cancellationToken = default);

    /// <summary>Discards working-tree changes (restores tracked files, deletes untracked ones).</summary>
    Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<GitCommit> CommitAsync(string repoPath, GitCommitOptions options, CancellationToken cancellationToken = default);

    // History
    Task<IReadOnlyList<GitCommit>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken cancellationToken = default);

    Task<GitCommitDetails> GetCommitDetailsAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    Task<FileDiff> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default);

    Task<int> CountCommitsAsync(string repoPath, string? revisionRange = null, DateTimeOffset? since = null, CancellationToken cancellationToken = default);

    // Branches
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, bool includeRemote = true, CancellationToken cancellationToken = default);

    Task CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    /// <summary>Checks out a remote branch as a new local tracking branch.</summary>
    Task CheckoutRemoteBranchAsync(string repoPath, string remoteBranch, string? localName = null, CancellationToken cancellationToken = default);

    Task CreateBranchAsync(string repoPath, string name, string? startPoint = null, bool checkout = true, CancellationToken cancellationToken = default);

    Task DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default);

    Task RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default);

    Task MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    Task AbortMergeAsync(string repoPath, CancellationToken cancellationToken = default);

    // Remote sync
    Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task FetchAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default);

    Task PullAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default);

    Task PushAsync(string repoPath, GitPushOptions options, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default);

    // Tags
    Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default);

    Task CreateTagAsync(string repoPath, string name, string? message = null, string? target = null, CancellationToken cancellationToken = default);

    Task PushTagAsync(string repoPath, string tagName, string? remote = null, CancellationToken cancellationToken = default);

    Task DeleteTagAsync(string repoPath, string name, CancellationToken cancellationToken = default);

    // Stash
    Task<IReadOnlyList<GitStash>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task StashAsync(string repoPath, string? message = null, bool includeUntracked = true, CancellationToken cancellationToken = default);

    Task StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default);

    Task StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default);

    // Misc
    /// <summary>All files known to git (tracked + untracked not ignored), relative paths.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken cancellationToken = default);
}
