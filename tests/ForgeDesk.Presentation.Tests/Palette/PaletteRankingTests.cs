using ForgeDesk.Presentation.Palette;

namespace ForgeDesk.Presentation.Tests.Palette;

public class PaletteRankingTests
{
    [Theory]
    [InlineData("", PaletteMode.All, "")]
    [InlineData("push", PaletteMode.All, "push")]
    [InlineData(">push", PaletteMode.Actions, "push")]
    [InlineData("> push ", PaletteMode.Actions, "push")]
    [InlineData("@forge", PaletteMode.Projects, "forge")]
    [InlineData("#12", PaletteMode.Tasks, "12")]
    [InlineData("/src/app", PaletteMode.Files, "src/app")]
    [InlineData("  @", PaletteMode.Projects, "")]
    public void Prefix_selects_the_mode_and_is_removed_from_the_term(string text, PaletteMode mode, string term)
    {
        var parsed = PaletteQueryParser.Parse(text);

        parsed.Mode.Should().Be(mode);
        parsed.Term.Should().Be(term);
    }

    [Fact]
    public void Modes_limit_the_categories()
    {
        PaletteQueryParser.Parse("text").Categories.Should().BeNull();
        PaletteQueryParser.Parse(">").Categories.Should().BeEquivalentTo(
            [PaletteCategory.Action, PaletteCategory.Navigation, PaletteCategory.Setting, PaletteCategory.Command]);
        PaletteQueryParser.Parse("@").Categories.Should().BeEquivalentTo([PaletteCategory.Project]);
        PaletteQueryParser.Parse("#").Categories.Should().BeEquivalentTo([PaletteCategory.Task]);
        PaletteQueryParser.Parse("/").Categories.Should().BeEquivalentTo([PaletteCategory.File]);
    }

    [Fact]
    public void Items_outside_the_mode_are_dropped()
    {
        var items = new[] { Item("forge-app", PaletteCategory.Project), Item("Fetch", PaletteCategory.Action) };

        var ranked = PaletteRanker.Rank(items, PaletteQueryParser.Parse("@"), new PaletteRecents(), 60);

        ranked.Select(r => r.Item.Title).Should().Equal("forge-app");
    }

    [Fact]
    public void Title_matches_rank_above_keyword_and_subtitle_matches()
    {
        var items = new[]
        {
            Item("Open in terminal", PaletteCategory.Action, keywords: "shell console"),
            Item("Shell integration", PaletteCategory.Setting),
            Item("forge-app", PaletteCategory.Project, subtitle: @"C:\dev\shell-tools"),
        };

        var ranked = PaletteRanker.Rank(items, PaletteQueryParser.Parse("shell"), new PaletteRecents(), 60);

        ranked.Select(r => r.Item.Title).Should().Equal("Shell integration", "Open in terminal", "forge-app");
        ranked[0].TitleIndices.Should().Equal(0, 1, 2, 3, 4);
        ranked[1].TitleIndices.Should().BeEmpty("the match was in the keywords");
    }

    [Fact]
    public void Every_word_of_the_query_must_match()
    {
        var items = new[] { Item("Open in Explorer", PaletteCategory.Action), Item("Open Settings", PaletteCategory.Navigation) };

        var ranked = PaletteRanker.Rank(items, PaletteQueryParser.Parse("open expl"), new PaletteRecents(), 60);

        ranked.Select(r => r.Item.Title).Should().Equal("Open in Explorer");
    }

    [Fact]
    public void Boost_orders_items_when_the_query_is_empty_and_breaks_ties()
    {
        var items = new[]
        {
            Item("alpha", PaletteCategory.Project, boost: 0),
            Item("beta", PaletteCategory.Project, boost: 3),
            Item("gamma", PaletteCategory.Project, boost: 1),
        };

        PaletteRanker.Rank(items, PaletteQueryParser.Parse(""), new PaletteRecents(), 60)
            .Select(r => r.Item.Title).Should().Equal("beta", "gamma", "alpha");
    }

    [Fact]
    public void Duplicates_from_several_sources_are_shown_once()
    {
        var items = new[] { Item("Fetch", PaletteCategory.Action), Item("Fetch", PaletteCategory.Action), Item("Fetch", PaletteCategory.Command) };

        PaletteRanker.Rank(items, PaletteQueryParser.Parse("fetch"), new PaletteRecents(), 60).Should().HaveCount(2);
    }

    [Fact]
    public void Results_are_capped()
    {
        var items = Enumerable.Range(0, 200).Select(i => Item($"Item {i}", PaletteCategory.File));

        PaletteRanker.Rank(items, PaletteQueryParser.Parse("item"), new PaletteRecents(), 60).Should().HaveCount(60);
    }

    [Fact]
    public void Recently_used_items_rank_higher()
    {
        var recents = new PaletteRecents();
        var pull = Item("Pull", PaletteCategory.Action);
        recents.Remember(pull);
        var items = new[] { Item("Push", PaletteCategory.Action), pull };

        var ranked = PaletteRanker.Rank(items, PaletteQueryParser.Parse("pu"), recents, 60);

        ranked[0].Item.Title.Should().Be("Pull");
        ranked[0].IsRecent.Should().BeTrue();
    }

    [Fact]
    public void Recents_keep_the_last_ten_by_title_and_category()
    {
        var recents = new PaletteRecents();
        for (var i = 0; i < 12; i++)
        {
            recents.Remember(Item($"Action {i}", PaletteCategory.Action));
        }

        recents.Remember(Item("Action 5", PaletteCategory.Action));

        recents.Count.Should().Be(PaletteRecents.Capacity);
        recents.RankOf(Item("Action 5", PaletteCategory.Action)).Should().Be(0);
        recents.RankOf(Item("Action 11", PaletteCategory.Action)).Should().Be(1);
        recents.RankOf(Item("Action 0", PaletteCategory.Action)).Should().Be(-1);
        recents.RankOf(Item("Action 5", PaletteCategory.Command)).Should().Be(-1);
    }

    [Fact]
    public void Title_is_split_into_highlighted_runs()
    {
        var segments = PaletteResultViewModel.Segment("Open in Explorer", [0, 1, 8]);

        segments.Should().Equal(
            new HighlightSegment("Op", true),
            new HighlightSegment("en in ", false),
            new HighlightSegment("E", true),
            new HighlightSegment("xplorer", false));
        PaletteResultViewModel.Segment("Fetch", []).Should().Equal(new HighlightSegment("Fetch", false));
    }

    internal static PaletteItem Item(string title, PaletteCategory category, string? subtitle = null, string? keywords = null, double boost = 0,
        Func<Task>? execute = null) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Category = category,
        Keywords = keywords,
        Boost = boost,
        Execute = execute ?? (() => Task.CompletedTask),
    };
}
