using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Shell.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class SidebarViewModelTests : IDisposable
{
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectStatusService _status = Substitute.For<IProjectStatusService>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private List<Project> _projects = [];
    private IReadOnlyList<IRunSession> _activeRuns = [];
    private SidebarViewModel? _sidebar;

    public SidebarViewModelTests()
    {
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<Project>)_projects.ToList());
        _runs.ActiveRuns.Returns(_ => _activeRuns);
    }

    public void Dispose() => _sidebar?.Dispose();

    [Fact]
    public void Pinned_projects_come_first_then_the_most_recently_opened_ones()
    {
        var (pinned, recent) = SidebarViewModel.Arrange(
        [
            TestData.Project("zeta", "/z", pinned: true, sortOrder: 2),
            TestData.Project("never-opened", "/n", added: TestData.Now.AddDays(-1)),
            TestData.Project("yesterday", "/y", lastOpened: TestData.Now.AddDays(-1)),
            TestData.Project("alpha", "/a", pinned: true, sortOrder: 1),
            TestData.Project("today", "/t", lastOpened: TestData.Now.AddHours(-1)),
            TestData.Project("beta", "/b", pinned: true, sortOrder: 1),
            TestData.Project("last-month", "/l", lastOpened: TestData.Now.AddDays(-30)),
        ]);

        pinned.Select(p => p.Name).Should().Equal("alpha", "beta", "zeta");
        recent.Select(p => p.Name).Should().Equal("today", "yesterday", "last-month", "never-opened");
    }

    [Fact]
    public void Recent_list_is_capped()
    {
        var projects = Enumerable.Range(0, 25).Select(i => TestData.Project($"p{i}", $"/p{i}", lastOpened: TestData.Now.AddMinutes(-i)));

        var (_, recent) = SidebarViewModel.Arrange(projects);

        recent.Should().HaveCount(SidebarViewModel.MaxRecentProjects);
        recent[0].Name.Should().Be("p0");
    }

    [Fact]
    public async Task Loading_shows_cached_status_before_any_refresh()
    {
        var project = TestData.Project("forge-app", "/dev/forge-app", lastOpened: TestData.Now);
        _projects = [project];
        _status.GetCachedAsync(project.Id, Arg.Any<CancellationToken>()).Returns(new ProjectSnapshot
        {
            ProjectId = project.Id,
            IsGitRepository = true,
            Branch = "main",
            ChangedFiles = 3,
            Attention = [new AttentionReason(AttentionLevel.Critical, "CI failing on main")],
        });
        var sidebar = Create();

        await sidebar.ReloadAsync();

        var item = sidebar.RecentProjects.Single();
        item.Branch.Should().Be("main");
        item.SecondaryText.Should().Be("main");
        item.ChangedFiles.Should().Be(3);
        item.Status.Should().Be(StatusTone.Danger);
        item.ToolTip.Should().Contain("3 changed files").And.Contain("CI failing on main");
        sidebar.HasPinned.Should().BeFalse();
        sidebar.HasRecent.Should().BeTrue();
    }

    [Fact]
    public async Task Entries_without_status_show_no_dot_and_missing_folders_say_so()
    {
        var fresh = TestData.Project("fresh", "/fresh", lastOpened: TestData.Now);
        var gone = TestData.Project("gone", "/gone", lastOpened: TestData.Now.AddHours(-1));
        _projects = [fresh, gone];
        _status.GetCachedAsync(gone.Id, Arg.Any<CancellationToken>())
            .Returns(new ProjectSnapshot { ProjectId = gone.Id, FolderExists = false });
        var sidebar = Create();

        await sidebar.ReloadAsync();

        sidebar.RecentProjects[0].Status.Should().Be(StatusTone.None);
        sidebar.RecentProjects[0].SecondaryText.Should().BeEmpty();
        sidebar.RecentProjects[1].Status.Should().Be(StatusTone.Warning);
        sidebar.RecentProjects[1].SecondaryText.Should().Be("Folder not found");
    }

    [Fact]
    public async Task Status_refresh_runs_three_projects_at_a_time_and_survives_failures()
    {
        _projects = Enumerable.Range(0, 7).Select(i => TestData.Project($"p{i}", $"/p{i}", lastOpened: TestData.Now.AddMinutes(-i))).ToList();
        var running = 0;
        var maxRunning = 0;
        var gate = new object();
        _status.RefreshAsync(Arg.Any<Project>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var project = call.Arg<Project>();
            lock (gate)
            {
                running++;
                maxRunning = Math.Max(maxRunning, running);
            }

            await Task.Delay(30);
            lock (gate)
            {
                running--;
            }

            if (project.Name == "p3")
            {
                throw new IOException("network drive unavailable");
            }

            return new ProjectSnapshot { ProjectId = project.Id, IsGitRepository = true, Branch = $"branch-{project.Name}" };
        });
        var sidebar = Create();
        await sidebar.ReloadAsync();

        await sidebar.RefreshStatusesAsync(TestContext.Current.CancellationToken);

        maxRunning.Should().Be(3);
        await _status.Received(7).RefreshAsync(Arg.Any<Project>(), true, Arg.Any<CancellationToken>());
        sidebar.RecentProjects.Where(p => p.Name != "p3").Should().OnlyContain(p => p.Branch == $"branch-{p.Name}");
        sidebar.RecentProjects.Single(p => p.Name == "p3").HasSnapshot.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_updates_and_runs_update_the_entries()
    {
        var project = TestData.Project("forge-app", "/dev/forge-app", lastOpened: TestData.Now);
        _projects = [project];
        var sidebar = Create();
        await sidebar.ReloadAsync();
        var item = sidebar.RecentProjects.Single();

        _status.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_status,
            new ProjectSnapshot { ProjectId = project.Id, IsGitRepository = true, Branch = "feature/x" });
        _activeRuns = [WorkspaceHarness.Run(project.Id, "npm run dev")];
        _runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_runs, _activeRuns[0]);

        item.Branch.Should().Be("feature/x");
        item.IsRunning.Should().BeTrue();

        _activeRuns = [];
        _runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_runs, new RunCompletedEventArgs(new RunRecord
        {
            Id = "r", ProjectId = project.Id, Label = "dev", CommandLine = "npm run dev", WorkingDirectory = ".", LogPath = "log",
        }));

        item.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Reload_keeps_entries_and_moves_them_between_sections()
    {
        var alpha = TestData.Project("alpha", "/a", lastOpened: TestData.Now);
        var beta = TestData.Project("beta", "/b", lastOpened: TestData.Now.AddHours(-1));
        _projects = [alpha, beta];
        var sidebar = Create();
        await sidebar.ReloadAsync();
        var betaItem = sidebar.RecentProjects[1];

        _projects = [alpha, beta with { IsPinned = true, Name = "Beta" }];
        await sidebar.ReloadAsync();

        sidebar.PinnedProjects.Should().ContainSingle().Which.Should().BeSameAs(betaItem);
        betaItem.Name.Should().Be("Beta");
        betaItem.PinMenuText.Should().Be("Unpin");
        sidebar.RecentProjects.Select(p => p.Name).Should().Equal("alpha");

        _projects = [alpha];
        await sidebar.ReloadAsync();

        sidebar.PinnedProjects.Should().BeEmpty();
    }

    [Fact]
    public async Task The_page_on_screen_is_highlighted()
    {
        var alpha = TestData.Project("alpha", "/a", lastOpened: TestData.Now);
        _projects = [alpha];
        var sidebar = Create(PageKind.Dashboard, PageKind.Activity);
        await sidebar.ReloadAsync();

        _navigation.CurrentPage.Returns(new object());
        _navigation.CurrentKind.Returns(PageKind.Project);
        _navigation.CurrentProjectId.Returns(alpha.Id);
        _navigation.Navigated += Raise.Event();

        sidebar.RecentProjects[0].IsCurrent.Should().BeTrue();
        sidebar.Home.IsCurrent.Should().BeFalse();

        _navigation.CurrentKind.Returns(PageKind.Dashboard);
        _navigation.CurrentProjectId.Returns((string?)null);
        _navigation.Navigated += Raise.Event();

        sidebar.RecentProjects[0].IsCurrent.Should().BeFalse();
        sidebar.Home.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Nav_entries_exist_only_for_registered_pages()
    {
        var sidebar = Create(PageKind.Dashboard);

        sidebar.Home.IsAvailable.Should().BeTrue();
        sidebar.Activity.IsAvailable.Should().BeFalse();

        sidebar.Home.NavigateCommand.Execute(null);
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Entry_commands_use_the_shared_project_actions()
    {
        var alpha = TestData.Project("alpha", "/a", lastOpened: TestData.Now);
        _projects = [alpha];
        var sidebar = Create();
        await sidebar.ReloadAsync();
        var item = sidebar.RecentProjects[0];

        await item.OpenCommand.ExecuteAsync(null);
        await item.TogglePinCommand.ExecuteAsync(null);
        await item.RenameCommand.ExecuteAsync(null);
        await item.RemoveCommand.ExecuteAsync(null);
        item.OpenInExplorerCommand.Execute(null);

        await _actions.Received(1).OpenAsync(alpha.Id);
        await _actions.Received(1).TogglePinAsync(alpha.Id);
        await _actions.Received(1).RenameAsync(alpha.Id);
        await _actions.Received(1).RemoveAsync(alpha.Id);
        _actions.Received(1).OpenInExplorer("/a");
    }

    [Fact]
    public void Sidebar_can_collapse_to_a_rail()
    {
        var sidebar = Create();

        sidebar.ToggleCollapsedCommand.Execute(null);

        sidebar.IsCollapsed.Should().BeTrue();
    }

    private SidebarViewModel Create(params PageKind[] pages)
    {
        _sidebar = new SidebarViewModel(_registry, _status, _runs, _navigation, new FakePageFactory(pages), _actions, ImmediateDispatcher.Instance,
            NullLogger<SidebarViewModel>.Instance);
        return _sidebar;
    }
}
