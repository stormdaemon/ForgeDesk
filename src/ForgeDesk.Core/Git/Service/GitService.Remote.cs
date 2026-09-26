using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var result = await RunAsync(repository, ["remote", "-v"], cancellationToken).ConfigureAwait(false);
        return GitRefParsers.ParseRemotes(result.StandardOutput);
    }

    public async Task FetchAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var remotes = await GetRemotesAsync(repository, cancellationToken).ConfigureAwait(false);
        if (remotes.Count == 0)
        {
            // Nothing to fetch from; background auto-fetch must stay silent for local-only repositories.
            return;
        }

        await RunNetworkAsync(repository, ["fetch", "--all", "--prune", "--progress"], RemoteUrls(remotes), progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task PullAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var remotes = await GetRemotesAsync(repository, cancellationToken).ConfigureAwait(false);
        string[] strategy = _settings.Current.PullStrategy switch
        {
            PullStrategy.Merge => ["--no-rebase", "--no-edit"],
            PullStrategy.Rebase => ["--rebase"],
            _ => ["--ff-only"],
        };
        await RunNetworkAsync(repository, ["pull", "--progress", .. strategy], RemoteUrls(remotes), progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task PushAsync(string repoPath, GitPushOptions options, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repository = RepositoryDirectory(repoPath);
        var branch = options.Branch is { } requested
            ? GitArguments.Name(requested, "branch name")
            : await CurrentBranchForPushAsync(repository, cancellationToken).ConfigureAwait(false);

        var remotes = await GetRemotesAsync(repository, cancellationToken).ConfigureAwait(false);
        var tracking = await LocalBranchAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        // "." means the upstream is another local branch: not something to push to.
        var upstreamRemote = tracking is { UpstreamRemote.Length: > 0 } t && t.UpstreamRemote != "." ? t.UpstreamRemote : null;
        var remoteName = options.Remote is { } explicitRemote
            ? GitArguments.Name(explicitRemote, "remote name")
            : upstreamRemote ?? DefaultRemote(remotes)
              ?? throw new ForgeException(ErrorKind.NoUpstream, "This repository has no remote to push to.",
                  "Add a remote (or publish the repository to GitHub), then push again.");
        var remote = remotes.FirstOrDefault(r => r.Name == remoteName)
                     ?? throw new ForgeException(ErrorKind.NoUpstream, $"There is no remote named '{remoteName}'.",
                         "Choose one of the repository's remotes.");

        List<string> arguments = ["push", "--progress"];
        string destination;
        if (tracking is { UpstreamRef.Length: > 0 } upstream && upstream.UpstreamRemote == remote.Name)
        {
            // Honor the upstream even when its name differs from the local branch.
            destination = upstream.UpstreamRef;
        }
        else if (options.SetUpstream)
        {
            destination = GitRefParsers.HeadsPrefix + branch;
            arguments.Add("--set-upstream");
        }
        else if (upstreamRemote is null)
        {
            throw new ForgeException(ErrorKind.NoUpstream, $"The branch '{branch}' has no upstream branch.",
                "Publish the branch to set its upstream, then push again.");
        }
        else
        {
            destination = GitRefParsers.HeadsPrefix + branch;
        }

        if (options.Force)
        {
            // --force-if-includes: background fetches update remote-tracking refs, which alone would
            // let --force-with-lease overwrite commits the user has never seen.
            arguments.AddRange(["--force-with-lease", "--force-if-includes"]);
        }

        if (options.Tags)
        {
            arguments.Add("--tags");
        }

        arguments.AddRange([remote.Name, $"{GitRefParsers.HeadsPrefix}{branch}:{destination}"]);
        await RunNetworkAsync(repository, arguments, [remote.FetchUrl, remote.PushUrl ?? remote.FetchUrl], progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task PushTagAsync(string repoPath, string tagName, string? remote = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var tag = GitArguments.Name(tagName, "tag name");
        var tagRef = GitRefParsers.TagsPrefix + tag;
        if (!(await ExecuteAsync(repository, ["rev-parse", "--verify", "--quiet", tagRef], cancellationToken).ConfigureAwait(false)).Succeeded)
        {
            throw new ForgeException(ErrorKind.NotFound, $"The tag '{tag}' doesn't exist in this repository.", "Create the tag first, then push it.");
        }

        var remotes = await GetRemotesAsync(repository, cancellationToken).ConfigureAwait(false);
        var remoteName = remote is null ? DefaultRemote(remotes) : GitArguments.Name(remote, "remote name");
        var target = remotes.FirstOrDefault(r => r.Name == remoteName)
                     ?? throw new ForgeException(ErrorKind.NoUpstream,
                         remoteName is null ? "This repository has no remote to push to." : $"There is no remote named '{remoteName}'.",
                         "Add a remote (or publish the repository to GitHub), then push again.");
        await RunNetworkAsync(repository, ["push", "--progress", target.Name, $"{tagRef}:{tagRef}"], [target.FetchUrl, target.PushUrl ?? target.FetchUrl], null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> CurrentBranchForPushAsync(string repository, CancellationToken cancellationToken)
    {
        var head = await ExecuteAsync(repository, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head.Succeeded && head.StandardOutput.Trim().Length > 0)
        {
            return head.StandardOutput.Trim();
        }

        // Exit code 1 without a message: HEAD is a commit, not a branch.
        throw head.ExitCode == 1
            ? GitErrorTranslator.Create(head, ErrorKind.DetachedHead, "You're not on a branch (detached HEAD), so there is nothing to push.",
                "Create a branch from this commit, then push it.")
            : GitErrorTranslator.Translate(head);
    }

    private async Task RunNetworkAsync(string repository, IReadOnlyList<string> arguments, IEnumerable<string> remoteUrls, IProgress<GitProgress>? progress,
        CancellationToken cancellationToken)
    {
        var request = new GitRequest
        {
            WorkingDirectory = repository,
            Arguments = arguments,
            Kind = GitCommandKind.Network,
            Environment = await CredentialEnvironmentAsync(remoteUrls, cancellationToken).ConfigureAwait(false),
            Progress = progress,
        };
        await _cli.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Credentials from the registered providers, as environment-based git configuration.</summary>
    private async Task<IReadOnlyDictionary<string, string>> CredentialEnvironmentAsync(IEnumerable<string> remoteUrls, CancellationToken cancellationToken)
    {
        if (_credentialProviders.Count == 0 || !_settings.Current.UseGitHubTokenForGit)
        {
            return new Dictionary<string, string>();
        }

        var inherited = _cli.BaseEnvironment.TryGetValue(GitCredentialEnvironment.CountVariable, out var overridden)
            ? overridden
            : Environment.GetEnvironmentVariable(GitCredentialEnvironment.CountVariable);
        return await GitCredentialEnvironment.BuildAsync(_credentialProviders, remoteUrls.Distinct(StringComparer.Ordinal).ToList(),
            GitCredentialEnvironment.InheritedConfigCount(inherited), _logger, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> RemoteUrls(IEnumerable<GitRemote> remotes) =>
        remotes.SelectMany(r => new[] { r.FetchUrl, r.PushUrl ?? r.FetchUrl });

    private static string? DefaultRemote(IReadOnlyList<GitRemote> remotes) =>
        remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? remotes.FirstOrDefault()?.Name;
}
