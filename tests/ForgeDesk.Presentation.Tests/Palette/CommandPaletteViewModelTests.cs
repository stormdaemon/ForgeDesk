using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ForgeDesk.Presentation.Tests.Palette.PaletteRankingTests;

namespace ForgeDesk.Presentation.Tests.Palette;

public sealed class CommandPaletteViewModelTests : IDisposable
{
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly RecordingSource _actions = new(
        Item("Fetch", PaletteCategory.Action),
        Item("Pull", PaletteCategory.Action),
        Item("Push", PaletteCategory.Action),
        Item("Add local project…", PaletteCategory.Action));

    private readonly RecordingSource _projects = new(
        Item("forge-app", PaletteCategory.Project, subtitle: @"C:\dev\forge-app"),
        Item("website", PaletteCategory.Project, subtitle: @"C:\dev\website"));

    private CommandPaletteViewModel? _palette;

    public void Dispose() => _palette?.Dispose();

    [Fact]
    public async Task Opening_lists_everything_grouped_by_category()
    {
        var palette = Create();

        palette.Open();
        await palette.PendingSearch;

        palette.IsOpen.Should().BeTrue();
        Headers(palette).Should().Equal("Projects", "Actions");
        Titles(palette).Should().Equal("forge-app", "website", "Add local project…", "Fetch", "Pull", "Push");
        palette.SelectedResult!.Title.Should().Be("forge-app");
        palette.ShowPrefixHints.Should().BeTrue();
    }

    [Fact]
    public async Task Recently_executed_items_come_first_in_a_recent_group()
    {
        var palette = Create();
        palette.Open();
        await palette.PendingSearch;
        await palette.ExecuteCommand.ExecuteAsync(Result(palette, "Push"));
        palette.Open();
        await palette.PendingSearch;
        await palette.ExecuteCommand.ExecuteAsync(Result(palette, "website"));

        palette.Open();
        await palette.PendingSearch;

        Headers(palette).Should().Equal("Recent", "Projects", "Actions");
        Titles(palette).Take(2).Should().Equal("website", "Push");
        Titles(palette).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Typing_ranks_matches_and_groups_them_by_best_score()
    {
        var palette = Create();
        palette.Open();
        await palette.PendingSearch;

        palette.QueryText = "pu";
        await palette.PendingSearch;

        Headers(palette).Should().Equal("Actions");
        Titles(palette).Should().Equal("Pull", "Push");
        palette.ShowPrefixHints.Should().BeFalse();
    }

    [Fact]
    public async Task Prefix_mode_limits_results_and_tells_sources_what_is_wanted()
    {
        var palette = Create();
        palette.Open("@");
        await palette.PendingSearch;

        palette.Mode.Should().Be(PaletteMode.Projects);
        palette.Placeholder.Should().Be("Open a project…");
        Titles(palette).Should().Equal("forge-app", "website");
        _projects.Queries.Last().Categories.Should().BeEquivalentTo([PaletteCategory.Project]);
        _projects.Queries.Last().Text.Should().BeEmpty();
    }

    [Fact]
    public async Task Prefix_chip_keeps_the_typed_term()
    {
        var palette = Create();
        palette.Open("forge");
        await palette.PendingSearch;

        palette.ApplyPrefixCommand.Execute(">");
        await palette.PendingSearch;

        palette.QueryText.Should().Be(">forge");
        palette.Mode.Should().Be(PaletteMode.Actions);
    }

    [Fact]
    public async Task Sources_receive_the_current_project()
    {
        _navigation.CurrentProjectId.Returns("p-42");
        var palette = Create();

        palette.Open();
        await palette.PendingSearch;

        _actions.Queries.Should().ContainSingle().Which.CurrentProjectId.Should().Be("p-42");
    }

    [Fact]
    public async Task Fast_typing_queries_the_sources_once_with_the_last_text()
    {
        var palette = Create(TimeSpan.FromMilliseconds(150));
        palette.Open();
        await palette.PendingSearch;
        _actions.Queries.Clear();

        palette.QueryText = "p";
        palette.QueryText = "pu";
        palette.QueryText = "pus";
        await palette.PendingSearch;

        _actions.Queries.Select(q => q.Text).Should().Equal("pus");
        Titles(palette).Should().Equal("Push");
    }

    [Fact]
    public async Task Results_of_a_superseded_query_are_discarded()
    {
        var slow = new RecordingSource(Item("Stale result", PaletteCategory.Action)) { Delay = TimeSpan.FromMilliseconds(300) };
        var palette = Create(TimeSpan.Zero, slow);
        palette.Open();
        var stale = palette.PendingSearch;

        slow.Delay = TimeSpan.Zero;
        slow.Items = [Item("Fresh result", PaletteCategory.Action)];
        palette.QueryText = "res";
        await palette.PendingSearch;
        await stale;

        Titles(palette).Should().Equal("Fresh result");
    }

    [Fact]
    public async Task Keyboard_navigation_skips_headers_and_wraps()
    {
        var palette = Create();
        palette.Open();
        await palette.PendingSearch;

        palette.MovePreviousCommand.Execute(null);
        palette.SelectedResult!.Title.Should().Be("Push");

        palette.MoveNextCommand.Execute(null);
        palette.SelectedResult!.Title.Should().Be("forge-app");

        palette.MoveNextCommand.Execute(null);
        palette.MoveNextCommand.Execute(null);
        palette.SelectedResult!.Title.Should().Be("Add local project…", "the Actions header is skipped");
        palette.SelectedResult.IsSelected.Should().BeTrue();
        Result(palette, "forge-app").IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task Enter_runs_the_selection_and_closes_the_palette()
    {
        var ran = 0;
        _actions.Items = [Item("Fetch", PaletteCategory.Action, execute: () =>
        {
            ran++;
            return Task.CompletedTask;
        })];
        var palette = Create();
        palette.Open(">");
        await palette.PendingSearch;

        await palette.ExecuteSelectedCommand.ExecuteAsync(null);

        ran.Should().Be(1);
        palette.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Escape_closes_without_running_anything()
    {
        var palette = Create();
        palette.Open();
        await palette.PendingSearch;

        palette.CloseCommand.Execute(null);

        palette.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_source_does_not_hide_the_others()
    {
        var broken = Substitute.For<IPaletteSource>();
        broken.GetItemsAsync(Arg.Any<PaletteQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PaletteItem>>(new InvalidOperationException("index corrupted")));
        var palette = Create(null, broken, _actions);

        palette.Open();
        await palette.PendingSearch;

        Titles(palette).Should().HaveCount(4);
    }

    [Fact]
    public async Task A_failing_entry_is_reported_as_an_error_notification()
    {
        _actions.Items = [Item("Fetch", PaletteCategory.Action, execute: () => throw new ForgeException(ErrorKind.NetworkUnavailable, "Offline."))];
        var palette = Create();
        palette.Open();
        await palette.PendingSearch;

        await palette.ExecuteCommand.ExecuteAsync(Result(palette, "Fetch"));

        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "Offline."), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task No_results_are_explained()
    {
        var palette = Create();
        palette.Open("zzzz");
        await palette.PendingSearch;

        palette.Rows.Should().BeEmpty();
        palette.ShowNoResults.Should().BeTrue();
        palette.SelectedResult.Should().BeNull();
        palette.ExecuteSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task At_most_sixty_results_are_shown()
    {
        var many = new RecordingSource(Enumerable.Range(0, 150).Select(i => Item($"File {i}", PaletteCategory.File)).ToArray());
        var palette = Create(null, many);

        palette.Open("file");
        await palette.PendingSearch;

        palette.Rows.OfType<PaletteResultViewModel>().Should().HaveCount(CommandPaletteViewModel.MaxResults);
    }

    private CommandPaletteViewModel Create(TimeSpan? debounce = null, params IPaletteSource[] sources)
    {
        _palette = new CommandPaletteViewModel(sources.Length == 0 ? [_projects, _actions] : sources, _navigation, _notifications,
            NullLogger<CommandPaletteViewModel>.Instance)
        {
            DebounceDelay = debounce ?? TimeSpan.FromMilliseconds(1),
        };
        return _palette;
    }

    private static IEnumerable<string> Headers(CommandPaletteViewModel palette) =>
        palette.Rows.OfType<PaletteGroupHeaderViewModel>().Select(h => h.Title);

    private static IEnumerable<string> Titles(CommandPaletteViewModel palette) =>
        palette.Rows.OfType<PaletteResultViewModel>().Select(r => r.Title);

    private static PaletteResultViewModel Result(CommandPaletteViewModel palette, string title) =>
        palette.Rows.OfType<PaletteResultViewModel>().Single(r => r.Title == title);

    private sealed class RecordingSource(params PaletteItem[] items) : IPaletteSource
    {
        public IReadOnlyList<PaletteItem> Items { get; set; } = items;

        public TimeSpan Delay { get; set; }

        public List<PaletteQuery> Queries { get; } = [];

        public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            var items = Items;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return items;
        }
    }
}
