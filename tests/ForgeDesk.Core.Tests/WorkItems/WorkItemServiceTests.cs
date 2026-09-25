using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Tests.Infrastructure;
using ForgeDesk.Core.WorkItems;

namespace ForgeDesk.Core.Tests.WorkItems;

public sealed class WorkItemServiceTests : IAsyncLifetime
{
    private readonly AdjustableClock _clock = new();
    private TestDatabase _db = null!;
    private ActivityLog _activity = null!;
    private WorkItemService _service = null!;
    private string _projectId = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _activity = new ActivityLog(_db.Database, _clock);
        _service = new WorkItemService(_db.Database, _activity, _clock);
        _projectId = await _db.InsertProjectRowAsync();
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task Create_numbers_items_sequentially_per_project()
    {
        var other = await _db.InsertProjectRowAsync("other");

        var first = await CreateAsync("First");
        var second = await CreateAsync("Second");
        var otherFirst = await _service.CreateAsync(other, new WorkItemDraft("Elsewhere"), Ct);

        first.Number.Should().Be(1);
        first.Key.Should().Be("#1");
        second.Number.Should().Be(2);
        otherFirst.Number.Should().Be(1);
    }

    [Fact]
    public async Task Create_under_concurrency_never_reuses_a_number()
    {
        var tasks = Enumerable.Range(0, 25).Select(i => _service.CreateAsync(_projectId, new WorkItemDraft($"Task {i}"), Ct));

        var created = await Task.WhenAll(tasks);

        created.Select(w => w.Number).Should().BeEquivalentTo(Enumerable.Range(1, 25));
    }

    [Fact]
    public async Task Create_from_two_service_instances_on_the_same_database_never_reuses_a_number()
    {
        // Two instances do not share the in-process gate: only the IMMEDIATE transaction protects numbering.
        var secondService = new WorkItemService(_db.Database, _activity, _clock);

        var tasks = Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? _service : secondService).CreateAsync(_projectId, new WorkItemDraft($"Task {i}"), Ct));
        var created = await Task.WhenAll(tasks);

        created.Select(w => w.Number).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Enumerable.Range(1, 20));
    }

    [Fact]
    public async Task Create_trims_the_title_and_fills_timestamps()
    {
        var item = await _service.CreateAsync(_projectId, new WorkItemDraft("  Fix   the login\tpage  ", "  Details  ", WorkItemStatus.InProgress, WorkItemPriority.High), Ct);

        item.Title.Should().Be("Fix the login page");
        item.Description.Should().Be("Details");
        item.Status.Should().Be(WorkItemStatus.InProgress);
        item.Priority.Should().Be(WorkItemPriority.High);
        item.CreatedAt.Should().Be(_clock.Now);
        item.UpdatedAt.Should().Be(_clock.Now);
        item.CompletedAt.Should().BeNull();
        ShouldEqual(await _service.GetAsync(item.Id, Ct), item);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_requires_a_title(string title)
    {
        var act = () => _service.CreateAsync(_projectId, new WorkItemDraft(title), Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Create_limits_the_title_to_200_characters()
    {
        (await CreateAsync(new string('a', 200))).Title.Should().HaveLength(200);

        var act = () => CreateAsync(new string('a', 201));

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Hint.Should().Contain("200");
    }

    [Fact]
    public async Task Create_normalizes_labels()
    {
        var item = await _service.CreateAsync(_projectId, new WorkItemDraft("Labelled", Labels: [" bug ", "UI", "bug", "", "ui", "docs, backend "]), Ct);

        item.Labels.Should().Equal("bug", "UI", "docs", "backend");
        (await _service.GetAsync(item.Id, Ct))!.Labels.Should().Equal("bug", "UI", "docs", "backend");
        var stored = await _db.Database.UseAsync(c => c.ExecuteScalarAsync<string>("SELECT labels FROM work_items WHERE id = @Id", new { item.Id }), Ct);
        stored.Should().Be("bug,UI,docs,backend");
    }

    [Fact]
    public async Task Create_rejects_too_many_or_too_long_labels()
    {
        var tooMany = () => _service.CreateAsync(_projectId, new WorkItemDraft("x", Labels: Enumerable.Range(0, 21).Select(i => $"l{i}").ToList()), Ct);
        var tooLong = () => _service.CreateAsync(_projectId, new WorkItemDraft("x", Labels: [new string('l', 41)]), Ct);

        (await tooMany.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        (await tooLong.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Create_records_a_created_event_and_activity()
    {
        var item = await CreateAsync("Write docs");

        var history = await _service.GetHistoryAsync(item.Id, Ct);
        history.Should().ContainSingle().Which.Should().Match<WorkItemEvent>(e =>
            e.Kind == WorkItemEventKind.Created && e.Summary == "Created in To do" && e.At == _clock.Now);

        var activity = await _activity.QueryAsync(new ActivityQuery { ProjectId = _projectId }, Ct);
        activity.Should().ContainSingle().Which.Should().Match<ActivityEntry>(e =>
            e.Kind == ActivityKind.WorkItemCreated && e.Title == "Created task #1: Write docs" && e.RefKind == "work-item" && e.RefValue == item.Id);
    }

    [Fact]
    public async Task Create_directly_in_done_sets_the_completion_date()
    {
        var item = await _service.CreateAsync(_projectId, new WorkItemDraft("Already done", Status: WorkItemStatus.Done), Ct);

        item.CompletedAt.Should().Be(_clock.Now);
        item.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Create_for_an_unknown_project_is_reported_as_not_found()
    {
        var act = () => _service.CreateAsync("no-such-project", new WorkItemDraft("x"), Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Create_puts_items_at_the_end_of_their_column()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var backlog = await _service.CreateAsync(_projectId, new WorkItemDraft("c", Status: WorkItemStatus.Backlog), Ct);
        var d = await CreateAsync("d");

        b.SortOrder.Should().BeGreaterThan(a.SortOrder);
        d.SortOrder.Should().BeGreaterThan(b.SortOrder);
        backlog.SortOrder.Should().Be(SortOrderMath.Step);
        await ExpectColumnAsync(WorkItemStatus.Todo, "a", "b", "d");
    }

    [Fact]
    public async Task Create_raises_Changed_with_project_and_item()
    {
        var raised = new List<WorkItemsChangedEventArgs>();
        _service.Changed += (_, e) => raised.Add(e);

        var item = await CreateAsync("x");

        raised.Should().ContainSingle().Which.Should().Be(new WorkItemsChangedEventArgs(_projectId, item.Id));
    }

    // --- Update -------------------------------------------------------------------------------

    [Fact]
    public async Task Update_records_one_readable_event_per_changed_field()
    {
        var item = await _service.CreateAsync(_projectId, new WorkItemDraft("Old title", "old", Labels: ["bug", "ui"]), Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var updated = await _service.UpdateAsync(item.Id, new WorkItemPatch
        {
            Title = "New title",
            Description = "new",
            Status = WorkItemStatus.InProgress,
            Priority = WorkItemPriority.Urgent,
            Labels = ["bug", "docs"],
        }, Ct);

        updated.Title.Should().Be("New title");
        updated.UpdatedAt.Should().Be(_clock.Now);
        updated.CreatedAt.Should().Be(item.CreatedAt);
        var history = (await _service.GetHistoryAsync(item.Id, Ct)).Skip(1).ToList();
        history.Select(e => (e.Kind, e.Summary)).Should().Equal(
            (WorkItemEventKind.TitleChanged, "Title: “Old title” → “New title”"),
            (WorkItemEventKind.DescriptionChanged, "Description edited"),
            (WorkItemEventKind.StatusChanged, "Status: To do → In progress"),
            (WorkItemEventKind.PriorityChanged, "Priority: None → Urgent"),
            (WorkItemEventKind.LabelsChanged, "Labels: added docs; removed ui"));
        history[2].OldValue.Should().Be("Todo");
        history[2].NewValue.Should().Be("InProgress");
        history[4].OldValue.Should().Be("bug,ui");
        history[4].NewValue.Should().Be("bug,docs");
        ShouldEqual(await _service.GetAsync(item.Id, Ct), updated);
    }

    [Fact]
    public async Task Update_without_actual_changes_writes_nothing()
    {
        var item = await _service.CreateAsync(_projectId, new WorkItemDraft("Same", "desc", Labels: ["a", "b"]), Ct);
        var raised = 0;
        _service.Changed += (_, _) => raised++;
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = await _service.UpdateAsync(item.Id, new WorkItemPatch { Title = " Same ", Description = "desc", Status = WorkItemStatus.Todo, Labels = ["b", "a"] }, Ct);

        ShouldEqual(result, item);
        raised.Should().Be(0);
        (await _service.GetHistoryAsync(item.Id, Ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_describes_added_and_removed_descriptions()
    {
        var item = await CreateAsync("x");

        await _service.UpdateAsync(item.Id, new WorkItemPatch { Description = "Some text" }, Ct);
        await _service.UpdateAsync(item.Id, new WorkItemPatch { Description = "  " }, Ct);

        (await _service.GetHistoryAsync(item.Id, Ct)).Select(e => e.Summary).Should().Equal("Created in To do", "Description added", "Description removed");
    }

    [Fact]
    public async Task Moving_to_done_sets_completion_and_reopening_clears_it()
    {
        var item = await CreateAsync("Ship it");
        _clock.Advance(TimeSpan.FromHours(1));

        var done = await _service.UpdateAsync(item.Id, new WorkItemPatch { Status = WorkItemStatus.Done }, Ct);
        done.CompletedAt.Should().Be(_clock.Now);
        done.IsOpen.Should().BeFalse();

        _clock.Advance(TimeSpan.FromHours(1));
        var reopened = await _service.UpdateAsync(item.Id, new WorkItemPatch { Status = WorkItemStatus.Todo }, Ct);
        reopened.CompletedAt.Should().BeNull();

        var history = await _service.GetHistoryAsync(item.Id, Ct);
        history.Select(e => (e.Kind, e.Summary)).Should().Equal(
            (WorkItemEventKind.Created, "Created in To do"),
            (WorkItemEventKind.StatusChanged, "Status: To do → Done"),
            (WorkItemEventKind.Reopened, "Reopened: Done → To do"));

        var activity = await _activity.QueryAsync(new ActivityQuery { ProjectId = _projectId }, Ct);
        activity.Select(e => (e.Kind, e.Title)).Should().Equal(
            (ActivityKind.WorkItemUpdated, "Reopened task #1: Ship it"),
            (ActivityKind.WorkItemCompleted, "Completed task #1: Ship it"),
            (ActivityKind.WorkItemCreated, "Created task #1: Ship it"));
        activity[1].Outcome.Should().Be(ActivityOutcome.Success);
    }

    [Fact]
    public async Task Only_status_changes_are_journaled_as_activity()
    {
        var item = await CreateAsync("x");

        await _service.UpdateAsync(item.Id, new WorkItemPatch { Title = "y", Priority = WorkItemPriority.Low }, Ct);
        await _service.UpdateAsync(item.Id, new WorkItemPatch { Status = WorkItemStatus.Review }, Ct);

        var activity = await _activity.QueryAsync(new ActivityQuery { ProjectId = _projectId }, Ct);
        activity.Select(e => e.Kind).Should().Equal(ActivityKind.WorkItemUpdated, ActivityKind.WorkItemCreated);
        activity[0].Title.Should().Be("Moved task #1 to Review: y");
        activity[0].Detail.Should().Be("Status: To do → Review");
    }

    [Fact]
    public async Task Update_status_moves_the_card_to_the_end_of_its_new_column()
    {
        var a = await CreateAsync("a");
        await _service.CreateAsync(_projectId, new WorkItemDraft("b", Status: WorkItemStatus.InProgress), Ct);

        await _service.UpdateAsync(a.Id, new WorkItemPatch { Status = WorkItemStatus.InProgress }, Ct);

        await ExpectColumnAsync(WorkItemStatus.InProgress, "b", "a");
    }

    [Fact]
    public async Task Update_due_date_changes_are_worded_and_can_be_cleared()
    {
        var item = await CreateAsync("x");
        var due = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var later = due.AddDays(7);

        await _service.UpdateAsync(item.Id, new WorkItemPatch { DueAt = due }, Ct);
        (await _service.GetAsync(item.Id, Ct))!.DueAt.Should().Be(due);
        await _service.UpdateAsync(item.Id, new WorkItemPatch { DueAt = later }, Ct);
        await _service.UpdateAsync(item.Id, new WorkItemPatch { DueAt = later }, Ct);
        var cleared = await _service.UpdateAsync(item.Id, new WorkItemPatch { ClearDueDate = true }, Ct);

        cleared.DueAt.Should().BeNull();
        var summaries = (await _service.GetHistoryAsync(item.Id, Ct)).Where(e => e.Kind == WorkItemEventKind.DueDateChanged).Select(e => e.Summary);
        summaries.Should().Equal(
            $"Due date: none → {WorkItemText.FormatDate(due)}",
            $"Due date: {WorkItemText.FormatDate(due)} → {WorkItemText.FormatDate(later)}",
            $"Due date: {WorkItemText.FormatDate(later)} → none");
    }

    [Fact]
    public async Task Update_validates_input_and_reports_missing_items()
    {
        var item = await CreateAsync("x");

        var emptyTitle = () => _service.UpdateAsync(item.Id, new WorkItemPatch { Title = " " }, Ct);
        var missing = () => _service.UpdateAsync("missing", new WorkItemPatch { Title = "y" }, Ct);

        (await emptyTitle.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        (await missing.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    // --- Move ---------------------------------------------------------------------------------

    [Fact]
    public async Task Move_places_an_item_between_its_new_neighbours()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var c = await CreateAsync("c");

        var moved = await _service.MoveAsync(c.Id, WorkItemStatus.Todo, a.Id, b.Id, Ct);

        moved.SortOrder.Should().BeGreaterThan(a.SortOrder).And.BeLessThan(b.SortOrder);
        await ExpectColumnAsync(WorkItemStatus.Todo, "a", "c", "b");
    }

    [Fact]
    public async Task Move_to_the_top_and_to_the_bottom_of_a_column()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var c = await CreateAsync("c");

        await _service.MoveAsync(c.Id, WorkItemStatus.Todo, null, a.Id, Ct);
        await ExpectColumnAsync(WorkItemStatus.Todo, "c", "a", "b");

        await _service.MoveAsync(c.Id, WorkItemStatus.Todo, b.Id, null, Ct);
        await ExpectColumnAsync(WorkItemStatus.Todo, "a", "b", "c");
    }

    [Fact]
    public async Task Move_to_another_column_changes_the_status_with_history_and_activity()
    {
        var a = await CreateAsync("a");
        var inProgress = await _service.CreateAsync(_projectId, new WorkItemDraft("wip", Status: WorkItemStatus.InProgress), Ct);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var moved = await _service.MoveAsync(a.Id, WorkItemStatus.InProgress, null, inProgress.Id, Ct);

        moved.Status.Should().Be(WorkItemStatus.InProgress);
        moved.UpdatedAt.Should().Be(_clock.Now);
        await ExpectColumnAsync(WorkItemStatus.InProgress, "a", "wip");
        await ExpectColumnAsync(WorkItemStatus.Todo);
        (await _service.GetHistoryAsync(a.Id, Ct))[^1].Summary.Should().Be("Status: To do → In progress");
        (await _activity.QueryAsync(new ActivityQuery { Kinds = [ActivityKind.WorkItemUpdated] }, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Move_to_done_completes_the_item()
    {
        var a = await CreateAsync("a");

        var done = await _service.MoveAsync(a.Id, WorkItemStatus.Done, null, null, Ct);

        done.CompletedAt.Should().Be(_clock.Now);
        (await _activity.QueryAsync(new ActivityQuery { Kinds = [ActivityKind.WorkItemCompleted] }, Ct)).Should().ContainSingle();
        (await _service.CountOpenAsync(_projectId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Reordering_within_a_column_does_not_touch_history_or_updated_at()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        _clock.Advance(TimeSpan.FromMinutes(3));

        var moved = await _service.MoveAsync(b.Id, WorkItemStatus.Todo, null, a.Id, Ct);

        moved.UpdatedAt.Should().Be(b.UpdatedAt);
        (await _service.GetHistoryAsync(b.Id, Ct)).Should().HaveCount(1);
        await ExpectColumnAsync(WorkItemStatus.Todo, "b", "a");
    }

    [Fact]
    public async Task Move_to_the_current_position_is_a_no_op()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var raised = 0;
        _service.Changed += (_, _) => raised++;

        var result = await _service.MoveAsync(b.Id, WorkItemStatus.Todo, a.Id, null, Ct);

        ShouldEqual(result, b);
        raised.Should().Be(0);
    }

    [Fact]
    public async Task Move_ignores_stale_neighbour_ids()
    {
        await CreateAsync("a");
        var b = await CreateAsync("b");
        var c = await CreateAsync("c");

        await _service.MoveAsync(b.Id, WorkItemStatus.Todo, "gone", "also-gone", Ct);
        await ExpectColumnAsync(WorkItemStatus.Todo, "a", "c", "b");

        await _service.MoveAsync(b.Id, WorkItemStatus.Todo, "gone", c.Id, Ct);
        await ExpectColumnAsync(WorkItemStatus.Todo, "a", "b", "c");
    }

    [Fact]
    public async Task Repeated_inserts_at_the_same_spot_renumber_the_column_and_keep_the_order()
    {
        var first = await CreateAsync("first");
        var last = await CreateAsync("last");
        var expected = new List<string> { "first" };

        // Each insert halves the gap after "first": it falls below the minimum gap after ~30 of them.
        for (var i = 0; i < 36; i++)
        {
            var item = await CreateAsync($"n{i}");
            await _service.MoveAsync(item.Id, WorkItemStatus.Todo, first.Id, null, Ct);
            expected.Insert(1, $"n{i}");
        }

        expected.Add("last");
        await ExpectColumnAsync(WorkItemStatus.Todo, [.. expected]);

        var orders = (await _service.GetAllAsync(_projectId, Ct)).Select(w => w.SortOrder).ToList();
        orders.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
        orders.Should().Contain(o => o % SortOrderMath.Step == 0 && o != first.SortOrder && o != last.SortOrder,
            "the column was renumbered to evenly spaced values at least once");
    }

    [Fact]
    public async Task Move_reports_missing_items()
    {
        var act = () => _service.MoveAsync("missing", WorkItemStatus.Done, null, null, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    // --- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_the_item_with_its_links_and_history()
    {
        var item = await CreateAsync("x");
        await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Branch, "feature/x", cancellationToken: Ct);
        var raised = new List<WorkItemsChangedEventArgs>();
        _service.Changed += (_, e) => raised.Add(e);

        await _service.DeleteAsync(item.Id, Ct);

        (await _service.GetAsync(item.Id, Ct)).Should().BeNull();
        (await _service.GetHistoryAsync(item.Id, Ct)).Should().BeEmpty();
        var links = await _db.Database.UseAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM work_item_links"), Ct);
        links.Should().Be(0);
        raised.Should().ContainSingle().Which.Should().Be(new WorkItemsChangedEventArgs(_projectId, item.Id));
    }

    [Fact]
    public async Task Deleting_a_missing_item_is_a_no_op()
    {
        var raised = 0;
        _service.Changed += (_, _) => raised++;

        await _service.DeleteAsync("missing", Ct);

        raised.Should().Be(0);
    }

    // --- Links --------------------------------------------------------------------------------

    [Fact]
    public async Task Links_are_added_deduplicated_and_removed_with_history()
    {
        var item = await CreateAsync("x");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var withBranch = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Branch, " feature/login ", cancellationToken: Ct);
        var withCommit = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Commit, "ABCDEF1234567", cancellationToken: Ct);
        var duplicate = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Commit, "abcdef1234567", cancellationToken: Ct);
        var withIssue = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Issue, "#42", "Login bug", Ct);
        var duplicateIssue = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Issue, "42", cancellationToken: Ct);

        withBranch.UpdatedAt.Should().Be(_clock.Now);
        withBranch.Links.Should().ContainSingle().Which.Value.Should().Be("feature/login");
        ShouldEqual(duplicate, withCommit);
        ShouldEqual(duplicateIssue, withIssue);
        withIssue.Links.Select(l => (l.Kind, l.Value, l.Label)).Should().Equal(
            (WorkItemLinkKind.Branch, "feature/login", null),
            (WorkItemLinkKind.Commit, "abcdef1234567", null),
            (WorkItemLinkKind.Issue, "42", "Login bug"));

        var withoutCommit = await _service.RemoveLinkAsync(item.Id, withIssue.Links[1].Id, Ct);
        withoutCommit.Links.Should().HaveCount(2);
        (await _service.GetAsync(item.Id, Ct))!.Links.Should().BeEquivalentTo(withoutCommit.Links);

        var history = await _service.GetHistoryAsync(item.Id, Ct);
        history.Select(e => (e.Kind, e.Summary)).Skip(1).Should().Equal(
            (WorkItemEventKind.LinkAdded, "Linked branch feature/login"),
            (WorkItemEventKind.LinkAdded, "Linked commit abcdef1"),
            (WorkItemEventKind.LinkAdded, "Linked issue #42"),
            (WorkItemEventKind.LinkRemoved, "Unlinked commit abcdef1"));
    }

    [Fact]
    public async Task File_links_are_compared_case_insensitively_with_either_slash()
    {
        var item = await CreateAsync("x");

        await _service.AddLinkAsync(item.Id, WorkItemLinkKind.File, @".\src\App.cs", cancellationToken: Ct);
        var again = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.File, "src/app.cs", cancellationToken: Ct);
        var otherKind = await _service.AddLinkAsync(item.Id, WorkItemLinkKind.Branch, "src/app.cs", cancellationToken: Ct);

        again.Links.Should().ContainSingle().Which.Value.Should().Be("src/App.cs");
        otherKind.Links.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(WorkItemLinkKind.Commit, "not-a-sha")]
    [InlineData(WorkItemLinkKind.Url, "ftp://example.com/file")]
    [InlineData(WorkItemLinkKind.Url, "example.com")]
    [InlineData(WorkItemLinkKind.Branch, "   ")]
    public async Task Invalid_links_are_rejected(WorkItemLinkKind kind, string value)
    {
        var item = await CreateAsync("x");

        var act = () => _service.AddLinkAsync(item.Id, kind, value, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Removing_an_unknown_link_is_a_no_op()
    {
        var item = await CreateAsync("x");

        var result = await _service.RemoveLinkAsync(item.Id, "missing", Ct);

        ShouldEqual(result, item);
        (await _service.GetHistoryAsync(item.Id, Ct)).Should().HaveCount(1);
    }

    // --- Queries ------------------------------------------------------------------------------

    [Fact]
    public async Task History_is_returned_oldest_first()
    {
        var item = await CreateAsync("x");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.UpdateAsync(item.Id, new WorkItemPatch { Priority = WorkItemPriority.Low }, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.UpdateAsync(item.Id, new WorkItemPatch { Priority = WorkItemPriority.High }, Ct);

        var history = await _service.GetHistoryAsync(item.Id, Ct);

        history.Select(e => e.Summary).Should().Equal("Created in To do", "Priority: None → Low", "Priority: Low → High");
        history.Select(e => e.At).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetAll_orders_by_column_then_position_and_includes_links()
    {
        var todo = await CreateAsync("todo");
        await _service.CreateAsync(_projectId, new WorkItemDraft("backlog", Status: WorkItemStatus.Backlog), Ct);
        await _service.CreateAsync(_projectId, new WorkItemDraft("done", Status: WorkItemStatus.Done), Ct);
        await _service.AddLinkAsync(todo.Id, WorkItemLinkKind.Url, "https://example.com/spec", cancellationToken: Ct);
        await _service.CreateAsync(await _db.InsertProjectRowAsync("other"), new WorkItemDraft("elsewhere"), Ct);

        var all = await _service.GetAllAsync(_projectId, Ct);

        all.Select(w => w.Title).Should().Equal("backlog", "todo", "done");
        all[1].Links.Should().ContainSingle().Which.Value.Should().Be("https://example.com/spec");
    }

    [Fact]
    public async Task CountOpen_ignores_done_items()
    {
        await CreateAsync("a");
        await _service.CreateAsync(_projectId, new WorkItemDraft("b", Status: WorkItemStatus.Review), Ct);
        await _service.CreateAsync(_projectId, new WorkItemDraft("c", Status: WorkItemStatus.Done), Ct);

        (await _service.CountOpenAsync(_projectId, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Search_matches_title_description_labels_and_key()
    {
        await CreateAsync("Fix login");
        await _service.CreateAsync(_projectId, new WorkItemDraft("Refactor", "The LOGIN flow is messy"), Ct);
        await _service.CreateAsync(_projectId, new WorkItemDraft("Styles", Labels: ["login-page"]), Ct);
        await CreateAsync("Unrelated");

        (await _service.SearchAsync(_projectId, "login", cancellationToken: Ct)).Select(w => w.Title)
            .Should().Equal("Fix login", "Styles", "Refactor");
        (await _service.SearchAsync(_projectId, "#4", cancellationToken: Ct)).Select(w => w.Title).Should().Equal("Unrelated");
        (await _service.SearchAsync(_projectId, "2", cancellationToken: Ct)).First().Title.Should().Be("Refactor");
    }

    [Fact]
    public async Task Search_ranks_open_items_before_done_ones()
    {
        await _service.CreateAsync(_projectId, new WorkItemDraft("Deploy v1", Status: WorkItemStatus.Done), Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await CreateAsync("Deploy v2");

        (await _service.SearchAsync(_projectId, "deploy", cancellationToken: Ct)).Select(w => w.Title).Should().Equal("Deploy v2", "Deploy v1");
    }

    [Fact]
    public async Task Search_treats_wildcards_literally()
    {
        await CreateAsync("Reach 100% coverage");
        await CreateAsync("Reach 1000 users");
        await CreateAsync("Rename my_var");
        await CreateAsync("Rename myXvar");

        (await _service.SearchAsync(_projectId, "100%", cancellationToken: Ct)).Select(w => w.Title).Should().Equal("Reach 100% coverage");
        (await _service.SearchAsync(_projectId, "my_var", cancellationToken: Ct)).Select(w => w.Title).Should().Equal("Rename my_var");
    }

    [Fact]
    public async Task Search_with_an_empty_query_returns_recent_open_items_within_the_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync($"t{i}");
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await _service.CreateAsync(_projectId, new WorkItemDraft("closed", Status: WorkItemStatus.Done), Ct);

        (await _service.SearchAsync(_projectId, "  ", 3, Ct)).Select(w => w.Title).Should().Equal("t4", "t3", "t2");
    }

    [Fact]
    public async Task Search_includes_links_of_the_results()
    {
        var item = await CreateAsync("linked");
        await _service.AddLinkAsync(item.Id, WorkItemLinkKind.PullRequest, "7", cancellationToken: Ct);

        var result = await _service.SearchAsync(_projectId, "linked", cancellationToken: Ct);

        result.Single().Links.Should().ContainSingle().Which.Kind.Should().Be(WorkItemLinkKind.PullRequest);
    }

    private Task<WorkItem> CreateAsync(string title) => _service.CreateAsync(_projectId, new WorkItemDraft(title), Ct);

    // WorkItem holds lists, which record equality compares by reference: compare member by member.
    private static void ShouldEqual(WorkItem? actual, WorkItem expected) =>
        actual.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<WorkItem>().WithStrictOrdering());

    private async Task ExpectColumnAsync(WorkItemStatus status, params string[] titles)
    {
        var all = await _service.GetAllAsync(_projectId, Ct);
        all.Where(w => w.Status == status).Select(w => w.Title).Should().Equal(titles);
    }
}
