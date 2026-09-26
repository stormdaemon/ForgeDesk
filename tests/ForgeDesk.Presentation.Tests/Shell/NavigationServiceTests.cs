using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Shell.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class NavigationServiceTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Git, WorkspaceSection.Commands);
    private readonly IProjectWatcher _watcher = Substitute.For<IProjectWatcher>();
    private readonly IProjectWorkspaceFactory _workspaces = Substitute.For<IProjectWorkspaceFactory>();
    private readonly FakePageFactory _pages = new(PageKind.Dashboard, PageKind.Activity, PageKind.Settings, PageKind.Onboarding);
    private readonly List<Func<AppSettings, AppSettings>> _settingsChanges = [];
    private readonly Project _alpha;
    private readonly Project _beta;
    private readonly NavigationService _navigation;

    public NavigationServiceTests()
    {
        _alpha = TestData.Project("alpha", _harness.Folder.Path);
        _beta = TestData.Project("beta", Directory.CreateDirectory(_harness.Folder.Combine("beta")).FullName);
        _harness.Registry.GetAsync(_alpha.Id, Arg.Any<CancellationToken>()).Returns(_ => _alpha);
        _harness.Registry.GetAsync(_beta.Id, Arg.Any<CancellationToken>()).Returns(_ => _beta);
        _harness.Settings.UpdateAsync(Arg.Do<Func<AppSettings, AppSettings>>(_settingsChanges.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _workspaces.Create(Arg.Any<Project>()).Returns(call => _harness.CreateWorkspace(call.Arg<Project>()));
        _navigation = new NavigationService(_pages, _workspaces, _harness.Registry, _harness.Settings, _watcher, ImmediateDispatcher.Instance,
            NullLogger<NavigationService>.Instance);
    }

    public void Dispose()
    {
        _navigation.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void Pages_are_created_once_and_back_walks_the_history()
    {
        var navigated = 0;
        _navigation.Navigated += (_, _) => navigated++;

        _navigation.GoToDashboard();
        _navigation.OpenActivity();
        _navigation.OpenSettings();
        _navigation.GoToDashboard();

        _pages.Created.Select(p => p.Kind).Should().Equal(PageKind.Dashboard, PageKind.Activity, PageKind.Settings);
        navigated.Should().Be(4);

        _navigation.GoBack();
        _navigation.CurrentKind.Should().Be(PageKind.Settings);
        _navigation.GoBack();
        _navigation.CurrentKind.Should().Be(PageKind.Activity);
        _navigation.GoBack();
        _navigation.CurrentPage.Should().BeSameAs(_pages.Page(PageKind.Dashboard));
        _navigation.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void Pages_are_told_when_they_are_shown_and_left()
    {
        _navigation.GoToDashboard();
        _navigation.OpenSettings("GitHub");
        _navigation.OpenSettings("Git");

        var settings = _pages.Page(PageKind.Settings);
        settings.NavigatedTo.Should().Equal("GitHub", "Git");
        _pages.Page(PageKind.Dashboard).NavigatedFromCount.Should().Be(1);
        _navigation.CanGoBack.Should().BeTrue();

        _navigation.GoBack();
        _navigation.CanGoBack.Should().BeFalse("choosing another settings section is not a new history entry");
    }

    [Fact]
    public void Unavailable_page_keeps_the_current_one()
    {
        var navigation = new NavigationService(new FakePageFactory(PageKind.Dashboard), _workspaces, _harness.Registry, _harness.Settings, _watcher,
            ImmediateDispatcher.Instance, NullLogger<NavigationService>.Instance);
        navigation.GoToDashboard();
        var dashboard = navigation.CurrentPage;

        navigation.OpenActivity();
        navigation.OpenSettings();

        navigation.CurrentPage.Should().BeSameAs(dashboard);
        navigation.CanGoBack.Should().BeFalse();
        navigation.IsAvailable(PageKind.Activity).Should().BeFalse();
    }

    [Fact]
    public void Onboarding_is_not_kept_in_the_history()
    {
        _navigation.OpenOnboarding();
        _navigation.GoToDashboard();

        _navigation.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Opening_a_project_marks_it_opened_remembers_it_and_watches_its_folder()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);

        var workspace = _navigation.CurrentPage.Should().BeOfType<ProjectWorkspaceViewModel>().Subject;
        workspace.ProjectId.Should().Be(_alpha.Id);
        _navigation.CurrentKind.Should().Be(PageKind.Project);
        _navigation.CurrentProjectId.Should().Be(_alpha.Id);
        await _harness.Registry.Received(1).MarkOpenedAsync(_alpha.Id, Arg.Any<CancellationToken>());
        _settingsChanges.Should().ContainSingle();
        _settingsChanges[0](AppSettings.Default).LastOpenedProjectId.Should().Be(_alpha.Id);
        _watcher.Received(1).Watch(_alpha.Path);
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Overview, "the workspace was initialized");
    }

    [Fact]
    public async Task Each_project_keeps_one_workspace_while_it_is_open()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);
        var first = _navigation.CurrentPage;
        await _navigation.OpenProjectAsync(_beta.Id);
        await _navigation.OpenProjectAsync(_alpha.Id);

        _navigation.CurrentPage.Should().BeSameAs(first);
        _workspaces.Received(2).Create(Arg.Any<Project>());
        _navigation.OpenWorkspaces.Should().HaveCount(2);
    }

    [Fact]
    public async Task Opening_a_project_on_a_section_passes_the_argument()
    {
        await _navigation.OpenProjectAsync(_alpha.Id, WorkspaceSection.Git, "src/main.rs");

        var workspace = (ProjectWorkspaceViewModel)_navigation.CurrentPage!;
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Git);
        _harness.Sections.Single(WorkspaceSection.Git).NavigatedTo.Should().Equal("src/main.rs");

        await _navigation.OpenProjectAsync(_alpha.Id, WorkspaceSection.Commands);

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Commands);
        _navigation.CanGoBack.Should().BeFalse("switching tabs of the current project is not page navigation");
    }

    [Fact]
    public async Task Unknown_project_cannot_be_opened()
    {
        var open = () => _navigation.OpenProjectAsync("missing");

        (await open.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
        _navigation.CurrentPage.Should().BeNull();
    }

    [Fact]
    public async Task Closing_the_current_project_goes_back_and_disposes_its_workspace()
    {
        _navigation.GoToDashboard();
        await _navigation.OpenProjectAsync(_alpha.Id);
        var lifetime = ((ProjectWorkspaceViewModel)_navigation.CurrentPage!).Context.Lifetime;

        _navigation.CloseProject(_alpha.Id);

        _navigation.CurrentPage.Should().BeSameAs(_pages.Page(PageKind.Dashboard));
        _watcher.Received(1).Unwatch(_alpha.Path);
        lifetime.IsCancellationRequested.Should().BeTrue();
        _navigation.OpenWorkspaces.Should().BeEmpty();
        _navigation.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Closing_the_only_page_falls_back_to_the_dashboard()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);

        _navigation.CloseProject(_alpha.Id);

        _navigation.CurrentKind.Should().Be(PageKind.Dashboard);
        _navigation.CurrentPage.Should().BeOfType<FakePage>();
    }

    [Fact]
    public async Task Closing_a_background_project_removes_it_from_the_history()
    {
        _navigation.GoToDashboard();
        await _navigation.OpenProjectAsync(_alpha.Id);
        await _navigation.OpenProjectAsync(_beta.Id);

        _navigation.CloseProject(_alpha.Id);
        _navigation.GoBack();

        _navigation.CurrentKind.Should().Be(PageKind.Dashboard);
    }

    [Fact]
    public async Task File_changes_reach_the_right_project()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);
        await _navigation.OpenProjectAsync(_beta.Id);
        var alpha = _navigation.OpenWorkspaces.Single(w => w.ProjectId == _alpha.Id);
        var beta = _navigation.OpenWorkspaces.Single(w => w.ProjectId == _beta.Id);
        var alphaChanges = 0;
        var betaRepositoryChanges = 0;
        alpha.Context.FilesChanged += (_, _) => alphaChanges++;
        beta.Context.RepositoryChanged += (_, _) => betaRepositoryChanges++;

        _watcher.Changed += Raise.Event<EventHandler<ProjectFilesChangedEventArgs>>(_watcher, new ProjectFilesChangedEventArgs(_alpha.Path, false, true));
        _watcher.Changed += Raise.Event<EventHandler<ProjectFilesChangedEventArgs>>(_watcher, new ProjectFilesChangedEventArgs(_beta.Path, true, false));

        alphaChanges.Should().Be(1);
        betaRepositoryChanges.Should().Be(1);
    }

    [Fact]
    public async Task Project_removed_from_the_registry_is_closed()
    {
        _navigation.GoToDashboard();
        await _navigation.OpenProjectAsync(_alpha.Id);
        _harness.Registry.GetAsync(_alpha.Id, Arg.Any<CancellationToken>()).Returns((Project?)null);

        _harness.Registry.Changed += Raise.Event<EventHandler<ProjectsChangedEventArgs>>(_harness.Registry, new ProjectsChangedEventArgs(_alpha.Id));

        _navigation.OpenWorkspaces.Should().BeEmpty();
        _navigation.CurrentKind.Should().Be(PageKind.Dashboard);
    }

    [Fact]
    public async Task Renamed_or_relocated_project_updates_its_workspace_and_watcher()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);
        var workspace = (ProjectWorkspaceViewModel)_navigation.CurrentPage!;
        var moved = _alpha with { Name = "alpha 2", Path = _beta.Path };
        _harness.Registry.GetAsync(_alpha.Id, Arg.Any<CancellationToken>()).Returns(moved);

        _harness.Registry.Changed += Raise.Event<EventHandler<ProjectsChangedEventArgs>>(_harness.Registry, new ProjectsChangedEventArgs(null));

        workspace.Name.Should().Be("alpha 2");
        _watcher.Received(1).Unwatch(_alpha.Path);
        _watcher.Received(1).Watch(_beta.Path);
    }

    [Fact]
    public async Task Leaving_a_workspace_deactivates_its_section()
    {
        await _navigation.OpenProjectAsync(_alpha.Id);

        _navigation.GoToDashboard();

        _harness.Sections.Single(WorkspaceSection.Overview).DeactivateCount.Should().Be(1);
        _navigation.OpenWorkspaces.Should().ContainSingle("the workspace stays open in the background");
    }

    [Fact]
    public async Task Dispose_closes_every_workspace_and_disposable_page()
    {
        _navigation.GoToDashboard();
        await _navigation.OpenProjectAsync(_alpha.Id);
        var lifetime = ((ProjectWorkspaceViewModel)_navigation.CurrentPage!).Context.Lifetime;

        _navigation.Dispose();

        lifetime.IsCancellationRequested.Should().BeTrue();
        _pages.Page(PageKind.Dashboard).IsDisposed.Should().BeTrue();
    }
}
