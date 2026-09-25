using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitErrorTranslatorTests
{
    private const string Command = "git push --progress origin refs/heads/main:refs/heads/main";

    public static TheoryData<string, string, ErrorKind> Samples => new()
    {
        {
            "not a repository",
            "fatal: not a git repository (or any of the parent directories): .git\n",
            ErrorKind.NotARepository
        },
        {
            "fetch first",
            "To https://github.com/octo/app.git\n ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to 'https://github.com/octo/app.git'\nhint: Updates were rejected because the remote contains work that you do\nhint: not have locally.\n",
            ErrorKind.NonFastForward
        },
        {
            "non-fast-forward",
            "To /tmp/bare.git\n ! [rejected]        main -> main (non-fast-forward)\nerror: failed to push some refs to '/tmp/bare.git'\nhint: Updates were rejected because the tip of your current branch is behind\n",
            ErrorKind.NonFastForward
        },
        {
            "stale lease",
            "To https://github.com/octo/app.git\n ! [rejected]        main -> main (stale info)\nerror: failed to push some refs to 'https://github.com/octo/app.git'\n",
            ErrorKind.NonFastForward
        },
        {
            "diverged ff-only pull",
            "hint: Diverging branches can't be fast-forwarded, you need to either:\nhint: \nhint: \tgit merge --no-ff\nfatal: Not possible to fast-forward, aborting.\n",
            ErrorKind.NonFastForward
        },
        {
            "merge conflict",
            "Auto-merging src/app.cs\nCONFLICT (content): Merge conflict in src/app.cs\nAutomatic merge failed; fix conflicts and then commit the result.\n",
            ErrorKind.MergeConflict
        },
        {
            "unmerged files block pull",
            "error: Pulling is not possible because you have unmerged files.\nhint: Fix them up in the work tree, and then use 'git add/rm <file>'\nfatal: Exiting because of an unresolved conflict.\n",
            ErrorKind.MergeConflict
        },
        {
            "rebase conflict",
            "error: could not apply 1a2b3c4... Change title\nhint: Resolve all conflicts manually, mark them as resolved with\nhint: \"git add/rm <conflicted_files>\", then run \"git rebase --continue\".\nCONFLICT (content): Merge conflict in README.md\n",
            ErrorKind.MergeConflict
        },
        {
            "checkout overwrite",
            "error: Your local changes to the following files would be overwritten by checkout:\n\tsrc/app.cs\n\tREADME.md\nPlease commit your changes or stash them before you switch branches.\nAborting\n",
            ErrorKind.DirtyWorkingTree
        },
        {
            "merge overwrite",
            "error: Your local changes to the following files would be overwritten by merge:\n\tf.txt\nPlease commit your changes or stash them before you merge.\nAborting\n",
            ErrorKind.DirtyWorkingTree
        },
        {
            "untracked overwrite",
            "error: The following untracked working tree files would be overwritten by checkout:\n\tnotes.txt\nPlease move or remove them before you switch branches.\nAborting\n",
            ErrorKind.DirtyWorkingTree
        },
        {
            "rebase with unstaged changes",
            "error: cannot pull with rebase: You have unstaged changes.\nerror: Please commit or stash them.\n",
            ErrorKind.DirtyWorkingTree
        },
        {
            "no upstream on push",
            "fatal: The current branch feature has no upstream branch.\nTo push the current branch and set the remote as upstream, use\n\n    git push --set-upstream origin feature\n",
            ErrorKind.NoUpstream
        },
        {
            "no tracking information on pull",
            "There is no tracking information for the current branch.\nPlease specify which branch you want to merge with.\n",
            ErrorKind.NoUpstream
        },
        {
            "no push destination",
            "fatal: No configured push destination.\nEither specify the URL from the command-line or configure a remote repository using\n",
            ErrorKind.NoUpstream
        },
        {
            "missing origin remote",
            "fatal: 'origin' does not appear to be a git repository\nfatal: Could not read from remote repository.\n\nPlease make sure you have the correct access rights\nand the repository exists.\n",
            ErrorKind.NoUpstream
        },
        {
            "nothing to commit (stdout)",
            "On branch main\nnothing to commit, working tree clean\n",
            ErrorKind.NothingToCommit
        },
        {
            "only untracked files",
            "On branch main\nUntracked files:\n\tnew.txt\n\nnothing added to commit but untracked files present (use \"git add\" to track)\n",
            ErrorKind.NothingToCommit
        },
        {
            "index lock",
            "fatal: Unable to create 'C:/Users/dev/app/.git/index.lock': File exists.\n\nAnother git process seems to be running in this repository, e.g.\nan editor opened by 'git commit'.\n",
            ErrorKind.RepositoryLocked
        },
        {
            "ref lock",
            "error: cannot lock ref 'refs/heads/main': Unable to create '/repo/.git/refs/heads/main.lock': File exists.\n",
            ErrorKind.RepositoryLocked
        },
        {
            "authentication failed",
            "remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/octo/app.git/'\n",
            ErrorKind.AuthenticationFailed
        },
        {
            "prompts disabled",
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n",
            ErrorKind.AuthenticationFailed
        },
        {
            "http 403",
            "remote: Permission to octo/app.git denied to someone.\nfatal: unable to access 'https://github.com/octo/app.git/': The requested URL returned error: 403\n",
            ErrorKind.AuthenticationFailed
        },
        {
            "http 401",
            "fatal: unable to access 'https://dev.azure.com/org/_git/app/': The requested URL returned error: 401\n",
            ErrorKind.AuthenticationFailed
        },
        {
            "ssh key rejected",
            "git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.\n\nPlease make sure you have the correct access rights\nand the repository exists.\n",
            ErrorKind.AuthenticationFailed
        },
        {
            "repository not found",
            "remote: Repository not found.\nfatal: repository 'https://github.com/octo/missing.git/' not found\n",
            ErrorKind.NotFound
        },
        {
            "local clone source missing",
            "fatal: repository '/tmp/does-not-exist' does not exist\n",
            ErrorKind.NotFound
        },
        {
            "dns failure",
            "fatal: unable to access 'https://github.com/octo/app.git/': Could not resolve host: github.com\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "connection timeout",
            "fatal: unable to access 'https://github.com/octo/app.git/': Failed to connect to github.com port 443 after 21045 ms: Connection timed out\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "ssh dns failure",
            "ssh: Could not resolve hostname github.com: No such host is known.\nfatal: Could not read from remote repository.\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "hung up",
            "error: RPC failed; curl 56 OpenSSL SSL_read: Connection was reset, errno 10054\nfatal: the remote end hung up unexpectedly\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "certificate",
            "fatal: unable to access 'https://git.corp.local/app.git/': SSL certificate problem: unable to get local issuer certificate\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "schannel revocation check",
            "fatal: unable to access 'https://github.com/octo/app.git/': schannel: next InitializeSecurityContext failed: Unknown error (0x80092012) - The revocation function was unable to check revocation for the certificate.\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "schannel untrusted root",
            "fatal: unable to access 'https://git.corp.local/app.git/': schannel: next InitializeSecurityContext failed: SEC_E_UNTRUSTED_ROOT (0x80090325) - The certificate chain was issued by an authority that is not trusted.\n",
            ErrorKind.NetworkUnavailable
        },
        {
            "lfs object missing on server is not a missing tool",
            "Error downloading object: big.bin (abc123): Smudge error: batch response: Repository or object not found: https://github.com/octo/app.git/info/lfs\nerror: external filter 'git-lfs filter-process' failed\nfatal: big.bin: smudge filter lfs failed\n",
            ErrorKind.GitCommandFailed
        },
        {
            "protected branch",
            "remote: error: GH006: Protected branch update failed for refs/heads/main.\nremote: error: Changes must be made through a pull request.\nTo https://github.com/octo/app.git\n ! [remote rejected] main -> main (protected branch hook declined)\nerror: failed to push some refs to 'https://github.com/octo/app.git'\n",
            ErrorKind.RemoteRejected
        },
        {
            "pre-receive hook",
            "remote: Commit message must reference a ticket\nTo ssh://git.corp.local/app.git\n ! [remote rejected] main -> main (pre-receive hook declined)\nerror: failed to push some refs to 'ssh://git.corp.local/app.git'\n",
            ErrorKind.RemoteRejected
        },
        {
            "push protection",
            "remote: error: GH013: Repository rule violations found for refs/heads/main.\nremote: - GITHUB PUSH PROTECTION\nremote:   Push cannot contain secrets\n ! [remote rejected] main -> main (push declined due to repository rule violations)\n",
            ErrorKind.RemoteRejected
        },
        {
            "tag exists remotely",
            "To https://github.com/octo/app.git\n ! [rejected]        v1.0 -> v1.0 (already exists)\nerror: failed to push some refs to 'https://github.com/octo/app.git'\nhint: Updates were rejected because the tag already exists in the remote.\n",
            ErrorKind.AlreadyExists
        },
        {
            "identity",
            "Author identity unknown\n\n*** Please tell me who you are.\n\nRun\n\n  git config --global user.email \"you@example.com\"\n  git config --global user.name \"Your Name\"\n\nfatal: unable to auto-detect email address (got 'dev@DESKTOP-1.(none)')\n",
            ErrorKind.InvalidInput
        },
        {
            "detached push",
            "fatal: You are not currently on a branch.\nTo push the history leading to the current (detached HEAD)\nstate now, use\n\n    git push origin HEAD:<name-of-remote-branch>\n",
            ErrorKind.DetachedHead
        },
        {
            "dubious ownership",
            "fatal: detected dubious ownership in repository at 'D:/shared/app'\n'D:/shared/app' is owned by:\n\t'S-1-5-32-544'\nbut the current user is:\n\t'S-1-5-21-1-2-3-1001'\nTo add an exception for this directory, call:\n\n\tgit config --global --add safe.directory D:/shared/app\n",
            ErrorKind.PermissionDenied
        },
        {
            "file in use",
            "error: unable to unlink old 'bin/app.dll': Permission denied\nfatal: Could not reset index file to revision 'HEAD'.\n",
            ErrorKind.PermissionDenied
        },
        {
            "branch exists",
            "fatal: a branch named 'feature' already exists\n",
            ErrorKind.AlreadyExists
        },
        {
            "clone target not empty",
            "fatal: destination path 'C:\\src\\app' already exists and is not an empty directory.\n",
            ErrorKind.AlreadyExists
        },
        {
            "unknown revision",
            "fatal: ambiguous argument 'nope': unknown revision or path not in the working tree.\nUse '--' to separate paths from revisions, like this:\n",
            ErrorKind.NotFound
        },
        {
            "invalid reference",
            "fatal: invalid reference: feature/missing\n",
            ErrorKind.NotFound
        },
        {
            "missing tag",
            "error: tag 'v9' not found.\n",
            ErrorKind.NotFound
        },
        {
            "missing stash",
            "error: stash@{5} is not a valid reference\n",
            ErrorKind.NotFound
        },
        {
            "no merge to abort",
            "fatal: There is no merge to abort (MERGE_HEAD missing).\n",
            ErrorKind.InvalidInput
        },
        {
            "delete checked out branch",
            "error: Cannot delete branch 'main' checked out at 'C:/src/app'\n",
            ErrorKind.InvalidInput
        },
        {
            "invalid branch name",
            "fatal: 'bad..name' is not a valid branch name\n",
            ErrorKind.InvalidInput
        },
        {
            "missing git-lfs",
            "git-lfs filter-process: git-lfs: command not found\nfatal: the remote end hung up unexpectedly\n",
            ErrorKind.ToolNotFound
        },
        {
            "not fully merged",
            "error: the branch 'feature' is not fully merged.\nIf you are sure you want to delete it, run 'git branch -D feature'\n",
            ErrorKind.GitCommandFailed
        },
        {
            "long path",
            "error: unable to create file src/a/very/deep/path/file.txt: Filename too long\n",
            ErrorKind.GitCommandFailed
        },
        {
            "unknown failure",
            "fatal: refusing to fetch into branch 'refs/heads/main' checked out at '/repo'\n",
            ErrorKind.GitCommandFailed
        },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Maps_real_git_output_to_error_kinds(string scenario, string output, ErrorKind expected)
    {
        // Git prints some of these on stdout (conflicts, "nothing to commit"): the translator reads both.
        var fromStderr = GitErrorTranslator.Translate(new GitResult(Command, 1, string.Empty, output));
        var fromStdout = GitErrorTranslator.Translate(new GitResult(Command, 1, output, string.Empty));

        fromStderr.Kind.Should().Be(expected, scenario);
        fromStdout.Kind.Should().Be(expected, scenario);
        fromStderr.Message.Should().NotBeNullOrWhiteSpace();
        fromStderr.Detail.Should().Contain(Command);
    }

    [Fact]
    public void Non_fast_forward_tells_the_user_to_pull()
    {
        var error = GitErrorTranslator.Translate(new GitResult(Command, 1, "", " ! [rejected]        main -> main (fetch first)\n"));

        error.Message.Should().Be("The remote has commits you don't have. Pull first.");
        error.Hint.Should().Contain("Pull");
    }

    [Fact]
    public void Dirty_tree_lists_the_files_in_the_detail()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git switch other", 1, "",
            "error: Your local changes to the following files would be overwritten by checkout:\n\tsrc/app.cs\n\tREADME.md\nPlease commit your changes or stash them before you switch branches.\nAborting\n"));

        error.Kind.Should().Be(ErrorKind.DirtyWorkingTree);
        error.Message.Should().Contain("2 files");
        error.Detail.Should().StartWith("Files with local changes:\n  src/app.cs\n  README.md");
        error.Detail.Should().Contain("$ git switch other");
    }

    [Fact]
    public void Merge_conflict_names_the_conflicted_file()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git merge --no-edit feature", 1,
            "Auto-merging f.txt\nCONFLICT (content): Merge conflict in f.txt\nAutomatic merge failed; fix conflicts and then commit the result.\n", ""));

        error.Message.Should().Be("There is a conflict in f.txt.");
        error.Detail.Should().Contain("Conflicted files:\n  f.txt");
    }

    [Fact]
    public void Stash_conflict_reassures_that_the_stash_was_kept()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git stash pop stash@{0}", 1,
            "Auto-merging f.txt\nCONFLICT (content): Merge conflict in f.txt\nThe stash entry is kept in case you need it again.\n", ""));

        error.Kind.Should().Be(ErrorKind.MergeConflict);
        error.Hint.Should().Contain("stash was kept");
    }

    [Fact]
    public void Revocation_failures_explain_the_windows_setting()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git fetch", 128, "",
            "fatal: unable to access 'https://github.com/o/r.git/': schannel: next InitializeSecurityContext failed: Unknown error (0x80092012) - The revocation function was unable to check revocation for the certificate.\n"));

        error.Hint.Should().Contain("http.schannelCheckRevoke false");
    }

    [Fact]
    public void Lock_hint_names_the_lock_file()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git add -A", 128, "",
            "fatal: Unable to create 'C:/src/app/.git/index.lock': File exists.\n"));

        error.Hint.Should().Contain("C:/src/app/.git/index.lock");
    }

    [Fact]
    public void Dubious_ownership_hint_gives_the_safe_directory_command()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git status", 128, "",
            "fatal: detected dubious ownership in repository at 'D:/shared/app'\n"));

        error.Hint.Should().Contain("safe.directory \"D:/shared/app\"");
    }

    [Fact]
    public void Permission_denied_on_a_repository_names_the_account()
    {
        var error = GitErrorTranslator.Translate(new GitResult(Command, 128, "",
            "remote: Permission to octo/app.git denied to someone.\nfatal: unable to access 'https://github.com/octo/app.git/': The requested URL returned error: 403\n"));

        error.Kind.Should().Be(ErrorKind.AuthenticationFailed);
        error.Message.Should().Contain("'someone'").And.Contain("octo/app.git");
    }

    [Fact]
    public void Fallback_uses_the_first_meaningful_line_as_a_sentence()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git fetch", 128, "",
            "hint: something\nwarning: something else\nremote: Counting objects: 100% (3/3), done.\nfatal: refusing to fetch into branch 'refs/heads/main' checked out at '/repo'\n"));

        error.Kind.Should().Be(ErrorKind.GitCommandFailed);
        error.Message.Should().Be("Refusing to fetch into branch 'refs/heads/main' checked out at '/repo'.");
    }

    [Fact]
    public void Empty_output_reports_the_exit_code()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git gc", 3, "", ""));

        error.Kind.Should().Be(ErrorKind.GitCommandFailed);
        error.Message.Should().Contain("exit code 3");
    }

    [Fact]
    public void Timeout_is_reported_as_timeout()
    {
        var error = GitErrorTranslator.Translate(new GitResult("git fetch", -1, "", "", TimedOut: true));

        error.Kind.Should().Be(ErrorKind.Timeout);
        error.Detail.Should().Contain("(timed out)");
    }

    [Fact]
    public void Detail_masks_credentials_and_skips_progress_noise()
    {
        var result = new GitResult(GitRedaction.RedactCommand(["clone", "https://user:s3cr3t@github.com/octo/app.git"]), 128, "",
            "Receiving objects:  45% (450/1000)\nfatal: unable to access 'https://user:s3cr3t@github.com/octo/app.git/': Could not resolve host: github.com\n");

        var error = GitErrorTranslator.Translate(result);

        error.Detail.Should().NotContain("s3cr3t");
        error.Detail.Should().NotContain("Receiving objects");
        error.Detail.Should().Contain("https://***@github.com/octo/app.git");
    }

    [Theory]
    [InlineData("fatal: a branch named 'x' already exists", "A branch named 'x' already exists.")]
    [InlineData("error: pathspec 'x' did not match any file(s) known to git", "Pathspec 'x' did not match any file(s) known to git.")]
    [InlineData("remote: error: GH006: Protected branch update failed.", "GH006: Protected branch update failed.")]
    [InlineData("Your local changes would be lost:", "Your local changes would be lost.")]
    public void Git_lines_become_sentences(string line, string expected) =>
        GitErrorTranslator.ToSentence(line).Should().Be(expected);
}
