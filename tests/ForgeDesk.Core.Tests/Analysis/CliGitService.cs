using System.Diagnostics;
using System.Globalization;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Analysis;

/// <summary>
/// Minimal <see cref="IGitService"/> over the real git CLI, covering what the analyzer reads.
/// The production implementation lives in the Git domain; this double lets the analyzer be
/// exercised against genuine repositories (dated commits, branches, stashes) in isolation.
/// </summary>
internal sealed class CliGitService : IGitService
{
    private const char Unit = '\u001f';
    private const char Record = '\u001e';

    public Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var (exitCode, output) = Run(path, "rev-parse", "--is-inside-work-tree");
        return Task.FromResult(exitCode == 0 && output.Trim() == "true");
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var output = Git(repoPath, "ls-files", "-z", "--cached", "--others", "--exclude-standard");
        return Task.FromResult<IReadOnlyList<string>>(output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList());
    }

    public Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var output = Git(repoPath, "status", "--porcelain=v2", "--branch", "--show-stash", "--untracked-files=all");
        string? branch = null;
        string? head = null;
        string? upstream = null;
        int ahead = 0, behind = 0, stashes = 0;
        var entries = new List<GitStatusEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                head = line[13..] == "(initial)" ? null : line[13..];
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = line[14..] == "(detached)" ? null : line[14..];
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line[18..];
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line[12..].Split(' ');
                ahead = int.Parse(parts[0].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(parts[1].TrimStart('-'), CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("# stash ", StringComparison.Ordinal))
            {
                stashes = int.Parse(line[8..], CultureInfo.InvariantCulture);
            }
            else if (line[0] is '1' or '2' or 'u' or '?')
            {
                entries.Add(new GitStatusEntry
                {
                    Path = line[(line.LastIndexOf(' ') + 1)..],
                    WorkTreeState = line[0] == '?' ? GitFileState.Untracked : GitFileState.Modified,
                });
            }
        }

        return Task.FromResult(new GitStatus
        {
            Branch = branch,
            HeadSha = head,
            IsDetached = branch is null && head is not null,
            IsUnborn = head is null,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            StashCount = stashes,
            Entries = entries,
        });
    }

    public Task<IReadOnlyList<GitCommit>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "log",
            $"--format=%H{Unit}%an{Unit}%ae{Unit}%aI{Unit}%P{Unit}%s{Record}",
            "--skip=" + query.Skip.ToString(CultureInfo.InvariantCulture),
            "-n", query.Take.ToString(CultureInfo.InvariantCulture),
            query.Revision ?? "HEAD",
        };
        var commits = Git(repoPath, [.. args])
            .Split(Record, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim('\n'))
            .Where(r => r.Length > 0)
            .Select(r =>
            {
                var f = r.Split(Unit);
                return new GitCommit
                {
                    Sha = f[0],
                    Subject = f[5],
                    Author = new GitSignature(f[1], f[2], DateTimeOffset.Parse(f[3], CultureInfo.InvariantCulture)),
                    Parents = f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                };
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<GitCommit>>(commits);
    }

    public Task<int> CountCommitsAsync(string repoPath, string? revisionRange = null, DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "rev-list", "--count" };
        if (since is { } s)
        {
            args.Add("--since=" + s.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        args.Add(revisionRange ?? "HEAD");
        return Task.FromResult(int.Parse(Git(repoPath, [.. args]).Trim(), CultureInfo.InvariantCulture));
    }

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, bool includeRemote = true, CancellationToken cancellationToken = default)
    {
        var refs = includeRemote ? new[] { "refs/heads", "refs/remotes" } : ["refs/heads"];
        var output = Git(repoPath, ["for-each-ref", $"--format=%(refname:short){Unit}%(refname){Unit}%(HEAD){Unit}%(objectname){Unit}%(committerdate:iso-strict)", .. refs]);
        var branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var f = line.Split(Unit);
            return new GitBranch
            {
                Name = f[0],
                FullName = f[1],
                IsRemote = f[1].StartsWith("refs/remotes/", StringComparison.Ordinal),
                IsCurrent = f[2] == "*",
                TipSha = f[3],
                TipDate = DateTimeOffset.Parse(f[4], CultureInfo.InvariantCulture),
            };
        }).ToList();
        return Task.FromResult<IReadOnlyList<GitBranch>>(branches);
    }

    public Task<IReadOnlyList<string>> GetMergedBranchesAsync(string repoPath, string? target = null, CancellationToken cancellationToken = default)
    {
        var into = target ?? "main";
        var output = Git(repoPath, "branch", "--merged", into, "--format=%(refname:short)");
        return Task.FromResult<IReadOnlyList<string>>(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(b => b != into).ToList());
    }

    public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var names = Git(repoPath, "remote").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult<IReadOnlyList<GitRemote>>(names.Select(n => new GitRemote(n, Git(repoPath, "remote", "get-url", n).Trim(), null)).ToList());
    }

    public Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");

    // --- Not used by the analyzer ------------------------------------------------------------

    public Task<GitInstallation?> FindGitAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<string?> GetRepositoryRootAsync(string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task InitAsync(string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task CloneAsync(string url, string targetDirectory, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitIdentity> GetIdentityAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task SetGlobalIdentityAsync(string name, string email, CancellationToken cancellationToken = default) => throw Unused();

    public Task<FileDiff> GetFileDiffAsync(string repoPath, string path, DiffTarget target, CancellationToken cancellationToken = default) => throw Unused();

    public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw Unused();

    public Task StageAllAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw Unused();

    public Task UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task ApplyHunkAsync(string repoPath, FileDiff diff, DiffHunk hunk, bool reverse, CancellationToken cancellationToken = default) => throw Unused();

    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitCommit> CommitAsync(string repoPath, GitCommitOptions options, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitCommitDetails> GetCommitDetailsAsync(string repoPath, string sha, CancellationToken cancellationToken = default) => throw Unused();

    public Task<FileDiff> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutRemoteBranchAsync(string repoPath, string remoteBranch, string? localName = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task CreateBranchAsync(string repoPath, string name, string? startPoint = null, bool checkout = true, CancellationToken cancellationToken = default) => throw Unused();

    public Task DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default) => throw Unused();

    public Task RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default) => throw Unused();

    public Task MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task AbortMergeAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task FetchAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task PullAsync(string repoPath, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task PushAsync(string repoPath, GitPushOptions options, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task CreateTagAsync(string repoPath, string name, string? message = null, string? target = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task PushTagAsync(string repoPath, string tagName, string? remote = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task DeleteTagAsync(string repoPath, string name, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitStash>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default) => throw Unused();

    public Task StashAsync(string repoPath, string? message = null, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw Unused();

    public Task StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default) => throw Unused();

    public Task StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default) => throw Unused();

    private static NotSupportedException Unused([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new($"{member} is not needed by the analyzer tests.");

    private static string Git(string repoPath, params string[] args)
    {
        var (exitCode, output) = Run(repoPath, args);
        return exitCode == 0
            ? output
            : throw new ForgeException(ErrorKind.GitCommandFailed, $"git {args[0]} failed.", detail: output);
    }

    private static (int ExitCode, string Output) Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : stderr);
    }
}
