using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Tasks.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Tasks;

public sealed class TasksBoardTests : IDisposable
{
    private readonly TasksHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static IEnumerable<string> Titles(TaskColumnViewModel column) => column.Cards.Select(c => c.Title);

    [Fact]
    public async Task Cards_are_grouped_by_status_and_ordered_by_sort_order()
    {
        _h.Items.AddRange([
            _h.Item(1, "Write docs", WorkItemStatus.Backlog),
            _h.Item(2, "Fix login", WorkItemStatus.Todo, sortOrder: 3000),
            _h.Item(3, "Add search", WorkItemStatus.Todo, sortOrder: 1000),
            _h.Item(4, "Refactor", WorkItemStatus.InProgress),
            _h.Item(5, "Review API", WorkItemStatus.Review),
            _h.Item(6, "Ship", WorkItemStatus.Done, completed: DateTimeOffset.Now.AddDays(-1)),
        ]);

        var section = await _h.CreateAsync();

        section.Columns.Select(c => c.Title).Should().Equal("Backlog", "To do", "In progress", "Review", "Done");
        Titles(section.Columns[1]).Should().Equal("Add search", "Fix login");
        section.Columns.Select(c => c.Count).Should().Equal(1, 2, 1, 1, 1);
        section.TotalCount.Should().Be(6);
        section.OpenCount.Should().Be(5);
        section.TotalText.Should().Be("5 open tasks · 6 tasks in total");
        section.ShowEmptyBoard.Should().BeFalse();
        section.Columns[1].Cards[0].Key.Should().Be("#3");
        section.Columns[1].Cards[0].AutomationId.Should().Be("Tasks.Card.3");
    }

    [Fact]
    public async Task An_empty_board_invites_to_create_the_first_task()
    {
        var section = await _h.CreateAsync();

        section.ShowEmptyBoard.Should().BeTrue();
        section.ShowBoardSurface.Should().BeFalse();

        section.NewTaskCommand.Execute(null);

        section.ShowEmptyBoard.Should().BeFalse();
        section.ShowBoardSurface.Should().BeTrue();
        section.Columns[(int)WorkItemStatus.Todo].IsQuickAddOpen.Should().BeTrue();
        section.Columns[(int)WorkItemStatus.Todo].QuickAddFocusRequest.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_load_failure_shows_the_error_and_retry_recovers()
    {
        _h.WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<WorkItem>>(_ => throw new IOException("locked"));
        var section = await _h.CreateAsync();
        section.Error.Should().NotBeNull();
        section.ShowEmptyBoard.Should().BeFalse();

        _h.WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([_h.Item(1, "Back")]);
        await section.RetryCommand.ExecuteAsync(null);

        section.Error.Should().BeNull();
        section.TotalCount.Should().Be(1);
    }

    // ----- Quick add --------------------------------------------------------------------

    [Fact]
    public async Task Quick_add_creates_the_task_in_its_column_and_stays_open()
    {
        var section = await _h.CreateAsync();
        var column = section.Columns[(int)WorkItemStatus.InProgress];
        _h.WorkItems.CreateAsync(_h.ProjectId, Arg.Any<WorkItemDraft>(), Arg.Any<CancellationToken>())
            .Returns(call => _h.Replace(_h.Item(7, ((WorkItemDraft)call[1]).Title, ((WorkItemDraft)call[1]).Status)));
        column.OpenQuickAddCommand.Execute(null);
        var focusRequests = column.QuickAddFocusRequest;

        column.QuickAddText = "  Wire the API  ";
        await column.SubmitQuickAddCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).CreateAsync(_h.ProjectId, Arg.Is<WorkItemDraft>(d => d.Title == "Wire the API" && d.Status == WorkItemStatus.InProgress),
            Arg.Any<CancellationToken>());
        Titles(column).Should().Equal("Wire the API");
        column.IsQuickAddOpen.Should().BeTrue();
        column.QuickAddText.Should().BeEmpty();
        column.QuickAddFocusRequest.Should().BeGreaterThan(focusRequests);
    }

    [Fact]
    public async Task Quick_add_with_no_text_or_escape_closes_the_box()
    {
        var section = await _h.CreateAsync();
        var column = section.Columns[(int)WorkItemStatus.Todo];
        column.OpenQuickAddCommand.Execute(null);

        await column.SubmitQuickAddCommand.ExecuteAsync(null);
        column.IsQuickAddOpen.Should().BeFalse();

        column.OpenQuickAddCommand.Execute(null);
        column.QuickAddText = "draft";
        column.CancelQuickAddCommand.Execute(null);

        column.IsQuickAddOpen.Should().BeFalse();
        column.QuickAddText.Should().BeEmpty();
        await _h.WorkItems.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_failed_quick_add_keeps_the_text()
    {
        var section = await _h.CreateAsync();
        var column = section.Columns[(int)WorkItemStatus.Todo];
        _h.WorkItems.CreateAsync(_h.ProjectId, Arg.Any<WorkItemDraft>(), Arg.Any<CancellationToken>())
            .Returns<WorkItem>(_ => throw new Core.Common.ForgeException(Core.Common.ErrorKind.InvalidInput, "The title is too long."));
        column.OpenQuickAddCommand.Execute(null);
        column.QuickAddText = "x";

        await column.SubmitQuickAddCommand.ExecuteAsync(null);

        column.QuickAddText.Should().Be("x");
        _h.Workspace.Notifications.Received(1).ShowError(Arg.Is<Core.Common.ErrorInfo>(e => e.Message == "The title is too long."), null);
    }

    // ----- Moves ------------------------------------------------------------------------

    [Fact]
    public async Task Dropping_a_card_between_two_others_moves_it_with_those_neighbours()
    {
        _h.Items.AddRange([
            _h.Item(1, "A", WorkItemStatus.Todo, 1000),
            _h.Item(2, "B", WorkItemStatus.InProgress, 1000),
            _h.Item(3, "C", WorkItemStatus.InProgress, 2000),
        ]);
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Todo].Cards.Single();
        _h.WorkItems.MoveAsync("w1", WorkItemStatus.InProgress, "w2", "w3", Arg.Any<CancellationToken>())
            .Returns(_ => _h.Replace(_h.Item(1, "A", WorkItemStatus.InProgress, 1500)));

        await section.MoveCardCommand.ExecuteAsync(new TaskDropRequest(card, WorkItemStatus.InProgress, 1));

        await _h.WorkItems.Received(1).MoveAsync("w1", WorkItemStatus.InProgress, "w2", "w3", Arg.Any<CancellationToken>());
        Titles(section.Columns[(int)WorkItemStatus.InProgress]).Should().Equal("B", "A", "C");
        section.Columns[(int)WorkItemStatus.Todo].Cards.Should().BeEmpty();
        section.Columns[(int)WorkItemStatus.InProgress].Cards[1].Should().BeSameAs(card, "cards keep their view model across moves");
    }

    [Fact]
    public async Task Dropping_a_card_where_it_already_is_does_nothing()
    {
        _h.Items.AddRange([_h.Item(1, "A", WorkItemStatus.Todo, 1000), _h.Item(2, "B", WorkItemStatus.Todo, 2000)]);
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Todo].Cards[0];

        await section.MoveCardCommand.ExecuteAsync(new TaskDropRequest(card, WorkItemStatus.Todo, 1));

        await _h.WorkItems.DidNotReceiveWithAnyArgs().MoveAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task A_failed_move_puts_the_card_back()
    {
        _h.Items.AddRange([_h.Item(1, "A", WorkItemStatus.Todo), _h.Item(2, "B", WorkItemStatus.Review)]);
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Todo].Cards.Single();
        _h.WorkItems.MoveAsync(default!, default, default, default, default).ReturnsForAnyArgs<WorkItem>(_ => throw new IOException("disk full"));

        await section.MoveCardCommand.ExecuteAsync(new TaskDropRequest(card, WorkItemStatus.Review, 0));

        Titles(section.Columns[(int)WorkItemStatus.Todo]).Should().Equal("A");
        Titles(section.Columns[(int)WorkItemStatus.Review]).Should().Equal("B");
        _h.Workspace.Notifications.ReceivedWithAnyArgs(1).ShowError(default!, default);
    }

    [Fact]
    public async Task Move_to_menu_puts_the_card_on_top_of_the_column()
    {
        _h.Items.AddRange([_h.Item(1, "A", WorkItemStatus.Backlog), _h.Item(2, "B", WorkItemStatus.Review)]);
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Backlog].Cards.Single();
        _h.WorkItems.MoveAsync("w1", WorkItemStatus.Review, null, "w2", Arg.Any<CancellationToken>())
            .Returns(_ => _h.Replace(_h.Item(1, "A", WorkItemStatus.Review, 10)));

        await section.MoveToReviewCommand.ExecuteAsync(card);

        await _h.WorkItems.Received(1).MoveAsync("w1", WorkItemStatus.Review, null, "w2", Arg.Any<CancellationToken>());
    }

    // ----- Filters ----------------------------------------------------------------------

    [Fact]
    public async Task Search_matches_titles_descriptions_labels_and_keys()
    {
        _h.Items.AddRange([
            _h.Item(1, "Login page", labels: ["ui"]),
            _h.Item(2, "Token refresh", description: "The login token expires"),
            _h.Item(12, "Release notes", labels: ["docs"]),
        ]);
        var section = await _h.CreateAsync();
        var todo = section.Columns[(int)WorkItemStatus.Todo];

        section.SearchText = "login";
        Titles(todo).Should().Equal("Login page", "Token refresh");

        section.SearchText = "docs";
        Titles(todo).Should().Equal("Release notes");

        section.SearchText = "#12";
        Titles(todo).Should().Equal("Release notes");

        section.SearchText = "nothing";
        section.ShowNoMatches.Should().BeTrue();
        section.HasFilters.Should().BeTrue();

        section.ClearFiltersCommand.Execute(null);
        todo.Cards.Should().HaveCount(3);
    }

    [Fact]
    public async Task Priority_and_label_filters_combine()
    {
        _h.Items.AddRange([
            _h.Item(1, "A", priority: WorkItemPriority.Urgent, labels: ["api"]),
            _h.Item(2, "B", priority: WorkItemPriority.Urgent, labels: ["ui"]),
            _h.Item(3, "C", priority: WorkItemPriority.Low, labels: ["api"]),
        ]);
        var section = await _h.CreateAsync();
        var todo = section.Columns[(int)WorkItemStatus.Todo];

        section.AvailableLabels.Should().Equal("api", "ui");
        section.HasAvailableLabels.Should().BeTrue();
        section.PriorityFilter = TaskPriorityFilter.All.Single(f => f.Priority == WorkItemPriority.Urgent);
        Titles(todo).Should().Equal("A", "B");

        section.FilterByLabelCommand.Execute("api");
        Titles(todo).Should().Equal("A");
        section.LabelFilterText.Should().Be("api");

        section.ClearLabelFilterCommand.Execute(null);
        Titles(todo).Should().Equal("A", "B");
    }

    [Fact]
    public async Task Done_tasks_older_than_two_weeks_collapse_behind_show_more_and_show_done_hides_the_column()
    {
        _h.Items.AddRange([
            _h.Item(1, "Recent", WorkItemStatus.Done, completed: DateTimeOffset.Now.AddDays(-2)),
            _h.Item(2, "Old", WorkItemStatus.Done, completed: DateTimeOffset.Now.AddDays(-30)),
            _h.Item(3, "Older", WorkItemStatus.Done, completed: DateTimeOffset.Now.AddDays(-60)),
        ]);
        var section = await _h.CreateAsync();
        var done = section.Columns[(int)WorkItemStatus.Done];

        Titles(done).Should().Equal("Recent");
        done.HiddenCount.Should().Be(2);
        done.ShowMoreText.Should().Be("Show 2 more");
        done.Count.Should().Be(3);

        done.ShowHiddenCommand.Execute(null);
        done.Cards.Should().HaveCount(3);
        done.HasHidden.Should().BeFalse();

        section.ShowDone = false;
        done.IsVisible.Should().BeFalse();
        done.Cards.Should().BeEmpty();
        section.ListItems.Should().BeEmpty();
    }

    // ----- List view --------------------------------------------------------------------

    [Fact]
    public async Task List_view_sorts_by_the_chosen_column_and_toggles_direction()
    {
        _h.Items.AddRange([
            _h.Item(1, "Beta", priority: WorkItemPriority.Low),
            _h.Item(2, "Alpha", priority: WorkItemPriority.Urgent),
            _h.Item(3, "Gamma", priority: WorkItemPriority.High),
        ]);
        var section = await _h.CreateAsync();
        section.IsListView = true;
        section.ViewMode.Should().Be(TasksViewMode.List);
        section.ShowListSurface.Should().BeTrue();

        section.SortByCommand.Execute(TaskSortColumn.Title);
        section.ListItems.Select(c => c.Title).Should().Equal("Alpha", "Beta", "Gamma");

        section.SortByCommand.Execute(TaskSortColumn.Title);
        section.ListItems.Select(c => c.Title).Should().Equal("Gamma", "Beta", "Alpha");

        section.SortByCommand.Execute(TaskSortColumn.Priority);
        section.SortDescending.Should().BeTrue();
        section.ListItems.Select(c => c.Title).Should().Equal("Alpha", "Gamma", "Beta");

        section.SortByCommand.Execute(TaskSortColumn.Key);
        section.ListItems.Select(c => c.Number).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Ctrl_n_switches_back_to_the_board_and_opens_quick_add_in_todo()
    {
        _h.Items.Add(_h.Item(1, "A"));
        var section = await _h.CreateAsync();
        section.ShowListCommand.Execute(null);

        section.NewTaskCommand.Execute(null);

        section.IsBoardView.Should().BeTrue();
        section.Columns[(int)WorkItemStatus.Todo].IsQuickAddOpen.Should().BeTrue();
    }

    // ----- Live refresh -----------------------------------------------------------------

    [Fact]
    public async Task Changes_reload_the_board_while_visible_and_mark_it_stale_otherwise()
    {
        var section = await _h.CreateAsync();
        _h.Items.Add(_h.Item(1, "From the palette"));

        _h.RaiseChanged("w1");
        section.TotalCount.Should().Be(1);

        section.Deactivate();
        _h.Items.Add(_h.Item(2, "While away"));
        _h.RaiseChanged("w2");
        section.TotalCount.Should().Be(1);

        await section.ActivateAsync();
        section.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Changes_of_other_projects_are_ignored()
    {
        var section = await _h.CreateAsync();
        _h.WorkItems.ClearReceivedCalls();

        _h.WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(_h.WorkItems, new WorkItemsChangedEventArgs("other", null));

        await _h.WorkItems.DidNotReceiveWithAnyArgs().GetAllAsync(default!, default);
        section.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Cards_linked_to_the_current_branch_are_highlighted_and_follow_checkouts()
    {
        _h.Items.Add(_h.Item(1, "Login", links: [new WorkItemLink("l1", WorkItemLinkKind.Branch, "task/1-login", null, DateTimeOffset.Now)]));
        _h.Workspace.CurrentStatus = Workspace.Support.TestData.Status(branch: "task/1-login");
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Todo].Cards.Single();
        card.IsOnCurrentBranch.Should().BeTrue();
        card.BranchCount.Should().Be(1);

        _h.Workspace.CurrentStatus = Workspace.Support.TestData.Status(branch: "main");
        await _h.Context.RefreshGitStatusAsync();

        card.IsOnCurrentBranch.Should().BeFalse();
    }

    [Fact]
    public async Task Overdue_cards_are_flagged()
    {
        _h.Items.AddRange([
            _h.Item(1, "Late", due: DateTimeOffset.Now.AddDays(-3)),
            _h.Item(2, "Today", due: DateTimeOffset.Now),
            _h.Item(3, "Late but done", WorkItemStatus.Done, due: DateTimeOffset.Now.AddDays(-3), completed: DateTimeOffset.Now),
        ]);
        var section = await _h.CreateAsync();
        var todo = section.Columns[(int)WorkItemStatus.Todo].Cards;

        todo.Single(c => c.Title == "Late").IsOverdue.Should().BeTrue();
        todo.Single(c => c.Title == "Late").DueText.Should().Be("Overdue by 3 days");
        todo.Single(c => c.Title == "Today").IsOverdue.Should().BeFalse();
        todo.Single(c => c.Title == "Today").DueText.Should().Be("Due today");
        section.Columns[(int)WorkItemStatus.Done].Cards.Single().IsOverdue.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_card_confirms_and_closes_its_details()
    {
        _h.Items.Add(_h.Item(1, "Obsolete"));
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)WorkItemStatus.Todo].Cards.Single();
        await section.OpenCommand.ExecuteAsync(card);
        _h.Workspace.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);

        await section.DeleteCardCommand.ExecuteAsync(card);

        await _h.Workspace.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.Title == "Delete #1?"));
        await _h.WorkItems.Received(1).DeleteAsync("w1", Arg.Any<CancellationToken>());
        section.Detail.Should().BeNull();
        section.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Navigation_by_id_opens_the_task_even_when_filtered_out()
    {
        _h.Items.AddRange([_h.Item(1, "A"), _h.Item(2, "Archived", WorkItemStatus.Done, completed: DateTimeOffset.Now.AddDays(-90))]);
        var section = await _h.CreateAsync();
        section.SearchText = "A";

        await section.NavigateToAsync("w2");

        section.Detail!.Key.Should().Be("#2");
        section.SearchText.Should().BeEmpty();
        section.ShowOlderDone.Should().BeTrue();
        section.SelectedCard!.Id.Should().Be("w2");
        section.Detail.Card.IsOpen.Should().BeTrue();

        await section.NavigateToAsync(TaskNavigation.CreateTask);
        section.Columns[(int)WorkItemStatus.Todo].IsQuickAddOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Navigation_to_an_unknown_task_reloads_then_notifies()
    {
        var section = await _h.CreateAsync();

        await section.NavigateToAsync("missing");

        section.Detail.Should().BeNull();
        _h.Workspace.Notifications.Received(1).Show("Task not found", Arg.Any<string?>(), NotificationSeverity.Warning, null);
    }
}
