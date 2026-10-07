using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Tasks.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Tasks;

public sealed class TaskDetailTests : IDisposable
{
    private readonly TasksHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(TasksSectionViewModel Section, TaskDetailViewModel Detail)> OpenAsync(WorkItem item)
    {
        _h.Items.Add(item);
        var section = await _h.CreateAsync();
        var card = section.Columns[(int)item.Status].Cards.Single(c => c.Id == item.Id);
        await section.OpenCommand.ExecuteAsync(card);
        return (section, section.Detail!);
    }

    private void UpdateReturns(Func<WorkItemPatch, WorkItem> apply) =>
        _h.WorkItems.UpdateAsync(Arg.Any<string>(), Arg.Any<WorkItemPatch>(), Arg.Any<CancellationToken>())
            .Returns(call => _h.Replace(apply((WorkItemPatch)call[1])));

    [Fact]
    public async Task Opening_a_card_shows_its_fields_and_history()
    {
        var item = _h.Item(4, "Fix login", priority: WorkItemPriority.High, labels: ["auth"], description: "Steps…", due: DateTimeOffset.Now.AddDays(2));
        _h.WorkItems.GetHistoryAsync("w4", Arg.Any<CancellationToken>()).Returns([
            new WorkItemEvent(1, DateTimeOffset.Now.AddDays(-3), WorkItemEventKind.Created, "Created", null, null),
            new WorkItemEvent(2, DateTimeOffset.Now.AddDays(-1), WorkItemEventKind.PriorityChanged, "Priority: None → High", "None", "High"),
        ]);

        var (section, detail) = await OpenAsync(item);

        section.IsDetailOpen.Should().BeTrue();
        detail.Key.Should().Be("#4");
        detail.Title.Should().Be("Fix login");
        detail.Priority.Should().Be(WorkItemPriority.High);
        detail.Labels.Should().Equal("auth");
        detail.DueDate.Should().Be(DateTimeOffset.Now.AddDays(2).Date);
        detail.History.Select(e => e.Summary).Should().Equal("Priority: None → High", "Created");
        detail.History[0].Icon.Should().Be("Flag16");
        detail.SaveState.Should().BeEmpty("nothing was saved yet");
        await _h.WorkItems.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
    }

    [Fact]
    public async Task Text_edits_are_saved_together_when_flushed()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Old title"));
        UpdateReturns(p => _h.Item(1, p.Title ?? "Old title", description: p.Description ?? string.Empty));

        detail.Title = "New title";
        detail.Description = "Some **markdown**";
        await _h.WorkItems.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);

        await detail.FlushAsync();

        await _h.WorkItems.Received(1).UpdateAsync("w1",
            Arg.Is<WorkItemPatch>(p => p.Title == "New title" && p.Description == "Some **markdown**" && p.Status == null), Arg.Any<CancellationToken>());
        detail.SaveState.Should().Be("Saved");
        detail.Card.Title.Should().Be("New title");
    }

    [Fact]
    public async Task An_empty_title_is_not_saved()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Title"));

        detail.Title = "   ";
        await detail.FlushAsync();

        detail.TitleError.Should().Be("Give the task a title.");
        await _h.WorkItems.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
    }

    [Fact]
    public async Task Typed_text_is_saved_after_a_pause()
    {
        _h.Items.Add(_h.Item(1, "Title"));
        var context = _h.Workspace.CreateContext();
        using var section = new TasksSectionViewModel(context, _h.Workspace.Services, _h.FileIndex) { TextSaveDelay = TimeSpan.FromMilliseconds(20) };
        await section.ActivateAsync();
        await section.OpenCommand.ExecuteAsync(section.Columns[1].Cards.Single());
        UpdateReturns(p => _h.Item(1, p.Title ?? "Title"));

        section.Detail!.Title = "Title v2";

        for (var i = 0; i < 200 && _h.WorkItems.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(IWorkItemService.UpdateAsync)); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.Title == "Title v2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Status_priority_and_due_date_are_saved_immediately()
    {
        var (section, detail) = await OpenAsync(_h.Item(1, "Task"));
        UpdateReturns(p => _h.Item(1, "Task", p.Status ?? WorkItemStatus.Todo, priority: p.Priority ?? WorkItemPriority.None, due: p.DueAt));

        detail.Status = WorkItemStatus.Review;
        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.Status == WorkItemStatus.Review), Arg.Any<CancellationToken>());
        section.Columns[(int)WorkItemStatus.Review].Cards.Single().Id.Should().Be("w1");

        detail.Priority = WorkItemPriority.Urgent;
        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.Priority == WorkItemPriority.Urgent), Arg.Any<CancellationToken>());

        detail.DueDate = new DateTime(2026, 12, 24);
        await _h.WorkItems.Received(1).UpdateAsync("w1",
            Arg.Is<WorkItemPatch>(p => p.DueAt.HasValue && p.DueAt.Value.Date == new DateTime(2026, 12, 24)), Arg.Any<CancellationToken>());

        detail.ClearDueDateCommand.Execute(null);
        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.ClearDueDate), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Labels_are_added_and_removed()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task", labels: ["api"]));
        UpdateReturns(p => _h.Item(1, "Task", labels: p.Labels));

        detail.NewLabelText = "ui, API, perf";
        await detail.AddLabelCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.Labels!.SequenceEqual(new[] { "api", "ui", "perf" })), Arg.Any<CancellationToken>());
        detail.Labels.Should().Equal("api", "ui", "perf");
        detail.NewLabelText.Should().BeEmpty();

        await detail.RemoveLabelCommand.ExecuteAsync("ui");
        detail.Labels.Should().Equal("api", "perf");
    }

    [Fact]
    public async Task A_failed_save_shows_the_stored_value_again()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task"));
        _h.WorkItems.UpdateAsync(default!, default!, default).ReturnsForAnyArgs<WorkItem>(_ => throw new IOException("disk full"));

        detail.Priority = WorkItemPriority.High;
        await Task.Yield();

        detail.Priority.Should().Be(WorkItemPriority.None);
        _h.Workspace.Notifications.ReceivedWithAnyArgs(1).ShowError(default!, default);
    }

    [Fact]
    public async Task Esc_closes_the_pane_saving_pending_text()
    {
        var (section, detail) = await OpenAsync(_h.Item(1, "Task"));
        UpdateReturns(p => _h.Item(1, "Task", description: p.Description ?? string.Empty));
        detail.Description = "notes";

        await detail.CloseCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).UpdateAsync("w1", Arg.Is<WorkItemPatch>(p => p.Description == "notes"), Arg.Any<CancellationToken>());
        section.Detail.Should().BeNull();
        detail.Card.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Selecting_another_card_while_open_switches_the_details()
    {
        _h.Items.Add(_h.Item(2, "Second"));
        var (section, detail) = await OpenAsync(_h.Item(1, "First"));

        section.SelectedCard = section.Columns[1].Cards.Single(c => c.Id == "w2");
        await Task.Yield();

        section.Detail!.Key.Should().Be("#2");
        detail.Card.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Edits_from_elsewhere_refresh_the_open_details_but_keep_unsaved_text()
    {
        var (section, detail) = await OpenAsync(_h.Item(1, "Title"));
        detail.Description = "typing…";

        _h.Replace(_h.Item(1, "Renamed elsewhere", WorkItemStatus.Review, description: "server text"));
        _h.RaiseChanged("w1");

        detail.Title.Should().Be("Renamed elsewhere");
        detail.Status.Should().Be(WorkItemStatus.Review);
        detail.Description.Should().Be("typing…");
        section.Columns[(int)WorkItemStatus.Review].Cards.Single().Should().BeSameAs(detail.Card);
    }

    // ----- Links ------------------------------------------------------------------------

    [Fact]
    public async Task Link_current_branch_adds_a_branch_link()
    {
        _h.Workspace.CurrentStatus = Workspace.Support.TestData.Status(branch: "feature/login");
        var (_, detail) = await OpenAsync(_h.Item(1, "Task"));
        var link = new WorkItemLink("l1", WorkItemLinkKind.Branch, "feature/login", null, DateTimeOffset.Now);
        _h.WorkItems.AddLinkAsync("w1", WorkItemLinkKind.Branch, "feature/login", null, Arg.Any<CancellationToken>())
            .Returns(_ => _h.Replace(_h.Item(1, "Task", links: [link])));
        detail.CanLinkCurrentBranch.Should().BeTrue();
        detail.LinkCurrentBranchText.Should().Be("Link current branch (feature/login)");

        await detail.LinkCurrentBranchCommand.ExecuteAsync(null);

        detail.Links.Single().Text.Should().Be("feature/login");
        detail.CanLinkCurrentBranch.Should().BeFalse();
        detail.Card.IsOnCurrentBranch.Should().BeTrue();
    }

    [Fact]
    public async Task Link_commit_picks_from_the_last_fifty_commits()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task"));
        var commit = new GitCommit { Sha = "3f2a9c1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Subject = "Fix the redirect", Author = new GitSignature("Ada", "ada@x", DateTimeOffset.Now) };
        _h.Git.GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns([commit]);
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var picker = (PickCommitDialogViewModel)call[0];
            picker.SelectedCommit.Should().NotBeNull("the newest commit is preselected");
            return true;
        });

        await detail.LinkCommitCommand.ExecuteAsync(null);

        await _h.Git.Received(1).GetLogAsync(_h.Context.Root, Arg.Is<GitLogQuery>(q => q.Take == 50), Arg.Any<CancellationToken>());
        await _h.WorkItems.Received(1).AddLinkAsync("w1", WorkItemLinkKind.Commit, commit.Sha, "Fix the redirect", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_file_searches_the_file_index()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task"));
        var snapshot = new FileIndexSnapshot(_h.Context.Root, ["src/app.ts", "src/login.ts", "README.md"], false, DateTimeOffset.Now);
        _h.FileIndex.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        _h.FileIndex.Search(snapshot, "login", Arg.Any<int>()).Returns([new FileMatch("src/login.ts", 1, [])]);
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var picker = (PickFileDialogViewModel)call[0];
            picker.Files.Should().HaveCount(3);
            picker.SearchText = "login";
            picker.Files.Select(f => f.RelativePath).Should().Equal("src/login.ts");
            picker.SelectedFile!.Name.Should().Be("login.ts");
            picker.SelectedFile.Folder.Should().Be("src");
            return true;
        });

        await detail.LinkFileCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).AddLinkAsync("w1", WorkItemLinkKind.File, "src/login.ts", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_issue_and_url_prompt_with_validation()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task"));
        _h.Workspace.Dialogs.PromptAsync(Arg.Is<PromptOptions>(o => o.Title == "Link an issue")).Returns(call =>
        {
            var options = (PromptOptions)call[0];
            options.Validate!("abc").Should().NotBeNull();
            options.Validate("#42").Should().BeNull();
            return "#42";
        });
        _h.Workspace.Dialogs.PromptAsync(Arg.Is<PromptOptions>(o => o.Title == "Link a web page")).Returns(call =>
        {
            var options = (PromptOptions)call[0];
            options.Validate!("ftp://x").Should().NotBeNull();
            return "https://example.com/spec";
        });

        await detail.LinkIssueCommand.ExecuteAsync(null);
        await detail.LinkUrlCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).AddLinkAsync("w1", WorkItemLinkKind.Issue, "42", null, Arg.Any<CancellationToken>());
        await _h.WorkItems.Received(1).AddLinkAsync("w1", WorkItemLinkKind.Url, "https://example.com/spec", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Links_open_in_the_right_place()
    {
        _h.Workspace.Project = _h.Workspace.Project with { GitHub = new GitHubRepoRef("forge", "app") };
        var now = DateTimeOffset.Now;
        var (_, detail) = await OpenAsync(_h.Item(1, "Task", links: [
            new WorkItemLink("b", WorkItemLinkKind.Branch, "task/1-x", null, now),
            new WorkItemLink("c", WorkItemLinkKind.Commit, "3f2a9c1abc", "Fix", now),
            new WorkItemLink("f", WorkItemLinkKind.File, "src/a.cs", null, now),
            new WorkItemLink("i", WorkItemLinkKind.Issue, "42", null, now),
            new WorkItemLink("p", WorkItemLinkKind.PullRequest, "7", null, now),
            new WorkItemLink("u", WorkItemLinkKind.Url, "https://example.com", null, now),
        ]));
        var requests = new List<WorkspaceNavigationRequest>();
        _h.Context.NavigationRequested += (_, r) => requests.Add(r);

        foreach (var link in detail.Links)
        {
            detail.OpenLinkCommand.Execute(link);
        }

        requests.Select(r => r.Section).Should().Equal(WorkspaceSection.Git, WorkspaceSection.Git, WorkspaceSection.Files);
        requests[0].Argument.Should().Be(GitNavigation.Branches("task/1-x"));
        requests[1].Argument.Should().Be(GitNavigation.History("3f2a9c1abc"));
        requests[2].Argument.Should().Be("src/a.cs");
        _h.Workspace.Shell.Received(1).OpenUrl("https://github.com/forge/app/issues/42");
        _h.Workspace.Shell.Received(1).OpenUrl("https://github.com/forge/app/pull/7");
        _h.Workspace.Shell.Received(1).OpenUrl("https://example.com");
        detail.Links.Single(l => l.Kind == WorkItemLinkKind.Commit).Text.Should().Be("Fix");
        detail.Links.Single(l => l.Kind == WorkItemLinkKind.Issue).Text.Should().Be("#42");
    }

    [Fact]
    public async Task Issue_links_without_a_github_remote_cannot_be_opened()
    {
        var (_, detail) = await OpenAsync(_h.Item(1, "Task", links: [new WorkItemLink("i", WorkItemLinkKind.Issue, "42", null, DateTimeOffset.Now)]));

        var link = detail.Links.Single();
        link.CanOpen.Should().BeFalse();
        link.Url.Should().BeNull();
        link.ToolTip.Should().Contain("no GitHub remote");
    }

    [Fact]
    public async Task Removing_a_link_updates_the_card()
    {
        var link = new WorkItemLink("u", WorkItemLinkKind.Url, "https://example.com", null, DateTimeOffset.Now);
        var (_, detail) = await OpenAsync(_h.Item(1, "Task", links: [link]));
        _h.WorkItems.RemoveLinkAsync("w1", "u", Arg.Any<CancellationToken>()).Returns(_ => _h.Replace(_h.Item(1, "Task")));

        await detail.RemoveLinkCommand.ExecuteAsync(detail.Links.Single());

        detail.Links.Should().BeEmpty();
        detail.HasLinks.Should().BeFalse();
        detail.Card.HasLinks.Should().BeFalse();
    }
}
