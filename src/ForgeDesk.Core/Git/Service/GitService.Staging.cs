using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    public async Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var relative = GitArguments.RelativePaths(paths);
        // -A: a pathspec of a deleted file stages the deletion too.
        await RunForPathsAsync(repository, ["add", "-A"], relative, cancellationToken).ConfigureAwait(false);
    }

    public async Task StageAllAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["add", "-A"], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var relative = GitArguments.RelativePaths(paths);
        if (relative.Count == 0)
        {
            return;
        }

        // Unstaging the new name of a staged rename must also restore the old name, or the index keeps
        // a lone deletion. "git reset -- paths" also works on an unborn branch (it empties those entries).
        var withRenameSources = await WithStagedRenameSourcesAsync(repository, relative, cancellationToken).ConfigureAwait(false);
        await RunForPathsAsync(repository, ["reset", "-q"], withRenameSources, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["reset", "-q"], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task ApplyHunkAsync(string repoPath, FileDiff diff, DiffHunk hunk, bool reverse, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(hunk);
        var repository = RepositoryDirectory(repoPath);
        var path = GitArguments.RelativePath(diff.Path);
        var source = diff;
        var intentToAdd = false;

        if (!reverse && diff.IsNewFile && !await IsInIndexAsync(repository, path, cancellationToken).ConfigureAwait(false))
        {
            // Part of an untracked file: register it as "intent to add" (an empty entry) so the hunk can
            // be applied to the index; git's own header then describes the file (mode, executable bit).
            await RunAsync(repository, ["add", "--intent-to-add", "--", GitArguments.Pathspec(path)], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
            intentToAdd = true;
        }

        try
        {
            if (intentToAdd && await ReadDiffAsync(repository, ["diff", .. DiffOptions], [path], path, cancellationToken).ConfigureAwait(false) is { } fresh)
            {
                source = diff with { HeaderLines = fresh.HeaderLines };
            }

            var patch = GitPatchBuilder.Build(source, hunk);
            string[] arguments = reverse
                ? ["apply", "--cached", "--reverse", "--whitespace=nowarn", "--recount", "-"]
                : ["apply", "--cached", "--whitespace=nowarn", "--recount", "-"];
            var result = await ExecuteAsync(repository, arguments, cancellationToken, GitCommandKind.Write, patch).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw IsStalePatch(result)
                    ? GitErrorTranslator.Create(result, ErrorKind.GitCommandFailed, "This change no longer matches the file.",
                        "The file changed since the diff was shown. Refresh and try again.")
                    : GitErrorTranslator.Translate(result);
            }
        }
        catch when (intentToAdd)
        {
            await UndoIntentToAddAsync(repository, path).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var relative = GitArguments.RelativePaths(paths);
        if (relative.Count == 0)
        {
            return;
        }

        var root = await GetRepositoryRootAsync(repository, cancellationToken).ConfigureAwait(false)
                   ?? throw new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.");
        // With both names in the pathspec, status reports a staged rename as one entry with its original path.
        var withRenameSources = await WithStagedRenameSourcesAsync(root, relative, cancellationToken).ConfigureAwait(false);
        var entries = await StatusOfAsync(root, withRenameSources, cancellationToken).ConfigureAwait(false);

        // "Back to HEAD" means restoring what HEAD has and dropping everything else (staged new files,
        // intent-to-add entries, new names of renames, files added on both sides of a conflict…).
        var tracked = entries.Where(e => !e.IsUntracked).ToList();
        var inHead = await PathsInHeadAsync(root, tracked.Select(e => e.Path).Concat(tracked.Select(e => e.OriginalPath).OfType<string>()).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var toRestore = new List<string>();
        var toRemoveFromIndex = new List<string>();
        var toDelete = entries.Where(e => e.IsUntracked).Select(e => e.Path).ToList();
        foreach (var entry in tracked)
        {
            if (inHead.Contains(entry.Path))
            {
                toRestore.Add(entry.Path);
            }
            else
            {
                toRemoveFromIndex.Add(entry.Path);
                toDelete.Add(entry.Path);
            }

            if (entry.OriginalPath is { } original && inHead.Contains(original))
            {
                toRestore.Add(original);
            }
        }

        // Checked before touching anything, so a refusal never leaves a half-done discard.
        if (toDelete.FirstOrDefault(p => WorkingTreeFiles.IsNestedRepository(root, p)) is { } nested)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{nested.TrimEnd('/')}' is a separate Git repository, so it wasn't discarded.",
                "Delete the folder yourself if you really want to remove it.");
        }

        if (toRestore.Count > 0)
        {
            await RunForPathsAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree"], toRestore.Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }

        if (toRemoveFromIndex.Count > 0)
        {
            await RunForPathsAsync(root, ["rm", "--cached", "-r", "-f", "-q", "--ignore-unmatch"], toRemoveFromIndex, cancellationToken).ConfigureAwait(false);
        }

        foreach (var path in toDelete)
        {
            WorkingTreeFiles.DeleteUntracked(root, path);
        }
    }

    private async Task<IReadOnlyList<string>> WithStagedRenameSourcesAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var renames = await StagedRenamesAsync(repository, cancellationToken).ConfigureAwait(false);
        return renames.Count == 0
            ? paths
            : paths.Concat(paths.Select(p => renames.GetValueOrDefault(p)).OfType<string>()).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<List<GitStatusEntry>> StatusOfAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var entries = new List<GitStatusEntry>();
        foreach (var batch in GitArguments.Batch(paths.Select(GitArguments.Pathspec)))
        {
            var result = await RunAsync(repository, ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--", .. batch], cancellationToken).ConfigureAwait(false);
            entries.AddRange(GitStatusParser.Parse(result.StandardOutput).Entries);
        }

        return entries;
    }

    /// <summary>Which of <paramref name="paths"/> exist in the HEAD commit (none before the first commit).</summary>
    private async Task<HashSet<string>> PathsInHeadAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var inHead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in GitArguments.Batch(paths.Distinct(StringComparer.Ordinal).Select(GitArguments.Pathspec)))
        {
            var result = await ExecuteAsync(repository, ["ls-tree", "-r", "--name-only", "-z", "--full-tree", "HEAD", "--", .. batch], cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return await IsUnbornAsync(repository, cancellationToken).ConfigureAwait(false) ? inHead : throw GitErrorTranslator.Translate(result);
            }

            inHead.UnionWith(result.StandardOutput.Split('\0').Where(p => p.Trim().Length > 0));
        }

        return inHead;
    }

    private async Task UndoIntentToAddAsync(string repository, string path)
    {
        try
        {
            await RunAsync(repository, ["rm", "--cached", "-f", "-q", "--ignore-unmatch", "--", GitArguments.Pathspec(path)], CancellationToken.None, GitCommandKind.Write)
                .ConfigureAwait(false);
        }
        catch (ForgeException)
        {
            // The original failure matters more; at worst the file stays marked "intent to add".
        }
    }

    private static bool IsStalePatch(GitResult result) =>
        result.StandardError.Contains("patch does not apply", StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains("patch failed", StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains("does not match index", StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains("does not exist in index", StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains("already exists in index", StringComparison.OrdinalIgnoreCase);
}
