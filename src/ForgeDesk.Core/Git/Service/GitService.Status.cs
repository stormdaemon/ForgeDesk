using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    private const int MaxStatusOutputChars = 64 * 1024 * 1024;

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var statusRequest = Request(repository, ["status", "--porcelain=v2", "-z", "--branch", "--show-stash", "--untracked-files=all"], GitCommandKind.Read, null, null)
            with { MaxOutputChars = MaxStatusOutputChars };
        var statusTask = _cli.RunAsync(statusRequest, cancellationToken);
        var gitDirectoryTask = ExecuteAsync(repository, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        await Task.WhenAll(statusTask, gitDirectoryTask).ConfigureAwait(false);

        var result = await statusTask.ConfigureAwait(false);
        if (result.IsOutputTruncated)
        {
            throw new ForgeException(ErrorKind.FileTooLarge, "This repository has too many changed files to list.",
                "Add generated folders (node_modules, build output…) to .gitignore, then refresh.");
        }

        var status = GitStatusParser.Parse(result.StandardOutput);
        var gitDirectory = await gitDirectoryTask.ConfigureAwait(false);
        return gitDirectory.Succeeded
            ? status with { State = DetectState(gitDirectory.StandardOutput.Trim()) }
            : status;
    }

    /// <summary>An operation in progress leaves marker files in the git directory (per worktree).</summary>
    internal static GitRepositoryState DetectState(string gitDirectory)
    {
        if (string.IsNullOrEmpty(gitDirectory) || !Directory.Exists(gitDirectory))
        {
            return GitRepositoryState.Normal;
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return GitRepositoryState.Rebasing;
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return GitRepositoryState.Merging;
        }

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
        {
            return GitRepositoryState.CherryPicking;
        }

        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
        {
            return GitRepositoryState.Reverting;
        }

        return File.Exists(Path.Combine(gitDirectory, "BISECT_LOG")) ? GitRepositoryState.Bisecting : GitRepositoryState.Normal;
    }
}
