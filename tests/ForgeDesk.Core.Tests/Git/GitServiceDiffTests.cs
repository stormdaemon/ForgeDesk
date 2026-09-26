using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceDiffTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    private static string Lines(int from, int to, Func<int, string>? line = null) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => (line ?? (n => $"line {n}"))(i) + "\n"));

    [Fact]
    public async Task Working_tree_diff_of_a_modified_file()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("src/app.txt", Lines(1, 5)));
        repo.WriteFile("src/app.txt", Lines(1, 5).Replace("line 3\n", "line three\n", StringComparison.Ordinal));

        var diff = await Git.GetFileDiffAsync(repo.Path, "src/app.txt", DiffTarget.WorkingTree, Ct);

        diff.Path.Should().Be("src/app.txt");
        diff.IsNewFile.Should().BeFalse();
        diff.HeaderLines[0].Should().Be("diff --git a/src/app.txt b/src/app.txt");
        var hunk = diff.Hunks.Should().ContainSingle().Subject;
        hunk.Lines.Where(l => l.Kind != DiffLineKind.Context).Select(l => (l.Kind, l.Text, l.OldLineNumber, l.NewLineNumber)).Should().Equal(
            (DiffLineKind.Removed, "line 3", 3, (int?)null),
            (DiffLineKind.Added, "line three", (int?)null, 3));
        hunk.Lines.Count(l => l.Kind == DiffLineKind.Context).Should().Be(4);
    }

    [Fact]
    public async Task Staged_and_working_tree_diffs_are_separate()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", "one\n"));
        repo.WriteFile("f.txt", "two\n");
        repo.Git("add", "f.txt");
        repo.WriteFile("f.txt", "three\n");

        var staged = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.Staged, Ct);
        var unstaged = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);

        staged.Hunks.Single().Lines.Select(l => l.Text).Should().Equal("one", "two");
        unstaged.Hunks.Single().Lines.Select(l => l.Text).Should().Equal("two", "three");
    }

    [Fact]
    public async Task Separate_changes_give_separate_hunks()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", Lines(1, 30)));
        repo.WriteFile("f.txt", Lines(1, 30).Replace("line 2\n", "line TWO\n", StringComparison.Ordinal).Replace("line 28\n", "line 28\nline 28.5\n", StringComparison.Ordinal));

        var diff = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);

        diff.Hunks.Should().HaveCount(2);
        diff.Hunks[1].NewStart.Should().Be(26);
        diff.Additions.Should().Be(2);
        diff.Deletions.Should().Be(1);
    }

    [Fact]
    public async Task Untracked_text_file_is_all_additions()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("notes/todo.md", "first\nsecond\nthird");

        var diff = await Git.GetFileDiffAsync(repo.Path, "notes/todo.md", DiffTarget.WorkingTree, Ct);

        diff.IsNewFile.Should().BeTrue();
        diff.Hunks.Should().ContainSingle();
        diff.Hunks[0].Lines.Select(l => (l.Kind, l.Text, l.NewLineNumber)).Should().Equal(
            (DiffLineKind.Added, "first", 1),
            (DiffLineKind.Added, "second", 2),
            (DiffLineKind.Added, "third", 3),
            (DiffLineKind.NoNewlineMarker, "No newline at end of file", (int?)null));
        diff.Hunks[0].Header.Should().Be("@@ -0,0 +1,3 @@");
    }

    [Fact]
    public async Task Untracked_binary_and_oversized_files()
    {
        var repo = _sandbox.CreateRepository();
        File.WriteAllBytes(repo.Combine("image.bin"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        File.WriteAllText(repo.Combine("huge.log"), new string('x', 80) + "\n" + new string('y', (int)UntrackedFileDiff.MaxBytes));
        File.WriteAllText(repo.Combine("empty.txt"), string.Empty);

        var binary = await Git.GetFileDiffAsync(repo.Path, "image.bin", DiffTarget.WorkingTree, Ct);
        var huge = await Git.GetFileDiffAsync(repo.Path, "huge.log", DiffTarget.WorkingTree, Ct);
        var empty = await Git.GetFileDiffAsync(repo.Path, "empty.txt", DiffTarget.WorkingTree, Ct);

        binary.Should().Match<FileDiff>(d => d.IsBinary && d.IsNewFile && d.Hunks.Count == 0);
        huge.Should().Match<FileDiff>(d => d.IsTooLarge && d.IsNewFile && d.Hunks.Count == 0);
        empty.Should().Match<FileDiff>(d => d.IsNewFile && !d.IsBinary && d.Hunks.Count == 0);
    }

    [Fact]
    public async Task Binary_changes_to_tracked_files()
    {
        var repo = _sandbox.CreateRepository();
        File.WriteAllBytes(repo.Combine("logo.png"), [0x89, 0x50, 0x00, 0x01]);
        repo.Commit("Logo");
        File.WriteAllBytes(repo.Combine("logo.png"), [0x89, 0x50, 0x00, 0x02]);

        var diff = await Git.GetFileDiffAsync(repo.Path, "logo.png", DiffTarget.WorkingTree, Ct);

        diff.IsBinary.Should().BeTrue();
        diff.Hunks.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_newline_at_end_of_file_is_marked()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("f.txt", "a\nb\n"));
        repo.WriteFile("f.txt", "a\nb");

        var diff = await Git.GetFileDiffAsync(repo.Path, "f.txt", DiffTarget.WorkingTree, Ct);

        diff.Hunks[0].Lines.Select(l => l.Kind).Should().Equal(
            DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.NoNewlineMarker);
    }

    [Fact]
    public async Task Crlf_content_keeps_its_line_endings()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("build.cmd", "@echo off\r\necho one\r\n"));
        repo.WriteFile("build.cmd", "@echo off\r\necho two\r\n");

        var diff = await Git.GetFileDiffAsync(repo.Path, "build.cmd", DiffTarget.WorkingTree, Ct);

        diff.Hunks[0].Lines.Should().OnlyContain(l => l.HasCarriageReturn && !l.Text.Contains('\r', StringComparison.Ordinal));
        diff.Hunks[0].Lines.Select(l => l.Text).Should().Equal("@echo off", "echo one", "echo two");
    }

    [Fact]
    public async Task Unicode_and_glob_like_paths()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("données/café.txt", "a\n"), ("a[1].txt", "x\n"), ("a1.txt", "y\n"));
        repo.WriteFile("données/café.txt", "b\n");
        repo.WriteFile("a[1].txt", "x2\n");
        repo.WriteFile("a1.txt", "y2\n");

        var unicode = await Git.GetFileDiffAsync(repo.Path, "données/café.txt", DiffTarget.WorkingTree, Ct);
        var literal = await Git.GetFileDiffAsync(repo.Path, "a[1].txt", DiffTarget.WorkingTree, Ct);

        unicode.Path.Should().Be("données/café.txt");
        unicode.Hunks[0].Lines.Select(l => l.Text).Should().Equal("a", "b");
        literal.Path.Should().Be("a[1].txt");
        literal.Hunks[0].Lines.Select(l => l.Text).Should().Equal("x", "x2");
    }

    [Fact]
    public async Task Special_characters_in_names()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names can't contain quotes or tabs.");
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("we\"ird\tname.txt", "a\n"));
        repo.WriteFile("we\"ird\tname.txt", "b\n");

        var diff = await Git.GetFileDiffAsync(repo.Path, "we\"ird\tname.txt", DiffTarget.WorkingTree, Ct);

        diff.Path.Should().Be("we\"ird\tname.txt");
        diff.Hunks.Should().ContainSingle();
    }

    [Fact]
    public async Task Staged_rename_shows_both_names()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("old name.txt", Lines(1, 10)));
        repo.Git("mv", "old name.txt", "new name.txt");
        repo.WriteFile("new name.txt", Lines(1, 10) + "line 11\n");
        repo.Git("add", "new name.txt");

        var diff = await Git.GetFileDiffAsync(repo.Path, "new name.txt", DiffTarget.Staged, Ct);

        diff.Path.Should().Be("new name.txt");
        diff.OldPath.Should().Be("old name.txt");
        diff.IsNewFile.Should().BeFalse();
        diff.Additions.Should().Be(1);
    }

    [Fact]
    public async Task Staged_diff_in_an_unborn_repository()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        repo.WriteFile("first.txt", "hello\n");
        repo.Git("add", "first.txt");

        var diff = await Git.GetFileDiffAsync(repo.Path, "first.txt", DiffTarget.Staged, Ct);

        diff.IsNewFile.Should().BeTrue();
        diff.Hunks.Single().Lines.Single().Text.Should().Be("hello");
    }

    [Fact]
    public async Task Deleted_and_unchanged_files()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("gone.txt", "bye\n"));
        File.Delete(repo.Combine("gone.txt"));

        var deleted = await Git.GetFileDiffAsync(repo.Path, "gone.txt", DiffTarget.WorkingTree, Ct);
        var unchanged = await Git.GetFileDiffAsync(repo.Path, "README.md", DiffTarget.WorkingTree, Ct);

        deleted.IsDeletedFile.Should().BeTrue();
        deleted.Deletions.Should().Be(1);
        unchanged.Hunks.Should().BeEmpty();
        unchanged.Path.Should().Be("README.md");
    }

    [Fact]
    public async Task Conflicted_file_shows_the_combined_diff()
    {
        var repo = _sandbox.CreateRepository();
        GitServiceStatusTests.CreateConflict(repo);

        var diff = await Git.GetFileDiffAsync(repo.Path, "shared.txt", DiffTarget.WorkingTree, Ct);

        diff.HeaderLines[0].Should().StartWith("diff --cc");
        diff.Hunks.Should().ContainSingle();
        diff.Hunks[0].Lines.Should().Contain(l => l.Text.StartsWith("<<<<<<<", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Huge_diffs_are_reported_as_too_large()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("data.csv", Lines(1, 30_000, i => $"{i},a")));
        repo.WriteFile("data.csv", Lines(1, 30_000, i => $"{i},b"));

        var diff = await Git.GetFileDiffAsync(repo.Path, "data.csv", DiffTarget.WorkingTree, Ct);

        diff.IsTooLarge.Should().BeTrue();
        diff.Hunks.Should().BeEmpty();
        diff.Path.Should().Be("data.csv");
    }

    [Fact]
    public async Task Paths_outside_the_repository_are_refused()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.GetFileDiffAsync(repo.Path, "../secret.txt", DiffTarget.WorkingTree, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Commit_file_diff_for_root_regular_and_merge_commits()
    {
        var repo = _sandbox.CreateRepository();
        var root = repo.Git("rev-parse", "HEAD");
        repo.Commit("Edit", ("README.md", "# Test\nmore\n"));
        var edit = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("Side", ("side.txt", "from side\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("Main", ("main.txt", "from main\n"));
        repo.Git("merge", "-q", "--no-edit", "side");
        var merge = repo.Git("rev-parse", "HEAD");

        var rootDiff = await Git.GetCommitFileDiffAsync(repo.Path, root, "README.md", Ct);
        var editDiff = await Git.GetCommitFileDiffAsync(repo.Path, edit, "README.md", Ct);
        var mergeDiff = await Git.GetCommitFileDiffAsync(repo.Path, merge, "side.txt", Ct);

        rootDiff.IsNewFile.Should().BeTrue();
        rootDiff.Hunks.Single().Lines.Single().Text.Should().Be("# Test");
        editDiff.Hunks.Single().Lines.Last().Should().Match<DiffLine>(l => l.Kind == DiffLineKind.Added && l.Text == "more");
        mergeDiff.IsNewFile.Should().BeTrue("a merge shows what it brought in compared to its first parent");
        mergeDiff.Hunks.Single().Lines.Single().Text.Should().Be("from side");
    }

    [Fact]
    public async Task Commit_file_diff_follows_renames()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("a.txt", Lines(1, 8)));
        repo.Git("mv", "a.txt", "b.txt");
        repo.WriteFile("b.txt", Lines(1, 8) + "line 9\n");
        repo.Commit("Rename");

        var diff = await Git.GetCommitFileDiffAsync(repo.Path, "HEAD", "b.txt", Ct);

        diff.OldPath.Should().Be("a.txt");
        diff.Additions.Should().Be(1);
    }

    [Fact]
    public async Task Commit_file_diff_of_an_unknown_commit()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.GetCommitFileDiffAsync(repo.Path, "0123456789abcdef0123456789abcdef01234567", "README.md", Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Diff_output_is_read_as_utf8()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("i18n.txt", "Grüße\n"));
        File.WriteAllText(repo.Combine("i18n.txt"), "こんにちは\n", new UTF8Encoding(false));

        var diff = await Git.GetFileDiffAsync(repo.Path, "i18n.txt", DiffTarget.WorkingTree, Ct);

        diff.Hunks[0].Lines.Select(l => l.Text).Should().Equal("Grüße", "こんにちは");
    }

    public void Dispose() => _sandbox.Dispose();
}
