using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Tasks;

public sealed class TasksPaletteAndMathTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Tasks, WorkspaceSection.Git);
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

    public void Dispose() => _harness.Dispose();

    // ----- Drop neighbours ----------------------------------------------------------------

    [Theory]
    [InlineData(0, null, "a")]
    [InlineData(1, "a", "b")]
    [InlineData(3, "c", null)]
    [InlineData(99, "c", null)]
    [InlineData(-5, null, "a")]
    public void Dropping_into_another_column_takes_the_cards_around_the_index(int index, string? after, string? before)
    {
        var position = TaskBoardMath.Neighbours(["a", "b", "c"], "x", index);

        (position.AfterId, position.BeforeId).Should().Be((after, before));
        position.IsUnchanged.Should().BeFalse();
    }

    [Fact]
    public void Moving_down_within_a_column_accounts_for_the_card_leaving_its_place()
    {
        // a [b] c d — dropped between c and d (insertion index 3 as displayed).
        var position = TaskBoardMath.Neighbours(["a", "b", "c", "d"], "b", 3);

        (position.AfterId, position.BeforeId).Should().Be(("c", "d"));
        position.Index.Should().Be(2);
    }

    [Fact]
    public void Moving_up_within_a_column()
    {
        var position = TaskBoardMath.Neighbours(["a", "b", "c", "d"], "d", 1);

        (position.AfterId, position.BeforeId).Should().Be(("a", "b"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Dropping_a_card_on_itself_is_unchanged(int index)
    {
        TaskBoardMath.Neighbours(["a", "b", "c"], "b", index).IsUnchanged.Should().BeTrue();
    }

    [Fact]
    public void An_empty_column_has_no_neighbours()
    {
        var position = TaskBoardMath.Neighbours([], "x", 0);

        (position.AfterId, position.BeforeId).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void Due_dates_read_naturally()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 7)));

        TaskBoardMath.DueText(now.AddDays(1), now, false).Should().Be("Due tomorrow");
        TaskBoardMath.DueText(now.AddDays(-1), now, false).Should().Be("Overdue by 1 day");
        TaskBoardMath.DueText(now.AddDays(30), now, false).Should().Be("Due Nov 6");
        TaskBoardMath.IsOverdue(now.AddDays(-1), now, isDone: true).Should().BeFalse();
        TaskBoardMath.IsOverdue(null, now, isDone: false).Should().BeFalse();
    }

    // ----- Palette ------------------------------------------------------------------------

    private TasksPaletteSource Source() => new(_navigation, _harness.WorkItems, NullLogger<TasksPaletteSource>.Instance);

    private static PaletteQuery Mode(string text, PaletteCategory category) =>
        new(text, null) { Categories = new HashSet<PaletteCategory> { category } };

    [Fact]
    public async Task Hash_mode_searches_the_tasks_of_the_project_and_opens_them()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        var item = new WorkItem
        {
            Id = "w12", ProjectId = workspace.ProjectId, Number = 12, Title = "Fix login", Status = WorkItemStatus.InProgress, Labels = ["auth"],
        };
        _harness.WorkItems.SearchAsync(workspace.ProjectId, "login", TasksPaletteSource.MaxResults, Arg.Any<CancellationToken>()).Returns([item]);

        var items = await Source().GetItemsAsync(Mode("login", PaletteCategory.Task), TestContext.Current.CancellationToken);

        var task = items.Single();
        task.Title.Should().Be("#12 Fix login");
        task.Subtitle.Should().Be("In progress · forge-app · auth");
        task.Category.Should().Be(PaletteCategory.Task);

        await task.Execute();
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Tasks);
        _harness.Sections.Single(WorkspaceSection.Tasks).NavigatedTo.Should().Equal("w12");
    }

    [Fact]
    public async Task Action_mode_offers_new_task()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        var items = await Source().GetItemsAsync(Mode(string.Empty, PaletteCategory.Action), TestContext.Current.CancellationToken);

        var newTask = items.Single();
        newTask.Title.Should().Be("New task…");
        newTask.Shortcut.Should().Be("Ctrl+N");
        await newTask.Execute();
        _harness.Sections.Single(WorkspaceSection.Tasks).NavigatedTo.Should().Equal(TaskNavigation.CreateTask);
        await _harness.WorkItems.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Short_searches_in_all_mode_do_not_query_tasks()
    {
        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);

        await Source().GetItemsAsync(new PaletteQuery("l", null), TestContext.Current.CancellationToken);

        await _harness.WorkItems.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task No_project_no_items_and_search_failures_are_swallowed()
    {
        (await Source().GetItemsAsync(Mode("x", PaletteCategory.Task), TestContext.Current.CancellationToken)).Should().BeEmpty();

        var workspace = await _harness.OpenWorkspaceAsync();
        _navigation.CurrentPage.Returns(workspace);
        _harness.WorkItems.SearchAsync(default!, default!, default, default).ReturnsForAnyArgs<IReadOnlyList<WorkItem>>(_ => throw new IOException("locked"));

        (await Source().GetItemsAsync(Mode("x", PaletteCategory.Task), TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
