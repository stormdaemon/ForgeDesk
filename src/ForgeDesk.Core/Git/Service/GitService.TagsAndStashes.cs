using System.Globalization;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    public async Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var result = await RunAsync(repository, ["for-each-ref", "--sort=-creatordate", GitRefParsers.TagFormat, "refs/tags"], cancellationToken).ConfigureAwait(false);
        return GitRefParsers.ParseTags(result.StandardOutput);
    }

    public async Task CreateTagAsync(string repoPath, string name, string? message = null, string? target = null, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var tag = GitArguments.Name(name, "tag name");
        var check = await ExecuteAsync(repository, ["check-ref-format", GitRefParsers.TagsPrefix + tag], cancellationToken).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{tag}' is not a valid tag name.",
                "Tag names can't contain spaces, '..' or the characters ~ ^ : ? * [ \\ (for example: v1.2.0).");
        }

        var text = message?.ReplaceLineEndings("\n").Trim();
        // Annotated tags read their message from stdin; "whitespace" keeps lines starting with '#'.
        List<string> arguments = string.IsNullOrEmpty(text) ? ["tag"] : ["tag", "--annotate", "--file=-", "--cleanup=whitespace"];
        arguments.Add(tag);
        if (target is not null)
        {
            arguments.Add(GitArguments.Name(target, "commit"));
        }

        await RunAsync(repository, arguments, cancellationToken, GitCommandKind.Write, string.IsNullOrEmpty(text) ? null : text + "\n").ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(string repoPath, string name, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["tag", "--delete", GitArguments.Name(name, "tag name")], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var result = await RunAsync(repository, ["stash", "list", GitRefParsers.StashFormat], cancellationToken, config: LogConfig).ConfigureAwait(false);
        return GitRefParsers.ParseStashes(result.StandardOutput);
    }

    public async Task StashAsync(string repoPath, string? message = null, bool includeUntracked = true, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        List<string> arguments = ["stash", "push"];
        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            arguments.Add("--message=" + message.ReplaceLineEndings(" ").Trim());
        }

        var result = await RunAsync(repository, arguments, cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
        // Git reports success even when it had nothing to save; callers expect a new stash entry.
        if (result.StandardOutput.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
        {
            throw GitErrorTranslator.Create(result, ErrorKind.NothingToCommit, "There are no local changes to stash.");
        }
    }

    public async Task StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["stash", "pop", StashRef(index)], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        await RunAsync(repository, ["stash", "drop", StashRef(index)], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    private static string StashRef(int index) =>
        index >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"stash@{{{index}}}")
            : throw ForgeException.InvalidInput("The stash index can't be negative.");
}
