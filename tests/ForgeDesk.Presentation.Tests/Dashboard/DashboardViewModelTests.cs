using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Settings.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static ForgeDesk.Presentation.Tests.Dashboard.DashboardTestData;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class DashboardViewModelTests : IDisposable
{
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectStatusService _status = Substitute.For<IProjectStatusService>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private readonly FakeSettingsService _settings = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly BackgroundOperations _operations = new(ImmediateDispatcher.Instance);
    private readonly Dictionary<string, ProjectSnapshot> _cached = new(StringComparer.Ordinal);
    private readonly TestFolder _folder = new();
    private List<Project> _projects = [];
    private IReadOnlyList<IRunSession> _activeRuns = [];
    private DashboardViewModel? _dashboard;

    public DashboardViewModelTests()
    {
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<Project>)_projects.ToList());
        _registry.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _projects.FirstOrDefault(p => p.Id == call.Arg<string>()));
        _status.GetCachedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _cached.TryGetValue(call.Arg<string>(), out var snapshot) ? snapshot : null);
        _runs.ActiveRuns.Returns(_ => _activeRuns);
    }

    public void Dispose()
    {
        _dashboard?.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Loading_shows_every_project_with_its_cached_status_and_the_counts()
    {
        var ok = Add("ok", lastOpened: TestData.Now);
        var failing = Add("failing", attention: [Critical("CI is failing on main.", "GitHub")]);
        var behind = Add("behind", attention: [Warning("2 commits behind.")]);
        var dashboard = Create();

        await dashboard.OnNavigatedToAsync(null);

        dashboard.IsLoaded.Should().BeTrue();
        dashboard.ShowProjects.Should().BeTrue();
        dashboard.ShowGrid.Should().BeTrue();
        dashboard.ShowHero.Should().BeFalse();
        dashboard.Projects.Select(p => p.Name).Should().Equal("failing", "behind", "ok");
        dashboard.ProjectCount.Should().Be(3);
        dashboard.AttentionCount.Should().Be(2);
        dashboard.SummaryText.Should().Be("3 projects · 2 need attention");
        dashboard.Projects.Single(p => p.Id == ok.Id).IsHealthy.Should().BeTrue();
        dashboard.Projects.Single(p => p.Id == failing.Id).NeedsAttention.Should().BeTrue();
        dashboard.Projects.Single(p => p.Id == behind.Id).HasSnapshot.Should().BeTrue();
    }

    [Fact]
    public async Task No_project_shows_the_welcome_hero()
    {
        var dashboard = Create();

        await dashboard.OnNavigatedToAsync(null);

        dashboard.ShowHero.Should().BeTrue();
        dashboard.ShowProjects.Should().BeFalse();
        dashboard.ShowToolbar.Should().BeFalse();
        dashboard.SummaryText.Should().Be("0 projects");
    }

    [Fact]
    public async Task A_registry_failure_shows_the_error_and_retry_recovers()
    {
        _registry.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Project>>(new ForgeException(ErrorKind.StorageFailure, "The database is locked.")),
                Task.FromResult<IReadOnlyList<Project>>([Project("forge")]));
        var dashboard = Create();

        await dashboard.OnNavigatedToAsync(null);

        dashboard.Error.Should().NotBeNull();
        dashboard.Error!.Title.Should().Be("Could not load your projects");
        dashboard.ShowHero.Should().BeFalse("an error is not an empty list");
        dashboard.ShowProjects.Should().BeFalse();

        await dashboard.RefreshCommand.ExecuteAsync(null);

        dashboard.Error.Should().BeNull();
        dashboard.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task Filter_chips_count_and_filter_projects()
    {
        Add("pinned", pinned: true);
        var running = Add("running");
        Add("broken", attention: [Critical("Conflicts")]);
        Add("api", group: "Work");
        Add("web", group: "Work");
        _activeRuns = [Run(running)];
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        dashboard.AllFilter.Count.Should().Be(5);
        dashboard.AttentionFilter.Count.Should().Be(1);
        dashboard.PinnedFilter.Count.Should().Be(1);
        dashboard.RunningFilter.Count.Should().Be(1);
        var work = dashboard.Filters.Single(f => f.Kind == DashboardFilterKind.Group);
        work.Title.Should().Be("Work");
        work.Count.Should().Be(2);

        dashboard.SelectedFilter = dashboard.AttentionFilter;
        dashboard.Projects.Select(p => p.Name).Should().Equal("broken");

        dashboard.SelectedFilter = dashboard.RunningFilter;
        dashboard.Projects.Select(p => p.Name).Should().Equal("running");

        dashboard.SelectedFilter = work;
        dashboard.Projects.Select(p => p.Name).Should().BeEquivalentTo("api", "web");
        dashboard.IsGrouped.Should().BeFalse("a single group needs no headers");
    }

    [Fact]
    public async Task A_selected_chip_stays_visible_and_deselecting_falls_back_to_all()
    {
        var running = Add("running");
        _activeRuns = [Run(running)];
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        dashboard.SelectedFilter = dashboard.RunningFilter;

        _activeRuns = [];
        _runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_runs, Run(running));

        dashboard.RunningFilter.Count.Should().Be(0);
        dashboard.RunningFilter.IsVisible.Should().BeTrue("the selected chip must not vanish");
        dashboard.ShowNoMatches.Should().BeTrue();

        dashboard.SelectedFilter = null!;

        dashboard.SelectedFilter.Should().BeSameAs(dashboard.AllFilter);
        dashboard.RunningFilter.IsVisible.Should().BeFalse();
        dashboard.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_narrows_the_list_and_clearing_restores_it()
    {
        var forge = Add("forge-app");
        Add("blog");
        _cached[forge.Id] = Snapshot(forge, branch: "feature/login", language: "TypeScript");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        dashboard.SearchText = "login";

        dashboard.Projects.Select(p => p.Name).Should().Equal("forge-app");

        dashboard.SearchText = "nothing-like-this";

        dashboard.ShowNoMatches.Should().BeTrue();
        dashboard.NoMatchesDescription.Should().Contain("nothing-like-this");

        dashboard.ClearFiltersCommand.Execute(null);

        dashboard.Projects.Should().HaveCount(2);
        dashboard.ShowNoMatches.Should().BeFalse();
    }

    [Fact]
    public async Task Sort_and_layout_are_restored_and_saved_in_the_settings()
    {
        _settings.ChangeExternally(s => s with { DashboardSort = "name", DashboardLayout = "list" });
        Add("beta");
        Add("alpha");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        dashboard.Sort.Should().Be(DashboardSortMode.Name);
        dashboard.IsListLayout.Should().BeTrue();
        dashboard.ShowList.Should().BeTrue();
        dashboard.Projects.Select(p => p.Name).Should().Equal("alpha", "beta");

        dashboard.SelectedSortOption = DashboardSortOption.For(DashboardSortMode.Recent);
        dashboard.IsGridLayout = true;

        _settings.Current.DashboardSort.Should().Be("recent");
        _settings.Current.DashboardLayout.Should().Be("grid");
        dashboard.ShowGrid.Should().BeTrue();
    }

    [Fact]
    public async Task Groups_get_headers_when_sorted_by_name()
    {
        Add("api", group: "Work");
        Add("blog", group: "Side");
        Add("tools");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        dashboard.IsGrouped.Should().BeFalse("the attention order ignores groups");

        dashboard.SelectedSortOption = DashboardSortOption.For(DashboardSortMode.Name);

        dashboard.IsGrouped.Should().BeTrue();
        dashboard.Projects.Select(p => p.GroupHeader).Should().Equal("Side", "Work", ProjectCardViewModel.UngroupedHeader);
    }

    [Fact]
    public async Task New_statuses_update_the_cards_and_the_order_live()
    {
        var first = Add("first", lastOpened: TestData.Now);
        var second = Add("second");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        dashboard.Projects.Select(p => p.Name).Should().Equal("first", "second");

        _status.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_status,
            Snapshot(second, capturedAt: TestData.Now.AddMinutes(1), attention: [Critical("Merge conflicts.")]));

        dashboard.Projects.Select(p => p.Name).Should().Equal("second", "first");
        dashboard.AttentionCount.Should().Be(1);
        dashboard.Projects[0].TopAttention!.Message.Should().Be("Merge conflicts.");
        dashboard.Projects.Should().Contain(p => p.Id == first.Id);
    }

    [Fact]
    public async Task Registry_changes_reload_the_list()
    {
        Add("first");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        Add("second");
        _registry.Changed += Raise.Event<EventHandler<ProjectsChangedEventArgs>>(_registry, new ProjectsChangedEventArgs(null));

        dashboard.Projects.Select(p => p.Name).Should().BeEquivalentTo("first", "second");
        dashboard.ProjectCount.Should().Be(2);
    }

    [Fact]
    public async Task Running_commands_update_the_running_count()
    {
        var project = Add("forge");
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        dashboard.RunningCount.Should().Be(0);

        var run = Run(project);
        _activeRuns = [run];
        _runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_runs, run);

        dashboard.RunningCount.Should().Be(1);
        dashboard.SummaryText.Should().Be("1 project · 1 running");
        dashboard.Projects[0].IsRunning.Should().BeTrue();

        _activeRuns = [];
        _runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_runs, new RunCompletedEventArgs(new RunRecord
        {
            Id = run.Id,
            ProjectId = project.Id,
            Label = "dev",
            CommandLine = "npm run dev",
            WorkingDirectory = "/dev/forge",
            LogPath = "/tmp/log",
        }));

        dashboard.RunningCount.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_all_refreshes_three_projects_at_a_time_and_reports_failures()
    {
        for (var i = 0; i < 7; i++)
        {
            Add($"p{i}");
        }

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

            await Task.Delay(20);
            lock (gate)
            {
                running--;
            }

            if (project.Name == "p3")
            {
                throw new ForgeException(ErrorKind.PathNotFound, "Unavailable");
            }

            return Snapshot(project, capturedAt: TestData.Now.AddMinutes(1), changed: 1);
        });
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        await dashboard.RefreshAllCommand.ExecuteAsync(null);

        await _status.Received(7).RefreshAsync(Arg.Any<Project>(), true, Arg.Any<CancellationToken>());
        maxRunning.Should().BeLessThanOrEqualTo(3);
        dashboard.IsRefreshingAll.Should().BeFalse();
        dashboard.Projects.Count(p => p.ChangedFiles == 1).Should().Be(6);
        _operations.Operations.Should().BeEmpty("the status bar operation ends with the refresh");
        _notifications.Received(1).Show("1 project could not be refreshed", Arg.Any<string?>(), NotificationSeverity.Warning, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Dropped_folders_are_added_and_the_rest_is_explained()
    {
        var existing = Add("existing");
        var newFolder = _folder.Combine("new-app");
        var otherFolder = _folder.Combine("other-app");
        Directory.CreateDirectory(newFolder);
        Directory.CreateDirectory(otherFolder);
        var file = _folder.Combine("notes.txt");
        File.WriteAllText(file, "x");
        var existingFolder = _folder.Combine("existing");
        Directory.CreateDirectory(existingFolder);
        _registry.FindByPathAsync(existingFolder, Arg.Any<CancellationToken>()).Returns(existing);
        _registry.AddAsync(Arg.Any<string>(), null, Arg.Any<CancellationToken>())
            .Returns(call => TestData.Project(Path.GetFileName(call.ArgAt<string>(0)), call.ArgAt<string>(0)));
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        await dashboard.AddFoldersCommand.ExecuteAsync(new[] { newFolder, otherFolder, file, existingFolder });

        await _registry.Received(1).AddAsync(newFolder, null, Arg.Any<CancellationToken>());
        await _registry.Received(1).AddAsync(otherFolder, null, Arg.Any<CancellationToken>());
        await _registry.DidNotReceive().AddAsync(existingFolder, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _notifications.Received(1).Show("Added 2 projects",
            "1 folder was already in ForgeDesk. 1 item was skipped: only folders can be added.", NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task A_single_dropped_folder_offers_to_open_it()
    {
        var folder = _folder.Combine("solo");
        Directory.CreateDirectory(folder);
        var project = TestData.Project("solo", folder);
        _registry.AddAsync(folder, null, Arg.Any<CancellationToken>()).Returns(project);
        NotificationAction? action = null;
        _notifications.Show("Added solo", Arg.Any<string?>(), NotificationSeverity.Success, Arg.Do<NotificationAction?>(a => action = a));
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        await dashboard.AddFoldersCommand.ExecuteAsync(new[] { folder });

        action.Should().NotBeNull();
        await action!.Execute();
        await _actions.Received(1).OpenAsync(project.Id, null, null);
    }

    [Fact]
    public async Task Setting_a_group_reuses_existing_spellings_and_empty_clears_it()
    {
        Add("api", group: "Work");
        var web = Add("web");
        _registry.UpdateAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var updated = call.Arg<Project>();
            _projects = _projects.Select(p => p.Id == updated.Id ? updated : p).ToList();
            return updated;
        });
        _dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns("work", string.Empty);
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        var card = dashboard.Projects.Single(p => p.Id == web.Id);

        await card.SetGroupCommand.ExecuteAsync(null);

        await _registry.Received(1).UpdateAsync(Arg.Is<Project>(p => p.Id == web.Id && p.Group == "Work"), Arg.Any<CancellationToken>());
        card.Group.Should().Be("Work");
        dashboard.Filters.Single(f => f.Kind == DashboardFilterKind.Group).Count.Should().Be(2);

        await card.SetGroupCommand.ExecuteAsync(null);

        await _registry.Received(1).UpdateAsync(Arg.Is<Project>(p => p.Id == web.Id && p.Group == null), Arg.Any<CancellationToken>());
        card.HasGroup.Should().BeFalse();
    }

    [Fact]
    public async Task The_group_prompt_lists_existing_groups_and_limits_the_length()
    {
        Add("api", group: "Work");
        var web = Add("web");
        PromptOptions? options = null;
        _dialogs.PromptAsync(Arg.Do<PromptOptions>(o => options = o)).Returns((string?)null);
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        await dashboard.Projects.Single(p => p.Id == web.Id).SetGroupCommand.ExecuteAsync(null);

        options!.Message.Should().Contain("Existing groups: Work");
        options.Validate!(new string('x', DashboardViewModel.MaxGroupLength + 1)).Should().NotBeNull();
        options.Validate!("Side projects").Should().BeNull();
        await _registry.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task Locating_a_missing_folder_relocates_and_refreshes_it()
    {
        var project = Add("moved");
        _cached[project.Id] = Snapshot(project, folderExists: false);
        var newPath = _folder.Combine("moved");
        Directory.CreateDirectory(newPath);
        var relocated = project with { Path = newPath };
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(newPath);
        _registry.RelocateAsync(project.Id, newPath, Arg.Any<CancellationToken>()).Returns(relocated);
        _status.RefreshAsync(relocated, false, Arg.Any<CancellationToken>()).Returns(Snapshot(relocated, capturedAt: TestData.Now.AddMinutes(1)));
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        var card = dashboard.Projects.Single();
        card.IsFolderMissing.Should().BeTrue();

        await card.LocateCommand.ExecuteAsync(null);

        card.Path.Should().Be(newPath);
        card.IsFolderMissing.Should().BeFalse();
        _notifications.Received(1).Show("Found moved", newPath, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Card_actions_go_through_the_shared_project_actions()
    {
        var project = Add("forge", attention: [Warning("Behind", "Git")]);
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);
        var card = dashboard.Projects.Single();

        await card.OpenCommand.ExecuteAsync(null);
        await card.OpenAttentionCommand.ExecuteAsync(card.TopAttention);
        await card.TogglePinCommand.ExecuteAsync(null);
        await card.RenameCommand.ExecuteAsync(null);
        await card.RemoveCommand.ExecuteAsync(null);
        card.OpenInExplorerCommand.Execute(null);

        await _actions.Received(1).OpenAsync(project.Id, null, null);
        await _actions.Received(1).OpenAsync(project.Id, WorkspaceSection.Git, null);
        await _actions.Received(1).TogglePinAsync(project.Id);
        await _actions.Received(1).RenameAsync(project.Id);
        await _actions.Received(1).RemoveAsync(project.Id);
        _actions.Received(1).OpenInExplorer(project.Path);
    }

    [Fact]
    public async Task Editor_and_terminal_failures_become_notifications()
    {
        Add("forge");
        _shell.When(s => s.OpenFolderInEditor(Arg.Any<string>())).Throw(new ForgeException(ErrorKind.ToolNotFound, "No code editor was found."));
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        dashboard.Projects.Single().OpenInEditorCommand.Execute(null);

        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "No code editor was found."), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Add_and_clone_use_the_shell_actions()
    {
        _actions.CanClone.Returns(true);
        var dashboard = Create();
        await dashboard.OnNavigatedToAsync(null);

        await dashboard.AddLocalProjectCommand.ExecuteAsync(null);
        await dashboard.CloneRepositoryCommand.ExecuteAsync(null);

        await _actions.Received(1).AddLocalProjectAsync();
        await _actions.Received(1).CloneRepositoryAsync();
    }

    [Fact]
    public void Cloning_is_disabled_without_a_clone_flow()
    {
        _actions.CanClone.Returns(false);

        Create().CloneRepositoryCommand.CanExecute(null).Should().BeFalse();
    }

    private DashboardViewModel Create() => _dashboard = new DashboardViewModel(_registry, _status, _runs, _actions, _settings, _dialogs,
        _notifications, _shell, _operations, ImmediateDispatcher.Instance, NullLogger<DashboardViewModel>.Instance)
    {
        LiveUpdateDelay = TimeSpan.Zero,
    };

    private Project Add(string name, bool pinned = false, string? group = null, DateTimeOffset? lastOpened = null, AttentionReason[]? attention = null)
    {
        var project = Project(name, pinned: pinned, group: group, lastOpened: lastOpened);
        _projects.Add(project);
        _cached[project.Id] = Snapshot(project, attention: attention ?? []);
        return project;
    }

    private static IRunSession Run(Project project)
    {
        var run = Substitute.For<IRunSession>();
        run.Id.Returns(Ids.New());
        run.Status.Returns(RunStatus.Running);
        run.Request.Returns(new RunRequest { ProjectId = project.Id, Label = "dev", CommandLine = "npm run dev", WorkingDirectory = project.Path });
        return run;
    }
}
