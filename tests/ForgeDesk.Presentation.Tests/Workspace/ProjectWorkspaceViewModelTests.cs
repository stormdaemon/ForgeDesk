using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Workspace;

public sealed class ProjectWorkspaceViewModelTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Git, WorkspaceSection.Commands,
        WorkspaceSection.Tasks, WorkspaceSection.GitHub);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Tabs_cover_every_section_in_order_and_only_registered_ones_are_available()
    {
        var workspace = _harness.CreateWorkspace();

        workspace.Tabs.Select(t => t.Section).Should().Equal(Enum.GetValues<WorkspaceSection>());
        workspace.AvailableTabs.Select(t => t.Section).Should().Equal(
            WorkspaceSection.Overview, WorkspaceSection.Git, WorkspaceSection.Commands, WorkspaceSection.Tasks, WorkspaceSection.GitHub);
        workspace.Tabs.Single(t => t.Section == WorkspaceSection.Terminal).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Shortcuts_follow_the_position_among_visible_tabs()
    {
        var workspace = _harness.CreateWorkspace();

        workspace.AvailableTabs.Select(t => t.Shortcut).Should().Equal("Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5");
        workspace.Tabs.Single(t => t.Section == WorkspaceSection.Files).Shortcut.Should().BeNull();
        workspace.Tabs.Single(t => t.Section == WorkspaceSection.Git).ToolTip.Should().Be("Git (Ctrl+2)");
    }

    [Fact]
    public async Task Opening_creates_and_activates_only_the_first_section()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        _harness.Sections.Created.Select(s => s.Section).Should().Equal(WorkspaceSection.Overview);
        _harness.Sections.Single(WorkspaceSection.Overview).ActivateCount.Should().Be(1);
        workspace.CurrentSection.Should().BeSameAs(_harness.Sections.Single(WorkspaceSection.Overview));
        workspace.SelectedTab!.Section.Should().Be(WorkspaceSection.Overview);
        workspace.OpenedTabs.Select(t => t.Section).Should().Equal(WorkspaceSection.Overview);
    }

    [Fact]
    public async Task Switching_tabs_deactivates_the_previous_section_and_keeps_it_alive()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var overview = _harness.Sections.Single(WorkspaceSection.Overview);

        await workspace.SelectSectionAsync(WorkspaceSection.Git);
        await workspace.SelectSectionAsync(WorkspaceSection.Overview);

        _harness.Sections.CountOf(WorkspaceSection.Overview).Should().Be(1, "sections are created once and kept alive");
        overview.ActivateCount.Should().Be(2);
        overview.DeactivateCount.Should().Be(1);
        _harness.Sections.Single(WorkspaceSection.Git).DeactivateCount.Should().Be(1);
        workspace.OpenedTabs.Select(t => t.Section).Should().Equal(WorkspaceSection.Overview, WorkspaceSection.Git);
        workspace.Tabs.Where(t => t.IsCurrent).Select(t => t.Section).Should().Equal(WorkspaceSection.Overview);
    }

    [Fact]
    public async Task Selecting_a_tab_in_the_strip_activates_its_section()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.SelectedTab = workspace.Tabs.Single(t => t.Section == WorkspaceSection.Tasks);

        _harness.Sections.Single(WorkspaceSection.Tasks).ActivateCount.Should().Be(1);
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Tasks);
    }

    [Fact]
    public async Task Ctrl_digit_selects_by_position_among_visible_tabs()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.SelectSectionByIndexCommand.ExecuteAsync(2);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Commands);
    }

    [Fact]
    public async Task Ctrl_digit_beyond_the_visible_tabs_does_nothing()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.SelectSectionByIndexCommand.ExecuteAsync(9);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Overview);
    }

    [Fact]
    public async Task Unregistered_sections_are_never_created()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.SelectSectionAsync(WorkspaceSection.Terminal);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Overview);
        _harness.Sections.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task A_section_can_open_another_one_with_an_argument()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.Context.RequestNavigation(WorkspaceSection.Git, "src/app.cs");

        var git = _harness.Sections.Single(WorkspaceSection.Git);
        workspace.CurrentSection.Should().BeSameAs(git);
        git.NavigatedTo.Should().Equal("src/app.cs");
        git.ActivateCount.Should().Be(1);
    }

    [Fact]
    public async Task Navigating_to_the_workspace_with_a_section_request_selects_it()
    {
        var workspace = _harness.CreateWorkspace();

        await workspace.OnNavigatedToAsync(new WorkspaceNavigationRequest(WorkspaceSection.Commands, "run-42"));

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Commands);
        _harness.Sections.Single(WorkspaceSection.Commands).NavigatedTo.Should().Equal("run-42");
        _harness.Sections.CountOf(WorkspaceSection.Overview).Should().Be(0);
    }

    [Fact]
    public async Task Leaving_the_page_deactivates_the_section_and_coming_back_reactivates_it()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var overview = _harness.Sections.Single(WorkspaceSection.Overview);

        workspace.OnNavigatedFrom();
        await workspace.OnNavigatedToAsync(null);

        overview.DeactivateCount.Should().Be(1);
        overview.ActivateCount.Should().Be(2);
        _harness.Sections.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task Section_that_cannot_be_created_shows_an_error_and_retry_recovers()
    {
        _harness.Sections.FailCreation = s => s == WorkspaceSection.Git ? new InvalidOperationException("boom") : null;
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.SelectSectionAsync(WorkspaceSection.Git);
        workspace.SectionError.Should().NotBeNull();
        workspace.SectionError!.Title.Should().Be("Could not open Git");
        workspace.CurrentSection.Should().BeNull();

        _harness.Sections.FailCreation = _ => null;
        await workspace.RetrySectionCommand.ExecuteAsync(null);

        workspace.SectionError.Should().BeNull();
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Git);
    }

    [Fact]
    public async Task Section_that_fails_to_load_shows_an_error()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        await workspace.SelectSectionAsync(WorkspaceSection.Tasks);
        var tasks = _harness.Sections.Single(WorkspaceSection.Tasks);
        await workspace.SelectSectionAsync(WorkspaceSection.Overview);
        tasks.ActivationFailure = new IOException("disk gone");

        await workspace.SelectSectionAsync(WorkspaceSection.Tasks);

        workspace.SectionError!.Title.Should().Be("Could not load Tasks");
    }

    [Fact]
    public async Task Header_shows_identity_and_git_state()
    {
        _harness.Project = TestData.Project("forge-app", _harness.Folder.Path, gitHub: new GitHubRepoRef("octo", "forge-app")) with { Color = "#3366FF" };
        _harness.CurrentStatus = TestData.Status("main", "origin/main", ahead: 2, behind: 1, entries: [TestData.Modified("a.txt"), TestData.Modified("b.txt")]);

        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.Name.Should().Be("forge-app");
        workspace.Path.Should().Be(_harness.Folder.Path);
        workspace.AvatarColor.Should().Be("#3366FF");
        workspace.GitHubUrl.Should().Be("https://github.com/octo/forge-app");
        workspace.IsGitRepository.Should().BeTrue();
        workspace.BranchName.Should().Be("main");
        workspace.Ahead.Should().Be(2);
        workspace.Behind.Should().Be(1);
        workspace.ChangesText.Should().Be("2 changes");
        workspace.IsFolderMissing.Should().BeFalse();
        workspace.HasGitError.Should().BeFalse();
    }

    [Fact]
    public async Task Not_a_repository_is_not_an_error()
    {
        _harness.CurrentStatus = null;

        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.IsRepositoryChecked.Should().BeTrue();
        workspace.IsGitRepository.Should().BeFalse();
        workspace.HasGitError.Should().BeFalse();
        workspace.Sync.FetchCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Missing_folder_is_reported_as_a_banner_not_as_a_git_error()
    {
        _harness.Project = TestData.Project("gone", _harness.Folder.Combine("does-not-exist"));

        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.IsFolderMissing.Should().BeTrue();
        workspace.HasGitError.Should().BeFalse();
        workspace.MissingFolderMessage.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Git_read_failure_is_shown_with_its_message()
    {
        _harness.Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Core.Git.GitStatus>(new ForgeException(ErrorKind.RepositoryLocked, "Another git process is running.")));

        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.HasGitError.Should().BeTrue();
        workspace.GitError!.Message.Should().Be("Another git process is running.");
    }

    [Fact]
    public async Task Locate_folder_relocates_the_project_and_refreshes()
    {
        _harness.Project = TestData.Project("moved", _harness.Folder.Combine("old-place"));
        var newPlace = Directory.CreateDirectory(_harness.Folder.Combine("new-place")).FullName;
        _harness.Dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(newPlace);
        _harness.Registry.RelocateAsync(_harness.Project.Id, newPlace, Arg.Any<CancellationToken>())
            .Returns(_harness.Project with { Path = newPlace });
        var workspace = await _harness.OpenWorkspaceAsync();
        workspace.IsFolderMissing.Should().BeTrue();

        await workspace.LocateFolderCommand.ExecuteAsync(null);

        workspace.Path.Should().Be(newPlace);
        workspace.IsFolderMissing.Should().BeFalse();
        workspace.IsGitRepository.Should().BeTrue();
    }

    [Fact]
    public async Task Git_badge_counts_changes_and_turns_red_on_conflicts()
    {
        _harness.CurrentStatus = TestData.Status(entries: [TestData.Modified("a"), TestData.Modified("b"), TestData.Modified("c")]);
        var workspace = await _harness.OpenWorkspaceAsync();
        var git = workspace.Tabs.Single(t => t.Section == WorkspaceSection.Git);

        git.BadgeText.Should().Be("3");
        git.BadgeKind.Should().Be(StatusTone.Neutral);
        git.BadgeDescription.Should().Be("3 changed files");

        _harness.CurrentStatus = TestData.Status(entries: [TestData.Conflicted("a"), TestData.Modified("b")]);
        await workspace.Context.RefreshGitStatusAsync();

        git.BadgeText.Should().Be("1");
        git.BadgeKind.Should().Be(StatusTone.Danger);

        _harness.CurrentStatus = TestData.Status();
        await workspace.Context.RefreshGitStatusAsync();

        git.HasBadgeText.Should().BeFalse();
        git.HasBadgeDot.Should().BeFalse();
    }

    [Fact]
    public async Task Tasks_badge_shows_open_tasks_and_follows_changes()
    {
        _harness.WorkItems.CountOpenAsync(_harness.Project.Id, Arg.Any<CancellationToken>()).Returns(4, 2);
        var workspace = await _harness.OpenWorkspaceAsync();
        var tasks = workspace.Tabs.Single(t => t.Section == WorkspaceSection.Tasks);
        tasks.BadgeText.Should().Be("4");

        _harness.WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(_harness.WorkItems, new WorkItemsChangedEventArgs("another-project", null));
        tasks.BadgeText.Should().Be("4");

        _harness.WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(_harness.WorkItems, new WorkItemsChangedEventArgs(_harness.Project.Id, "w1"));
        tasks.BadgeText.Should().Be("2");
    }

    [Fact]
    public async Task GitHub_badge_reflects_the_ci_state()
    {
        _harness.Status.GetCachedAsync(_harness.Project.Id, Arg.Any<CancellationToken>()).Returns(new ProjectSnapshot
        {
            ProjectId = _harness.Project.Id,
            Ci = new CiSummary { State = CiState.Failure, Branch = "main" },
        });
        var workspace = await _harness.OpenWorkspaceAsync();
        var github = workspace.Tabs.Single(t => t.Section == WorkspaceSection.GitHub);

        github.HasBadgeDot.Should().BeTrue();
        github.BadgeKind.Should().Be(StatusTone.Danger);
        github.BadgeDescription.Should().Be("CI failing on main");

        _harness.Status.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_harness.Status,
            new ProjectSnapshot { ProjectId = _harness.Project.Id, Ci = new CiSummary { State = CiState.Success } });

        github.BadgeKind.Should().Be(StatusTone.Success);

        _harness.Status.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_harness.Status,
            new ProjectSnapshot { ProjectId = _harness.Project.Id, Ci = new CiSummary { State = CiState.None } });

        github.HasBadgeDot.Should().BeFalse();
    }

    [Fact]
    public async Task Commands_badge_counts_this_projects_running_commands()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var commands = workspace.Tabs.Single(t => t.Section == WorkspaceSection.Commands);
        commands.HasBadgeText.Should().BeFalse();

        _harness.ActiveRuns =
        [
            WorkspaceHarness.Run(_harness.Project.Id, "npm run dev"),
            WorkspaceHarness.Run(_harness.Project.Id, "npm test", RunStatus.Queued),
            WorkspaceHarness.Run(_harness.Project.Id, "npm run build", RunStatus.Succeeded),
            WorkspaceHarness.Run("other", "cargo run"),
        ];
        _harness.Runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_harness.Runs, _harness.ActiveRuns[0]);

        commands.BadgeText.Should().Be("2");
        commands.BadgeKind.Should().Be(StatusTone.Running);

        _harness.ActiveRuns = [];
        _harness.Runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_harness.Runs, new RunCompletedEventArgs(new RunRecord
        {
            Id = "r1", ProjectId = _harness.Project.Id, Label = "npm run dev", CommandLine = "npm run dev", WorkingDirectory = ".", LogPath = "log",
        }));

        commands.HasBadgeText.Should().BeFalse();
    }

    [Fact]
    public async Task Quick_commands_are_the_first_dev_build_and_test_commands()
    {
        _harness.Profile = new ProjectProfile
        {
            Commands =
            [
                Command("npm:lint", "lint", CommandCategory.Lint),
                Command("npm:test", "test", CommandCategory.Test),
                Command("npm:dev", "dev", CommandCategory.Dev),
                Command("npm:build", "build", CommandCategory.Build),
                Command("npm:test:e2e", "test:e2e", CommandCategory.Test),
            ],
        };

        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.QuickCommands.Select(q => (q.Label, q.Command.Id)).Should().Equal(
            ("Dev", "npm:dev"), ("Build", "npm:build"), ("Test", "npm:test"));
    }

    [Fact]
    public async Task Running_a_quick_command_starts_it_in_its_folder_and_shows_the_commands_tab()
    {
        Directory.CreateDirectory(_harness.Folder.Combine("web"));
        _harness.Profile = new ProjectProfile { Commands = [Command("npm:dev", "dev", CommandCategory.Dev) with { WorkingDirectory = "web" }] };
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.RunQuickCommandCommand.ExecuteAsync(workspace.QuickCommands.Single());

        await _harness.Runs.Received(1).StartAsync(Arg.Is<RunRequest>(r =>
            r.ProjectId == _harness.Project.Id
            && r.CommandId == "npm:dev"
            && r.CommandLine == "npm run dev"
            && r.Category == CommandCategory.Dev
            && r.WorkingDirectory == Path.Combine(_harness.Folder.Path, "web")), Arg.Any<CancellationToken>());
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Commands);
    }

    [Fact]
    public async Task Quick_command_that_cannot_start_is_reported()
    {
        _harness.Profile = new ProjectProfile { Commands = [Command("npm:dev", "dev", CommandCategory.Dev)] };
        _harness.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IRunSession>(new ForgeException(ErrorKind.ToolNotFound, "npm was not found.")));
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.RunQuickCommandCommand.ExecuteAsync(workspace.QuickCommands.Single());

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "npm was not found."), Arg.Any<NotificationAction?>());
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Overview);
        workspace.QuickCommands.Single().IsStarting.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_reloads_git_status_and_badges()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _harness.Git.ClearReceivedCalls();
        _harness.WorkItems.ClearReceivedCalls();

        await workspace.RefreshCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).GetStatusAsync(_harness.Folder.Path, Arg.Any<CancellationToken>());
        await _harness.WorkItems.Received(1).CountOpenAsync(_harness.Project.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Open_in_actions_use_the_shell_and_report_failures()
    {
        _harness.Shell.When(s => s.OpenFolderInEditor(Arg.Any<string>()))
            .Do(_ => throw new ForgeException(ErrorKind.ToolNotFound, "No code editor was found."));
        var workspace = await _harness.OpenWorkspaceAsync();

        workspace.OpenFolderCommand.Execute(null);
        workspace.OpenTerminalCommand.Execute(null);
        workspace.OpenInEditorCommand.Execute(null);

        _harness.Shell.Received(1).OpenFolder(_harness.Folder.Path);
        _harness.Shell.Received(1).OpenExternalTerminal(_harness.Folder.Path);
        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "No code editor was found."), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Remove_delegates_to_the_shared_project_action()
    {
        var workspace = await _harness.OpenWorkspaceAsync();

        await workspace.RemoveProjectCommand.ExecuteAsync(null);

        await _harness.ProjectActions.Received(1).RemoveAsync(_harness.Project.Id);
    }

    [Fact]
    public async Task Dispose_releases_sections_and_the_project_context()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        await workspace.SelectSectionAsync(WorkspaceSection.Git);
        var lifetime = workspace.Context.Lifetime;

        workspace.Dispose();

        _harness.Sections.Created.Should().OnlyContain(s => s.IsDisposed);
        _harness.Sections.Single(WorkspaceSection.Git).DeactivateCount.Should().Be(1);
        lifetime.IsCancellationRequested.Should().BeTrue();
    }

    private static DetectedCommand Command(string id, string name, CommandCategory category) => new()
    {
        Id = id,
        Name = name,
        CommandLine = $"npm run {name}",
        Category = category,
        Source = "package.json",
    };
}
