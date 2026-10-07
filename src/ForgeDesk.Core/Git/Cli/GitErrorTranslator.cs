using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Turns a failed git invocation (exit code, stderr, stdout) into a <see cref="ForgeException"/> with a
/// stable <see cref="ErrorKind"/>, a message written for users and a recovery hint. The raw output and
/// the command line (credentials masked) always go to <see cref="ForgeException.Detail"/>.
/// </summary>
/// <remarks>
/// Git runs with LC_ALL=C, so its messages are in English and can be matched reliably. Rules are ordered
/// from the most to the least specific: e.g. HTTP 403 answers also contain "unable to access", which on
/// its own means a network failure.
/// </remarks>
internal static partial class GitErrorTranslator
{
    private const int MaxDetailChars = 16 * 1024;

    public static ForgeException Translate(GitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var failure = new Failure(result);

        if (result.TimedOut)
        {
            return failure.Error(ErrorKind.Timeout, "Git didn't finish in time.",
                "Try again. If it keeps happening, make sure no other program is blocking the repository.");
        }

        return TranslateHookFailure(failure, result)
            ?? TranslateRepositoryAccess(failure)
            ?? TranslateLocalState(failure)
            ?? TranslateRemote(failure)
            ?? TranslateFileSystem(failure)
            ?? TranslateCommandSpecific(failure)
            ?? failure.Error(ErrorKind.GitCommandFailed, failure.Summary());
    }

    /// <summary>An error chosen by the caller, with the same technical detail as a translated one.</summary>
    public static ForgeException Create(GitResult result, ErrorKind kind, string message, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new Failure(result).Error(kind, message, hint);
    }

    /// <summary>
    /// A failing pre-commit / commit-msg / pre-push hook makes git fail without a message of its own:
    /// the output is the hook's (linters, tests…), whose phrases ("does not exist", "connection refused")
    /// must not be matched against git's rules. Detected by the absence of any line git itself writes.
    /// </summary>
    private static ForgeException? TranslateHookFailure(Failure f, GitResult result)
    {
        var subcommand = Subcommand(result.Command);
        var gitLines = f.Lines.Where(IsGitOriginated).ToList();
        bool stoppedByHook;
        string message;
        if (subcommand == "commit")
        {
            stoppedByHook = gitLines.Count == 0
                            && !f.Has("nothing to commit") && !f.Has("nothing added to commit") && !f.Has("no changes added to commit")
                            && !f.Has("aborting commit") && !f.Has("please tell me who you are");
            message = "A Git hook stopped the commit.";
        }
        else if (subcommand == "push")
        {
            // Git's only line then is "error: failed to push some refs to '…'".
            stoppedByHook = gitLines.Count > 0
                            && gitLines.All(l => l.StartsWith("error: failed to push some refs", StringComparison.OrdinalIgnoreCase))
                            && !f.Has("[rejected]") && !f.Has("[remote rejected]");
            message = "A Git hook stopped the push.";
        }
        else
        {
            return null;
        }

        return stoppedByHook && f.Lines.Any(l => l.Trim().Length > 0 && !l.StartsWith("error: failed to push some refs", StringComparison.OrdinalIgnoreCase))
            ? f.Error(ErrorKind.GitCommandFailed, message, "Read the hook's output in the details, fix what it reports, then try again.")
            : null;
    }

    private static bool IsGitOriginated(string line) =>
        line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith(" ! [", StringComparison.Ordinal)
        || line.StartsWith("CONFLICT (", StringComparison.Ordinal);

    private static string? Subcommand(string command)
    {
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "git" ? parts[1] : null;
    }

    private static ForgeException? TranslateRepositoryAccess(Failure f)
    {
        if (DubiousOwnership().Match(f.Text) is { Success: true } dubious)
        {
            var path = dubious.Groups["path"].Value;
            return f.Error(ErrorKind.PermissionDenied,
                "Git doesn't trust this folder because it belongs to another user account.",
                $"If you trust it, run: git config --global --add safe.directory \"{path}\"");
        }

        if (f.Has("not a git repository") || f.Has("must be run in a work tree"))
        {
            return f.Error(ErrorKind.NotARepository, "This folder is not a Git repository.",
                "Choose a folder that contains a repository, or initialize one here.");
        }

        if (LockFile().Match(f.Text) is { Success: true } lockMatch)
        {
            return f.Error(ErrorKind.RepositoryLocked, "Another Git process is using this repository.",
                $"Wait for it to finish. If no Git program is running, delete '{lockMatch.Groups["lock"].Value}' and try again.");
        }

        if (f.Has("index.lock") || f.Has("another git process seems to be running"))
        {
            return f.Error(ErrorKind.RepositoryLocked, "Another Git process is using this repository.",
                "Wait for it to finish. If no Git program is running, delete the '.git/index.lock' file and try again.");
        }

        if (f.Has("please tell me who you are") || f.Has("identity unknown") || f.Has("empty ident name") || f.Has("unable to auto-detect email address"))
        {
            return f.Error(ErrorKind.InvalidInput, "Git doesn't know your name and email yet.",
                "Set your name and email in Settings › Git (git config --global user.name / user.email), then try again.");
        }

        // Only a missing program: LFS server errors ("object not found") must fall through to the other rules.
        if (f.Has("git-lfs: command not found") || f.Has("git-lfs: not found")
            || (f.Has("git-lfs") && (f.Has("was not found on your path") || f.Has("is not recognized as an internal or external command"))))
        {
            return f.Error(ErrorKind.ToolNotFound, "This repository uses Git LFS, which isn't installed.",
                "Install Git LFS from https://git-lfs.com and try again.");
        }

        if (f.Has("smudge filter lfs failed") || f.Has("filter 'git-lfs"))
        {
            return f.Error(ErrorKind.GitCommandFailed, "Git LFS couldn't download some large files.",
                "Check your network and that your account can access the repository's LFS storage, then try again.");
        }

        return null;
    }

    private static ForgeException? TranslateLocalState(Failure f)
    {
        if (f.Has("you are not currently on a branch"))
        {
            return f.Error(ErrorKind.DetachedHead, "You're not on a branch (detached HEAD).",
                "Create a branch from this commit, then try again.");
        }

        if (f.Has("has no upstream branch") || f.Has("no tracking information for the current branch") || f.Has("no upstream configured for branch"))
        {
            return f.Error(ErrorKind.NoUpstream, "The current branch has no upstream branch.",
                "Publish the branch to the remote first.");
        }

        if (f.Has("no configured push destination") || f.Has("no remote repository specified"))
        {
            return f.Error(ErrorKind.NoUpstream, "This repository has no remote to sync with.",
                "Add a remote (or publish the repository to GitHub), then try again.");
        }

        if (NotARepositoryRemote().Match(f.Text) is { Success: true } remote)
        {
            var name = remote.Groups["name"].Value;
            return LooksLikeUrlOrPath(name)
                ? f.Error(ErrorKind.NotFound, "The remote repository was not found.",
                    "Check the address, and that your account has access to the repository.")
                : f.Error(ErrorKind.NoUpstream, $"There is no remote named '{name}'.",
                    "Add the remote in the repository settings, or choose another remote.");
        }

        if (TranslateConflict(f) is { } conflict)
        {
            return conflict;
        }

        if (TranslateDirtyTree(f) is { } dirty)
        {
            return dirty;
        }

        if (f.Has("nothing to commit") || f.Has("nothing added to commit") || f.Has("no changes added to commit"))
        {
            return f.Error(ErrorKind.NothingToCommit, "There are no staged changes to commit.",
                "Stage the changes you want to commit, then try again.");
        }

        if (f.Has("no local changes to save"))
        {
            return f.Error(ErrorKind.NothingToCommit, "There are no local changes to stash.");
        }

        if (f.Has("you do not have the initial commit yet"))
        {
            return f.Error(ErrorKind.NothingToCommit, "Changes can't be stashed before the first commit.",
                "Commit once, then try again.");
        }

        if (SourceRefspecMissing().IsMatch(f.Text))
        {
            return f.Error(ErrorKind.NothingToCommit, "This branch has no commits to push yet.",
                "Commit your changes first, then push.");
        }

        return null;
    }

    private static ForgeException? TranslateConflict(Failure f)
    {
        var conflictLines = f.Lines.Where(l => l.StartsWith("CONFLICT (", StringComparison.Ordinal)).ToList();
        var files = conflictLines.Select(ConflictPath).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var inRebase = f.Has("could not apply") || f.Has("git rebase --continue");
        if (conflictLines.Count > 0 || f.Has("automatic merge failed") || inRebase)
        {
            var message = files.Count switch
            {
                0 => "The changes conflict with each other.",
                1 => $"There is a conflict in {files[0]}.",
                _ => $"There are conflicts in {files.Count} files.",
            };
            var hint = f.Has("stash entry is kept")
                ? "Resolve the conflicts, then stage the files. The stash was kept, so nothing is lost."
                : inRebase
                    ? "Resolve the conflicts and stage the files, then continue the rebase (git rebase --continue) or abort it (git rebase --abort)."
                    : "Resolve the conflicts and commit the result, or abort the merge.";
            var listed = files.Count > 0 ? FileList("Conflicted files", files) : FileList("Conflicts", conflictLines);
            return f.Error(ErrorKind.MergeConflict, message, hint, listed);
        }

        if (f.Has("unmerged files") || f.Has("unresolved conflict") || f.Has("resolve your current index first"))
        {
            return f.Error(ErrorKind.MergeConflict, "You have unresolved conflicts.",
                "Resolve the conflicts and commit them (or abort the merge) before continuing.");
        }

        return null;
    }

    private static ForgeException? TranslateDirtyTree(Failure f)
    {
        var overwritten = f.Has("would be overwritten by") || f.Has("would be removed by");
        if (overwritten)
        {
            var files = IndentedFilesAfter(f.Lines, l => l.Contains("would be overwritten by", StringComparison.OrdinalIgnoreCase)
                                                         || l.Contains("would be removed by", StringComparison.OrdinalIgnoreCase));
            var untracked = f.Has("untracked working tree files");
            var count = files.Count == 0 ? "some files" : files.Count == 1 ? "1 file" : $"{files.Count} files";
            return untracked
                ? f.Error(ErrorKind.DirtyWorkingTree, $"Untracked files would be overwritten ({count}).",
                    "Move, delete or commit the listed files, then try again.", FileList("Untracked files", files))
                : f.Error(ErrorKind.DirtyWorkingTree, $"Your local changes to {count} would be overwritten.",
                    "Commit or stash your changes, then try again.", FileList("Files with local changes", files));
        }

        if (f.Has("could not restore untracked files from stash") || f.Has("already exists, no checkout"))
        {
            return f.Error(ErrorKind.DirtyWorkingTree, "Some untracked files from the stash already exist in your working tree.",
                "Move or delete those files, then apply the stash again. The stash was kept.");
        }

        if (f.Has("you have unstaged changes") || f.Has("please commit or stash them") || f.Has("your index contains uncommitted changes"))
        {
            return f.Error(ErrorKind.DirtyWorkingTree, "You have uncommitted changes.",
                "Commit or stash your changes, then try again.");
        }

        return null;
    }

    private static ForgeException? TranslateRemote(Failure f)
    {
        if (f.Has("(stale info)"))
        {
            return f.Error(ErrorKind.NonFastForward, "The remote branch changed since you last fetched it.",
                "Fetch and review the new commits before force pushing.");
        }

        if (f.Has("[rejected]") && f.Has("(already exists)"))
        {
            return f.Error(ErrorKind.AlreadyExists, "This already exists on the remote.",
                "Delete it on the remote first, or use a different name.");
        }

        if (f.Has("(fetch first)") || f.Has("(non-fast-forward)") || f.Has("updates were rejected because") || f.Has("[rejected]"))
        {
            return f.Error(ErrorKind.NonFastForward, "The remote has commits you don't have. Pull first.",
                "Pull to bring in the remote changes, then push again.");
        }

        if (f.Has("not possible to fast-forward") || f.Has("divergent branches"))
        {
            return f.Error(ErrorKind.NonFastForward, "Your branch and its upstream have diverged, so they can't be fast-forwarded.",
                "Choose the Merge or Rebase pull strategy in Settings, or merge the upstream branch yourself.");
        }

        if (f.Has("GH013") || f.Has("repository rule violations") || f.Has("push cannot contain secrets"))
        {
            return f.Error(ErrorKind.RemoteRejected, "The push breaks a rule of the remote repository.",
                "Read the details to see which rule was violated (for example a secret in a commit), fix your commits, then push again.");
        }

        if (f.Has("GH006") || f.Has("protected branch"))
        {
            return f.Error(ErrorKind.RemoteRejected, "This branch is protected on the remote.",
                "Push your commits to a new branch and open a pull request instead.");
        }

        if (f.Has("[remote rejected]") || f.Has("hook declined"))
        {
            var reason = RemoteRejectedReason().Match(f.Text) is { Success: true } m ? $" ({m.Groups["reason"].Value})" : string.Empty;
            return f.Error(ErrorKind.RemoteRejected, $"The remote rejected the push{reason}.",
                "Read the details for the reason given by the server.");
        }

        if (PermissionToRepository().Match(f.Text) is { Success: true } permission)
        {
            return f.Error(ErrorKind.AuthenticationFailed,
                $"The account '{permission.Groups["user"].Value}' isn't allowed to access {permission.Groups["repo"].Value}.",
                "Ask the repository owner for access, or sign in with an account that has it.");
        }

        if (f.Has("host key verification failed"))
        {
            return f.Error(ErrorKind.AuthenticationFailed, "The server's SSH host key couldn't be verified.",
                "Connect once from a terminal (for example: ssh -T git@github.com) to trust the server, then try again.");
        }

        if (f.Has("permission denied (publickey"))
        {
            return f.Error(ErrorKind.AuthenticationFailed, "The server rejected your SSH key.",
                "Add your SSH key to your account and load it in the SSH agent, or use an HTTPS remote address.");
        }

        if (IsAuthenticationFailure(f))
        {
            return f.Error(ErrorKind.AuthenticationFailed, "Git couldn't sign in to the remote repository.",
                "Sign in to GitHub from ForgeDesk, or update the credentials saved in Git Credential Manager, then try again.");
        }

        if (f.Has("repository not found") || RepositoryMissing().IsMatch(f.Text) || f.Has("returned error: 404"))
        {
            return f.Error(ErrorKind.NotFound, "The remote repository was not found.",
                "Check the address, and that your account has access to the repository.");
        }

        // Windows (schannel) cannot reach the revocation server, typically behind a corporate proxy.
        if (f.Has("unable to check revocation") || f.Has("CRYPT_E_NO_REVOCATION_CHECK") || f.Has("CRYPT_E_REVOCATION_OFFLINE"))
        {
            return f.Error(ErrorKind.NetworkUnavailable, "Windows couldn't check whether the server's certificate was revoked.",
                "Behind a corporate proxy this check often fails. You can turn it off with: git config --global http.schannelCheckRevoke false");
        }

        if (f.Has("SEC_E_UNTRUSTED_ROOT") || f.Has("issued by an authority that is not trusted"))
        {
            return f.Error(ErrorKind.NetworkUnavailable, "The server's certificate isn't trusted by Windows.",
                "Install your organization's root certificate in Windows, or ask your IT department for help.");
        }

        if (f.Has("ssl certificate problem") || f.Has("certificate verify failed") || f.Has("unable to get local issuer certificate"))
        {
            return f.Error(ErrorKind.NetworkUnavailable, "The server's security certificate couldn't be verified.",
                "Behind a corporate proxy, let Git use the Windows certificate store: git config --global http.sslBackend schannel");
        }

        if (IsNetworkFailure(f))
        {
            return f.Error(ErrorKind.NetworkUnavailable, "Couldn't reach the remote server.",
                "Check your internet connection and proxy settings, then try again.");
        }

        return null;
    }

    private static bool IsAuthenticationFailure(Failure f) =>
        f.Has("authentication failed") || f.Has("could not read username") || f.Has("could not read password")
        || f.Has("invalid username or password") || f.Has("http basic: access denied") || f.Has("returned error: 401")
        || f.Has("returned error: 403") || f.Has("password authentication was removed") || f.Has("terminal prompts disabled")
        || f.Has("401 unauthorized") || f.Has("403 forbidden") || f.Has("logon failed");

    private static bool IsNetworkFailure(Failure f) =>
        f.Has("could not resolve host") || f.Has("could not resolve proxy") || f.Has("failed to connect")
        || f.Has("connection timed out") || f.Has("operation timed out") || f.Has("connection refused")
        || f.Has("connection reset") || f.Has("connection was reset") || f.Has("network is unreachable")
        || f.Has("no route to host") || f.Has("remote end hung up unexpectedly") || f.Has("rpc failed")
        || f.Has("early eof") || f.Has("unexpected disconnect") || f.Has("unable to access")
        || f.Has("could not read from remote repository") || f.Has("connect tunnel failed") || f.Has("proxy connect aborted")
        || f.Has("ssl_connect") || f.Has("gnutls_handshake") || f.Has("schannel") || f.Has("temporary failure in name resolution");

    private static ForgeException? TranslateFileSystem(Failure f)
    {
        if (f.Has("filename too long"))
        {
            return f.Error(ErrorKind.GitCommandFailed, "A file path is too long for Windows.",
                "Enable long paths in Git: git config --global core.longpaths true");
        }

        if (f.Has("permission denied") || f.Has("access is denied") || f.Has("operation not permitted") || f.Has("unable to unlink"))
        {
            return f.Error(ErrorKind.PermissionDenied, "Git couldn't access some files (permission denied).",
                "Close programs that may be using these files (editors, terminals, antivirus scans), then try again.");
        }

        if (f.Has("no space left on device") || f.Has("not enough space on the disk"))
        {
            return f.Error(ErrorKind.GitCommandFailed, "The disk is full.", "Free some disk space and try again.");
        }

        return null;
    }

    private static ForgeException? TranslateCommandSpecific(Failure f)
    {
        if (NotFullyMerged().Match(f.Text) is { Success: true } unmerged)
        {
            return f.Error(ErrorKind.GitCommandFailed,
                $"The branch '{unmerged.Groups["branch"].Value}' has commits that aren't merged into another branch.",
                "Delete it anyway (force delete) if you no longer need those commits.");
        }

        if (CheckedOutBranch().Match(f.Text) is { Success: true } current)
        {
            return f.Error(ErrorKind.InvalidInput, $"The branch '{current.Groups["branch"].Value}' is checked out, so it can't be deleted.",
                "Switch to another branch first.");
        }

        if (f.Has("there is no merge to abort"))
        {
            return f.Error(ErrorKind.InvalidInput, "There is no merge in progress.");
        }

        if (f.Has("refusing to merge unrelated histories"))
        {
            return f.Error(ErrorKind.GitCommandFailed, "These branches don't share any history.",
                "If you really want to combine them, merge from a terminal with --allow-unrelated-histories.");
        }

        if (f.Has("failed to sign") || f.Has("gpg failed") || f.Has("cannot run gpg") || (f.Has("signing") && f.Has("failed to write commit object")))
        {
            return f.Error(ErrorKind.GitCommandFailed, "Git couldn't sign the commit.",
                "Check your commit signing setup (GPG or SSH key), or turn off commit.gpgsign.");
        }

        if (f.Has("already exists"))
        {
            return f.Error(ErrorKind.AlreadyExists, f.Summary());
        }

        if (f.Has("not a valid branch name") || f.Has("not a valid tag name") || f.Has("not a valid ref name") || f.Has("invalid branch name"))
        {
            return f.Error(ErrorKind.InvalidInput, f.Summary(),
                "Names can't contain spaces or the characters ~ ^ : ? * [ \\, and can't start with '-' or end with '.lock'.");
        }

        if (f.Has("unknown revision") || f.Has("invalid reference") || f.Has("not a valid object name") || f.Has("bad revision")
            || f.Has("bad object") || f.Has("malformed object name")
            || f.Has("did not match any file(s) known to git") || f.Has("not something we can merge") || f.Has("no stash entries found")
            || f.Has("is not a valid reference") || f.Has("does not exist") || NotFoundLine().IsMatch(f.Text))
        {
            return f.Error(ErrorKind.NotFound, f.Summary());
        }

        return null;
    }

    private static string? ConflictPath(string conflictLine) =>
        ConflictFile().Match(conflictLine) is { Success: true } m ? m.Groups["file"].Value : null;

    private static bool LooksLikeUrlOrPath(string value) =>
        value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal);

    private static List<string> IndentedFilesAfter(IReadOnlyList<string> lines, Func<string, bool> isHeader)
    {
        var files = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!isHeader(lines[i]))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Count && lines[j].Length > 0 && char.IsWhiteSpace(lines[j][0]); j++)
            {
                var file = lines[j].Trim();
                if (file.Length > 0 && !files.Contains(file, StringComparer.Ordinal))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private static string? FileList(string title, IReadOnlyCollection<string> files) =>
        files.Count == 0 ? null : $"{title}:\n" + string.Join('\n', files.Select(f => "  " + f));

    /// <summary>The first line of output that says what went wrong, rephrased as a sentence.</summary>
    internal static string FirstMeaningfulLine(string standardError, string standardOutput)
    {
        var candidates = SplitLines(standardError).Concat(SplitLines(standardOutput))
            .Select(l => l.Trim())
            .Where(IsMeaningful)
            .ToList();
        var line = candidates.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                                                  || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                                                  || l.StartsWith("remote: error:", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        return line is null ? string.Empty : ToSentence(line);
    }

    private static bool IsMeaningful(string line) =>
        line.Length > 0
        && !line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
        && !line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
        && !line.StartsWith("To ", StringComparison.Ordinal)
        && !line.Equals("Aborting", StringComparison.Ordinal)
        && !line.Equals("remote:", StringComparison.Ordinal)
        && !line.StartsWith("(use ", StringComparison.Ordinal)
        && !GitProgressParser.IsProgressLine(line);

    internal static string ToSentence(string line)
    {
        var text = line.Trim();
        foreach (var prefix in new[] { "remote: error:", "remote:", "fatal:", "error:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
            }
        }

        if (text.Length == 0)
        {
            return text;
        }

        text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        if (text.EndsWith(':'))
        {
            text = text[..^1];
        }

        return text[^1] is '.' or '!' or '?' ? text : text + ".";
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r'));

    private sealed class Failure
    {
        private readonly GitResult _result;

        public Failure(GitResult result)
        {
            _result = result;
            Text = result.StandardError + "\n" + result.StandardOutput;
            Lines = SplitLines(Text).ToList();
        }

        public string Text { get; }

        public IReadOnlyList<string> Lines { get; }

        public bool Has(string fragment) => Text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        public string Summary()
        {
            var line = FirstMeaningfulLine(_result.StandardError, _result.StandardOutput);
            return line.Length > 0 ? line : $"Git failed with exit code {_result.ExitCode}.";
        }

        public ForgeException Error(ErrorKind kind, string message, string? hint = null, string? extraDetail = null) =>
            new(kind, message, hint, BuildDetail(extraDetail));

        private string BuildDetail(string? extraDetail)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(extraDetail))
            {
                builder.Append(extraDetail).Append("\n\n");
            }

            builder.Append("$ ").Append(_result.Command).Append('\n');
            AppendOutput(builder, _result.StandardError);
            AppendOutput(builder, _result.StandardOutput);
            builder.Append(_result.TimedOut ? "(timed out)" : $"(exit code {_result.ExitCode})");

            var detail = GitRedaction.Redact(builder.ToString());
            return detail.Length <= MaxDetailChars ? detail : detail[..MaxDetailChars] + "\n…";
        }

        private static void AppendOutput(StringBuilder builder, string output)
        {
            foreach (var line in SplitLines(output))
            {
                if (line.Trim().Length > 0 && !GitProgressParser.IsProgressLine(line))
                {
                    builder.Append(line).Append('\n');
                }
            }
        }
    }

    [GeneratedRegex(@"detected dubious ownership in repository at '(?<path>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DubiousOwnership();

    [GeneratedRegex(@"Unable to create '(?<lock>[^']+\.lock)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockFile();

    [GeneratedRegex(@"'(?<name>[^']+)' does not appear to be a git repository", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotARepositoryRemote();

    [GeneratedRegex(@"^CONFLICT \([^)]*\): (?:Merge conflict in (?<file>.+?)|(?<file>.+?) deleted in .*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictFile();

    [GeneratedRegex(@"src refspec .+ does not match any", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceRefspecMissing();

    [GeneratedRegex(@"\[remote rejected\][^(\n]*\((?<reason>[^)\n]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRejectedReason();

    [GeneratedRegex(@"Permission to (?<repo>\S+?) denied to (?<user>\S+?)\.?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PermissionToRepository();

    [GeneratedRegex(@"repository '[^']+' (?:not found|does not exist)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryMissing();

    [GeneratedRegex(@"branch '(?<branch>[^']+)' is not fully merged", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotFullyMerged();

    [GeneratedRegex(@"cannot delete branch '(?<branch>[^']+)' (?:checked out|used by worktree)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CheckedOutBranch();

    [GeneratedRegex(@"^(?:error|fatal): (?:tag|branch|remote|ref) '[^']+' not found", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NotFoundLine();
}
