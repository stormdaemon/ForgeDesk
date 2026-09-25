namespace ForgeDesk.Core.Files;

public interface IFileService
{
    /// <summary>Lists one directory level, directories first. Throws PathNotFound/PermissionDenied.</summary>
    Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string projectRoot, string relativeDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a file for display. Text beyond <paramref name="maxTextBytes"/> is truncated
    /// (IsTruncated = true); binary files are detected and not decoded.
    /// </summary>
    Task<FileContent> ReadAsync(string projectRoot, string relativePath, long maxTextBytes = 4 * 1024 * 1024, CancellationToken cancellationToken = default);

    /// <summary>Is this a well-known heavy folder name (node_modules, .git, bin, obj, target, dist…)?</summary>
    bool IsHeavyFolderName(string name);
}

/// <summary>Fast file-name index per project (git ls-files when possible, bounded walk otherwise).</summary>
public interface IFileIndex
{
    Task<FileIndexSnapshot> GetAsync(string projectRoot, bool forceRefresh = false, CancellationToken cancellationToken = default);

    void Invalidate(string projectRoot);

    /// <summary>Fuzzy matches file paths (fzf-like scoring favouring file-name and consecutive matches).</summary>
    IReadOnlyList<FileMatch> Search(FileIndexSnapshot snapshot, string query, int max = 100);
}

public interface IContentSearchService
{
    /// <summary>
    /// Streams matches as they are found (git grep in repositories, managed search otherwise).
    /// Honors cancellation promptly.
    /// </summary>
    Task<ContentSearchSummary> SearchAsync(string projectRoot, ContentSearchQuery query, Action<ContentMatch> onMatch, CancellationToken cancellationToken = default);
}

/// <summary>Watches open projects for file changes (debounced) to refresh git status and trees.</summary>
public interface IProjectWatcher : IDisposable
{
    /// <summary>Raised (debounced) with the project root whose files changed. Changes under .git only are flagged.</summary>
    event EventHandler<ProjectFilesChangedEventArgs>? Changed;

    void Watch(string projectRoot);

    void Unwatch(string projectRoot);
}

public sealed record ProjectFilesChangedEventArgs(string ProjectRoot, bool GitMetadataChanged, bool WorkingTreeChanged);
