using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    private const string BranchNameRules =
        "Branch names can't contain spaces, '..', '@{' or the characters ~ ^ : ? * [ \\, and can't start with '-' or end with '.' or '.lock'.";

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, bool includeRemote = true, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        string[] arguments = includeRemote
            ? ["for-each-ref", GitRefParsers.BranchFormat, "refs/heads", "refs/remotes"]
            : ["for-each-ref", GitRefParsers.BranchFormat, "refs/heads"];
        var refsTask = RunAsync(repository, arguments, cancellationToken);
        var remotesTask = includeRemote ? RemoteNamesAsync(repository, cancellationToken) : Task.FromResult<IReadOnlyList<string>>([]);
        await Task.WhenAll(refsTask, remotesTask).ConfigureAwait(false);
        return GitRefParsers.ParseBranches((await refsTask.ConfigureAwait(false)).StandardOutput, await remotesTask.ConfigureAwait(false));
    }

    public async Task CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var name = GitArguments.Name(branch, "branch name");
        var result = await ExecuteAsync(repository, ["switch", name], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return;
        }

        // "origin/feature": create the local tracking branch instead of refusing.
        if (result.StandardError.Contains("got remote branch", StringComparison.OrdinalIgnoreCase))
        {
            await CheckoutRemoteBranchAsync(repository, name, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw GitErrorTranslator.Translate(result);
    }

    public async Task CheckoutRemoteBranchAsync(string repoPath, string remoteBranch, string? localName = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var remoteRef = GitArguments.Name(remoteBranch, "remote branch");
        if (remoteRef.StartsWith(GitRefParsers.RemotesPrefix, StringComparison.Ordinal))
        {
            remoteRef = remoteRef[GitRefParsers.RemotesPrefix.Length..];
        }

        var remote = GitRefParsers.RemoteOf(remoteRef, await RemoteNamesAsync(repository, cancellationToken).ConfigureAwait(false));
        if (remote is null || remoteRef.Length <= remote.Length + 1)
        {
            throw ForgeException.InvalidInput($"'{remoteBranch}' is not a remote branch (expected a name like origin/main).");
        }

        var local = await ValidateBranchNameAsync(repository, localName ?? remoteRef[(remote.Length + 1)..], cancellationToken).ConfigureAwait(false);
        var fullRemoteRef = GitRefParsers.RemotesPrefix + remoteRef;
        if (await LocalBranchAsync(repository, local, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (existing.Upstream != fullRemoteRef)
            {
                throw new ForgeException(ErrorKind.AlreadyExists, $"A branch named '{local}' already exists.",
                    "Check out the existing branch, or choose another name for the new one.");
            }

            // Already tracking that remote branch: switching to it is what the user wants.
            await RunAsync(repository, ["switch", local], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
            return;
        }

        await RunAsync(repository, ["switch", "--create", local, "--track", fullRemoteRef], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task CreateBranchAsync(string repoPath, string name, string? startPoint = null, bool checkout = true, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var branch = await ValidateBranchNameAsync(repository, name, cancellationToken).ConfigureAwait(false);
        List<string> arguments = checkout ? ["switch", "--create", branch] : ["branch", branch];
        if (startPoint is not null)
        {
            arguments.Add(GitArguments.Name(startPoint, "start point"));
        }

        await RunAsync(repository, arguments, cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var branch = GitArguments.Name(name, "branch name");
        await RunAsync(repository, ["branch", force ? "-D" : "-d", "--", branch], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var from = GitArguments.Name(oldName, "branch name");
        var to = await ValidateBranchNameAsync(repository, newName, cancellationToken).ConfigureAwait(false);
        await RunAsync(repository, ["branch", "-m", "--", from, to], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetMergedBranchesAsync(string repoPath, string? target = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var into = target is null
            ? await GetDefaultBranchAsync(repository, cancellationToken).ConfigureAwait(false)
            : GitArguments.Name(target, "branch name");
        if (into is null)
        {
            return [];
        }

        var result = await RunAsync(repository, ["branch", "--merged", into, "--format=%(refname)"], cancellationToken).ConfigureAwait(false);
        return OutputLines(result)
            .Where(r => r.StartsWith(GitRefParsers.HeadsPrefix, StringComparison.Ordinal))
            .Select(GitRefParsers.ShortName)
            .Where(b => b != into)
            .ToList();
    }

    public async Task MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var name = GitArguments.Name(branch, "branch name");
        // On conflicts git exits non-zero and leaves the repository merging, which is what the user resolves.
        await RunAsync(repository, ["merge", "--no-edit", name], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task AbortMergeAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["merge", "--abort"], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        const string originPrefix = "refs/remotes/origin/";
        var originHead = await ExecuteAsync(repository, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"], cancellationToken).ConfigureAwait(false);
        var target = originHead.StandardOutput.Trim();
        if (originHead.Succeeded && target.StartsWith(originPrefix, StringComparison.Ordinal) && target.Length > originPrefix.Length)
        {
            return target[originPrefix.Length..];
        }

        var locals = await RunAsync(repository, ["for-each-ref", "--format=%(refname)", "refs/heads/main", "refs/heads/master"], cancellationToken).ConfigureAwait(false);
        var existing = OutputLines(locals).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in new[] { "main", "master" })
        {
            if (existing.Contains(GitRefParsers.HeadsPrefix + candidate))
            {
                return candidate;
            }
        }

        var current = await ExecuteAsync(repository, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        return current.Succeeded && current.StandardOutput.Trim().Length > 0 ? current.StandardOutput.Trim() : null;
    }

    /// <summary>Validates with git's own rules ("git check-ref-format --branch") and returns the trimmed name.</summary>
    private async Task<string> ValidateBranchNameAsync(string repository, string name, CancellationToken cancellationToken)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw ForgeException.InvalidInput("Enter a branch name.");
        }

        if (trimmed.StartsWith('-'))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{trimmed}' is not a valid branch name.", BranchNameRules);
        }

        var result = await ExecuteAsync(repository, ["check-ref-format", "--branch", trimmed], cancellationToken).ConfigureAwait(false);
        // The output must be the name itself: "@{-1}" is valid syntax but expands to another branch.
        if (!result.Succeeded || result.StandardOutput.Trim() != trimmed)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{trimmed}' is not a valid branch name.", BranchNameRules);
        }

        return trimmed;
    }

    private async Task<(string Name, string Upstream, string UpstreamRemote, string UpstreamRef)?> LocalBranchAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var fullName = GitRefParsers.HeadsPrefix + branch;
        var result = await RunAsync(repository,
            ["for-each-ref", "--format=%(refname)%1f%(upstream)%1f%(upstream:remotename)%1f%(upstream:remoteref)", fullName],
            cancellationToken).ConfigureAwait(false);
        // Patterns also match "refs/heads/<branch>/…", so look for the exact ref.
        foreach (var line in OutputLines(result))
        {
            var fields = line.Split('\x1f');
            if (fields.Length >= 4 && fields[0] == fullName)
            {
                return (branch, fields[1], fields[2], fields[3]);
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> RemoteNamesAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["remote"], cancellationToken).ConfigureAwait(false);
        return OutputLines(result).Select(l => l.Trim()).ToList();
    }
}
