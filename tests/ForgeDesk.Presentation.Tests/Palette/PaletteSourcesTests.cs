using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Palette;

public sealed class PaletteSourcesTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Git, WorkspaceSection.Terminal);
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IPageFactory _pages = Substitute.For<IPageFactory>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private readonly IGitHubAccountService _github = Substitute.For<IGitHubAccountService>();

    public void Dispose() => _harness.Dispose();

    private static PaletteQuery Everything => new(string.Empty, null);

    [Fact]
    public async Task Navigation_lists_registered_pages_only()
    {
        _pages.IsAvailable(PageKind.Dashboard).Returns(true);
        _pages.IsAvailable(PageKind.Settings).Returns(true);

        var items = await new NavigationPaletteSource(_navigation, _pages).GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().Equal("Go to Dashboard", "Open Settings");
        items.Single(i => i.Title == "Open Settings").Shortcut.Should().Be("Ctrl+,");
        await items[0].Execute();
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Navigation_lists_the_open_projects_tabs_with_their_shortcuts()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        _navigation.CanGoBack.Returns(true);

        var items = await new NavigationPaletteSource(_navigation, _pages).GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Where(i => i.Title.StartsWith("Go to ", StringComparison.Ordinal))
            .Select(i => (i.Title, i.Shortcut, i.Subtitle))
            .Should().Equal(("Go to Overview", "Ctrl+1", "forge-app"), ("Go to Git", "Ctrl+2", "forge-app"), ("Go to Terminal", "Ctrl+3", "forge-app"));
        items.Should().Contain(i => i.Title == "Go back" && i.Shortcut == "Alt+Left");

        await items.Single(i => i.Title == "Go to Terminal").Execute();
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Terminal);
    }

    [Fact]
    public async Task Sources_skip_categories_the_mode_does_not_want()
    {
        var projectsOnly = new PaletteQuery("x", null) { Categories = new HashSet<PaletteCategory> { PaletteCategory.Project } };
        _pages.IsAvailable(Arg.Any<PageKind>()).Returns(true);

        (await new NavigationPaletteSource(_navigation, _pages).GetItemsAsync(projectsOnly, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await ShellActions().GetItemsAsync(projectsOnly, TestContext.Current.CancellationToken)).Should().BeEmpty();
        await _harness.Registry.DidNotReceiveWithAnyArgs().GetAllAsync(default);
    }

    [Fact]
    public async Task Shell_actions_without_a_project_offer_app_level_actions()
    {
        _actions.CanClone.Returns(false);

        var items = await ShellActions().GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().Equal("Add local project…", "Sign in to GitHub");
        items[0].Shortcut.Should().Be("Ctrl+O");
    }

    [Fact]
    public async Task Clone_theme_and_account_actions_follow_what_is_available()
    {
        _actions.CanClone.Returns(true);
        var theme = Substitute.For<IThemeSwitcher>();
        theme.IsDark.Returns(true);
        _github.Current.Returns(new GitHubAccount(new GitHubUser("octocat", null, "https://avatars/octocat", "https://github.com/octocat"), [],
            GitHubAuthMethod.GitHubCli));

        var items = await ShellActions(theme).GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().Equal("Add local project…", "Clone repository…", "Switch to light theme", "GitHub account");
        items.Single(i => i.Title == "GitHub account").Subtitle.Should().Be("Signed in as octocat");

        await items.Single(i => i.Title == "Switch to light theme").Execute();
        await theme.Received(1).ToggleAsync();
    }

    [Fact]
    public async Task Shell_actions_include_the_open_projects_git_and_open_in_actions()
    {
        _harness.CurrentStatus = TestData.Status("feature/x", upstream: null);
        _harness.Shell.EditorName.Returns("Visual Studio Code");
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await ShellActions().GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().Contain(["Fetch", "Pull", "Publish branch", "Create branch…", "Switch branch…", "Open in Explorer",
            "Open in Visual Studio Code", "Open in terminal", "Copy project path", "Close project", "Remove from ForgeDesk…"]);
        items.Should().NotContain(i => i.Title == "Open on GitHub");
        items.Single(i => i.Title == "Fetch").Subtitle.Should().Be("forge-app");

        await items.Single(i => i.Title == "Switch branch…").Execute();
        workspace.Branches.IsOpen.Should().BeTrue();

        await items.Single(i => i.Title == "Close project").Execute();
        _navigation.Received(1).CloseProject(_harness.Project.Id);
    }

    [Fact]
    public async Task Git_actions_are_hidden_outside_a_repository()
    {
        _harness.CurrentStatus = null;
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await ShellActions().GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Should().NotContain(i => i.Title == "Fetch" || i.Title == "Push" || i.Title == "Create branch…");
        items.Should().Contain(i => i.Title == "Open in Explorer");
    }

    [Fact]
    public async Task Projects_are_offered_with_pinned_and_recent_ones_boosted()
    {
        var now = DateTimeOffset.UtcNow;
        var pinned = TestData.Project("pinned", "/p/pinned", pinned: true, lastOpened: now.AddDays(-3));
        var recent = TestData.Project("recent", "/p/recent", lastOpened: now.AddHours(-2), gitHub: new GitHubRepoRef("octo", "recent"));
        var old = TestData.Project("old", "/p/old", lastOpened: now.AddDays(-40));
        var current = TestData.Project("current", "/p/current", lastOpened: now);
        _harness.Registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns([pinned, recent, old, current]);
        var source = new ProjectsPaletteSource(_harness.Registry, _actions, NullLogger<ProjectsPaletteSource>.Instance);

        var items = await source.GetItemsAsync(new PaletteQuery(string.Empty, current.Id), TestContext.Current.CancellationToken);

        items.Should().OnlyContain(i => i.Category == PaletteCategory.Project);
        items.Single(i => i.Title == "recent").Keywords.Should().Be("octo/recent");
        items.Single(i => i.Title == "recent").Subtitle.Should().Be("/p/recent");
        items.OrderByDescending(i => i.Boost).Select(i => i.Title).Should().Equal("pinned", "recent", "old", "current");

        await items.Single(i => i.Title == "old").Execute();
        await _actions.Received(1).OpenAsync(old.Id);
    }

    [Fact]
    public async Task Project_source_never_throws()
    {
        _harness.Registry.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Project>>(new IOException("database is locked")));
        var source = new ProjectsPaletteSource(_harness.Registry, _actions, NullLogger<ProjectsPaletteSource>.Instance);

        (await source.GetItemsAsync(Everything, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private ShellActionsPaletteSource ShellActions(IThemeSwitcher? theme = null) =>
        new(_actions, _navigation, _github, theme is null ? [] : [theme]);
}
