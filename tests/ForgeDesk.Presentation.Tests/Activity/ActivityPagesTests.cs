using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Activity;

/// <summary>The project Activity tab and the global Activity page around the shared timeline.</summary>
public sealed class ActivityPagesTests : IDisposable
{
    private static readonly DateTimeOffset Now = FrozenTime.Now;

    private readonly ActivityHarness _h = new();
    private readonly TestFolder _folder = new();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectActions _projectActions = Substitute.For<IProjectActions>();
    private readonly ProjectContext _context;
    private readonly List<WorkspaceNavigationRequest> _navigations = [];

    public ActivityPagesTests()
    {
        var project = TestData.Project("forge-app", _folder.Path, id: "p1");
        _context = new ProjectContext(project, Substitute.For<IGitService>(), Substitute.For<IProjectDetector>(), _registry, ImmediateDispatcher.Instance);
        _context.NavigationRequested += (_, request) => _navigations.Add(request);
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Project>>(
        [
            TestData.Project("forge-app", _folder.Path, id: "p1") with { Color = "#112233" },
            TestData.Project("website", _folder.Path, id: "p2"),
        ]));
    }

    public void Dispose()
    {
        _context.Dispose();
        _folder.Dispose();
    }

    private ActivitySectionViewModel CreateSection() =>
        new(_context, _h.Log, _h.Dialogs, _h.Notifications, _h.Shell, ImmediateDispatcher.Instance, ActivityHarness.Options());

    private GlobalActivityViewModel CreatePage() =>
        new(_h.Log, _registry, _projectActions, _h.Dialogs, _h.Notifications, _h.Shell, ImmediateDispatcher.Instance, ActivityHarness.Options());

    [Fact]
    public async Task The_tab_loads_this_project_once_and_refreshes_on_F5()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Mine"));
        _h.Entries.Add(ActivityHarness.Entry(2, Now, "Not mine", projectId: "p2"));
        using var section = CreateSection();

        section.Section.Should().Be(WorkspaceSection.Activity);
        await section.ActivateAsync();
        section.Deactivate();
        await section.ActivateAsync();

        _h.Queries.Should().ContainSingle().Which.ProjectId.Should().Be("p1");
        section.Timeline.Rows.OfType<ActivityItemViewModel>().Should().ContainSingle(i => i.Title == "Mine");
        section.Timeline.IsGlobal.Should().BeFalse();

        await section.RefreshCommand.ExecuteAsync(null);
        _h.Queries.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_tab_opens_references_in_the_other_tabs_of_the_project()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Ran tests", ActivityKind.RunCompleted, refKind: "run", refValue: "run-1"));
        _h.Entries.Add(ActivityHarness.Entry(2, Now.AddMinutes(-1), "Committed", refKind: "commit", refValue: "abcdef12"));
        using var section = CreateSection();
        await section.ActivateAsync();
        var items = section.Timeline.Rows.OfType<ActivityItemViewModel>().ToList();

        await section.Timeline.OpenEntryCommand.ExecuteAsync(items[0]);
        await section.Timeline.OpenEntryCommand.ExecuteAsync(items[1]);

        _navigations.Should().Equal(
            new WorkspaceNavigationRequest(WorkspaceSection.Commands, "run-1"),
            new WorkspaceNavigationRequest(WorkspaceSection.Git, "abcdef12"));
    }

    [Fact]
    public async Task The_page_shows_every_project_with_its_name_and_color()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Committed", projectId: "p1"));
        _h.Entries.Add(ActivityHarness.Entry(2, Now.AddMinutes(-1), "Released", ActivityKind.ReleasePublished, projectId: "p2"));
        using var page = CreatePage();

        await page.OnNavigatedToAsync(null);
        await page.OnNavigatedToAsync(null);

        _h.Queries.Should().ContainSingle().Which.ProjectId.Should().BeNull();
        var items = page.Timeline.Rows.OfType<ActivityItemViewModel>().ToList();
        items.Select(i => i.ProjectName).Should().Equal("forge-app", "website");
        items[0].ProjectColor.Should().Be("#112233");
        items.Should().OnlyContain(i => i.ShowProject && i.HasProject);
    }

    [Fact]
    public async Task The_page_opens_the_project_of_an_entry_at_the_right_tab()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Added project", ActivityKind.ProjectAdded, projectId: "p2"));
        _h.Entries.Add(ActivityHarness.Entry(2, Now.AddMinutes(-1), "Committed", refKind: "commit", refValue: "abcdef12", projectId: "p1"));
        using var page = CreatePage();
        await page.OnNavigatedToAsync(null);
        var items = page.Timeline.Rows.OfType<ActivityItemViewModel>().ToList();

        await page.Timeline.OpenEntryCommand.ExecuteAsync(items[0]);
        await page.Timeline.OpenEntryCommand.ExecuteAsync(items[1]);

        await _projectActions.Received(1).OpenAsync("p2", WorkspaceSection.Overview, null);
        await _projectActions.Received(1).OpenAsync("p1", WorkspaceSection.Git, "abcdef12");
    }

    [Fact]
    public async Task Entries_of_removed_projects_keep_their_text_but_lead_nowhere()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Old work", projectId: "gone", refKind: "commit", refValue: "abcdef12"));
        using var page = CreatePage();

        await page.OnNavigatedToAsync(null);

        var item = page.Timeline.Rows.OfType<ActivityItemViewModel>().Single();
        item.CanFollow.Should().BeFalse();
        item.HasProject.Should().BeFalse();
    }

    [Fact]
    public async Task Renamed_projects_update_the_page_live()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Committed", projectId: "p2"));
        using var page = CreatePage();
        await page.OnNavigatedToAsync(null);
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Project>>(
            [TestData.Project("marketing-site", _folder.Path, id: "p2")]));

        _registry.Changed += Raise.Event<EventHandler<ProjectsChangedEventArgs>>(_registry, new ProjectsChangedEventArgs("p2"));

        page.Timeline.Rows.OfType<ActivityItemViewModel>().Single().ProjectName.Should().Be("marketing-site");
    }

    [Fact]
    public void The_registration_adds_the_tab_and_the_page()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddActivityPresentation();

        var instances = services.Select(d => d.ImplementationInstance).ToList();
        instances.Should().Contain(new WorkspaceSectionRegistration(WorkspaceSection.Activity, typeof(ActivitySectionViewModel)));
        instances.Should().Contain(new PageRegistration(PageKind.Activity, typeof(GlobalActivityViewModel)));
    }
}
