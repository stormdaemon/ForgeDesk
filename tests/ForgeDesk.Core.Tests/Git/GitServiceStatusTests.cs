using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceStatusTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    [Fact]
    public async Task Clean_repository()
    {
        var repo = _sandbox.CreateRepository();

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.IsClean.Should().BeTrue();
        status.Branch.Should().Be("main");
        status.HeadSha.Should().Be(repo.Git("rev-parse", "HEAD"));
        status.State.Should().Be(GitRepositoryState.Normal);
        status.Upstream.Should().BeNull();
    }

    [Fact]
    public async Task Every_kind_of_change_is_reported()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("keep.txt", "keep\n"), ("edit.txt", "1\n"), ("remove.txt", "r\n"), ("both.txt", "b\n"));
        repo.WriteFile("edit.txt", "2\n");
        File.Delete(repo.Combine("remove.txt"));
        repo.WriteFile("staged.txt", "s\n");
        repo.Git("add", "staged.txt");
        repo.WriteFile("both.txt", "b2\n");
        repo.Git("add", "both.txt");
        repo.WriteFile("both.txt", "b3\n");
        repo.WriteFile("new/untracked file.txt", "u\n");

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        GitStatusEntry Entry(string path) => status.Entries.Single(e => e.Path == path);

        status.Entries.Should().HaveCount(5);
        Entry("edit.txt").Should().Match<GitStatusEntry>(e => e.IndexState == GitFileState.Unmodified && e.WorkTreeState == GitFileState.Modified);
        Entry("remove.txt").WorkTreeState.Should().Be(GitFileState.Deleted);
        Entry("staged.txt").Should().Match<GitStatusEntry>(e => e.IndexState == GitFileState.Added && e.HasStagedChanges && !e.HasUnstagedChanges);
        Entry("both.txt").Should().Match<GitStatusEntry>(e => e.HasStagedChanges && e.HasUnstagedChanges);
        Entry("new/untracked file.txt").IsUntracked.Should().BeTrue();
        status.Staged.Select(e => e.Path).Should().BeEquivalentTo("staged.txt", "both.txt");
        status.Unstaged.Select(e => e.Path).Should().BeEquivalentTo("edit.txt", "remove.txt", "both.txt", "new/untracked file.txt");
    }

    [Fact]
    public async Task Staged_rename_reports_the_original_path()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Doc", ("docs/guide.md", "line 1\nline 2\nline 3\n"));
        repo.Git("mv", "docs/guide.md", "docs/manual.md");

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        var entry = status.Entries.Should().ContainSingle().Subject;
        entry.Path.Should().Be("docs/manual.md");
        entry.OriginalPath.Should().Be("docs/guide.md");
        entry.IndexState.Should().Be(GitFileState.Renamed);
    }

    [Fact]
    public async Task Merge_conflict_is_conflicted_and_the_repository_is_merging()
    {
        var repo = _sandbox.CreateRepository();
        CreateConflict(repo);

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.State.Should().Be(GitRepositoryState.Merging);
        status.Conflicted.Should().ContainSingle().Which.Path.Should().Be("shared.txt");
        status.Entries.Single().IndexState.Should().Be(GitFileState.Conflicted);
    }

    [Fact]
    public async Task Unborn_repository_lists_its_first_files()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        repo.WriteFile("first.txt", "hello\n");

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.IsUnborn.Should().BeTrue();
        status.HeadSha.Should().BeNull();
        status.Branch.Should().Be("main");
        status.Entries.Should().ContainSingle(e => e.Path == "first.txt" && e.IsUntracked);
    }

    [Fact]
    public async Task Detached_head()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Second", ("b.txt", "b\n"));
        repo.Git("checkout", "-q", "--detach", "HEAD~1");

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.IsDetached.Should().BeTrue();
        status.Branch.Should().BeNull();
        status.HeadSha.Should().Be(repo.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Ahead_and_behind_against_the_upstream()
    {
        var repo = _sandbox.CreateRepository();
        var bare = _sandbox.CreateRemoteFor(repo);
        var other = _sandbox.Clone(bare.Path);
        _sandbox.Commit(other, "Theirs 1", null, ("theirs1.txt", "1\n"));
        _sandbox.Commit(other, "Theirs 2", null, ("theirs2.txt", "2\n"));
        _sandbox.Run(other, "push", "-q", "origin", "main");
        repo.Commit("Mine", ("mine.txt", "m\n"));
        repo.Git("fetch", "-q");

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.Upstream.Should().Be("origin/main");
        status.Ahead.Should().Be(1);
        status.Behind.Should().Be(2);
    }

    [Fact]
    public async Task Stash_count_is_reported()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        repo.Git("stash", "push", "-q");
        repo.WriteFile("README.md", "changed again\n");
        repo.Git("stash", "push", "-q");

        (await Git.GetStatusAsync(repo.Path, Ct)).StashCount.Should().Be(2);
    }

    [Fact]
    public async Task Status_does_not_need_the_index_lock()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        File.WriteAllText(repo.Combine(".git", "index.lock"), string.Empty);

        var status = await Git.GetStatusAsync(repo.Path, Ct);

        status.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Writes_while_locked_report_a_locked_repository()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        File.WriteAllText(repo.Combine(".git", "index.lock"), string.Empty);

        var act = () => Git.StageAllAsync(repo.Path, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.RepositoryLocked);
    }

    [Theory]
    [InlineData("rebase-merge", true, GitRepositoryState.Rebasing)]
    [InlineData("rebase-apply", true, GitRepositoryState.Rebasing)]
    [InlineData("MERGE_HEAD", false, GitRepositoryState.Merging)]
    [InlineData("CHERRY_PICK_HEAD", false, GitRepositoryState.CherryPicking)]
    [InlineData("REVERT_HEAD", false, GitRepositoryState.Reverting)]
    [InlineData("BISECT_LOG", false, GitRepositoryState.Bisecting)]
    public void Repository_state_comes_from_marker_files(string marker, bool isDirectory, GitRepositoryState expected)
    {
        var gitDirectory = _sandbox.NewDirectory("gitdir");
        if (isDirectory)
        {
            Directory.CreateDirectory(Path.Combine(gitDirectory, marker));
        }
        else
        {
            File.WriteAllText(Path.Combine(gitDirectory, marker), string.Empty);
        }

        GitService.DetectState(gitDirectory).Should().Be(expected);
        GitService.DetectState(_sandbox.NewDirectory("empty")).Should().Be(GitRepositoryState.Normal);
    }

    internal static void CreateConflict(Infrastructure.TestRepository repo)
    {
        repo.Commit("Shared", ("shared.txt", "a\nb\nc\n"));
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Commit("Feature change", ("shared.txt", "a\nFEATURE\nc\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("Main change", ("shared.txt", "a\nMAIN\nc\n"));
        try
        {
            repo.Git("merge", "--no-edit", "feature");
        }
        catch (InvalidOperationException)
        {
            // Expected: the merge stops on the conflict.
        }
    }

    public void Dispose() => _sandbox.Dispose();
}
