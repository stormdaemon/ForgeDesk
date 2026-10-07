using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Infrastructure;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Activity;

public sealed class ActivityTimelineViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = FrozenTime.Now;

    private readonly ActivityHarness _h = new();
    private readonly List<ActivityTarget> _opened = [];
    private readonly List<ActivityTimelineViewModel> _timelines = [];

    public void Dispose()
    {
        foreach (var timeline in _timelines)
        {
            timeline.Dispose();
        }
    }

    private ActivityTimelineViewModel Create(string? projectId = "p1", bool global = false, int pageSize = 100)
    {
        var timeline = new ActivityTimelineViewModel(_h.Log, _h.Dialogs, _h.Notifications, _h.Shell, ImmediateDispatcher.Instance,
            ActivityHarness.Options(pageSize) with { ProjectId = projectId, IsGlobal = global },
            target =>
            {
                _opened.Add(target);
                return Task.CompletedTask;
            });
        _timelines.Add(timeline);
        return timeline;
    }

    // ----- Grouping -------------------------------------------------------------------

    [Fact]
    public async Task Entries_are_grouped_under_day_headers_newest_first()
    {
        _h.Entries.AddRange(
        [
            ActivityHarness.Entry(1, Now.AddHours(-1), "Today 1"),
            ActivityHarness.Entry(2, Now.AddHours(-2), "Today 2"),
            ActivityHarness.Entry(3, Now.AddDays(-1), "Yesterday"),
            ActivityHarness.Entry(4, Now.AddDays(-3), "Sunday"),
            ActivityHarness.Entry(5, new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero), "Summer"),
            ActivityHarness.Entry(6, new DateTimeOffset(2025, 12, 24, 9, 0, 0, TimeSpan.Zero), "Last year"),
        ]);
        var timeline = Create();

        await timeline.LoadAsync();

        var headers = timeline.Rows.OfType<ActivityDayHeaderViewModel>().ToList();
        headers.Select(h => h.Label).Should().Equal(
            "Today",
            "Yesterday",
            System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DayOfWeek.Sunday),
            new DateOnly(2026, 7, 14).ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture),
            new DateOnly(2025, 12, 24).ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        headers[0].Count.Should().Be(2);
        timeline.Rows.Select(r => r is ActivityItemViewModel item ? item.Title : "|").Should().Equal(
            "|", "Today 1", "Today 2", "|", "Yesterday", "|", "Sunday", "|", "Summer", "|", "Last year");
        timeline.ShowEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Each_entry_gets_an_icon_per_kind_and_a_tone_per_outcome()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Build failed", ActivityKind.RunCompleted, ActivityOutcome.Failure, detail: "exit code 1"));
        var timeline = Create();

        await timeline.LoadAsync();

        var item = timeline.Rows.OfType<ActivityItemViewModel>().Single();
        item.Group.Should().Be(ActivityGroup.Commands);
        item.Icon.Should().Be("Play20");
        item.Tone.Should().Be(StatusTone.Danger);
        item.IsFailure.Should().BeTrue();
        item.Detail.Should().Be("exit code 1");
        item.TimeText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_empty_journal_shows_the_empty_state()
    {
        var timeline = Create();

        await timeline.LoadAsync();

        timeline.ShowEmpty.Should().BeTrue();
        timeline.ShowNoMatches.Should().BeFalse();
        timeline.ClearHistoryCommand.CanExecute(null).Should().BeFalse();
        _h.Queries.Should().ContainSingle().Which.ProjectId.Should().Be("p1");
    }

    [Fact]
    public async Task A_failed_load_shows_the_error_and_retry_reloads()
    {
        _h.QueryFailure = new ForgeException(ErrorKind.StorageFailure, "The database is locked.");
        var timeline = Create();

        await timeline.LoadAsync();

        timeline.Error!.Message.Should().Be("The database is locked.");
        timeline.ShowEmpty.Should().BeFalse();

        _h.QueryFailure = null;
        await timeline.RefreshCommand.ExecuteAsync(null);

        timeline.HasError.Should().BeFalse();
        timeline.ShowEmpty.Should().BeTrue();
    }

    // ----- Filters -----------------------------------------------------------------------

    [Fact]
    public async Task Group_chips_outcome_and_search_filter_the_query()
    {
        _h.Entries.AddRange(
        [
            ActivityHarness.Entry(1, Now.AddMinutes(-1), "Pushed main", ActivityKind.GitPush),
            ActivityHarness.Entry(2, Now.AddMinutes(-2), "Push rejected", ActivityKind.GitPush, ActivityOutcome.Failure),
            ActivityHarness.Entry(3, Now.AddMinutes(-3), "npm test", ActivityKind.RunCompleted, ActivityOutcome.Failure),
        ]);
        var timeline = Create();
        await timeline.LoadAsync();

        timeline.SelectedFilter = timeline.Filters.Single(f => f.Group == ActivityGroup.Git);
        Titles(timeline).Should().Equal("Pushed main", "Push rejected");
        _h.Queries[^1].Kinds.Should().Contain(ActivityKind.GitPush).And.NotContain(ActivityKind.RunCompleted);

        timeline.FailuresOnly = true;
        Titles(timeline).Should().Equal("Push rejected");
        _h.Queries[^1].Outcome.Should().Be(ActivityOutcome.Failure);

        timeline.SelectedFilter = timeline.Filters[0];
        timeline.SearchText = "npm";
        Titles(timeline).Should().Equal("npm test");
        _h.Queries[^1].Should().Match<ActivityQuery>(q => q.Search == "npm" && q.Kinds == null && q.Outcome == ActivityOutcome.Failure);
        timeline.HasFilters.Should().BeTrue();

        timeline.SearchText = "nothing like this";
        timeline.ShowNoMatches.Should().BeTrue();

        timeline.ClearFiltersCommand.Execute(null);
        Titles(timeline).Should().HaveCount(3);
        timeline.HasFilters.Should().BeFalse();
    }

    [Fact]
    public void Kind_groups_cover_every_kind_once()
    {
        foreach (var kind in Enum.GetValues<ActivityKind>())
        {
            var group = ActivityGroups.Of(kind);
            if (group != ActivityGroup.Other)
            {
                ActivityGroups.KindsOf(group).Should().Contain(kind);
            }
        }

        ActivityGroups.KindsOf(ActivityGroup.All).Should().BeNull();
        ActivityGroups.Of(ActivityKind.ReleaseFailed).Should().Be(ActivityGroup.Releases);
        ActivityGroups.Of(ActivityKind.WorkflowRerun).Should().Be(ActivityGroup.GitHub);
        ActivityGroups.Of(ActivityKind.ProjectCloned).Should().Be(ActivityGroup.Projects);
    }

    // ----- Paging ----------------------------------------------------------------------

    [Fact]
    public async Task Load_more_continues_after_the_last_entry_with_a_keyset()
    {
        // Five entries share the same instant: the keyset must neither repeat nor skip them.
        var same = Now.AddHours(-1);
        _h.Entries.AddRange(Enumerable.Range(1, 5).Select(i => ActivityHarness.Entry(i, same, $"Same {i}")));
        _h.Entries.AddRange(Enumerable.Range(6, 3).Select(i => ActivityHarness.Entry(i, same.AddMinutes(-i), $"Older {i}")));
        var timeline = Create(pageSize: 3);

        await timeline.LoadAsync();
        timeline.HasMore.Should().BeTrue();
        Titles(timeline).Should().Equal("Same 5", "Same 4", "Same 3");

        await timeline.LoadMoreCommand.ExecuteAsync(null);
        _h.Queries[^1].Should().Match<ActivityQuery>(q => q.Before == same && q.BeforeId == 3 && q.Limit == 3);
        Titles(timeline).Should().Equal("Same 5", "Same 4", "Same 3", "Same 2", "Same 1", "Older 6");

        await timeline.LoadMoreCommand.ExecuteAsync(null);
        Titles(timeline).Should().HaveCount(8).And.OnlyHaveUniqueItems();
        timeline.HasMore.Should().BeFalse("the last page was not full");

        var queries = _h.Queries.Count;
        await timeline.LoadMoreCommand.ExecuteAsync(null);
        _h.Queries.Should().HaveCount(queries, "there is nothing more to load");
    }

    [Fact]
    public async Task Paging_keeps_the_filters()
    {
        _h.Entries.AddRange(Enumerable.Range(1, 6).Select(i => ActivityHarness.Entry(i, Now.AddMinutes(-i), $"Fail {i}", outcome: ActivityOutcome.Failure)));
        var timeline = Create(pageSize: 2);
        await timeline.LoadAsync();
        timeline.FailuresOnly = true;

        await timeline.LoadMoreCommand.ExecuteAsync(null);

        _h.Queries[^1].Outcome.Should().Be(ActivityOutcome.Failure);
        _h.Queries[^1].Before.Should().NotBeNull();
    }

    // ----- Live insertion --------------------------------------------------------------

    [Fact]
    public async Task New_entries_matching_the_filters_appear_live_at_their_place()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now.AddDays(-1), "Yesterday"));
        var timeline = Create();
        await timeline.LoadAsync();

        _h.Raise(ActivityHarness.Entry(2, Now, "Just committed"));
        _h.Raise(ActivityHarness.Entry(3, Now, "Other project", projectId: "p2"));
        _h.Raise(ActivityHarness.Entry(2, Now, "Just committed"));

        timeline.Rows.Select(r => r is ActivityItemViewModel item ? item.Title : ((ActivityDayHeaderViewModel)r).Label)
            .Should().Equal("Today", "Just committed", "Yesterday", "Yesterday");
    }

    [Fact]
    public async Task New_entries_outside_the_filters_are_not_inserted()
    {
        var timeline = Create();
        await timeline.LoadAsync();
        timeline.SelectedFilter = timeline.Filters.Single(f => f.Group == ActivityGroup.Tasks);

        _h.Raise(ActivityHarness.Entry(10, Now, "Pushed", ActivityKind.GitPush));
        _h.Raise(ActivityHarness.Entry(11, Now, "Created task #4", ActivityKind.WorkItemCreated));

        Titles(timeline).Should().Equal("Created task #4");
    }

    [Fact]
    public async Task Entries_older_than_the_loaded_pages_wait_for_paging()
    {
        _h.Entries.AddRange(Enumerable.Range(1, 2).Select(i => ActivityHarness.Entry(i, Now.AddMinutes(-i), $"E{i}")));
        var timeline = Create(pageSize: 2);
        await timeline.LoadAsync();

        _h.Raise(ActivityHarness.Entry(99, Now.AddDays(-30), "Imported"));

        Titles(timeline).Should().Equal("E1", "E2");
    }

    [Fact]
    public async Task The_global_timeline_receives_entries_of_every_project_with_their_names()
    {
        var timeline = Create(projectId: null, global: true);
        timeline.SetProjectLookup(id => id == "p2" ? new ActivityProjectInfo("Website", "#FF0000") : null);
        await timeline.LoadAsync();

        _h.Raise(ActivityHarness.Entry(5, Now, "Released v1.2", ActivityKind.ReleasePublished, projectId: "p2"));

        var item = timeline.Rows.OfType<ActivityItemViewModel>().Single();
        item.HasProject.Should().BeTrue();
        item.ProjectName.Should().Be("Website");
        item.ProjectColor.Should().Be("#FF0000");
    }

    // ----- References --------------------------------------------------------------------

    [Fact]
    public async Task Opening_an_entry_follows_its_reference()
    {
        _h.Entries.AddRange(
        [
            ActivityHarness.Entry(1, Now.AddMinutes(-1), "Commit", refKind: "commit", refValue: "abc1234def"),
            ActivityHarness.Entry(2, Now.AddMinutes(-2), "Run", ActivityKind.RunCompleted, refKind: "run", refValue: "run-7"),
            ActivityHarness.Entry(3, Now.AddMinutes(-3), "Task", ActivityKind.WorkItemCreated, refKind: "work-item", refValue: "wi-3"),
            ActivityHarness.Entry(4, Now.AddMinutes(-4), "Release", ActivityKind.ReleasePublished, refKind: "url", refValue: "https://github.com/acme/app/releases/v1"),
            ActivityHarness.Entry(5, Now.AddMinutes(-5), "Fetched", ActivityKind.GitFetch),
        ]);
        var timeline = Create();
        await timeline.LoadAsync();
        var items = timeline.Rows.OfType<ActivityItemViewModel>().ToList();

        foreach (var item in items)
        {
            await timeline.OpenEntryCommand.ExecuteAsync(item);
        }

        _opened.Select(t => (t.Section, t.Argument)).Should().Equal(
            (Presentation.Workspace.WorkspaceSection.Git, "abc1234def"),
            (Presentation.Workspace.WorkspaceSection.Commands, "run-7"),
            (Presentation.Workspace.WorkspaceSection.Tasks, "wi-3"));
        _h.Shell.Received(1).OpenUrl("https://github.com/acme/app/releases/v1");
        items[4].CanFollow.Should().BeFalse();
        items[0].ActionText.Should().Be("Show commit");
    }

    [Fact]
    public async Task Headers_and_entries_without_reference_open_nothing()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Fetched", ActivityKind.GitFetch));
        var timeline = Create();
        await timeline.LoadAsync();

        await timeline.OpenEntryCommand.ExecuteAsync(timeline.Rows[0]);
        await timeline.OpenEntryCommand.ExecuteAsync(timeline.Rows[1]);

        _opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_puts_the_entry_on_the_clipboard()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Push rejected", detail: "Your branch is behind."));
        var timeline = Create();
        await timeline.LoadAsync();

        timeline.CopyEntryCommand.Execute(timeline.Rows[1]);

        _h.Shell.Received(1).CopyToClipboard(Arg.Is<string>(s => s.Contains("Push rejected") && s.Contains("Your branch is behind.")));
    }

    // ----- Clear ---------------------------------------------------------------------------

    [Fact]
    public async Task Clear_history_confirms_then_clears_the_scope()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Committed"));
        _h.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(Task.FromResult(true));
        var timeline = Create();
        await timeline.LoadAsync();

        await timeline.ClearHistoryCommand.ExecuteAsync(null);

        await _h.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Clear history"));
        await _h.Log.Received(1).ClearAsync("p1", Arg.Any<CancellationToken>());
        timeline.Rows.Should().BeEmpty();
        timeline.ShowEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Declining_the_confirmation_keeps_the_history()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Committed"));
        _h.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(Task.FromResult(false));
        var timeline = Create();
        await timeline.LoadAsync();

        await timeline.ClearHistoryCommand.ExecuteAsync(null);

        await _h.Log.DidNotReceive().ClearAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        timeline.HasEntries.Should().BeTrue();
    }

    [Fact]
    public async Task The_global_timeline_clears_every_project()
    {
        _h.Entries.Add(ActivityHarness.Entry(1, Now, "Committed"));
        _h.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(Task.FromResult(true));
        var timeline = Create(projectId: null, global: true);
        await timeline.LoadAsync();

        await timeline.ClearHistoryCommand.ExecuteAsync(null);

        await _h.Log.Received(1).ClearAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disposing_stops_live_updates()
    {
        var timeline = Create();
        await timeline.LoadAsync();

        timeline.Dispose();
        _h.Raise(ActivityHarness.Entry(1, Now, "After close"));

        timeline.Rows.Should().BeEmpty();
    }

    private static IReadOnlyList<string> Titles(ActivityTimelineViewModel timeline) =>
        timeline.Rows.OfType<ActivityItemViewModel>().Select(i => i.Title).ToList();
}
