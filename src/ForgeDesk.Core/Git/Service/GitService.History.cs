using System.Globalization;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    private const int MaxLogPage = 10_000;

    public async Task<GitCommit> CommitAsync(string repoPath, GitCommitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repository = RepositoryDirectory(repoPath);
        var message = (options.Message ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (message.Length == 0)
        {
            throw ForgeException.InvalidInput("Enter a commit message.");
        }

        if (options.StageAll)
        {
            // "commit -a" ignores untracked files; "add -A" stages everything the user sees.
            await StageAllAsync(repository, cancellationToken).ConfigureAwait(false);
        }

        // The message goes through stdin: no quoting issues, any length, several paragraphs.
        string[] arguments = options.Amend ? ["commit", "--file=-", "--amend"] : ["commit", "--file=-"];
        await RunAsync(repository, arguments, cancellationToken, GitCommandKind.Write, message + "\n").ConfigureAwait(false);

        var head = await RunAsync(repository, ["log", "-1", GitLogParser.Format, "HEAD", "--"], cancellationToken, config: LogConfig).ConfigureAwait(false);
        return GitLogParser.Parse(head.StandardOutput).FirstOrDefault()
               ?? throw GitErrorTranslator.Create(head, ErrorKind.GitCommandFailed, "The commit was created but couldn't be read back.");
    }

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var repository = RepositoryDirectory(repoPath);
        if (query.Take <= 0)
        {
            return [];
        }

        var skip = Math.Max(0, query.Skip);
        var take = Math.Min(query.Take, MaxLogPage);
        List<string> walk = ["log", GitLogParser.Format];
        if (query.AllBranches)
        {
            // Every branch and tag; the stash is a ref too but its WIP commits aren't history.
            walk.AddRange(["--exclude=refs/stash", "--all"]);
        }
        else
        {
            walk.Add(GitArguments.Name(query.Revision ?? "HEAD", "revision"));
        }

        string[] pathFilter = string.IsNullOrWhiteSpace(query.PathFilter) ? ["--"] : ["--", GitArguments.Pathspec(GitArguments.RelativePath(query.PathFilter))];
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return await LogPageAsync(repository, [.. walk, $"--skip={skip}", $"--max-count={take}", .. pathFilter], query, cancellationToken).ConfigureAwait(false);
        }

        // git ANDs --grep with --author, so "message OR author" takes two walks merged by date. Each
        // returns its first skip+take matches, enough to cut the requested page from the union.
        var search = query.Search.Trim();
        var limit = $"--max-count={skip + take}";
        string[] matching = ["--regexp-ignore-case", "--fixed-strings"];
        var byMessage = LogPageAsync(repository, [.. walk, .. matching, "--grep=" + search, limit, .. pathFilter], query, cancellationToken);
        var byAuthor = LogPageAsync(repository, [.. walk, .. matching, "--author=" + search, limit, .. pathFilter], query, cancellationToken);
        await Task.WhenAll(byMessage, byAuthor).ConfigureAwait(false);
        return MergeByDate(await byMessage.ConfigureAwait(false), await byAuthor.ConfigureAwait(false)).Skip(skip).Take(take).ToList();
    }

    public async Task<GitCommitDetails> GetCommitDetailsAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var revision = GitArguments.Name(sha, "commit");
        string[] changes = ["diff-tree", "-r", "-M", "-z", "--root", "--no-commit-id", "--diff-merges=first-parent"];
        var commitTask = RunAsync(repository, ["log", "-1", GitLogParser.Format, revision, "--"], cancellationToken, config: LogConfig);
        var numstatTask = RunAsync(repository, [.. changes, "--numstat", revision], cancellationToken);
        var nameStatusTask = RunAsync(repository, [.. changes, "--name-status", revision], cancellationToken);
        await WithRevisionNotFoundAsync(sha, () => Task.WhenAll(commitTask, numstatTask, nameStatusTask)).ConfigureAwait(false);

        var commit = GitLogParser.Parse((await commitTask.ConfigureAwait(false)).StandardOutput).FirstOrDefault()
                     ?? throw ForgeException.NotFound($"The commit '{sha}'");
        var files = GitRefParsers.ParseFileChanges((await numstatTask.ConfigureAwait(false)).StandardOutput, (await nameStatusTask.ConfigureAwait(false)).StandardOutput);
        return new GitCommitDetails(commit, files);
    }

    public async Task<int> CountCommitsAsync(string repoPath, string? revisionRange = null, DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        List<string> arguments = ["rev-list", "--count"];
        if (since is { } date)
        {
            arguments.Add("--since=" + date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        arguments.Add(revisionRange is null ? "HEAD" : GitArguments.Name(revisionRange, "revision range"));
        arguments.Add("--");
        var result = await ExecuteAsync(repository, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // No commits yet: HEAD doesn't resolve, which simply means zero.
            return revisionRange is null && await IsUnbornAsync(repository, cancellationToken).ConfigureAwait(false)
                ? 0
                : throw GitErrorTranslator.Translate(result);
        }

        return int.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    private async Task<IReadOnlyList<GitCommit>> LogPageAsync(string repository, IReadOnlyList<string> arguments, GitLogQuery query, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(repository, arguments, cancellationToken, config: LogConfig).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return GitLogParser.Parse(result.StandardOutput);
        }

        if (!query.AllBranches && query.Revision is null && await IsUnbornAsync(repository, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var error = GitErrorTranslator.Translate(result);
        throw error.Kind == ErrorKind.NotFound && query.Revision is { } revision
            ? new ForgeException(ErrorKind.NotFound, $"'{revision}' was not found in this repository.", "Check the branch, tag or commit name.", error.Detail)
            : error;
    }

    /// <summary>True when HEAD points to a branch without commits (a new repository).</summary>
    private async Task<bool> IsUnbornAsync(string repository, CancellationToken cancellationToken)
    {
        var head = await ExecuteAsync(repository, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head.Succeeded)
        {
            return false;
        }

        var branch = await ExecuteAsync(repository, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        return branch.Succeeded;
    }

    /// <summary>Merges two date-ordered commit lists into one, newest first, without duplicates.</summary>
    internal static IEnumerable<GitCommit> MergeByDate(IReadOnlyList<GitCommit> first, IReadOnlyList<GitCommit> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0, j = 0;
        while (i < first.Count || j < second.Count)
        {
            var takeFirst = j >= second.Count || (i < first.Count && Date(first[i]) >= Date(second[j]));
            var commit = takeFirst ? first[i++] : second[j++];
            if (seen.Add(commit.Sha))
            {
                yield return commit;
            }
        }

        static DateTimeOffset Date(GitCommit commit) => commit.Committer?.When ?? commit.Author.When;
    }

    private static async Task<T> WithRevisionNotFoundAsync<T>(string revision, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            throw new ForgeException(ErrorKind.NotFound, $"The commit '{revision}' was not found in this repository.",
                "It may belong to a branch that was deleted, or to a remote you haven't fetched.", ex.Detail, ex);
        }
    }

    private static async Task WithRevisionNotFoundAsync(string revision, Func<Task> action) =>
        await WithRevisionNotFoundAsync(revision, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
}
