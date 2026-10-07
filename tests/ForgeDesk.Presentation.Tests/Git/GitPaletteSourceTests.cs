using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class GitPaletteSourceTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Git);
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

    public GitPaletteSourceTests()
    {
        _harness.Git.GetBranchesAsync(Arg.Any<string>(), false, Arg.Any<CancellationToken>()).Returns(
        [
            TestData.LocalBranch("main", current: true, tip: TestData.Now),
            TestData.LocalBranch("old-work", tip: TestData.Now.AddDays(-30)),
            TestData.LocalBranch("feature/login", tip: TestData.Now.AddHours(-2)),
        ]);
    }

    public void Dispose() => _harness.Dispose();

    private GitPaletteSource Source => new(_navigation, _harness.Git, NullLogger<GitPaletteSource>.Instance);

    private static PaletteQuery Everything => new(string.Empty, null);

    [Fact]
    public async Task Nothing_is_offered_outside_a_repository()
    {
        _harness.CurrentStatus = null;
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_offered_on_other_pages()
    {
        _navigation.CurrentPage.Returns(new object());

        var items = await Source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Local_branches_can_be_switched_to_most_recent_first()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        var branches = items.Where(i => i.Category == PaletteCategory.Branch).ToList();
        branches.Select(i => i.Title).Should().Equal("Switch to branch feature/login", "Switch to branch old-work");
        await branches[0].Execute();
        await _harness.Git.Received(1).CheckoutAsync(_harness.Folder.Path, "feature/login", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefix_modes_skip_the_other_categories()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        var branchesOnly = new PaletteQuery("log", null) { Categories = new HashSet<PaletteCategory> { PaletteCategory.Branch } };
        var actionsOnly = new PaletteQuery("com", null) { Categories = new HashSet<PaletteCategory> { PaletteCategory.Action } };

        (await Source.GetItemsAsync(branchesOnly, TestContext.Current.CancellationToken)).Should().OnlyContain(i => i.Category == PaletteCategory.Branch);
        (await Source.GetItemsAsync(actionsOnly, TestContext.Current.CancellationToken)).Should().OnlyContain(i => i.Category == PaletteCategory.Action);
        await _harness.Git.Received(1).GetBranchesAsync(Arg.Any<string>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Commit_and_history_entries_open_the_git_tab_there()
    {
        _harness.CurrentStatus = TestData.Status(entries: [new GitStatusEntry { Path = "a.cs", IndexState = GitFileState.Modified }]);
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);
        var commit = items.Single(i => i.Title == "Commit staged changes");
        commit.Subtitle.Should().Be("forge-app · 1 staged file");
        await commit.Execute();
        await items.Single(i => i.Title == "Git history").Execute();
        await items.Single(i => i.Title == "Stash changes…").Execute();

        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Git);
        _harness.Sections.Single(WorkspaceSection.Git).NavigatedTo.Should().Equal(GitNavigation.Commit(), GitNavigation.History(), GitNavigation.StashChanges());
    }

    [Fact]
    public async Task Branch_listing_failures_are_swallowed()
    {
        _harness.Git.GetBranchesAsync(Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitBranch>>(new InvalidOperationException("boom")));
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source.GetItemsAsync(Everything, TestContext.Current.CancellationToken);

        items.Should().NotContain(i => i.Category == PaletteCategory.Branch);
        items.Should().Contain(i => i.Title == "Commit staged changes");
    }
}
