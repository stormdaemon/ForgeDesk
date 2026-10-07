using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Releases;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub;

public sealed class GitHubPaletteAndCompositionTests : IDisposable
{
    private static readonly GitHubRepoRef Repo = new("acme", "forge-app");
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.GitHub, WorkspaceSection.Releases);
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

    public void Dispose() => _harness.Dispose();

    private static PaletteQuery Everything => new(string.Empty, null);

    [Fact]
    public async Task Linked_project_offers_github_entries_that_open_the_right_view()
    {
        _harness.Project = TestData.Project("forge-app", _harness.Folder.Path, gitHub: Repo);
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        var source = new GitHubPaletteSource(_navigation, _harness.Shell, _harness.Notifications);

        var items = await source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().BeEquivalentTo("Open repository on GitHub", "New issue…", "New pull request…", "Pull requests", "Issues", "Actions");
        await items.Single(i => i.Title == "Open repository on GitHub").Execute();
        _harness.Shell.Received(1).OpenUrl("https://github.com/acme/forge-app");

        await items.Single(i => i.Title == "New issue…").Execute();
        var section = _harness.Sections.Created.Single(s => s.Section == WorkspaceSection.GitHub);
        section.NavigatedTo.Should().ContainSingle().Which.Should().Be(GitHubNavigation.NewIssue());

        var actionsOnly = new PaletteQuery("is", null) { Categories = new HashSet<PaletteCategory> { PaletteCategory.Navigation } };
        (await source.GetItemsAsync(actionsOnly, TestContext.Current.CancellationToken)).Should().OnlyContain(i => i.Category == PaletteCategory.Navigation);
    }

    [Fact]
    public async Task Unlinked_project_and_other_pages_offer_nothing()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        (await new GitHubPaletteSource(_navigation, _harness.Shell, _harness.Notifications).GetItemsAsync(Everything, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
        (await new ReleasesPaletteSource(_navigation).GetItemsAsync(Everything, TestContext.Current.CancellationToken)).Should().BeEmpty();

        _navigation.CurrentPage.Returns(new object());
        (await new GitHubPaletteSource(_navigation, _harness.Shell, _harness.Notifications).GetItemsAsync(Everything, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task New_release_opens_the_wizard_of_the_releases_tab()
    {
        _harness.Project = TestData.Project("forge-app", _harness.Folder.Path, gitHub: Repo);
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await new ReleasesPaletteSource(_navigation).GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Should().ContainSingle(i => i.Title == "New release…");
        await items[0].Execute();
        _harness.Sections.Created.Single(s => s.Section == WorkspaceSection.Releases).NavigatedTo.Should().ContainSingle()
            .Which.Should().Be(ReleasesNavigation.NewRelease());
    }

    [Fact]
    public async Task The_github_and_releases_tabs_are_created_by_the_workspace_with_their_services()
    {
        using var folder = new TestFolder();
        using var provider = Build();
        var project = TestData.Project("forge-app", folder.Path, gitHub: Repo);

        using var workspace = provider.GetRequiredService<IProjectWorkspaceFactory>().Create(project);
        await workspace.SelectSectionAsync(WorkspaceSection.GitHub);
        workspace.CurrentSection.Should().BeOfType<GitHubSectionViewModel>();
        await workspace.SelectSectionAsync(WorkspaceSection.Releases);
        workspace.CurrentSection.Should().BeOfType<ReleasesViewModel>();

        provider.GetServices<IPaletteSource>().Should().ContainSingle(s => s is GitHubPaletteSource);
        provider.GetServices<IPaletteSource>().Should().ContainSingle(s => s is ReleasesPaletteSource);
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IUiDispatcher>(ImmediateDispatcher.Instance);
        foreach (var contract in new[]
        {
            typeof(IDialogService), typeof(INotificationService), typeof(IShellIntegration), typeof(IProjectRegistry), typeof(IProjectStatusService),
            typeof(IRunService), typeof(IWorkItemService), typeof(IActivityLog), typeof(IGitService), typeof(IProjectDetector), typeof(IProjectWatcher),
            typeof(IGitHubAccountService), typeof(IGitHubService), typeof(IReleaseService),
        })
        {
            services.AddSingleton(contract, Substitute.For([contract], []));
        }

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default);
        services.AddSingleton(settings);
        services.AddForgeDeskPresentation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
