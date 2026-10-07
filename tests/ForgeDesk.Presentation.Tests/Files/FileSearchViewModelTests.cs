using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Files.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Files;

public sealed class FileSearchViewModelTests : IDisposable
{
    private readonly FilesHarness _harness = new();
    private readonly List<FileLocation> _opened = [];

    public void Dispose() => _harness.Dispose();

    private FileSearchViewModel Create() => new(_harness.Context, _harness.Search, ImmediateDispatcher.Instance, location =>
    {
        _opened.Add(location);
        return Task.CompletedTask;
    });

    private void Engine(Func<ContentSearchQuery, Action<ContentMatch>, CancellationToken, Task<ContentSearchSummary>> run) =>
        _harness.Search.SearchAsync(_harness.Root, Arg.Any<ContentSearchQuery>(), Arg.Any<Action<ContentMatch>>(), Arg.Any<CancellationToken>())
            .Returns(call => run(call.ArgAt<ContentSearchQuery>(1), call.ArgAt<Action<ContentMatch>>(2), call.ArgAt<CancellationToken>(3)));

    [Fact]
    public async Task Matches_stream_in_grouped_by_file_with_a_summary()
    {
        ContentSearchQuery? received = null;
        Engine((query, onMatch, _) =>
        {
            received = query;
            onMatch(new ContentMatch("src/a.cs", 3, 5, 4, "    var todo = 1;"));
            onMatch(new ContentMatch("src/b.cs", 1, 1, 4, "todo first"));
            onMatch(new ContentMatch("src/a.cs", 9, 1, 4, "todo again"));
            return Task.FromResult(new ContentSearchSummary(3, 2, false, TimeSpan.FromMilliseconds(120), "git grep"));
        });
        var search = Create();
        search.Query = "todo";
        search.MatchCase = true;
        search.WholeWord = true;
        search.IncludeGlob = " *.cs ";

        await search.SearchCommand.ExecuteAsync(null);

        received!.Pattern.Should().Be("todo");
        received.MatchCase.Should().BeTrue();
        received.WholeWord.Should().BeTrue();
        received.IsRegex.Should().BeFalse();
        received.PathFilter.Should().Be("*.cs");

        search.Rows.Select(r => r is SearchFileRow f ? $"{f.RelativePath} ({f.MatchCount})" : $"  {((SearchMatchRow)r).LineNumber}")
            .Should().Equal("src/a.cs (2)", "  3", "  9", "src/b.cs (1)", "  1");
        search.MatchCount.Should().Be(3);
        search.FileCount.Should().Be(2);
        search.Summary.Should().Be("3 matches in 2 files · git grep · 120 ms");
        search.IsSearching.Should().BeFalse();
        search.HasResults.Should().BeTrue();
        search.ShowNoResults.Should().BeFalse();
    }

    [Fact]
    public async Task Match_rows_split_the_line_around_the_match()
    {
        Engine((_, onMatch, _) =>
        {
            onMatch(new ContentMatch("a.cs", 3, 9, 4, "    var todo = 1;"));
            return Task.FromResult(new ContentSearchSummary(1, 1, false, TimeSpan.Zero, "managed"));
        });
        var search = Create();
        search.Query = "todo";

        await search.SearchCommand.ExecuteAsync(null);

        var row = search.Rows.OfType<SearchMatchRow>().Single();
        row.Before.Should().Be("var ");
        row.Match.Should().Be("todo");
        row.After.Should().Be(" = 1;");
        row.ToolTip.Should().Be("a.cs:3:9");
    }

    [Fact]
    public void Long_lines_are_shortened_around_the_match()
    {
        var line = new string('x', 200) + "NEEDLE" + new string('y', 400);

        var (before, match, after) = SearchMatchRow.Split(line, 201, 6);

        match.Should().Be("NEEDLE");
        before.Should().StartWith("…").And.HaveLength(SearchMatchRow.ContextBefore + 1);
        after.Should().EndWith("…").And.HaveLength(SearchMatchRow.MaxAfter + 1);
        SearchMatchRow.Split("abc", 10, 5).Should().Be(("abc", string.Empty, string.Empty));
    }

    [Fact]
    public async Task Truncated_results_say_so()
    {
        Engine((_, onMatch, _) =>
        {
            onMatch(new ContentMatch("a.cs", 1, 1, 1, "x"));
            return Task.FromResult(new ContentSearchSummary(1, 1, true, TimeSpan.FromSeconds(2), "git grep"));
        });
        var search = Create();
        search.Query = "x";

        await search.SearchCommand.ExecuteAsync(null);

        search.IsTruncated.Should().BeTrue();
        search.TruncatedMessage.Should().StartWith("Showing the first 1 match");
    }

    [Fact]
    public async Task No_results_are_explained()
    {
        Engine((_, _, _) => Task.FromResult(new ContentSearchSummary(0, 0, false, TimeSpan.FromMilliseconds(5), "git grep")));
        var search = Create();
        search.ShowIntro.Should().BeTrue();
        search.Query = "nothing";

        await search.SearchCommand.ExecuteAsync(null);

        search.ShowNoResults.Should().BeTrue();
        search.ShowIntro.Should().BeFalse();
        search.SearchedQuery.Should().Be("nothing");
    }

    [Fact]
    public async Task Cancel_stops_the_search_and_keeps_what_was_found()
    {
        var started = new TaskCompletionSource();
        Engine(async (_, onMatch, ct) =>
        {
            onMatch(new ContentMatch("a.cs", 1, 1, 1, "x"));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ContentSearchSummary(1, 1, false, TimeSpan.Zero, "git grep");
        });
        var search = Create();
        search.Query = "x";

        var running = search.SearchCommand.ExecuteAsync(null);
        await started.Task;
        search.IsSearching.Should().BeTrue();
        search.CancelCommand.CanExecute(null).Should().BeTrue();
        search.CancelCommand.Execute(null);
        await running;

        search.IsSearching.Should().BeFalse();
        search.MatchCount.Should().Be(1);
        search.Summary.Should().StartWith("1 match in 1 file · cancelled");
    }

    [Fact]
    public async Task A_new_search_replaces_the_running_one()
    {
        var calls = 0;
        Engine(async (query, onMatch, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            onMatch(new ContentMatch("b.cs", 2, 1, 1, query.Pattern));
            return new ContentSearchSummary(1, 1, false, TimeSpan.Zero, "git grep");
        });
        var search = Create();
        search.Query = "first";
        var first = search.SearchCommand.ExecuteAsync(null);

        search.Query = "second";
        await search.SearchCommand.ExecuteAsync(null);
        await first;

        search.SearchedQuery.Should().Be("second");
        search.Rows.OfType<SearchMatchRow>().Single().Match.Should().Be("s");
        search.Summary.Should().Contain("git grep");
    }

    [Fact]
    public async Task Invalid_patterns_show_an_error()
    {
        Engine((_, _, _) => Task.FromException<ContentSearchSummary>(new ForgeException(ErrorKind.InvalidInput, "The regular expression is not valid.")));
        var search = Create();
        search.Query = "(";
        search.IsRegex = true;

        await search.SearchCommand.ExecuteAsync(null);

        search.Error!.Title.Should().Be("Invalid search");
        search.ShowNoResults.Should().BeFalse();
    }

    [Fact]
    public async Task Results_open_the_file_at_the_line()
    {
        Engine((_, onMatch, _) =>
        {
            onMatch(new ContentMatch("src/a.cs", 42, 1, 1, "x"));
            return Task.FromResult(new ContentSearchSummary(1, 1, false, TimeSpan.Zero, "git grep"));
        });
        var search = Create();
        search.Query = "x";
        await search.SearchCommand.ExecuteAsync(null);

        await search.OpenResultCommand.ExecuteAsync(search.Rows[1]);
        await search.OpenResultCommand.ExecuteAsync(search.Rows[0]);

        _opened.Should().Equal(new FileLocation("src/a.cs", 42), new FileLocation("src/a.cs"));
    }

    [Fact]
    public void Blank_queries_cannot_be_searched_and_scope_sets_the_glob()
    {
        var search = Create();
        search.SearchCommand.CanExecute(null).Should().BeFalse();
        search.Query = "x";
        search.SearchCommand.CanExecute(null).Should().BeTrue();

        search.ScopeTo("src/app");
        search.IncludeGlob.Should().Be("src/app/**");
        search.ScopeTo(string.Empty);
        search.IncludeGlob.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_resets_everything()
    {
        Engine((_, onMatch, _) =>
        {
            onMatch(new ContentMatch("a.cs", 1, 1, 1, "x"));
            return Task.FromResult(new ContentSearchSummary(1, 1, false, TimeSpan.Zero, "git grep"));
        });
        var search = Create();
        search.Query = "x";
        await search.SearchCommand.ExecuteAsync(null);

        search.ClearCommand.Execute(null);

        search.Rows.Should().BeEmpty();
        search.Query.Should().BeEmpty();
        search.ShowIntro.Should().BeTrue();
        search.Summary.Should().BeNull();
    }
}
