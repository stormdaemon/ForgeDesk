using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    /// <summary>Diffs larger than this are reported as too large instead of being parsed.</summary>
    internal const long MaxDiffBytes = 3 * 1024 * 1024;

    private const int TooLargeHeaderBytes = 64 * 1024;

    /// <summary>
    /// Plain unified diffs whatever the user's configuration: no colors, external tools or text
    /// conversion filters (their output can't be applied back), standard a/ b/ prefixes.
    /// </summary>
    private static readonly string[] DiffOptions = ["--no-color", "--no-ext-diff", "--no-textconv", "-U3", "--src-prefix=a/", "--dst-prefix=b/"];

    public async Task<FileDiff> GetFileDiffAsync(string repoPath, string path, DiffTarget target, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var relative = GitArguments.RelativePath(path);

        if (target == DiffTarget.Staged)
        {
            // Against the empty tree automatically when HEAD is unborn.
            string[] staged = ["diff", "--cached", "-M", .. DiffOptions];
            var diff = await ReadDiffAsync(repository, staged, [relative], relative, cancellationToken).ConfigureAwait(false);
            if (diff is { IsNewFile: true } && await StagedRenameSourceAsync(repository, relative, cancellationToken).ConfigureAwait(false) is { } source)
            {
                // A pathspec limited to the new name hides the rename: include both sides.
                diff = await ReadDiffAsync(repository, staged, [source, relative], relative, cancellationToken).ConfigureAwait(false) ?? diff;
            }

            return diff ?? new FileDiff { Path = relative };
        }

        var workingTree = await ReadDiffAsync(repository, ["diff", .. DiffOptions], [relative], relative, cancellationToken).ConfigureAwait(false);
        if (workingTree is not null)
        {
            return workingTree;
        }

        if (!await IsInIndexAsync(repository, relative, cancellationToken).ConfigureAwait(false))
        {
            var root = await GetRepositoryRootAsync(repository, cancellationToken).ConfigureAwait(false) ?? repository;
            return await UntrackedFileDiff.CreateAsync(PathUtil.ResolveUnder(root, relative), relative, UnifiedDiffParser.DefaultMaxLines, cancellationToken)
                .ConfigureAwait(false);
        }

        return new FileDiff { Path = relative };
    }

    public async Task<FileDiff> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var revision = GitArguments.Name(sha, "commit");
        var relative = GitArguments.RelativePath(path);
        // --root handles the first commit, --diff-merges=first-parent shows what a merge brought in.
        string[] arguments = ["diff-tree", "-p", "-M", "--root", "--no-commit-id", "--diff-merges=first-parent", .. DiffOptions, revision];

        var diff = await WithRevisionNotFoundAsync(sha,
            () => ReadDiffAsync(repository, arguments, [relative], relative, cancellationToken)).ConfigureAwait(false);
        if (diff is { IsNewFile: true })
        {
            var renamed = (await CommitNameStatusAsync(repository, revision, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(e => e.Path == relative && e.OldPath is not null);
            if (renamed.OldPath is { } oldPath)
            {
                diff = await ReadDiffAsync(repository, arguments, [oldPath, relative], relative, cancellationToken).ConfigureAwait(false) ?? diff;
            }
        }

        return diff ?? new FileDiff { Path = relative };
    }

    /// <summary>
    /// Runs a diff command writing to a temporary file (--output) and parses its raw bytes: captured
    /// text would lose the CR of CR LF lines. The file size is checked before anything is loaded.
    /// </summary>
    private async Task<FileDiff?> ReadDiffAsync(string repository, IReadOnlyList<string> arguments, IReadOnlyList<string> paths, string wantedPath,
        CancellationToken cancellationToken, GitCommandKind kind = GitCommandKind.Read)
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"forgedesk-diff-{Guid.NewGuid():N}.patch");
        try
        {
            var result = await RunAsync(repository, [.. arguments, "--output=" + outputFile, "--", .. paths.Select(GitArguments.Pathspec)], cancellationToken, kind)
                .ConfigureAwait(false);
            var info = new FileInfo(outputFile);
            byte[] bytes;
            bool tooLarge;
            if (info.Exists && info.Length > 0)
            {
                tooLarge = info.Length > MaxDiffBytes;
                bytes = tooLarge
                    ? await ReadPrefixAsync(outputFile, TooLargeHeaderBytes, cancellationToken).ConfigureAwait(false)
                    : await File.ReadAllBytesAsync(outputFile, cancellationToken).ConfigureAwait(false);
            }
            else if (result.StandardOutput.Length > 0 && !result.IsOutputTruncated)
            {
                // Combined diffs of conflicted files ignore --output and go to stdout. They are only
                // displayed, never applied, so the line endings lost by text capture don't matter.
                bytes = Encoding.UTF8.GetBytes(result.StandardOutput);
                tooLarge = bytes.Length > MaxDiffBytes;
            }
            else
            {
                return null;
            }

            var diffs = UnifiedDiffParser.Parse(bytes, tooLarge ? 0 : UnifiedDiffParser.DefaultMaxLines);
            // A type change (link ↔ file) comes as a deletion plus an addition of the same path.
            var diff = diffs.FirstOrDefault(d => d.Path == wantedPath && !d.IsDeletedFile)
                       ?? diffs.FirstOrDefault(d => d.Path == wantedPath)
                       ?? diffs.FirstOrDefault();
            return diff is not null && tooLarge ? diff with { IsTooLarge = true, Hunks = [] } : diff;
        }
        finally
        {
            TryDeleteFile(outputFile);
        }
    }

    private async Task<string?> StagedRenameSourceAsync(string repository, string path, CancellationToken cancellationToken) =>
        (await StagedRenamesAsync(repository, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(path);

    /// <summary>
    /// Renames staged in the index, new path → old path. Pathspec-limited commands can't see them (the
    /// other name is outside the pathspec), so callers working on a few paths ask for them here.
    /// </summary>
    private async Task<Dictionary<string, string>> StagedRenamesAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["diff", "--cached", "-M", "-z", "--name-status", "--diff-filter=R"], cancellationToken).ConfigureAwait(false);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, path, oldPath) in GitRefParsers.ParseNameStatus(result.StandardOutput))
        {
            if (oldPath is not null)
            {
                renames[path] = oldPath;
            }
        }

        return renames;
    }

    private async Task<IReadOnlyList<(string Status, string Path, string? OldPath)>> CommitNameStatusAsync(string repository, string revision, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository,
            ["diff-tree", "-r", "-M", "-z", "--name-status", "--root", "--no-commit-id", "--diff-merges=first-parent", revision],
            cancellationToken).ConfigureAwait(false);
        return GitRefParsers.ParseNameStatus(result.StandardOutput);
    }

    private async Task<bool> IsInIndexAsync(string repository, string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["ls-files", "-z", "--cached", "--", GitArguments.Pathspec(path)], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim().Length > 0;
    }

    private static async Task<byte[]> ReadPrefixAsync(string path, int length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
        var buffer = new byte[Math.Min(length, stream.Length)];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the OS cleans the temp folder.
        }
    }
}
