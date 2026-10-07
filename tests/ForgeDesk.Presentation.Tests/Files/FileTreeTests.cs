using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using static ForgeDesk.Presentation.Tests.Files.Support.FilesHarness;

namespace ForgeDesk.Presentation.Tests.Files;

public sealed class FileTreeTests
{
    [Fact]
    public void Merge_reuses_nodes_by_path_and_keeps_their_state()
    {
        var first = FileTree.Merge(null, [Dir("src"), File("README.md")], 0)!;
        first[0].IsExpanded = true;

        var second = FileTree.Merge(first, [Dir("src"), File("LICENSE"), File("README.md")], 0)!;

        second.Should().HaveCount(3);
        second[0].Should().BeSameAs(first[0]);
        second[0].IsExpanded.Should().BeTrue();
        second[2].Should().BeSameAs(first[1]);
        second[1].Name.Should().Be("LICENSE");
    }

    [Fact]
    public void Merge_returns_null_when_nothing_changed()
    {
        var first = FileTree.Merge(null, [Dir("src"), File("README.md")], 0)!;

        FileTree.Merge(first, [Dir("src"), File("README.md")], 0).Should().BeNull();
        FileTree.Merge(first, [Dir("src"), File("README.md", size: 999)], 0).Should().NotBeNull("the size changed");
    }

    [Fact]
    public void Merge_replaces_a_file_that_became_a_folder()
    {
        var first = FileTree.Merge(null, [File("build")], 0)!;

        var second = FileTree.Merge(first, [Dir("build")], 0)!;

        second[0].Should().NotBeSameAs(first[0]);
        second[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Flatten_descends_only_into_expanded_loaded_folders()
    {
        var roots = FileTree.Merge(null, [Dir("src"), Dir("docs"), File("a.txt")], 0)!;
        roots[0].Children = FileTree.Merge(null, [Dir("src/app"), File("src/main.cs")], 1);
        roots[0].Children![0].Children = FileTree.Merge(null, [File("src/app/x.cs")], 2);
        roots[0].IsExpanded = true;
        roots[1].IsExpanded = true; // expanded but never listed: nothing below it

        Paths(FileTree.Flatten(roots)).Should().Equal("src", "src/app", "src/main.cs", "docs", "a.txt");

        roots[0].Children![0].IsExpanded = true;
        Paths(FileTree.Flatten(roots)).Should().Equal("src", "src/app", "src/app/x.cs", "src/main.cs", "docs", "a.txt");
    }

    [Fact]
    public void Flatten_filter_hides_a_folder_with_its_content()
    {
        var roots = FileTree.Merge(null, [Dir(".github"), File(".env"), File("a.txt")], 0)!;
        roots[0].Children = FileTree.Merge(null, [File(".github/ci.yml")], 1);
        roots[0].IsExpanded = true;

        Paths(FileTree.Flatten(roots, n => !n.IsHidden)).Should().Equal("a.txt");
        Paths(FileTree.Flatten(roots)).Should().Equal(".github", ".github/ci.yml", ".env", "a.txt");
    }

    [Fact]
    public void Nodes_are_indented_by_depth_and_flat_rows_are_not()
    {
        var node = new FileTreeNodeViewModel(File("src/app/x.cs"), 2);
        var flat = FileTreeNodeViewModel.ForFlatFile("src/app/x.cs");

        node.Indent.Should().Be(2 * FileTreeNodeViewModel.IndentPerLevel);
        node.HasDirectory.Should().BeFalse();
        flat.Indent.Should().Be(0);
        flat.Directory.Should().Be("src/app");
        flat.HasDirectory.Should().BeTrue();
        flat.HasChevron.Should().BeFalse();
    }

    [Fact]
    public void Folder_icons_follow_the_expansion_and_files_their_type()
    {
        var folder = new FileTreeNodeViewModel(Dir("src"), 0);
        folder.Icon.Should().Be("Folder16");
        folder.IsExpanded = true;
        folder.Icon.Should().Be("FolderOpen16");

        FileIcons.For("main.cs").Should().Be("Code16");
        FileIcons.For("logo.png").Should().Be("Image16");
        FileIcons.For("README.md").Should().Be("DocumentText16");
        FileIcons.For("package.json").Should().Be("DocumentBulletList16");
        FileIcons.For(".gitignore").Should().Be("DocumentBulletList16");
        FileIcons.For("data.bin").Should().Be("Document16");
    }

    [Fact]
    public void Heavy_and_ignored_entries_are_dimmed()
    {
        new FileTreeNodeViewModel(Dir("node_modules", heavy: true), 0).IsDimmed.Should().BeTrue();
        new FileTreeNodeViewModel(File("debug.log") with { IsIgnored = true }, 0).IsDimmed.Should().BeTrue();
        new FileTreeNodeViewModel(File("main.cs"), 0).IsDimmed.Should().BeFalse();
        FileTreeNodeViewModel.ForMissingFile("gone.cs").IsDimmed.Should().BeTrue();
    }
}

public sealed class FileDecorationsTests
{
    private static GitStatus Status(params GitStatusEntry[] entries) => TestData.Status(entries: entries);

    [Fact]
    public void Entries_map_to_the_most_important_state()
    {
        FileDecorations.StateOf(TestData.Modified("a")).Should().Be(FileGitState.Modified);
        FileDecorations.StateOf(TestData.Conflicted("a")).Should().Be(FileGitState.Conflicted);
        FileDecorations.StateOf(new GitStatusEntry { Path = "a", WorkTreeState = GitFileState.Untracked }).Should().Be(FileGitState.Untracked);
        FileDecorations.StateOf(new GitStatusEntry { Path = "a", IndexState = GitFileState.Added }).Should().Be(FileGitState.Added);
        FileDecorations.StateOf(new GitStatusEntry { Path = "a", WorkTreeState = GitFileState.Deleted }).Should().Be(FileGitState.Deleted);
        FileDecorations.StateOf(new GitStatusEntry { Path = "a", IndexState = GitFileState.Renamed, OriginalPath = "b" }).Should().Be(FileGitState.Renamed);
        FileDecorations.StateOf(new GitStatusEntry { Path = "a", IndexState = GitFileState.Added, WorkTreeState = GitFileState.Deleted })
            .Should().Be(FileGitState.Deleted);
    }

    [Fact]
    public void States_have_the_design_tones()
    {
        FileDecorations.ToneOf(FileGitState.Modified).Should().Be(StatusTone.Warning);
        FileDecorations.ToneOf(FileGitState.Added).Should().Be(StatusTone.Success);
        FileDecorations.ToneOf(FileGitState.Untracked).Should().Be(StatusTone.Success);
        FileDecorations.ToneOf(FileGitState.Deleted).Should().Be(StatusTone.Danger);
        FileDecorations.ToneOf(FileGitState.Conflicted).Should().Be(StatusTone.Danger);
        FileDecorations.ToneOf(FileGitState.None).Should().Be(StatusTone.None);
        FileDecorations.LetterOf(FileGitState.Conflicted).Should().Be("!");
    }

    [Fact]
    public void Folders_containing_changes_are_marked_and_conflicts_win()
    {
        var decorations = FileDecorations.From(Status(TestData.Modified("src/app/a.cs"), TestData.Conflicted("src/lib/b.cs")));

        decorations.FolderStateOf("src").Should().Be(FileGitState.Conflicted);
        decorations.FolderStateOf("src/app").Should().Be(FileGitState.Modified);
        decorations.FolderStateOf("src/lib").Should().Be(FileGitState.Conflicted);
        decorations.FolderStateOf("docs").Should().Be(FileGitState.None);
        decorations.StateOf("src/app/a.cs").Should().Be(FileGitState.Modified);
        decorations.StateOf("src/app/other.cs").Should().Be(FileGitState.None);
    }

    [Fact]
    public void Nodes_take_their_decoration()
    {
        var decorations = FileDecorations.From(Status(TestData.Conflicted("src/a.cs")));
        var folder = new FileTreeNodeViewModel(Dir("src"), 0);
        var file = new FileTreeNodeViewModel(File("src/a.cs"), 1);

        folder.ApplyDecorations(decorations);
        file.ApplyDecorations(decorations);

        folder.ContainsChanges.Should().BeTrue();
        folder.GitTone.Should().Be(StatusTone.Danger);
        file.IsConflicted.Should().BeTrue();
        file.GitLetter.Should().Be("!");
        file.GitTone.Should().Be(StatusTone.Danger);
    }

    [Fact]
    public void No_status_means_no_decorations()
    {
        FileDecorations.From(null).Count.Should().Be(0);
        FileDecorations.From(Status()).Entries.Should().BeEmpty();
    }
}

public sealed class FileLocationTests
{
    [Theory]
    [InlineData("src/app.cs", "src/app.cs")]
    [InlineData("src\\app.cs", "src/app.cs")]
    [InlineData("./src/app.cs", "src/app.cs")]
    [InlineData("/src/", "src")]
    [InlineData(".", "")]
    public void Paths_are_normalized(string input, string expected) => FileLocation.Normalize(input).Should().Be(expected);

    [Fact]
    public void Navigation_arguments_are_read()
    {
        FileLocation.From("src\\a.cs").Should().Be(new FileLocation("src/a.cs"));
        FileLocation.From(new FileLocation("./src/a.cs", 12)).Should().Be(new FileLocation("src/a.cs", 12));
        FileLocation.From(new FileLocation("a.cs", 0))!.Line.Should().BeNull();
        FileLocation.From(42).Should().BeNull();
        FileLocation.From("  ").Should().BeNull();
    }

    [Fact]
    public void Ancestors_parent_and_name()
    {
        FileLocation.AncestorsOf("a/b/c.txt").Should().Equal("a", "a/b");
        FileLocation.AncestorsOf("c.txt").Should().BeEmpty();
        FileLocation.ParentOf("a/b/c.txt").Should().Be("a/b");
        FileLocation.ParentOf("c.txt").Should().BeEmpty();
        FileLocation.NameOf("a/b/c.txt").Should().Be("c.txt");
    }
}
