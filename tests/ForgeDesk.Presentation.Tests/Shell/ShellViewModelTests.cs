using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Shell.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class ShellViewModelTests : IDisposable
{
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Git, WorkspaceSection.Terminal);
    private readonly Project _last = TestData.Project("last", "/dev/last", lastOpened: TestData.Now);
    private AppSettings _currentSettings = AppSettings.Default with { OnboardingCompleted = true };
    private ShellViewModel? _shell;

    public ShellViewModelTests()
    {
        _settings.Current.Returns(_ => _currentSettings);
        _runs.ActiveRuns.Returns([]);
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        _registry.GetAsync(_last.Id, Arg.Any<CancellationToken>()).Returns(_last);
        _actions.OpenAsync(Arg.Any<string>(), Arg.Any<WorkspaceSection?>(), Arg.Any<object?>()).Returns(true);
    }

    public void Dispose()
    {
        _shell?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public async Task First_start_shows_onboarding()
    {
        _currentSettings = AppSettings.Default;

        await Create(PageKind.Dashboard, PageKind.Onboarding).InitializeAsync();

        _navigation.Received(1).OpenOnboarding();
        _navigation.DidNotReceive().GoToDashboard();
    }

    [Fact]
    public async Task Without_an_onboarding_page_the_dashboard_is_shown()
    {
        _currentSettings = AppSettings.Default;

        var shell = Create(PageKind.Dashboard);
        await shell.InitializeAsync();

        _navigation.DidNotReceive().OpenOnboarding();
        _navigation.Received(1).GoToDashboard();
        shell.IsStarted.Should().BeTrue();
    }

    [Fact]
    public async Task The_last_project_is_restored_when_it_still_exists()
    {
        _currentSettings = _currentSettings with { LastOpenedProjectId = _last.Id };

        await Create(PageKind.Dashboard).InitializeAsync();

        await _actions.Received(1).OpenAsync(_last.Id, null, null);
        _navigation.DidNotReceive().GoToDashboard();
    }

    [Fact]
    public async Task A_last_project_that_was_removed_leads_to_the_dashboard()
    {
        _currentSettings = _currentSettings with { LastOpenedProjectId = "removed" };

        await Create(PageKind.Dashboard).InitializeAsync();

        await _actions.DidNotReceiveWithAnyArgs().OpenAsync(default!, default, default);
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Restoring_can_be_turned_off()
    {
        _currentSettings = _currentSettings with { LastOpenedProjectId = _last.Id, RestoreLastProjectOnStartup = false };

        await Create(PageKind.Dashboard).InitializeAsync();

        await _actions.DidNotReceiveWithAnyArgs().OpenAsync(default!, default, default);
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task A_project_that_fails_to_open_at_startup_falls_back_to_the_dashboard()
    {
        _currentSettings = _currentSettings with { LastOpenedProjectId = _last.Id };
        _actions.OpenAsync(_last.Id, null, null).Returns(false);

        await Create(PageKind.Dashboard).InitializeAsync();

        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Activation_wins_over_restoring_the_last_project()
    {
        _currentSettings = _currentSettings with { LastOpenedProjectId = _last.Id };
        var shell = Create(PageKind.Dashboard);

        await shell.HandleActivationAsync("requested", null);
        await shell.InitializeAsync();

        await _actions.Received(1).OpenAsync("requested", null, null);
        await _actions.DidNotReceive().OpenAsync(_last.Id, Arg.Any<WorkspaceSection?>(), Arg.Any<object?>());
        _navigation.DidNotReceive().GoToDashboard();
    }

    [Fact]
    public async Task Activation_with_a_folder_adds_and_opens_it()
    {
        var shell = Create(PageKind.Dashboard);
        _actions.AddOrOpenFolderAsync(@"C:\dev\new-app").Returns(true);

        await shell.HandleActivationAsync(null, @"C:\dev\new-app");

        await _actions.Received(1).AddOrOpenFolderAsync(@"C:\dev\new-app");
        await _actions.DidNotReceiveWithAnyArgs().OpenAsync(default!, default, default);
    }

    [Fact]
    public async Task Failed_activation_leaves_the_user_on_a_page()
    {
        _actions.OpenAsync("gone", null, null).Returns(false);
        var shell = Create(PageKind.Dashboard);

        await shell.HandleActivationAsync("gone", null);

        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Page_changes_update_breadcrumb_and_window_title()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var shell = Create(PageKind.Dashboard);

        Navigate(PageKind.Project, workspace, workspace.ProjectId);

        shell.CurrentWorkspace.Should().BeSameAs(workspace);
        shell.PageTitle.Should().Be("Projects");
        shell.WindowTitle.Should().Be("forge-app — ForgeDesk");

        Navigate(PageKind.Settings, new object(), null);

        shell.HasWorkspace.Should().BeFalse();
        shell.PageTitle.Should().Be("Settings");
        shell.WindowTitle.Should().Be("ForgeDesk");
    }

    [Fact]
    public async Task Ctrl_digit_selects_workspace_tabs_by_position()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var shell = Create(PageKind.Dashboard);
        Navigate(PageKind.Project, workspace, workspace.ProjectId);

        await shell.SelectTabCommand.ExecuteAsync("2");

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Terminal);

        await shell.SelectTabCommand.ExecuteAsync(1);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Git);
    }

    [Fact]
    public async Task Ctrl_backtick_opens_the_terminal_tab()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var shell = Create(PageKind.Dashboard);
        Navigate(PageKind.Project, workspace, workspace.ProjectId);

        await shell.OpenTerminalCommand.ExecuteAsync(null);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Terminal);
    }

    [Fact]
    public async Task F5_refreshes_the_current_section_first()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var shell = Create(PageKind.Dashboard);
        Navigate(PageKind.Project, workspace, workspace.ProjectId);

        await shell.RefreshCommand.ExecuteAsync(null);

        _harness.Sections.Single(WorkspaceSection.Overview).RefreshCount.Should().Be(1);
    }

    [Fact]
    public async Task F5_refreshes_a_refreshable_page()
    {
        var page = new RefreshablePage();
        var shell = Create(PageKind.Dashboard);
        Navigate(PageKind.Dashboard, page, null);

        await shell.RefreshCommand.ExecuteAsync(null);

        page.Refreshed.Should().Be(1);
    }

    [Fact]
    public void Go_to_file_opens_the_palette_in_file_mode_only_inside_a_project()
    {
        var shell = Create(PageKind.Dashboard);

        shell.GoToFileCommand.Execute(null);

        shell.Palette.IsOpen.Should().BeTrue();
        shell.Palette.QueryText.Should().BeEmpty();
    }

    [Fact]
    public async Task Go_to_file_uses_the_file_prefix_in_a_project()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        var shell = Create(PageKind.Dashboard);
        Navigate(PageKind.Project, workspace, workspace.ProjectId);

        shell.GoToFileCommand.Execute(null);

        shell.Palette.QueryText.Should().Be("/");
        shell.Palette.Mode.Should().Be(PaletteMode.Files);
    }

    [Fact]
    public void Back_is_enabled_only_when_there_is_history()
    {
        var shell = Create(PageKind.Dashboard);
        shell.GoBackCommand.CanExecute(null).Should().BeFalse();

        _navigation.CanGoBack.Returns(true);
        Navigate(PageKind.Activity, new object(), null);

        shell.GoBackCommand.CanExecute(null).Should().BeTrue();
        shell.GoBackCommand.Execute(null);
        _navigation.Received(1).GoBack();
    }

    [Fact]
    public void Clone_entry_points_follow_the_clone_handler()
    {
        _actions.CanClone.Returns(false);
        var shell = Create(PageKind.Dashboard);

        shell.CloneRepositoryCommand.CanExecute(null).Should().BeFalse();
        shell.CloneActionText.Should().BeNull();

        _actions.CanClone.Returns(true);
        var withClone = Create(PageKind.Dashboard);

        withClone.CloneRepositoryCommand.CanExecute(null).Should().BeTrue();
        withClone.CloneActionText.Should().Be("Clone repository…");
    }

    [Fact]
    public async Task Nothing_to_show_is_reported_only_after_startup()
    {
        var shell = Create();
        shell.ShowNoPage.Should().BeFalse();

        await shell.InitializeAsync();

        shell.ShowNoPage.Should().BeTrue("no page could be shown, so the shell offers to add a project");
    }

    private void Navigate(PageKind kind, object page, string? projectId)
    {
        _navigation.CurrentKind.Returns(kind);
        _navigation.CurrentPage.Returns(page);
        _navigation.CurrentProjectId.Returns(projectId);
        _navigation.Navigated += Raise.Event();
    }

    private ShellViewModel Create(params PageKind[] pages)
    {
        _shell?.Dispose();
        var factory = new FakePageFactory(pages);
        var status = Substitute.For<IProjectStatusService>();
        var github = Substitute.For<IGitHubAccountService>();
        var sidebar = new SidebarViewModel(_registry, status, _runs, _navigation, factory, _actions, ImmediateDispatcher.Instance,
            NullLogger<SidebarViewModel>.Instance) { RefreshInterval = TimeSpan.FromHours(1) };
        var statusBar = new StatusBarViewModel(_navigation, _runs, new BackgroundOperations(ImmediateDispatcher.Instance), github, _actions,
            ImmediateDispatcher.Instance);
        var palette = new CommandPaletteViewModel([], _navigation, Substitute.For<INotificationService>(), NullLogger<CommandPaletteViewModel>.Instance);
        _shell = new ShellViewModel(_navigation, factory, _settings, _registry, _actions, sidebar, statusBar, palette, NullLogger<ShellViewModel>.Instance);
        return _shell;
    }

    private sealed class RefreshablePage : IRefreshable
    {
        public RefreshablePage() => RefreshCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() =>
        {
            Refreshed++;
            return Task.CompletedTask;
        });

        public int Refreshed { get; private set; }

        public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand RefreshCommand { get; }
    }
}
