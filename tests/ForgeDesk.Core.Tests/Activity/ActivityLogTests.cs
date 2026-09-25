using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Activity;

public sealed class ActivityLogTests : IAsyncLifetime
{
    private readonly AdjustableClock _clock = new();
    private TestDatabase _db = null!;
    private ActivityLog _log = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _log = new ActivityLog(_db.Database, _clock);
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Record_assigns_an_id_and_fills_the_timestamp_from_the_clock()
    {
        var projectId = await _db.InsertProjectRowAsync();

        var saved = await _log.RecordAsync(new ActivityEntry { ProjectId = projectId, Kind = ActivityKind.GitCommit, Outcome = ActivityOutcome.Success, Title = "Committed 3 files" }, Ct);

        saved.Should().NotBeNull();
        saved!.Id.Should().BePositive();
        saved.At.Should().Be(_clock.Now);
        var stored = await _log.QueryAsync(new ActivityQuery(), Ct);
        stored.Should().ContainSingle().Which.Should().Be(saved);
    }

    [Fact]
    public async Task Record_keeps_an_explicit_timestamp()
    {
        var at = new DateTimeOffset(2025, 12, 24, 18, 0, 0, TimeSpan.FromHours(2));

        var saved = await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPush, Title = "Pushed", At = at }, Ct);

        saved!.At.Should().Be(at);
        (await _log.QueryAsync(new ActivityQuery(), Ct)).Single().At.Should().Be(at);
    }

    [Fact]
    public async Task Record_trims_and_clips_long_texts()
    {
        var saved = await _log.RecordAsync(new ActivityEntry
        {
            Kind = ActivityKind.RunCompleted,
            Title = "  " + new string('t', 1000) + "  ",
            Detail = new string('d', 20_000),
            RefKind = "  run ",
            RefValue = "   ",
        }, Ct);

        saved!.Title.Should().HaveLength(ActivityLog.MaxTitleLength).And.EndWith("…").And.StartWith("ttt");
        saved.Detail.Should().HaveLength(ActivityLog.MaxDetailLength).And.EndWith("…");
        saved.RefKind.Should().Be("run");
        saved.RefValue.Should().BeNull();
    }

    [Fact]
    public async Task Record_uses_the_kind_as_title_when_the_title_is_blank()
    {
        var saved = await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitFetch, Title = "   " }, Ct);

        saved!.Title.Should().Be("GitFetch");
    }

    [Fact]
    public async Task Record_raises_EntryAdded_with_the_saved_entry()
    {
        var raised = new List<ActivityEntry>();
        _log.EntryAdded += (_, e) => raised.Add(e);

        var saved = await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPull, Title = "Pulled" }, Ct);

        raised.Should().ContainSingle().Which.Should().Be(saved);
    }

    [Fact]
    public async Task Record_survives_a_faulty_subscriber()
    {
        _log.EntryAdded += (_, _) => throw new InvalidOperationException("subscriber bug");

        var saved = await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPull, Title = "Pulled" }, Ct);

        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task Record_never_throws_when_storage_rejects_the_entry()
    {
        var raised = 0;
        _log.EntryAdded += (_, _) => raised++;

        // The project was removed meanwhile: the foreign key rejects the row.
        var saved = await _log.RecordAsync(new ActivityEntry { ProjectId = "missing-project", Kind = ActivityKind.GitCommit, Title = "Committed" }, Ct);

        saved.Should().BeNull();
        raised.Should().Be(0);
        (await _log.QueryAsync(new ActivityQuery(), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Record_never_throws_when_cancelled_or_given_null()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitCommit, Title = "x" }, cts.Token)).Should().BeNull();
        (await _log.RecordAsync(null!, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Record_never_throws_when_the_database_is_unusable()
    {
        await _db.Database.UseAsync(c => c.ExecuteAsync("DROP TABLE activity"), Ct);

        var saved = await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitCommit, Title = "x" }, Ct);

        saved.Should().BeNull();
    }

    [Fact]
    public async Task Query_filters_by_project_kind_outcome_and_search()
    {
        var alpha = await _db.InsertProjectRowAsync("alpha");
        var beta = await _db.InsertProjectRowAsync("beta");
        await RecordAsync(alpha, ActivityKind.GitCommit, ActivityOutcome.Success, "Committed login form");
        await RecordAsync(alpha, ActivityKind.GitPush, ActivityOutcome.Failure, "Push rejected", "remote has new commits");
        await RecordAsync(alpha, ActivityKind.RunCompleted, ActivityOutcome.Success, "npm test passed");
        await RecordAsync(beta, ActivityKind.GitCommit, ActivityOutcome.Success, "Committed README");
        await RecordAsync(null, ActivityKind.Error, ActivityOutcome.Failure, "GitHub sign-in failed");

        (await _log.QueryAsync(new ActivityQuery { ProjectId = alpha }, Ct)).Should().HaveCount(3);
        (await _log.QueryAsync(new ActivityQuery { Kinds = [ActivityKind.GitCommit] }, Ct)).Select(e => e.Title)
            .Should().BeEquivalentTo("Committed login form", "Committed README");
        (await _log.QueryAsync(new ActivityQuery { Kinds = [ActivityKind.GitCommit, ActivityKind.GitPush], ProjectId = alpha }, Ct)).Should().HaveCount(2);
        (await _log.QueryAsync(new ActivityQuery { Outcome = ActivityOutcome.Failure }, Ct)).Select(e => e.Title)
            .Should().BeEquivalentTo("Push rejected", "GitHub sign-in failed");
        (await _log.QueryAsync(new ActivityQuery { Search = "readme" }, Ct)).Should().ContainSingle().Which.ProjectId.Should().Be(beta);
        (await _log.QueryAsync(new ActivityQuery { Search = "new commits" }, Ct)).Should().ContainSingle().Which.Title.Should().Be("Push rejected");
        (await _log.QueryAsync(new ActivityQuery { Kinds = [] }, Ct)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Query_search_matches_wildcard_characters_literally()
    {
        await RecordAsync(null, ActivityKind.RunCompleted, ActivityOutcome.Success, "Coverage 100% reached");
        await RecordAsync(null, ActivityKind.RunCompleted, ActivityOutcome.Success, "Coverage 1000 lines");
        await RecordAsync(null, ActivityKind.RunCompleted, ActivityOutcome.Success, "Ran my_script");
        await RecordAsync(null, ActivityKind.RunCompleted, ActivityOutcome.Success, "Ran myXscript");
        await RecordAsync(null, ActivityKind.RunCompleted, ActivityOutcome.Success, @"Built C:\src\app");

        (await _log.QueryAsync(new ActivityQuery { Search = "100%" }, Ct)).Should().ContainSingle().Which.Title.Should().Be("Coverage 100% reached");
        (await _log.QueryAsync(new ActivityQuery { Search = "my_script" }, Ct)).Should().ContainSingle().Which.Title.Should().Be("Ran my_script");
        (await _log.QueryAsync(new ActivityQuery { Search = @"C:\src" }, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Query_returns_newest_first_and_pages_with_keyset_without_gaps_or_duplicates()
    {
        // Several entries share a timestamp: paging on the timestamp alone would skip some of them.
        for (var i = 0; i < 9; i++)
        {
            await _log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitFetch, Title = $"Entry {i}" }, Ct);
            if (i % 3 == 2)
            {
                _clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var seen = new List<string>();
        DateTimeOffset? before = null;
        long? beforeId = null;
        while (true)
        {
            var page = await _log.QueryAsync(new ActivityQuery { Before = before, BeforeId = beforeId, Limit = 2 }, Ct);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(e => e.Title));
            before = page[^1].At;
            beforeId = page[^1].Id;
        }

        seen.Should().Equal(Enumerable.Range(0, 9).Reverse().Select(i => $"Entry {i}"));
    }

    [Fact]
    public async Task Query_before_without_id_returns_strictly_older_entries()
    {
        await RecordAsync(null, ActivityKind.GitFetch, ActivityOutcome.Info, "old");
        _clock.Advance(TimeSpan.FromHours(1));
        var cutoff = _clock.Now;
        await RecordAsync(null, ActivityKind.GitFetch, ActivityOutcome.Info, "at cutoff");
        _clock.Advance(TimeSpan.FromHours(1));
        await RecordAsync(null, ActivityKind.GitFetch, ActivityOutcome.Info, "new");

        var result = await _log.QueryAsync(new ActivityQuery { Before = cutoff }, Ct);

        result.Select(e => e.Title).Should().Equal("old");
    }

    [Fact]
    public async Task Query_clamps_the_limit()
    {
        for (var i = 0; i < 3; i++)
        {
            await RecordAsync(null, ActivityKind.GitFetch, ActivityOutcome.Info, $"e{i}");
        }

        (await _log.QueryAsync(new ActivityQuery { Limit = 0 }, Ct)).Should().HaveCount(1);
        (await _log.QueryAsync(new ActivityQuery { Limit = 2 }, Ct)).Should().HaveCount(2);
        (await _log.QueryAsync(new ActivityQuery { Limit = int.MaxValue }, Ct)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Clear_removes_one_project_or_everything()
    {
        var alpha = await _db.InsertProjectRowAsync("alpha");
        var beta = await _db.InsertProjectRowAsync("beta");
        await RecordAsync(alpha, ActivityKind.GitCommit, ActivityOutcome.Success, "a");
        await RecordAsync(beta, ActivityKind.GitCommit, ActivityOutcome.Success, "b");
        await RecordAsync(null, ActivityKind.Error, ActivityOutcome.Failure, "global");

        await _log.ClearAsync(alpha, Ct);
        (await _log.QueryAsync(new ActivityQuery(), Ct)).Select(e => e.Title).Should().BeEquivalentTo("b", "global");

        await _log.ClearAsync(null, Ct);
        (await _log.QueryAsync(new ActivityQuery(), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_keeps_the_newest_entries_of_each_project_and_of_global_entries()
    {
        var alpha = await _db.InsertProjectRowAsync("alpha");
        var beta = await _db.InsertProjectRowAsync("beta");
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync(alpha, ActivityKind.GitCommit, ActivityOutcome.Success, $"alpha {i}");
            await RecordAsync(null, ActivityKind.Error, ActivityOutcome.Failure, $"global {i}");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        await RecordAsync(beta, ActivityKind.GitCommit, ActivityOutcome.Success, "beta 0");

        await _log.PruneAsync(2, Ct);

        (await _log.QueryAsync(new ActivityQuery { ProjectId = alpha }, Ct)).Select(e => e.Title).Should().Equal("alpha 4", "alpha 3");
        (await _log.QueryAsync(new ActivityQuery { ProjectId = beta }, Ct)).Select(e => e.Title).Should().Equal("beta 0");
        (await _log.QueryAsync(new ActivityQuery { Kinds = [ActivityKind.Error] }, Ct)).Select(e => e.Title).Should().Equal("global 4", "global 3");
    }

    [Fact]
    public async Task Prune_rejects_a_negative_quota()
    {
        var act = () => _log.PruneAsync(-1, Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private Task RecordAsync(string? projectId, ActivityKind kind, ActivityOutcome outcome, string title, string? detail = null) =>
        _log.RecordAsync(new ActivityEntry { ProjectId = projectId, Kind = kind, Outcome = outcome, Title = title, Detail = detail }, Ct);
}
