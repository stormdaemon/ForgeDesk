using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Files;

public sealed class FilesPaletteSourceTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Overview, WorkspaceSection.Files);
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IFileIndex _index = Substitute.For<IFileIndex>();
    private readonly FileIndexSnapshot _snapshot;

    public FilesPaletteSourceTests()
    {
        _snapshot = new FileIndexSnapshot(_harness.Folder.Path, ["src/app/main.cs", "src/app/mainview.cs", "docs/main.md"], false, TestData.Now);
        _index.GetAsync(_harness.Folder.Path, false, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _index.Search(_snapshot, "main", Arg.Any<int>()).Returns(
        [
            new FileMatch("src/app/main.cs", 30, [0, 1, 2, 3]),
            new FileMatch("docs/main.md", 25, [0, 1, 2, 3]),
            new FileMatch("src/app/mainview.cs", 20, [0, 1, 2, 3]),
        ]);
    }

    public void Dispose() => _harness.Dispose();

    private FilesPaletteSource Source => new(_navigation, _index, NullLogger<FilesPaletteSource>.Instance);

    private static PaletteQuery FileMode(string text) => new(text, "p") { Categories = new HashSet<PaletteCategory> { PaletteCategory.File } };

    private async Task<ProjectWorkspaceViewModel> OnWorkspaceAsync()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        return workspace;
    }

    [Fact]
    public async Task Nothing_is_offered_without_an_open_project()
    {
        _navigation.CurrentPage.Returns(new object());

        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_offered_when_files_are_not_wanted()
    {
        await OnWorkspaceAsync();
        var query = new PaletteQuery("main", "p") { Categories = new HashSet<PaletteCategory> { PaletteCategory.Project } };

        var items = await Source.GetItemsAsync(query, TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
        await _index.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default);
    }

    [Fact]
    public async Task File_mode_keeps_the_index_ranking_through_boosts()
    {
        await OnWorkspaceAsync();

        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().Equal("main.cs", "main.md", "mainview.cs");
        items.Select(i => i.Boost).Should().BeInDescendingOrder();
        items[0].Subtitle.Should().Be("forge-app/src/app");
        items[0].Keywords.Should().Be("src/app/main.cs");
        items.Should().OnlyContain(i => i.Category == PaletteCategory.File);
        _index.Received().Search(_snapshot, "main", FilesPaletteSource.MaxFileModeResults);
    }

    [Fact]
    public async Task The_ranker_keeps_the_best_index_match_first()
    {
        await OnWorkspaceAsync();
        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        var ranked = PaletteRanker.Rank(items, PaletteQueryParser.Parse("/main"), new PaletteRecents(), 10);

        ranked[0].Item.Title.Should().Be("main.cs");
    }

    [Fact]
    public async Task Choosing_a_file_opens_it_in_the_files_tab()
    {
        var workspace = await OnWorkspaceAsync();
        var requests = new List<WorkspaceNavigationRequest>();
        workspace.Context.NavigationRequested += (_, request) => requests.Add(request);
        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        await items[0].Execute();

        requests.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Files, new FileLocation("src/app/main.cs")));
    }

    [Fact]
    public async Task An_empty_file_query_suggests_changed_files()
    {
        _harness.CurrentStatus = TestData.Status(entries:
        [
            TestData.Modified("src/app/main.cs"),
            new GitStatusEntry { Path = "gone.txt", WorkTreeState = GitFileState.Deleted },
        ]);
        await OnWorkspaceAsync();

        var items = await Source.GetItemsAsync(FileMode(string.Empty), TestContext.Current.CancellationToken);

        items.Should().ContainSingle();
        items[0].Title.Should().Be("main.cs");
        items[0].Subtitle.Should().EndWith("Modified");
    }

    [Fact]
    public async Task The_mixed_palette_gets_a_few_files_for_real_queries_only()
    {
        await OnWorkspaceAsync();
        var mixed = new PaletteQuery("m", "p");

        (await Source.GetItemsAsync(mixed, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await Source.GetItemsAsync(new PaletteQuery(string.Empty, "p"), TestContext.Current.CancellationToken)).Should().BeEmpty();

        var items = await Source.GetItemsAsync(new PaletteQuery("main", "p"), TestContext.Current.CancellationToken);
        items.Should().HaveCount(3);
        items.Should().OnlyContain(i => i.Boost == 0);
        _index.Received().Search(_snapshot, "main", FilesPaletteSource.MaxMixedResults);
    }

    [Fact]
    public async Task Index_failures_are_swallowed()
    {
        await OnWorkspaceAsync();
        _index.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileIndexSnapshot>(new IOException("disk gone")));

        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Files_tab_must_be_available()
    {
        using var harness = new WorkspaceHarness(WorkspaceSection.Overview);
        var workspace = await harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source.GetItemsAsync(FileMode("main"), TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }
}
