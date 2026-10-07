using ForgeDesk.Core.Activity;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub;

public sealed class IssuesViewModelTests : IDisposable
{
    private readonly GitHubHarness _harness = new();

    public IssuesViewModelTests()
    {
        Returns(IssueStateFilter.Open, GitHubData.Issue(1, "Crash on start"), GitHubData.Issue(2, "Dark mode", body: ""));
        Returns(IssueStateFilter.Closed, GitHubData.Issue(8, "Old bug", open: false));
        _harness.GitHub.GetIssueCommentsAsync(GitHubHarness.Repo, 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubComment>>([GitHubData.Comment(2, "Second"), GitHubData.Comment(5, "First")]));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Issues_load_filter_and_search()
    {
        var issues = await OpenAsync();

        issues.Items.Select(i => i.Number).Should().Equal(1, 2);
        issues.Items[0].Labels.Should().ContainSingle(l => l.Name == "bug" && l.Color == "#D73A4A");
        issues.SearchText = "bug";
        issues.Items.Select(i => i.Number).Should().Equal(1, 2);
        issues.SearchText = "dark";
        issues.Items.Select(i => i.Number).Should().Equal(2);
        issues.SearchText = string.Empty;

        issues.SelectedFilter = StateFilterOption.For(IssueStateFilter.Closed);

        issues.Items.Should().ContainSingle(i => i.Number == 8 && i.StateText == "Closed");
    }

    [Fact]
    public async Task Empty_open_list_offers_a_new_issue()
    {
        Returns(IssueStateFilter.Open);

        var issues = await OpenAsync();

        issues.IsEmpty.Should().BeTrue();
        issues.EmptyTitle.Should().Be("No open issues");
    }

    [Fact]
    public async Task Selecting_shows_the_description_then_the_comments_oldest_first()
    {
        var issues = await OpenAsync();

        issues.SelectedItem = issues.Items[0];

        var detail = issues.Detail!;
        detail.Title.Should().Be("Crash on start");
        detail.Comments.Select(c => c.Body).Should().Equal("First", "Second");
        detail.Thread.Should().HaveCount(3);
        detail.Thread[0].IsDescription.Should().BeTrue();
        detail.Thread[0].Body.Should().Be("It crashes.");
        detail.CommentsText.Should().Be("2 comments");
    }

    [Fact]
    public async Task Adding_a_comment_posts_it_and_clears_the_box()
    {
        _harness.GitHub.AddIssueCommentAsync(GitHubHarness.Repo, 1, "Looking into it", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubData.Comment(9, "Looking into it", "me")));
        var issues = await OpenAsync();
        issues.SelectedItem = issues.Items[0];
        var detail = issues.Detail!;

        detail.NewComment = "   ";
        await issues.AddCommentCommand.ExecuteAsync(null);
        await _harness.GitHub.DidNotReceiveWithAnyArgs().AddIssueCommentAsync(default!, default, default!, default);

        detail.NewComment = "  Looking into it ";
        await issues.AddCommentCommand.ExecuteAsync(null);

        detail.Comments.Last().Body.Should().Be("Looking into it");
        detail.Thread.Last().Author.Should().Be("me");
        detail.NewComment.Should().BeEmpty();
        issues.Items[0].Comments.Should().Be(1);
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.IssueCommented), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closing_asks_first_and_reopening_does_not()
    {
        _harness.GitHub.SetIssueOpenAsync(GitHubHarness.Repo, 1, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(GitHubData.Issue(1, open: call.ArgAt<bool>(2))));
        var issues = await OpenAsync();
        issues.SelectedItem = issues.Items[0];

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);
        await issues.ToggleStateCommand.ExecuteAsync(null);
        await _harness.GitHub.DidNotReceiveWithAnyArgs().SetIssueOpenAsync(default!, default, default, default);
        await _harness.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Close issue"));

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);
        await issues.ToggleStateCommand.ExecuteAsync(null);

        issues.Detail!.IsOpen.Should().BeFalse();
        issues.Detail.ToggleStateText.Should().Be("Reopen issue");
        issues.Items[0].StateText.Should().Be("Closed");
        _harness.Dialogs.ClearReceivedCalls();

        await issues.ToggleStateCommand.ExecuteAsync(null);

        await _harness.Dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!);
        await _harness.GitHub.Received(1).SetIssueOpenAsync(GitHubHarness.Repo, 1, true, Arg.Any<CancellationToken>());
        issues.Detail.IsOpen.Should().BeTrue();
        await _harness.Activity.Received(2).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.IssueStateChanged), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creating_a_task_links_it_to_the_issue_and_offers_to_open_it()
    {
        var created = new WorkItem { Id = "task-1", ProjectId = _harness.Project.Id, Number = 4, Title = "Crash on start" };
        _harness.WorkItems.CreateAsync(_harness.Project.Id, Arg.Any<WorkItemDraft>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(created));
        NotificationAction? action = null;
        _harness.Notifications.When(n => n.Show("Task #4 created", Arg.Any<string?>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>()))
            .Do(call => action = call.ArgAt<NotificationAction?>(3));
        WorkspaceNavigationRequest? navigation = null;
        _harness.Context.NavigationRequested += (_, request) => navigation = request;
        var issues = await OpenAsync();

        await issues.CreateTaskCommand.ExecuteAsync(issues.Items[0]);

        await _harness.WorkItems.Received(1).CreateAsync(_harness.Project.Id,
            Arg.Is<WorkItemDraft>(d => d.Title == "Crash on start" && d.Description.Contains("https://github.com/acme/forge-app/issues/1") && d.Description.Contains("It crashes.")),
            Arg.Any<CancellationToken>());
        await _harness.WorkItems.Received(1).AddLinkAsync("task-1", WorkItemLinkKind.Issue, "https://github.com/acme/forge-app/issues/1", "#1", Arg.Any<CancellationToken>());
        action!.Label.Should().Be("Open task");

        await action.Execute();

        navigation!.Section.Should().Be(WorkspaceSection.Tasks);
        navigation.Argument.Should().Be("task-1");
    }

    [Fact]
    public async Task New_issue_is_created_and_selected()
    {
        _harness.AnswerDialog<NewIssueDialogViewModel>(dialog =>
        {
            dialog.IssueTitle = " Login fails ";
            dialog.Body = "Steps…";
        });
        _harness.GitHub.CreateIssueAsync(GitHubHarness.Repo, "Login fails", "Steps…", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubData.Issue(30, "Login fails")));
        var issues = await OpenAsync();
        Returns(IssueStateFilter.Open, GitHubData.Issue(30, "Login fails"), GitHubData.Issue(1));

        await issues.NewIssueCommand.ExecuteAsync(null);

        issues.SelectedItem!.Number.Should().Be(30);
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.IssueCreated && e.RefValue!.EndsWith("/30")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Issue_form_needs_a_title()
    {
        var dialog = new NewIssueDialogViewModel("acme/forge-app");
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;

        dialog.ConfirmCommand.Execute(null);
        dialog.ValidationMessage.Should().NotBeNull();
        closed.Should().BeNull();

        dialog.IssueTitle = "Bug";
        dialog.ValidationMessage.Should().BeNull();
        dialog.ConfirmCommand.Execute(null);
        closed.Should().BeTrue();
    }

    [Fact]
    public void Task_description_links_the_issue_and_cuts_long_bodies()
    {
        var description = IssuesViewModel.TaskDescription(GitHubData.Issue(3, body: new string('x', 10_000)));

        description.Should().StartWith("From GitHub issue [#3](https://github.com/acme/forge-app/issues/3) opened by grace.");
        description.Length.Should().BeLessThan(4_200);
        description.Should().EndWith("…");
        IssuesViewModel.TaskDescription(GitHubData.Issue(3, body: "")).Should().NotContain("\n");
    }

    private void Returns(IssueStateFilter state, params GitHubIssue[] issues) =>
        _harness.GitHub.GetIssuesAsync(GitHubHarness.Repo, state, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubIssue>>(issues));

    private async Task<IssuesViewModel> OpenAsync()
    {
        var section = await _harness.OpenGitHubAsync();
        await section.SelectViewAsync(GitHubView.Issues);
        return section.Issues;
    }
}
