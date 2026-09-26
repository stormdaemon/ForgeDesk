using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Settings;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceRemoteTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    /// <summary>A working copy "mine", its bare remote, and a second working copy "theirs".</summary>
    private (string Mine, string Remote, string Theirs) CreateTopology()
    {
        var seed = _sandbox.CreateRepository();
        var bare = _sandbox.CreateRemoteFor(seed);
        // Clones through file:// so fetches use the pack protocol, as with a real server.
        var remoteUrl = new Uri(bare.Path).AbsoluteUri;
        return (_sandbox.Clone(remoteUrl), bare.Path, _sandbox.Clone(remoteUrl));
    }

    private void UsePullStrategy(PullStrategy strategy) =>
        _sandbox.Settings.Current.Returns(AppSettings.Default with { PullStrategy = strategy });

    [Fact]
    public async Task Remotes_are_listed()
    {
        var (mine, remote, _) = CreateTopology();
        // Neutral hosts: a machine's url.<base>.insteadOf settings may rewrite well-known ones.
        _sandbox.Run(mine, "remote", "add", "upstream", "https://git.example.com/octo/app.git");
        _sandbox.Run(mine, "remote", "set-url", "--push", "upstream", "ssh://git@git.example.com/octo/app.git");

        var remotes = await Git.GetRemotesAsync(mine, Ct);

        remotes.Should().HaveCount(2);
        remotes[0].Should().Be(new GitRemote("origin", new Uri(remote).AbsoluteUri, new Uri(remote).AbsoluteUri));
        remotes[1].Should().Be(new GitRemote("upstream", "https://git.example.com/octo/app.git", "ssh://git@git.example.com/octo/app.git"));
    }

    [Fact]
    public async Task Fetch_updates_remote_branches_and_reports_progress()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Commit(theirs, "Theirs", null, ("theirs.txt", "t\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");
        var reports = new List<GitProgress>();

        await Git.FetchAsync(mine, new SyncProgress<GitProgress>(reports.Add), Ct);

        (await Git.GetStatusAsync(mine, Ct)).Behind.Should().Be(1);
        reports.Should().Contain(r => r.Stage == "Counting objects" && r.Percent == 100);
    }

    [Fact]
    public async Task Fetch_prunes_deleted_remote_branches()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Run(theirs, "push", "-q", "origin", "main:temporary");
        await Git.FetchAsync(mine, cancellationToken: Ct);
        (await Git.GetBranchesAsync(mine, cancellationToken: Ct)).Should().Contain(b => b.Name == "origin/temporary");
        _sandbox.Run(theirs, "push", "-q", "origin", "--delete", "temporary");

        await Git.FetchAsync(mine, cancellationToken: Ct);

        (await Git.GetBranchesAsync(mine, cancellationToken: Ct)).Should().NotContain(b => b.Name == "origin/temporary");
    }

    [Fact]
    public async Task Pull_fast_forwards()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Commit(theirs, "Theirs", null, ("theirs.txt", "t\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");

        await Git.PullAsync(mine, cancellationToken: Ct);

        File.Exists(Path.Combine(mine, "theirs.txt")).Should().BeTrue();
        var status = await Git.GetStatusAsync(mine, Ct);
        (status.Ahead, status.Behind).Should().Be((0, 0));
    }

    [Fact]
    public async Task Fast_forward_only_pull_refuses_diverged_branches()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Commit(theirs, "Theirs", null, ("theirs.txt", "t\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");
        _sandbox.Commit(mine, "Mine", null, ("mine.txt", "m\n"));

        var act = () => Git.PullAsync(mine, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NonFastForward);
        error.Hint.Should().Contain("pull strategy");
    }

    [Fact]
    public async Task Merge_and_rebase_pull_strategies_integrate_diverged_branches()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Commit(theirs, "Theirs", null, ("theirs.txt", "t\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");
        _sandbox.Commit(mine, "Mine", null, ("mine.txt", "m\n"));

        UsePullStrategy(PullStrategy.Merge);
        await Git.PullAsync(mine, cancellationToken: Ct);

        var merged = await Git.GetLogAsync(mine, new GitLogQuery { Take = 1 }, Ct);
        merged[0].IsMerge.Should().BeTrue();
        _sandbox.Run(mine, "push", "-q", "origin", "main");

        _sandbox.Run(theirs, "pull", "-q", "--ff-only");
        _sandbox.Commit(theirs, "Theirs again", null, ("theirs2.txt", "t2\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");
        _sandbox.Commit(mine, "Mine again", null, ("mine2.txt", "m2\n"));
        UsePullStrategy(PullStrategy.Rebase);
        await Git.PullAsync(mine, cancellationToken: Ct);

        var rebased = await Git.GetLogAsync(mine, new GitLogQuery { Take = 2 }, Ct);
        rebased.Select(c => c.Subject).Should().Equal("Mine again", "Theirs again");
        (await Git.GetStatusAsync(mine, Ct)).Ahead.Should().Be(1);
    }

    [Fact]
    public async Task Pull_without_upstream()
    {
        var (mine, _, _) = CreateTopology();
        _sandbox.Run(mine, "checkout", "-q", "-b", "local-only");

        var act = () => Git.PullAsync(mine, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NoUpstream);
    }

    [Fact]
    public async Task Push_sends_commits_to_the_upstream()
    {
        var (mine, remote, _) = CreateTopology();
        _sandbox.Commit(mine, "Mine", null, ("mine.txt", "m\n"));

        await Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);

        _sandbox.Run(remote, "log", "-1", "--format=%s", "main").Should().Be("Mine");
        (await Git.GetStatusAsync(mine, Ct)).Ahead.Should().Be(0);
    }

    [Fact]
    public async Task Push_publishes_a_new_branch_and_sets_its_upstream()
    {
        var (mine, remote, _) = CreateTopology();
        await Git.CreateBranchAsync(mine, "feature/new", cancellationToken: Ct);
        _sandbox.Commit(mine, "Feature", null, ("f.txt", "f\n"));

        await Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);

        _sandbox.Run(remote, "log", "-1", "--format=%s", "feature/new").Should().Be("Feature");
        (await Git.GetStatusAsync(mine, Ct)).Upstream.Should().Be("origin/feature/new");
    }

    [Fact]
    public async Task Push_without_upstream_when_not_allowed_to_set_it()
    {
        var (mine, _, _) = CreateTopology();
        await Git.CreateBranchAsync(mine, "unpublished", cancellationToken: Ct);

        var act = () => Git.PushAsync(mine, new GitPushOptions(SetUpstream: false), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NoUpstream);
    }

    [Fact]
    public async Task Push_honors_an_upstream_with_a_different_name()
    {
        var (mine, remote, _) = CreateTopology();
        _sandbox.Run(mine, "checkout", "-q", "-b", "local-name", "--track", "origin/main");
        _sandbox.Commit(mine, "Via other name", null, ("o.txt", "o\n"));

        await Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);

        _sandbox.Run(remote, "log", "-1", "--format=%s", "main").Should().Be("Via other name");
        _sandbox.TryRun(remote, "rev-parse", "--verify", "-q", "refs/heads/local-name").ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task Push_behind_the_remote_is_non_fast_forward_and_the_lease_protects_their_work()
    {
        var (mine, remote, theirs) = CreateTopology();
        _sandbox.Commit(theirs, "Theirs", null, ("theirs.txt", "t\n"));
        _sandbox.Run(theirs, "push", "-q", "origin", "main");
        _sandbox.Commit(mine, "Mine", null, ("mine.txt", "m\n"));

        var push = () => Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);
        var force = () => Git.PushAsync(mine, new GitPushOptions(Force: true), cancellationToken: Ct);

        var error = (await push.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NonFastForward);
        error.Message.Should().Be("The remote has commits you don't have. Pull first.");
        // Never fetched their commit: forcing would destroy it, so the lease refuses.
        (await force.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NonFastForward);
        _sandbox.Run(remote, "log", "-1", "--format=%s", "main").Should().Be("Theirs");
    }

    [Fact]
    public async Task Force_push_after_amending_a_pushed_commit()
    {
        var (mine, remote, _) = CreateTopology();
        _sandbox.Commit(mine, "Mine", null, ("mine.txt", "m\n"));
        await Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);
        await Git.CommitAsync(mine, new GitCommitOptions("Mine, amended", Amend: true), Ct);

        var push = () => Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);

        (await push.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NonFastForward);
        await Git.PushAsync(mine, new GitPushOptions(Force: true), cancellationToken: Ct);
        _sandbox.Run(remote, "log", "-1", "--format=%s", "main").Should().Be("Mine, amended");
    }

    [Fact]
    public async Task Pushing_a_detached_head_is_refused()
    {
        var (mine, _, _) = CreateTopology();
        _sandbox.Run(mine, "checkout", "-q", "--detach", "HEAD");

        var act = () => Git.PushAsync(mine, new GitPushOptions(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.DetachedHead);
    }

    [Fact]
    public async Task Pushing_without_any_remote_is_refused()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.PushAsync(repo.Path, new GitPushOptions(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NoUpstream);
    }

    [Fact]
    public async Task Pushing_an_empty_branch_says_there_is_nothing_to_push()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        var bare = _sandbox.NewDirectory("bare");
        _sandbox.Run(bare, "init", "-q", "--bare");
        repo.Git("remote", "add", "origin", bare);

        var act = () => Git.PushAsync(repo.Path, new GitPushOptions(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NothingToCommit);
    }

    [Fact]
    public async Task Tags_are_pushed_individually_or_with_the_branch()
    {
        var (mine, remote, _) = CreateTopology();
        await Git.CreateTagAsync(mine, "v1.0", "First release", cancellationToken: Ct);
        await Git.CreateTagAsync(mine, "v1.1", cancellationToken: Ct);

        await Git.PushTagAsync(mine, "v1.0", cancellationToken: Ct);
        _sandbox.Run(remote, "tag", "--list").Should().Be("v1.0");

        _sandbox.Commit(mine, "Next", null, ("n.txt", "n\n"));
        await Git.PushAsync(mine, new GitPushOptions(Tags: true), cancellationToken: Ct);
        _sandbox.Run(remote, "tag", "--list").Split('\n').Should().Equal("v1.0", "v1.1");
    }

    [Fact]
    public async Task Pushing_an_unknown_tag_is_not_found()
    {
        var (mine, _, _) = CreateTopology();

        var act = () => Git.PushTagAsync(mine, "v9.9", cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NotFound);
        error.Message.Should().Contain("v9.9");
    }

    [Fact]
    public async Task Pushing_a_tag_that_exists_remotely_with_another_target()
    {
        var (mine, _, theirs) = CreateTopology();
        _sandbox.Run(theirs, "tag", "v1.0");
        _sandbox.Run(theirs, "push", "-q", "origin", "v1.0");
        _sandbox.Commit(mine, "Different", null, ("d.txt", "d\n"));
        await Git.CreateTagAsync(mine, "v1.0", cancellationToken: Ct);

        var act = () => Git.PushTagAsync(mine, "v1.0", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
    }

    public void Dispose() => _sandbox.Dispose();
}
