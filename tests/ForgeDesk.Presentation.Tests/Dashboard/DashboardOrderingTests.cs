using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using static ForgeDesk.Presentation.Tests.Dashboard.DashboardTestData;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class DashboardOrderingTests
{
    private readonly ProjectCardViewModelTests.RecordingHost _host = new();

    [Fact]
    public void Attention_order_puts_missing_folders_then_critical_then_warnings_first()
    {
        var fine = Card("fine", lastOpened: TestData.Now);
        var info = Card("info", attention: [Info("1 commit not pushed.")]);
        var warning = Card("warning", attention: [Warning("Behind")]);
        var critical = Card("critical", attention: [Critical("Conflicts")]);
        var missing = Card("missing", folderExists: false);
        var unknown = Card("unknown", snapshot: false);

        var ordered = DashboardOrdering.Order([fine, info, warning, critical, missing, unknown], DashboardSortMode.Attention, grouped: false);

        ordered.Select(c => c.Name).Should().Equal("missing", "critical", "warning", "info", "unknown", "fine");
    }

    [Fact]
    public void Recent_order_puts_opened_projects_first_newest_first()
    {
        var old = Card("old", lastOpened: TestData.Now.AddDays(-10));
        var today = Card("today", lastOpened: TestData.Now);
        var never = Card("never", added: TestData.Now);

        var ordered = DashboardOrdering.Order([old, never, today], DashboardSortMode.Recent, grouped: false);

        ordered.Select(c => c.Name).Should().Equal("today", "old", "never");
    }

    [Fact]
    public void Name_order_is_case_insensitive()
    {
        var ordered = DashboardOrdering.Order([Card("beta"), Card("Alpha"), Card("gamma")], DashboardSortMode.Name, grouped: false);

        ordered.Select(c => c.Name).Should().Equal("Alpha", "beta", "gamma");
    }

    [Fact]
    public void Grouped_order_lists_named_groups_alphabetically_then_ungrouped_projects()
    {
        var cards = new[]
        {
            Card("zeta"),
            Card("api", group: "Work"),
            Card("blog", group: "Side"),
            Card("admin", group: "Work"),
            Card("alpha"),
        };

        var ordered = DashboardOrdering.Order(cards, DashboardSortMode.Name, grouped: true);

        ordered.Select(c => c.Name).Should().Equal("blog", "admin", "api", "alpha", "zeta");
        ordered.Select(c => c.GroupHeader).Should().Equal("Side", "Work", "Work", ProjectCardViewModel.UngroupedHeader, ProjectCardViewModel.UngroupedHeader);
    }

    [Theory]
    [InlineData(DashboardSortMode.Name, true)]
    [InlineData(DashboardSortMode.Recent, true)]
    [InlineData(DashboardSortMode.Attention, false)]
    public void Groups_show_when_sorting_by_name_or_recency(DashboardSortMode mode, bool expected)
    {
        DashboardOrdering.ShouldGroup(mode, [Card("a", group: "Work"), Card("b")]).Should().Be(expected);
        DashboardOrdering.ShouldGroup(mode, [Card("a"), Card("b")]).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("forge", new[] { "forge" })]
    [InlineData("  forge   main ", new[] { "forge", "main" })]
    public void Search_text_splits_into_terms(string? text, string[] expected)
    {
        DashboardOrdering.SearchTerms(text).Should().Equal(expected);
    }

    [Theory]
    [InlineData("attention", DashboardSortMode.Attention)]
    [InlineData("recent", DashboardSortMode.Recent)]
    [InlineData("NAME", DashboardSortMode.Name)]
    [InlineData("garbage", DashboardSortMode.Attention)]
    [InlineData(null, DashboardSortMode.Attention)]
    public void Sort_preference_round_trips(string? stored, DashboardSortMode expected)
    {
        DashboardPreferences.ParseSort(stored).Should().Be(expected);
        DashboardPreferences.ParseSort(DashboardPreferences.ToSetting(expected)).Should().Be(expected);
    }

    [Theory]
    [InlineData("grid", DashboardLayout.Grid)]
    [InlineData("list", DashboardLayout.List)]
    [InlineData("LIST", DashboardLayout.List)]
    [InlineData(null, DashboardLayout.Grid)]
    public void Layout_preference_round_trips(string? stored, DashboardLayout expected)
    {
        DashboardPreferences.ParseLayout(stored).Should().Be(expected);
        DashboardPreferences.ParseLayout(DashboardPreferences.ToSetting(expected)).Should().Be(expected);
    }

    private ProjectCardViewModel Card(string name, string? group = null, DateTimeOffset? lastOpened = null, DateTimeOffset? added = null,
        bool folderExists = true, bool snapshot = true, AttentionReason[]? attention = null)
    {
        var project = Project(name, group: group, lastOpened: lastOpened, added: added);
        var card = new ProjectCardViewModel(project, _host);
        if (snapshot)
        {
            card.Apply(Snapshot(project, folderExists: folderExists, attention: attention ?? []));
        }

        return card;
    }
}
