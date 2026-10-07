using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ForgeDesk.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Files;

/// <summary>
/// Per-project list of files for "Go to file". In git repositories it comes from
/// <c>git ls-files</c> (tracked + untracked, minus ignored and deleted files — fast, and
/// .gitignore-aware); elsewhere from a bounded walk that skips heavy folders. Snapshots are
/// cached per root until invalidated (the watcher does that when files change).
/// </summary>
internal sealed class FileIndex : IFileIndex
{
    public const int MaxFiles = 300_000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);

    private readonly GitCli _git;
    private readonly IClock _clock;
    private readonly ILogger<FileIndex> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<FileIndexSnapshot>>> _cache = new(PathUtil.Comparer);
    private readonly ConditionalWeakTable<FileIndexSnapshot, FuzzyMatcher> _matchers = new();

    public FileIndex(GitCli git, IClock? clock = null, ILogger<FileIndex>? logger = null)
    {
        _git = git;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<FileIndex>.Instance;
    }

    /// <summary>Lets tests exercise the managed walker inside repositories.</summary>
    internal bool UseGit { get; init; } = true;

    public async Task<FileIndexSnapshot> GetAsync(string projectRoot, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = PathUtil.Normalize(projectRoot);
        if (forceRefresh)
        {
            _cache.TryRemove(root, out _);
        }

        // Task.Run: building starts processes and walks folders synchronously; callers are often on the UI thread.
        var entry = _cache.GetOrAdd(root, static (key, self) => new Lazy<Task<FileIndexSnapshot>>(() => Task.Run(() => self.BuildAsync(key))), this);
        try
        {
            // The build is shared by every caller: one caller giving up must not cancel it for the others.
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _cache.TryRemove(KeyValuePair.Create(root, entry));
            throw;
        }
    }

    public void Invalidate(string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            _cache.TryRemove(PathUtil.Normalize(projectRoot), out _);
        }
    }

    public IReadOnlyList<FileMatch> Search(FileIndexSnapshot snapshot, string query, int max = 100)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var matcher = _matchers.GetValue(snapshot, static s => FuzzyMatcher.Create(s.Files));
        return matcher.Search(query, max);
    }

    private async Task<FileIndexSnapshot> BuildAsync(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{root}' does not exist.",
                "The project may have been moved or deleted.");
        }

        if (UseGit && GitCli.IsInsideRepository(root))
        {
            var listed = await TryListWithGitAsync(root).ConfigureAwait(false);
            if (listed is { } result)
            {
                return CreateSnapshot(root, result.Files, result.Truncated);
            }
        }

        var walked = Walk(root);
        return CreateSnapshot(root, walked.Files, walked.Truncated);
    }

    private FileIndexSnapshot CreateSnapshot(string root, List<string> files, bool truncated)
    {
        files.Sort(StringComparer.Ordinal);
        var snapshot = new FileIndexSnapshot(root, files, truncated, _clock.Now);

        // Prepare the matcher now, off the UI thread, rather than on the first keystroke.
        _matchers.GetValue(snapshot, static s => FuzzyMatcher.Create(s.Files));
        return snapshot;
    }

    private async Task<(List<string> Files, bool Truncated)?> TryListWithGitAsync(string root)
    {
        // Untracked, non-ignored files in heavy folders mean a missing .gitignore entry (node_modules…):
        // leave them out at the source. Tracked files are always the user's own content, even inside a
        // folder named "build" or "packages", so that listing is not filtered.
        var magic = OperatingSystem.IsWindows() ? "exclude,glob,icase" : "exclude,glob";
        string[] untrackedArguments = ["ls-files", "--others", "--exclude-standard", "--", .. HeavyFolders.Names.Select(n => $":({magic})**/{n}/**")];
        // -t --stage: the tag flags skip-worktree entries (S) and the mode flags submodules (160000).
        var tracked = ListGitPathsAsync(root, ["ls-files", "--cached", "-t", "--stage"], MaxFiles + 1, raw: true);
        var untracked = ListGitPathsAsync(root, untrackedArguments, MaxFiles + 1);
        var deleted = ListGitPathsAsync(root, ["ls-files", "--deleted"], MaxFiles + 1);
        await Task.WhenAll(tracked, untracked, deleted).ConfigureAwait(false);
        if (tracked.Result is not { } trackedLines || untracked.Result is not { } untrackedEntries || deleted.Result is not { } deletedPaths)
        {
            return null;
        }

        // Submodules and untracked nested repositories ("inner/") are folders to walk, not files.
        var nested = new List<string>();
        var trackedPaths = ParseTrackedEntries(root, trackedLines, nested);
        var untrackedPaths = new List<string>(untrackedEntries.Count);
        foreach (var entry in untrackedEntries)
        {
            if (entry.EndsWith('/'))
            {
                nested.Add(entry.TrimEnd('/'));
            }
            else
            {
                untrackedPaths.Add(entry);
            }
        }

        var removed = new HashSet<string>(deletedPaths, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>(Math.Min(trackedPaths.Count + untrackedPaths.Count, MaxFiles));
        var truncated = false;

        foreach (var paths in new[] { trackedPaths, untrackedPaths, WalkNested(root, nested) })
        {
            if (truncated)
            {
                break;
            }

            foreach (var path in paths)
            {
                if (removed.Contains(path) || !seen.Add(path))
                {
                    continue;
                }

                if (files.Count == MaxFiles)
                {
                    truncated = true;
                    break;
                }

                files.Add(path);
            }
        }

        return (files, truncated);
    }

    /// <summary>
    /// Parses "TAG MODE OBJECT STAGE\tPATH" lines of <c>ls-files -t --stage</c>. Submodules (gitlinks) go to
    /// <paramref name="nested"/>; skip-worktree entries missing on disk (outside a sparse checkout) are dropped.
    /// </summary>
    private static List<string> ParseTrackedEntries(string root, List<string> lines, List<string> nested)
    {
        var paths = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }

            var meta = line.AsSpan(0, tab);
            var path = GitCli.UnquotePath(line[(tab + 1)..]);
            if (meta.Contains(" 160000 ", StringComparison.Ordinal))
            {
                nested.Add(path);
            }
            else if (!meta.StartsWith("S ", StringComparison.Ordinal) || File.Exists(Path.Combine(root, path)))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>The files of submodules and nested repositories, which git lists as a single folder entry.</summary>
    private static IEnumerable<string> WalkNested(string root, List<string> folders)
    {
        foreach (var folder in folders)
        {
            var full = Path.Combine(root, folder);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in ProjectFileWalker.EnumerateFiles(full))
            {
                yield return $"{folder}/{file}";
            }
        }
    }

    /// <summary>Streams one git ls-files listing; null when git is unavailable or fails.</summary>
    private async Task<List<string>?> ListGitPathsAsync(string root, string[] arguments, int cap, bool raw = false)
    {
        var paths = new List<string>();
        var gate = new Lock();
        using var enough = new CancellationTokenSource();
        try
        {
            var result = await _git.TryStreamAsync(root, arguments, line =>
            {
                lock (gate)
                {
                    if (line.Length == 0 || paths.Count >= cap)
                    {
                        return;
                    }

                    paths.Add(raw ? line : GitCli.UnquotePath(line));
                    if (paths.Count >= cap)
                    {
                        enough.Cancel();
                    }
                }
            }, enough.Token, GitTimeout).ConfigureAwait(false);

            if (result is null || result.ExitCode != 0)
            {
                if (result is not null)
                {
                    _logger.LogInformation("git {Command} failed in {Root}: {Error}", string.Join(' ', arguments), root, result.StandardError.Trim());
                }

                return null;
            }
        }
        catch (OperationCanceledException) when (enough.IsCancellationRequested)
        {
            // Stopped on purpose: the cap was reached.
        }

        lock (gate)
        {
            return [.. paths];
        }
    }

    private static (List<string> Files, bool Truncated) Walk(string root)
    {
        var files = new List<string>();
        foreach (var path in ProjectFileWalker.EnumerateFiles(root))
        {
            if (files.Count == MaxFiles)
            {
                return (files, true);
            }

            files.Add(path);
        }

        return (files, false);
    }
}
