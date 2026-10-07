using System.Diagnostics;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

/// <summary>Regression tests for git review findings (hooks, filters, cancellation, truncation, discard).</summary>
public sealed class GitReviewRegressionTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    private static void WriteHook(string repository, string name, string body)
    {
        var path = GitSandbox.WriteFile(repository, ".git/hooks/" + name, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    // GIT-01 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Staging_an_untracked_file_hunk_normalizes_line_endings_like_git_add()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "core.autocrlf", "true");
        repo.WriteFile("new.txt", "a\r\nb\r\nc\r\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.WorkingTree, Ct);

        await Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[0], reverse: false, Ct);

        repo.Git("ls-files", "--eol", "--", "new.txt").Should().StartWith("i/lf");
    }

    [Fact]
    public async Task Staging_part_of_an_untracked_file_normalizes_line_endings()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile(".gitattributes", "* text=auto\n");
        repo.WriteFile("new.txt", "a\r\nb\r\nc\r\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.WorkingTree, Ct);
        var firstTwo = diff.Hunks[0] with { Lines = diff.Hunks[0].Lines.Take(2).ToList() };

        await Git.ApplyHunkAsync(repo.Path, diff, firstTwo, reverse: false, Ct);

        repo.Git("ls-files", "--eol", "--", "new.txt").Should().StartWith("i/lf");
        repo.Git("show", ":new.txt").Should().Be("a\nb");
    }

    [Fact]
    public async Task Staging_a_whole_untracked_file_runs_its_clean_filter()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "filter.upper.clean", "tr a-z A-Z");
        repo.Git("config", "filter.upper.smudge", "cat");
        repo.WriteFile(".gitattributes", "*.dat filter=upper\n");
        repo.WriteFile("x.dat", "abc\ndef\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "x.dat", DiffTarget.WorkingTree, Ct);

        await Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[0], reverse: false, Ct);

        repo.Git("show", ":x.dat").Should().Be("ABC\nDEF");
    }

    [Fact]
    public async Task Staging_part_of_an_untracked_file_with_a_clean_filter_is_refused()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "filter.upper.clean", "tr a-z A-Z");
        repo.Git("config", "filter.upper.smudge", "cat");
        repo.WriteFile(".gitattributes", "*.dat filter=upper\n");
        repo.WriteFile("x.dat", "abc\ndef\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "x.dat", DiffTarget.WorkingTree, Ct);
        var firstLine = diff.Hunks[0] with { Lines = diff.Hunks[0].Lines.Take(1).ToList() };

        var act = () => Git.ApplyHunkAsync(repo.Path, diff, firstLine, reverse: false, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        repo.Git("status", "--porcelain", "--", "x.dat").Should().Be("?? x.dat");
    }

    // GIT-03 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_checkout_lets_git_finish_and_never_leaves_the_index_locked()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "filter.slow.smudge", "sleep 3; cat");
        repo.Git("config", "filter.slow.clean", "cat");
        repo.Git("switch", "-q", "-c", "other");
        repo.Commit("Slow file", (".gitattributes", "*.slow filter=slow\n"), ("a.slow", "content\n"));
        repo.Git("switch", "-q", "main");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(1000));

        try
        {
            await Git.CheckoutAsync(repo.Path, "other", cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Whatever the call reported, git must not have been killed half-way.
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        File.Exists(repo.Combine(".git", "index.lock")).Should().BeFalse();
        repo.Git("status", "--porcelain").Should().BeEmpty();
    }

    // GIT-04 ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("src/a.ts(3,5): error TS2339: Property 'foo' does not exist on type 'Bar'.")]
    [InlineData("E   ConnectionRefusedError: [Errno 111] Connection refused")]
    [InlineData("cp: cannot create regular file 'x': Permission denied")]
    public async Task A_failing_pre_commit_hook_is_reported_as_a_hook_failure(string hookOutput)
    {
        var repo = _sandbox.CreateRepository();
        WriteHook(repo.Path, "pre-commit", $"cat <<'OUT'\n{hookOutput}\nOUT\nexit 1");
        repo.WriteFile("f.txt", "x\n");
        await Git.StageAsync(repo.Path, ["f.txt"], Ct);

        var act = () => Git.CommitAsync(repo.Path, new GitCommitOptions("msg"), Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitCommandFailed);
        error.Message.Should().Contain("hook");
        error.Detail.Should().Contain(hookOutput.Split(' ')[^1]);
    }

    [Fact]
    public async Task A_failing_pre_push_hook_is_reported_as_a_hook_failure()
    {
        var repo = _sandbox.CreateRepository();
        _sandbox.CreateRemoteFor(repo);
        repo.Commit("More", ("f.txt", "x\n"));
        WriteHook(repo.Path, "pre-push", "echo 'ConnectionRefusedError: Connection refused'\nexit 1");

        var act = () => Git.PushAsync(repo.Path, new GitPushOptions(), null, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitCommandFailed);
        error.Message.Should().Contain("hook");
    }

    [Fact]
    public async Task Git_own_commit_errors_are_still_translated_when_hooks_exist()
    {
        var repo = _sandbox.CreateRepository();
        WriteHook(repo.Path, "pre-commit", "exit 0");

        var act = () => Git.CommitAsync(repo.Path, new GitCommitOptions("msg"), Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NothingToCommit);
    }

    // GIT-05 ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Truncated_output_is_detected_whatever_the_line_ending(string newline)
    {
        var result = new GitResult("git status", 0, "a\n" + GitCli.OutputTruncatedMarker + newline, string.Empty);

        result.IsOutputTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_status_too_large_to_capture_is_reported_instead_of_a_clean_tree()
    {
        var runner = new FakeGitRunner
        {
            Respond = spec => FakeGitRunner.Command(spec)[0] == "status"
                ? FakeGitRunner.Ok(GitCli.OutputTruncatedMarker + "\r\n")
                : FakeGitRunner.Ok(),
        };
        var service = new GitService(_sandbox.Settings, runner, [], _sandbox.Environment);
        using var dir = new Infrastructure.TempDirectory();

        var act = () => service.GetStatusAsync(dir.Path, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.FileTooLarge);
    }

    // GIT-06 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Discarding_a_file_that_replaced_a_folder_restores_the_folder()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Folder", ("d/f.txt", "inside\n"));
        repo.Git("rm", "-q", "-r", "d");
        repo.WriteFile("d", "now a file\n");
        repo.Git("add", "d");

        await Git.DiscardAsync(repo.Path, ["d"], Ct);

        GitSandbox.ReadFile(repo.Path, "d/f.txt").Should().Be("inside\n");
        repo.Git("status", "--porcelain").Should().BeEmpty();
    }

    // GIT-07 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_background_process_started_by_a_hook_does_not_hold_the_commit()
    {
        var repo = _sandbox.CreateRepository();
        WriteHook(repo.Path, "post-commit", "sleep 20 &");
        repo.WriteFile("f.txt", "x\n");
        await Git.StageAsync(repo.Path, ["f.txt"], Ct);
        var stopwatch = Stopwatch.StartNew();

        await Git.CommitAsync(repo.Path, new GitCommitOptions("msg"), Ct);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    public void Dispose() => _sandbox.Dispose();
}
