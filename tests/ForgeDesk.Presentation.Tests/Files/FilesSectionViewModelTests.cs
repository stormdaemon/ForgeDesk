using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Files.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;
using static ForgeDesk.Presentation.Tests.Files.Support.FilesHarness;

namespace ForgeDesk.Presentation.Tests.Files;

public sealed class FilesSectionViewModelTests : IDisposable
{
    private readonly FilesHarness _harness = new();

    public FilesSectionViewModelTests()
    {
        _harness.Files.SetFolder(string.Empty, Dir(".github"), Dir("node_modules", heavy: true), Dir("src"), File(".env"), File("README.md"));
        _harness.Files.SetFolder("src", Dir("src/app"), File("src/main.cs"));
        _harness.Files.SetFolder("src/app", File("src/app/view.cs"), File("src/app/logo.png"));
        _harness.Files.SetFolder("node_modules", Dir("node_modules/left-pad"));
        _harness.Files.SetText("README.md", "# Forge\n");
        _harness.Files.SetText("src/main.cs", "line 1\nline 2\nline 3\n");
        _harness.Files.SetText("src/app/view.cs", "class View {}\n");
    }

    public void Dispose() => _harness.Dispose();

    private FileTreeNodeViewModel Row(FilesSectionViewModel section, string path) =>
        section.Rows.Single(r => r.RelativePath == path);

    [Fact]
    public async Task Activation_lists_the_root_only_and_hides_hidden_files()
    {
        var section = await _harness.OpenAsync();

        Paths(section.Rows).Should().Equal("node_modules", "src", "README.md");
        section.IsLoadingTree.Should().BeFalse();
        section.ShowRows.Should().BeTrue();
        _harness.Files.ListCalls.Keys.Should().Equal(string.Empty);
    }

    [Fact]
    public async Task Heavy_folders_are_dimmed_and_never_expanded_automatically()
    {
        var section = await _harness.OpenAsync();

        var heavy = Row(section, "node_modules");
        heavy.IsDimmed.Should().BeTrue();
        heavy.IsExpanded.Should().BeFalse();
        _harness.Files.ListCalls.Should().NotContainKey("node_modules");
    }

    [Fact]
    public async Task Expanding_lists_a_folder_once_and_collapsing_hides_its_rows()
    {
        var section = await _harness.OpenAsync();

        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        Paths(section.Rows).Should().Equal("node_modules", "src", "src/app", "src/main.cs", "README.md");

        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        Paths(section.Rows).Should().Equal("node_modules", "src", "README.md");

        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        _harness.Files.ListCalls["src"].Should().Be(1, "a loaded folder is not listed again");
        Row(section, "src/app").Depth.Should().Be(1);
    }

    [Fact]
    public async Task Show_hidden_files_reveals_dot_files()
    {
        var section = await _harness.OpenAsync();

        section.ShowHiddenFiles = true;

        Paths(section.Rows).Should().Equal(".github", "node_modules", "src", ".env", "README.md");
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_shows_its_error_on_the_row()
    {
        _harness.Files.FailListing("src", new ForgeException(ErrorKind.PermissionDenied, "ForgeDesk is not allowed to open 'src'."));
        var section = await _harness.OpenAsync();

        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));

        Row(section, "src").LoadError.Should().Contain("not allowed");
        Row(section, "src").HasLoadError.Should().BeTrue();
        section.Error.Should().BeNull();
    }

    [Fact]
    public async Task Root_listing_errors_are_shown_with_a_retry()
    {
        _harness.Files.FailListing(string.Empty, new ForgeException(ErrorKind.PathNotFound, "The folder does not exist."));
        var section = await _harness.OpenAsync();

        section.Error.Should().NotBeNull();
        section.ShowRows.Should().BeFalse();

        _harness.Files.ClearFailure(string.Empty);
        await section.RefreshCommand.ExecuteAsync(null);

        section.Error.Should().BeNull();
        section.Rows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_empty_project_offers_to_show_hidden_files()
    {
        _harness.Files.SetFolder(string.Empty, File(".gitignore"));
        var section = await _harness.OpenAsync();

        section.ShowEmpty.Should().BeTrue();
        section.EmptyTitle.Should().Be("This folder is empty");
        section.EmptyActionText.Should().Be("Show hidden files");

        section.EmptyActionCommand!.Execute(null);

        section.ShowEmpty.Should().BeFalse();
        Paths(section.Rows).Should().Equal(".gitignore");
    }

    [Fact]
    public async Task Git_decorations_color_files_and_mark_their_folders()
    {
        _harness.Status = TestData.Status(entries: [TestData.Modified("src/main.cs"), TestData.Conflicted("src/app/view.cs")]);
        var section = await _harness.OpenAsync();
        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));

        Row(section, "src").ContainsChanges.Should().BeTrue();
        Row(section, "src").GitTone.Should().Be(StatusTone.Danger);
        Row(section, "src/main.cs").GitState.Should().Be(FileGitState.Modified);
        Row(section, "src/main.cs").GitTone.Should().Be(StatusTone.Warning);
        Row(section, "README.md").HasGitState.Should().BeFalse();

        await _harness.SetStatusAsync(TestData.Status());

        Row(section, "src").ContainsChanges.Should().BeFalse();
        Row(section, "src/main.cs").HasGitState.Should().BeFalse();
    }

    [Fact]
    public async Task Status_changes_while_inactive_are_applied_on_activation()
    {
        var section = await _harness.OpenAsync();
        section.Deactivate();

        await _harness.SetStatusAsync(TestData.Status(entries: TestData.Modified("README.md")));
        Row(section, "README.md").HasGitState.Should().BeFalse("decorations wait until the tab is shown");

        await section.ActivateAsync();
        Row(section, "README.md").GitState.Should().Be(FileGitState.Modified);
    }

    [Fact]
    public async Task Only_changed_files_lists_changed_paths_flat_including_deleted_ones()
    {
        _harness.Status = TestData.Status(entries:
        [
            TestData.Modified("src/main.cs"),
            new GitStatusEntry { Path = "old.txt", WorkTreeState = GitFileState.Deleted },
            new GitStatusEntry { Path = "src/new.cs", WorkTreeState = GitFileState.Untracked },
        ]);
        var section = await _harness.OpenAsync();

        section.OnlyChangedFiles = true;

        Paths(section.Rows).Should().Equal("old.txt", "src/main.cs", "src/new.cs");
        section.Rows.Should().OnlyContain(r => r.IsFlat);
        Row(section, "old.txt").Exists.Should().BeFalse();
        Row(section, "src/new.cs").GitState.Should().Be(FileGitState.Untracked);
        Row(section, "src/main.cs").Directory.Should().Be("src");
        section.ChangedFilesText.Should().Be("3 changed files");

        section.OnlyChangedFiles = false;
        Paths(section.Rows).Should().Equal("node_modules", "src", "README.md");
    }

    [Fact]
    public async Task Selecting_a_deleted_file_explains_it_without_reading()
    {
        _harness.Status = TestData.Status(entries: new GitStatusEntry { Path = "old.txt", WorkTreeState = GitFileState.Deleted });
        var section = await _harness.OpenAsync();
        section.OnlyChangedFiles = true;

        section.SelectedNode = Row(section, "old.txt");

        section.Viewer.Error!.Title.Should().Be("File deleted");
        section.Viewer.GitState.Should().Be(FileGitState.Deleted);
        _harness.Files.ReadCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_changed_files_on_a_clean_tree_explains_it()
    {
        var section = await _harness.OpenAsync();

        section.OnlyChangedFiles = true;

        section.ShowEmpty.Should().BeTrue();
        section.EmptyTitle.Should().Be("No changes");
        section.EmptyActionCommand.Should().BeSameAs(section.ShowAllFilesCommand);
    }

    [Fact]
    public async Task Only_changed_files_outside_a_repository_explains_it()
    {
        _harness.Status = null;
        var section = await _harness.OpenAsync();

        section.OnlyChangedFiles = true;

        section.EmptyTitle.Should().Be("Not a Git repository");
    }

    [Fact]
    public async Task File_changes_reload_expanded_folders_and_keep_expansion_and_selection()
    {
        var section = await _harness.OpenAsync();
        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        var main = Row(section, "src/main.cs");
        section.SelectedNode = main;
        await section.Viewer.PendingLoad;

        _harness.Files.SetFolder("src", Dir("src/app"), File("src/added.cs"), File("src/main.cs"));
        _harness.Context.NotifyFilesChanged(gitMetadataChanged: false);
        await WaitUntilAsync(() => section.Rows.Any(r => r.RelativePath == "src/added.cs"));

        Paths(section.Rows).Should().Equal("node_modules", "src", "src/app", "src/added.cs", "src/main.cs", "README.md");
        Row(section, "src").IsExpanded.Should().BeTrue();
        section.SelectedNode.Should().BeSameAs(main);
        _harness.Index.Received().Invalidate(_harness.Root);
    }

    [Fact]
    public async Task File_changes_while_inactive_wait_for_the_next_activation()
    {
        var section = await _harness.OpenAsync();
        section.Deactivate();
        _harness.Files.SetFolder(string.Empty, Dir("src"), File("README.md"), File("new.txt"));

        _harness.Context.NotifyFilesChanged(gitMetadataChanged: false);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _harness.Files.ListCalls[string.Empty].Should().Be(1);

        await section.ActivateAsync();
        Paths(section.Rows).Should().Contain("new.txt");
    }

    [Fact]
    public async Task Selecting_a_file_previews_it_and_a_folder_summarizes_it()
    {
        var section = await _harness.OpenAsync();
        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));

        section.SelectedNode = Row(section, "src/main.cs");
        await section.Viewer.PendingLoad;
        section.Viewer.IsText.Should().BeTrue();
        section.Viewer.Text.Should().StartWith("line 1");

        section.SelectedNode = Row(section, "src");
        section.Viewer.IsFolder.Should().BeTrue();
        section.Viewer.FolderSummary.Should().Be("1 folder · 1 file");
    }

    [Fact]
    public async Task Navigating_to_a_location_expands_selects_and_reveals_the_line()
    {
        var section = await _harness.OpenAsync();
        FileTreeNodeViewModel? revealed = null;
        section.RevealRequested += (_, node) => revealed = node;

        await section.NavigateToAsync(new FileLocation("src/app/view.cs", 1));
        await section.Viewer.PendingLoad;

        Row(section, "src").IsExpanded.Should().BeTrue();
        Row(section, "src/app").IsExpanded.Should().BeTrue();
        section.SelectedNode!.RelativePath.Should().Be("src/app/view.cs");
        revealed.Should().BeSameAs(section.SelectedNode);
        section.Viewer.RelativePath.Should().Be("src/app/view.cs");
        section.Viewer.ScrollToLine.Should().Be(1);
    }

    [Fact]
    public async Task Navigating_with_a_plain_path_works_before_activation_and_shows_hidden_targets()
    {
        _harness.Files.SetFolder(".github", File(".github/ci.yml"));
        _harness.Files.SetText(".github/ci.yml", "on: push\n");
        var section = _harness.Create();

        await section.NavigateToAsync(".github\\ci.yml");
        await section.Viewer.PendingLoad;

        section.ShowHiddenFiles.Should().BeTrue();
        section.SelectedNode!.RelativePath.Should().Be(".github/ci.yml");
        section.Viewer.IsText.Should().BeTrue();
    }

    [Fact]
    public async Task Navigating_to_a_missing_file_still_tries_to_show_it()
    {
        var section = await _harness.OpenAsync();

        await section.NavigateToAsync(new FileLocation("src/gone.cs"));
        await section.Viewer.PendingLoad;

        section.SelectedNode.Should().BeNull();
        section.Viewer.Error.Should().NotBeNull();
        section.Viewer.Error!.Title.Should().Be("File not found");
    }

    [Fact]
    public async Task Navigation_leaves_only_changed_mode_when_the_target_is_unchanged()
    {
        var section = await _harness.OpenAsync();
        section.OnlyChangedFiles = true;

        await section.NavigateToAsync("README.md");

        section.OnlyChangedFiles.Should().BeFalse();
        section.SelectedNode!.RelativePath.Should().Be("README.md");
    }

    [Fact]
    public async Task Go_to_file_shows_index_matches_and_enter_reveals_the_best_one()
    {
        var snapshot = new FileIndexSnapshot(_harness.Root, ["src/main.cs", "src/app/view.cs"], false, TestData.Now);
        _harness.Index.GetAsync(_harness.Root, false, Arg.Any<CancellationToken>()).Returns(snapshot);
        _harness.Index.Search(snapshot, "view", FilesSectionViewModel.MaxGoToResults).Returns([new FileMatch("src/app/view.cs", 10, [8, 9, 10, 11])]);
        var section = await _harness.OpenAsync();

        section.GoToText = "view";
        await section.PendingGoTo;

        Paths(section.Rows).Should().Equal("src/app/view.cs");
        section.Rows[0].IsFlat.Should().BeTrue();

        await section.AcceptGoToCommand.ExecuteAsync(null);
        await section.Viewer.PendingLoad;

        section.GoToText.Should().BeEmpty();
        section.SelectedNode!.RelativePath.Should().Be("src/app/view.cs");
        section.SelectedNode.IsFlat.Should().BeFalse("the tree node is selected");
        section.Viewer.RelativePath.Should().Be("src/app/view.cs");
    }

    [Fact]
    public async Task Go_to_file_without_matches_explains_and_clears()
    {
        var snapshot = new FileIndexSnapshot(_harness.Root, [], false, TestData.Now);
        _harness.Index.GetAsync(_harness.Root, false, Arg.Any<CancellationToken>()).Returns(snapshot);
        _harness.Index.Search(snapshot, Arg.Any<string>(), Arg.Any<int>()).Returns([]);
        var section = await _harness.OpenAsync();

        section.GoToText = "zzz";
        await section.PendingGoTo;

        section.ShowEmpty.Should().BeTrue();
        section.EmptyTitle.Should().Be("No matching files");
        section.EmptyActionCommand!.Execute(null);
        section.GoToText.Should().BeEmpty();
        Paths(section.Rows).Should().Equal("node_modules", "src", "README.md");
    }

    [Fact]
    public async Task Context_actions_deep_link_to_git_terminal_and_search()
    {
        var section = await _harness.OpenAsync();
        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        var main = Row(section, "src/main.cs");

        section.ShowChangesCommand.Execute(main);
        section.OpenTerminalHereCommand.Execute(main);
        section.OpenTerminalHereCommand.Execute(Row(section, "src/app"));
        section.SearchInFolderCommand.Execute(Row(section, "src/app"));

        _harness.NavigationRequests.Should().BeEquivalentTo(
        [
            new WorkspaceNavigationRequest(WorkspaceSection.Git, "src/main.cs"),
            new WorkspaceNavigationRequest(WorkspaceSection.Terminal, "src"),
            new WorkspaceNavigationRequest(WorkspaceSection.Terminal, "src/app"),
        ]);
        section.IsSearchOpen.Should().BeTrue();
        section.Search.IncludeGlob.Should().Be("src/app/**");
    }

    [Fact]
    public async Task Shell_actions_use_full_paths()
    {
        var section = await _harness.OpenAsync();
        var readme = Row(section, "README.md");
        var full = Path.Combine(_harness.Root, "README.md");

        section.OpenInEditorCommand.Execute(readme);
        section.RevealInExplorerCommand.Execute(readme);
        section.CopyPathCommand.Execute(readme);
        section.CopyRelativePathCommand.Execute(readme);
        section.OpenWithDefaultAppCommand.Execute(Row(section, "src"));

        _harness.Shell.Received().OpenInEditor(full, null);
        _harness.Shell.Received().RevealInExplorer(full);
        _harness.Shell.Received().CopyToClipboard(full);
        _harness.Shell.Received().CopyToClipboard("README.md");
        _harness.Shell.Received().OpenFolder(Path.Combine(_harness.Root, "src"));
    }

    [Fact]
    public async Task Shell_failures_become_notifications()
    {
        _harness.Shell.When(s => s.OpenInEditor(Arg.Any<string>(), Arg.Any<int?>())).Do(_ => throw new InvalidOperationException("No editor"));
        var section = await _harness.OpenAsync();

        section.OpenInEditorCommand.Execute(Row(section, "README.md"));

        _harness.Notifications.Received().ShowError(Arg.Is<ErrorInfo>(e => e.Title == "Could not open the code editor"), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Left_collapses_then_selects_the_parent()
    {
        var section = await _harness.OpenAsync();
        await section.ToggleNodeCommand.ExecuteAsync(Row(section, "src"));
        var main = Row(section, "src/main.cs");
        section.SelectedNode = main;

        section.CollapseNodeCommand.Execute(main);
        section.SelectedNode.Should().BeSameAs(Row(section, "src"));

        section.CollapseNodeCommand.Execute(section.SelectedNode);
        Row(section, "src").IsExpanded.Should().BeFalse();
    }

    [Fact]
    public async Task Collapse_all_closes_every_folder()
    {
        var section = await _harness.OpenAsync();
        await section.NavigateToAsync("src/app/view.cs");

        section.CollapseAllCommand.Execute(null);

        Paths(section.Rows).Should().Equal("node_modules", "src", "README.md");
        section.SelectedNode.Should().BeNull();
    }

    [Fact]
    public async Task Search_panel_opens_and_closes()
    {
        var section = await _harness.OpenAsync();
        var focused = false;
        section.Search.FocusRequested += (_, _) => focused = true;

        section.OpenSearchCommand.Execute(null);
        section.IsSearchOpen.Should().BeTrue();
        section.IsTreeVisible.Should().BeFalse();
        focused.Should().BeTrue();

        section.CloseSearchCommand.Execute(null);
        section.IsTreeVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_stops_listening_to_the_project()
    {
        var section = await _harness.OpenAsync();
        section.Dispose();

        _harness.Context.NotifyFilesChanged(gitMetadataChanged: false);
        await _harness.SetStatusAsync(TestData.Status(entries: TestData.Modified("README.md")));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _harness.Files.ListCalls[string.Empty].Should().Be(1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }
}
