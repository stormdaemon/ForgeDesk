using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceBranchTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    [Fact]
    public async Task Branches_with_upstreams_tracking_and_gone_remotes()
    {
        var repo = _sandbox.CreateRepository();
        _sandbox.CreateRemoteFor(repo);
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Git("push", "-q", "-u", "origin", "feature");
        repo.Git("push", "-q", "origin", "--delete", "feature");
        repo.Git("fetch", "-q", "--prune");
        repo.Git("checkout", "-q", "main");
        repo.Commit("Ahead", ("ahead.txt", "a\n"));

        var branches = await Git.GetBranchesAsync(repo.Path, cancellationToken: Ct);

        branches.Select(b => b.Name).Should().Equal("feature", "main", "origin/main");
        var main = branches.Single(b => b.Name == "main");
        main.Should().Match<GitBranch>(b => b.IsCurrent && !b.IsRemote && b.Upstream == "origin/main" && b.Ahead == 1 && b.Behind == 0);
        main.FullName.Should().Be("refs/heads/main");
        main.TipSha.Should().Be(repo.Git("rev-parse", "HEAD"));
        main.TipSubject.Should().Be("Ahead");
        main.TipAuthor.Should().Be("Test User");
        main.TipDate.Should().NotBeNull();
        branches.Single(b => b.Name == "feature").UpstreamGone.Should().BeTrue();
        branches.Single(b => b.Name == "origin/main").Should().Match<GitBranch>(b => b.IsRemote && b.RemoteName == "origin" && !b.IsCurrent);
        (await Git.GetBranchesAsync(repo.Path, includeRemote: false, Ct)).Should().OnlyContain(b => !b.IsRemote);
    }

    [Fact]
    public async Task Create_switch_rename_and_delete()
    {
        var repo = _sandbox.CreateRepository();

        await Git.CreateBranchAsync(repo.Path, "feature/login", cancellationToken: Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Branch.Should().Be("feature/login");

        await Git.CreateBranchAsync(repo.Path, "parked", "main", checkout: false, Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Branch.Should().Be("feature/login");

        await Git.CheckoutAsync(repo.Path, "main", Ct);
        await Git.RenameBranchAsync(repo.Path, "parked", "archived", Ct);
        await Git.DeleteBranchAsync(repo.Path, "feature/login", cancellationToken: Ct);

        (await Git.GetBranchesAsync(repo.Path, false, Ct)).Select(b => b.Name).Should().Equal("archived", "main");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("double..dot")]
    [InlineData("ends.lock")]
    [InlineData("-starts-with-dash")]
    [InlineData("tilde~1")]
    [InlineData("@{-1}")]
    [InlineData("")]
    public async Task Invalid_branch_names_are_rejected_with_the_rules(string name)
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.CreateBranchAsync(repo.Path, name, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        if (name.Length > 0)
        {
            error.Hint.Should().Contain("can't contain");
        }
    }

    [Fact]
    public async Task Existing_branch_name_is_reported()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("branch", "taken");

        var act = () => Git.CreateBranchAsync(repo.Path, "taken", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task Checkout_with_conflicting_local_changes_is_a_dirty_tree()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("checkout", "-q", "-b", "other");
        repo.Commit("Other", ("README.md", "other version\n"));
        repo.Git("checkout", "-q", "main");
        repo.WriteFile("README.md", "local edit\n");

        var act = () => Git.CheckoutAsync(repo.Path, "other", Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.DirtyWorkingTree);
        error.Detail.Should().Contain("README.md");
        (await Git.GetStatusAsync(repo.Path, Ct)).Branch.Should().Be("main");
    }

    [Fact]
    public async Task Checkout_of_an_unknown_branch()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.CheckoutAsync(repo.Path, "nope", Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Remote_branches_are_checked_out_as_tracking_branches()
    {
        var repo = _sandbox.CreateRepository();
        var bare = _sandbox.CreateRemoteFor(repo);
        var other = _sandbox.Clone(bare.Path);
        _sandbox.Run(other, "checkout", "-q", "-b", "feature/x");
        _sandbox.Commit(other, "Remote feature", null, ("x.txt", "x\n"));
        _sandbox.Run(other, "push", "-q", "origin", "feature/x");
        repo.Git("fetch", "-q");

        await Git.CheckoutRemoteBranchAsync(repo.Path, "origin/feature/x", cancellationToken: Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Branch.Should().Be("feature/x");
        status.Upstream.Should().Be("origin/feature/x");

        // Again: the tracking branch exists, so this just switches back to it.
        await Git.CheckoutAsync(repo.Path, "main", Ct);
        await Git.CheckoutRemoteBranchAsync(repo.Path, "origin/feature/x", cancellationToken: Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Branch.Should().Be("feature/x");
    }

    [Fact]
    public async Task Remote_branch_checkout_with_a_custom_name_and_via_checkout()
    {
        var repo = _sandbox.CreateRepository();
        var bare = _sandbox.CreateRemoteFor(repo);
        var other = _sandbox.Clone(bare.Path);
        _sandbox.Run(other, "push", "-q", "origin", "main:release");
        repo.Git("fetch", "-q");

        await Git.CheckoutRemoteBranchAsync(repo.Path, "origin/release", "my-release", Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Upstream.Should().Be("origin/release");

        await Git.CheckoutAsync(repo.Path, "main", Ct);
        repo.Git("branch", "-D", "my-release");
        await Git.CheckoutAsync(repo.Path, "origin/release", Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Branch.Should().Be("release");
    }

    [Fact]
    public async Task Remote_branch_checkout_refuses_an_unrelated_local_branch_with_the_same_name()
    {
        var repo = _sandbox.CreateRepository();
        _sandbox.CreateRemoteFor(repo);
        repo.Git("push", "-q", "origin", "main:shared");
        repo.Git("fetch", "-q");
        repo.Git("branch", "shared");

        var act = () => Git.CheckoutRemoteBranchAsync(repo.Path, "origin/shared", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task Deleting_an_unmerged_branch_needs_force()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("checkout", "-q", "-b", "experiment");
        repo.Commit("Experiment", ("e.txt", "e\n"));
        repo.Git("checkout", "-q", "main");

        var act = () => Git.DeleteBranchAsync(repo.Path, "experiment", cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitCommandFailed);
        error.Message.Should().Contain("experiment");
        error.Hint.Should().Contain("force");

        await Git.DeleteBranchAsync(repo.Path, "experiment", force: true, Ct);
        (await Git.GetBranchesAsync(repo.Path, false, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_the_current_branch_is_refused()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.DeleteBranchAsync(repo.Path, "main", force: true, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Merged_branches_exclude_the_target()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("branch", "merged-one");
        repo.Git("checkout", "-q", "-b", "open-work");
        repo.Commit("Open", ("o.txt", "o\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("checkout", "-q", "-b", "done");
        repo.Commit("Done", ("d.txt", "d\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-edit", "done");

        var merged = await Git.GetMergedBranchesAsync(repo.Path, cancellationToken: Ct);
        var intoOpenWork = await Git.GetMergedBranchesAsync(repo.Path, "open-work", Ct);

        merged.Should().BeEquivalentTo("merged-one", "done");
        intoOpenWork.Should().BeEquivalentTo("merged-one");
    }

    [Fact]
    public async Task Default_branch_prefers_the_remote_head_then_main_or_master()
    {
        var masterRepo = _sandbox.CreateRepository(branch: "master");
        var trunkRepo = _sandbox.CreateRepository(branch: "trunk");
        var cloned = _sandbox.Clone(_sandbox.CreateRemoteFor(_sandbox.CreateRepository()).Path);
        _sandbox.Run(cloned, "checkout", "-q", "-b", "elsewhere");

        (await Git.GetDefaultBranchAsync(masterRepo.Path, Ct)).Should().Be("master");
        (await Git.GetDefaultBranchAsync(trunkRepo.Path, Ct)).Should().Be("trunk");
        (await Git.GetDefaultBranchAsync(cloned, Ct)).Should().Be("main");
    }

    [Fact]
    public async Task Merge_fast_forwards_and_conflicts_leave_the_repository_merging()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("checkout", "-q", "-b", "ff");
        repo.Commit("Fast forward", ("ff.txt", "ff\n"));
        repo.Git("checkout", "-q", "main");

        await Git.MergeAsync(repo.Path, "ff", Ct);
        File.Exists(repo.Combine("ff.txt")).Should().BeTrue();

        repo.Git("checkout", "-q", "-b", "theirs");
        repo.Commit("Theirs", ("README.md", "theirs\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("Ours", ("README.md", "ours\n"));

        var act = () => Git.MergeAsync(repo.Path, "theirs", Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.MergeConflict);
        error.Message.Should().Contain("README.md");
        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.State.Should().Be(GitRepositoryState.Merging);
        status.Conflicted.Should().ContainSingle();

        await Git.AbortMergeAsync(repo.Path, Ct);

        status = await Git.GetStatusAsync(repo.Path, Ct);
        status.State.Should().Be(GitRepositoryState.Normal);
        status.IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_into_a_dirty_tree_and_aborting_nothing()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("checkout", "-q", "-b", "theirs");
        repo.Commit("Theirs", ("README.md", "theirs\n"));
        repo.Git("checkout", "-q", "main");
        repo.WriteFile("README.md", "uncommitted\n");

        var merge = () => Git.MergeAsync(repo.Path, "theirs", Ct);
        var abort = () => Git.AbortMergeAsync(repo.Path, Ct);

        (await merge.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.DirtyWorkingTree);
        (await abort.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    public void Dispose() => _sandbox.Dispose();
}
