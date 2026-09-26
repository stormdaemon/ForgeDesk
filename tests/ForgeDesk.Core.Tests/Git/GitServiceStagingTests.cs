using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceStagingTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    private static string Lines(int from, int to) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"line {i}\n"));

    private async Task<GitStatusEntry?> EntryAsync(string repo, string path) =>
        (await Git.GetStatusAsync(repo, Ct)).Entries.SingleOrDefault(e => e.Path == path);

    [Fact]
    public async Task Stage_and_unstage_files()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        repo.WriteFile("new.txt", "new\n");

        await Git.StageAsync(repo.Path, ["README.md", "new.txt"], Ct);

        (await Git.GetStatusAsync(repo.Path, Ct)).Staged.Select(e => e.Path).Should().BeEquivalentTo("README.md", "new.txt");

        await Git.UnstageAsync(repo.Path, ["README.md", "new.txt"], Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Staged.Should().BeEmpty();
        status.Entries.Single(e => e.Path == "new.txt").IsUntracked.Should().BeTrue();
    }

    [Fact]
    public async Task Staging_a_deleted_file_stages_the_deletion()
    {
        var repo = _sandbox.CreateRepository();
        File.Delete(repo.Combine("README.md"));

        await Git.StageAsync(repo.Path, ["README.md"], Ct);

        (await EntryAsync(repo.Path, "README.md"))!.IndexState.Should().Be(GitFileState.Deleted);
    }

    [Fact]
    public async Task Paths_with_glob_characters_are_taken_literally()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("a[1].txt", "x\n");
        repo.WriteFile("a1.txt", "y\n");
        if (!OperatingSystem.IsWindows())
        {
            // '*' is not a valid file name character on Windows.
            repo.WriteFile("star*.txt", "z\n");
        }

        await Git.StageAsync(repo.Path, ["a[1].txt"], Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Staged.Select(e => e.Path).Should().Equal("a[1].txt");
    }

    [Fact]
    public async Task Many_files_are_staged_in_batches()
    {
        var repo = _sandbox.CreateRepository();
        var paths = Enumerable.Range(0, 400).Select(i => $"generated/a-rather-long-folder-name-for-batching/file-number-{i:0000}.txt").ToList();
        foreach (var path in paths)
        {
            repo.WriteFile(path, path);
        }

        await Git.StageAsync(repo.Path, paths, Ct);

        (await Git.GetStatusAsync(repo.Path, Ct)).Staged.Should().HaveCount(400);
    }

    [Fact]
    public async Task Stage_all_and_unstage_all()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        repo.WriteFile("new/file.txt", "n\n");

        await Git.StageAllAsync(repo.Path, Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Staged.Should().HaveCount(2);

        await Git.UnstageAllAsync(repo.Path, Ct);
        (await Git.GetStatusAsync(repo.Path, Ct)).Staged.Should().BeEmpty();
    }

    [Fact]
    public async Task Unstaging_works_before_the_first_commit()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        repo.WriteFile("a.txt", "a\n");
        repo.WriteFile("b.txt", "b\n");
        repo.Git("add", "-A");
        repo.WriteFile("a.txt", "a changed after staging\n");

        await Git.UnstageAsync(repo.Path, ["a.txt"], Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Entries.Single(e => e.Path == "a.txt").IsUntracked.Should().BeTrue();
        status.Entries.Single(e => e.Path == "b.txt").IndexState.Should().Be(GitFileState.Added);

        await Git.UnstageAllAsync(repo.Path, Ct);

        (await Git.GetStatusAsync(repo.Path, Ct)).Entries.Should().OnlyContain(e => e.IsUntracked);
        File.ReadAllText(repo.Combine("a.txt")).Should().Be("a changed after staging\n");
    }

    [Fact]
    public async Task Unstaging_a_rename_restores_both_names()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("old.txt", Lines(1, 5)));
        repo.Git("mv", "old.txt", "new.txt");

        await Git.UnstageAsync(repo.Path, ["new.txt"], Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Staged.Should().BeEmpty();
        status.Entries.Single(e => e.Path == "old.txt").WorkTreeState.Should().Be(GitFileState.Deleted);
        status.Entries.Single(e => e.Path == "new.txt").IsUntracked.Should().BeTrue();
    }

    [Fact]
    public async Task Staging_and_unstaging_individual_hunks()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", Lines(1, 30)));
        var edited = Lines(1, 30).Replace("line 2\n", "line TWO\n", StringComparison.Ordinal).Replace("line 29\n", "line TWENTY-NINE\n", StringComparison.Ordinal);
        repo.WriteFile("f.txt", edited);
        var diff = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);
        diff.Hunks.Should().HaveCount(2);

        await Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[1], reverse: false, Ct);

        var staged = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.Staged, Ct);
        var unstaged = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);
        staged.Hunks.Should().ContainSingle().Which.Lines.Should().Contain(l => l.Kind == DiffLineKind.Added && l.Text == "line TWENTY-NINE");
        unstaged.Hunks.Should().ContainSingle().Which.Lines.Should().Contain(l => l.Kind == DiffLineKind.Added && l.Text == "line TWO");
        File.ReadAllText(repo.Combine("f.txt")).Should().Be(edited, "the working tree is never touched");

        await Git.ApplyHunkAsync(repo.Path, staged, staged.Hunks[0], reverse: true, Ct);

        (await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.Staged, Ct)).Hunks.Should().BeEmpty();
        (await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct)).Hunks.Should().HaveCount(2);
    }

    [Fact]
    public async Task Staging_a_hunk_of_an_untracked_file()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("new.txt", "alpha\nbeta\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.WorkingTree, Ct);

        await Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[0], reverse: false, Ct);

        var entry = await EntryAsync(repo.Path, "new.txt");
        entry!.IndexState.Should().Be(GitFileState.Added);
        entry.WorkTreeState.Should().Be(GitFileState.Unmodified);
        repo.Git("show", ":new.txt").Should().Be("alpha\nbeta");
    }

    [Fact]
    public async Task Staging_part_of_a_new_file_with_a_trimmed_hunk()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("new.txt", "keep\ndrop\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.WorkingTree, Ct);
        var firstLineOnly = diff.Hunks[0] with { Lines = diff.Hunks[0].Lines.Take(1).ToList() };

        await Git.ApplyHunkAsync(repo.Path, diff, firstLineOnly, reverse: false, Ct);

        repo.Git("show", ":new.txt").Should().Be("keep");
        (await EntryAsync(repo.Path, "new.txt"))!.Should().Match<GitStatusEntry>(e => e.IndexState == GitFileState.Added && e.WorkTreeState == GitFileState.Modified);
    }

    [Fact]
    public async Task Unstaging_the_hunk_of_a_staged_new_file_makes_it_untracked_again()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("new.txt", "content\n");
        repo.Git("add", "new.txt");
        var staged = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.Staged, Ct);

        await Git.ApplyHunkAsync(repo.Path, staged, staged.Hunks[0], reverse: true, Ct);

        (await EntryAsync(repo.Path, "new.txt"))!.IsUntracked.Should().BeTrue();
    }

    [Fact]
    public async Task Hunks_of_crlf_files_apply_exactly()
    {
        var repo = _sandbox.CreateRepository();
        var original = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\r\n"));
        repo.Commit("Base", ("win.txt", original));
        repo.WriteFile("win.txt", original.Replace("line 3\r\n", "line three\r\n", StringComparison.Ordinal).Replace("line 18\r\n", "line eighteen\r\n", StringComparison.Ordinal));
        var diff = await Git.GetFileDiffAsync(repo.Path, "win.txt", DiffTarget.WorkingTree, Ct);

        await Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[0], reverse: false, Ct);

        var index = repo.Git("show", ":win.txt");
        index.Should().Contain("line three\r\n").And.Contain("line 18\r\n").And.NotContain("line eighteen");
        (await Git.GetFileDiffAsync(repo.Path, "win.txt", DiffTarget.WorkingTree, Ct)).Hunks.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stale_hunk_is_refused_with_a_clear_message()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", Lines(1, 5)));
        repo.WriteFile("f.txt", Lines(1, 5).Replace("line 3\n", "line three\n", StringComparison.Ordinal));
        var diff = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);
        repo.Commit("Someone else changed it", ("f.txt", "completely\ndifferent\n"));

        var act = () => Git.ApplyHunkAsync(repo.Path, diff, diff.Hunks[0], reverse: false, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Message.Should().Be("This change no longer matches the file.");
        error.Detail.Should().Contain("git apply");
    }

    [Fact]
    public async Task A_failed_hunk_on_an_untracked_file_leaves_it_untracked()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("new.txt", "one\n");
        var diff = await Git.GetFileDiffAsync(repo.Path, "new.txt", DiffTarget.WorkingTree, Ct);
        var bogus = diff.Hunks[0] with { OldStart = 5, OldCount = 1, Lines = [new DiffLine(DiffLineKind.Removed, "not there", 5, null)] };

        var act = () => Git.ApplyHunkAsync(repo.Path, diff, bogus, reverse: false, Ct);

        await act.Should().ThrowAsync<ForgeException>();
        (await EntryAsync(repo.Path, "new.txt"))!.IsUntracked.Should().BeTrue();
    }

    [Fact]
    public async Task Discard_restores_tracked_files_and_deletes_untracked_ones()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("keep.txt", "original\n"), ("deleted.txt", "d\n"));
        repo.WriteFile("keep.txt", "edited\n");
        File.Delete(repo.Combine("deleted.txt"));
        repo.WriteFile("scratch/deep/tmp.txt", "tmp\n");
        repo.WriteFile("scratch/other.txt", "other\n");

        await Git.DiscardAsync(repo.Path, ["keep.txt", "deleted.txt", "scratch/deep/tmp.txt"], Ct);

        File.ReadAllText(repo.Combine("keep.txt")).Should().Be("original\n");
        File.Exists(repo.Combine("deleted.txt")).Should().BeTrue();
        File.Exists(repo.Combine("scratch", "deep", "tmp.txt")).Should().BeFalse();
        Directory.Exists(repo.Combine("scratch", "deep")).Should().BeFalse("folders emptied by the discard are removed");
        File.Exists(repo.Combine("scratch", "other.txt")).Should().BeTrue();
        (await Git.GetStatusAsync(repo.Path, Ct)).Entries.Should().ContainSingle(e => e.Path == "scratch/other.txt");
    }

    [Fact]
    public async Task Discard_of_staged_changes_goes_back_to_head()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", "original\n"));
        repo.WriteFile("f.txt", "staged\n");
        repo.Git("add", "f.txt");
        repo.WriteFile("f.txt", "staged and edited\n");
        repo.WriteFile("added.txt", "brand new\n");
        repo.Git("add", "added.txt");

        await Git.DiscardAsync(repo.Path, ["f.txt", "added.txt"], Ct);

        File.ReadAllText(repo.Combine("f.txt")).Should().Be("original\n");
        File.Exists(repo.Combine("added.txt")).Should().BeFalse();
        (await Git.GetStatusAsync(repo.Path, Ct)).IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Discard_of_a_staged_rename_restores_the_original()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("old.txt", Lines(1, 5)));
        repo.Git("mv", "old.txt", "new.txt");

        await Git.DiscardAsync(repo.Path, ["new.txt"], Ct);

        File.Exists(repo.Combine("old.txt")).Should().BeTrue();
        File.Exists(repo.Combine("new.txt")).Should().BeFalse();
        (await Git.GetStatusAsync(repo.Path, Ct)).IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Discard_resolves_a_conflict_to_the_current_branch()
    {
        var repo = _sandbox.CreateRepository();
        GitServiceStatusTests.CreateConflict(repo);

        await Git.DiscardAsync(repo.Path, ["shared.txt"], Ct);

        File.ReadAllText(repo.Combine("shared.txt")).Should().Be("a\nMAIN\nc\n");
        (await Git.GetStatusAsync(repo.Path, Ct)).Conflicted.Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_of_a_conflict_on_a_file_deleted_in_head_removes_it()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("doomed.txt", "v1\n"));
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Commit("Modify", ("doomed.txt", "v2\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("rm", "-q", "doomed.txt");
        repo.Commit("Delete");
        _sandbox.TryRun(repo.Path, "merge", "--no-edit", "feature").ExitCode.Should().NotBe(0);
        (await Git.GetStatusAsync(repo.Path, Ct)).Conflicted.Should().ContainSingle();

        await Git.DiscardAsync(repo.Path, ["doomed.txt"], Ct);

        File.Exists(repo.Combine("doomed.txt")).Should().BeFalse();
        (await Git.GetStatusAsync(repo.Path, Ct)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_before_the_first_commit_deletes_staged_files()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        repo.WriteFile("staged.txt", "s\n");
        repo.Git("add", "staged.txt");

        await Git.DiscardAsync(repo.Path, ["staged.txt"], Ct);

        File.Exists(repo.Combine("staged.txt")).Should().BeFalse();
        (await Git.GetStatusAsync(repo.Path, Ct)).IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Discard_never_deletes_a_nested_repository()
    {
        var repo = _sandbox.CreateRepository();
        var nested = Path.Combine(repo.Path, "vendor", "lib");
        _sandbox.Run(repo.Path, "init", "-q", nested);
        GitSandbox.WriteFile(nested, "code.c", "int x;\n");
        repo.WriteFile("README.md", "edited\n");

        var act = () => Git.DiscardAsync(repo.Path, ["vendor/lib/", "README.md"], Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        File.Exists(Path.Combine(nested, "code.c")).Should().BeTrue();
        File.ReadAllText(repo.Combine("README.md")).Should().Be("edited\n", "nothing is discarded when the request is refused");
    }

    [Fact]
    public async Task Discard_refuses_paths_outside_the_repository()
    {
        var repo = _sandbox.CreateRepository();
        var outside = Path.Combine(Path.GetDirectoryName(repo.Path)!, "precious-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "keep me");
        try
        {
            var act = () => Git.DiscardAsync(repo.Path, ["../" + Path.GetFileName(outside)], Ct);

            (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
            File.Exists(outside).Should().BeTrue();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    public void Dispose() => _sandbox.Dispose();
}
