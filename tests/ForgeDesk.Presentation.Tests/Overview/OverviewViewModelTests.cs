using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Overview;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Presentation.Tests.Overview;

public sealed class OverviewViewModelTests : IDisposable
{
    private readonly OverviewHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ----- Activation -------------------------------------------------------------------

    [Fact]
    public async Task Activation_loads_every_card_from_the_services()
    {
        _h.Status = TestData.Status();
        _h.Commits.AddRange(Enumerable.Range(1, 6).Select(i => OverviewHarness.Commit($"{i:x}{new string('a', 39)}", $"Commit {i}")));
        _h.Tasks.Add(OverviewHarness.Item("t1", 1, "Write docs", WorkItemStatus.Todo));
        _h.Entries.Add(Entry(1, "Pushed main"));
        _h.Snapshot = new ProjectSnapshot { ProjectId = _h.Project.Id, CapturedAt = DateTimeOffset.UtcNow };
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();

        await overview.ActivateAsync();

        overview.Cards.Should().OnlyContain(c => c.HasLoaded && !c.HasError);
        overview.Commits.Commits.Should().HaveCount(6);
        await _h.Git.Received().GetLogAsync(_h.Project.Path, Arg.Is<GitLogQuery>(q => q.Take == 6), Arg.Any<CancellationToken>());
        overview.Tasks.OpenCount.Should().Be(1);
        overview.Activity.Entries.Should().ContainSingle();
        await _h.Activity.Received().QueryAsync(Arg.Is<ActivityQuery>(q => q.ProjectId == _h.Project.Id && q.Limit == 8), Arg.Any<CancellationToken>());
        overview.Attention.IsHealthy.Should().BeTrue();
        overview.Ci.Hint.Should().Be(CiHint.NotLinked);
    }

    [Fact]
    public async Task A_second_activation_does_not_reload_fresh_cards()
    {
        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        await overview.ActivateAsync();
        overview.Deactivate();
        _h.Git.ClearReceivedCalls();

        await overview.ActivateAsync();

        await _h.Git.DidNotReceive().GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_card_failure_stays_inside_the_card_and_can_be_retried()
    {
        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();
        _h.WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<WorkItem>>(new ForgeException(ErrorKind.Unknown, "Database is locked.")));
        using var overview = _h.Create();

        await overview.ActivateAsync();

        overview.Tasks.HasError.Should().BeTrue();
        overview.Tasks.Error!.Message.Should().Be("Database is locked.");
        overview.Commits.HasError.Should().BeFalse();

        _h.WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<WorkItem>>([]));
        await overview.Tasks.LoadCommand.ExecuteAsync(null);

        overview.Tasks.HasError.Should().BeFalse();
        overview.Tasks.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_folder_replaces_the_cards_with_a_locate_state()
    {
        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        _h.Folder.Dispose();

        await _h.RefreshStatusAsync();

        overview.IsFolderMissing.Should().BeTrue();
        overview.ShowContent.Should().BeFalse();
    }

    // ----- Working tree ----------------------------------------------------------------

    [Fact]
    public async Task Working_tree_counts_follow_the_shared_git_status()
    {
        _h.Status = TestData.Status(ahead: 2, behind: 1, entries:
        [
            new GitStatusEntry { Path = "a.cs", IndexState = GitFileState.Modified, WorkTreeState = GitFileState.Unmodified },
            TestData.Modified("b.cs"),
            new GitStatusEntry { Path = "new.txt", IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Untracked },
            TestData.Conflicted("c.cs"),
        ]);
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        var tree = overview.WorkingTree;

        tree.IsRepository.Should().BeTrue();
        tree.BranchText.Should().Be("main");
        tree.UpstreamText.Should().Be("origin/main");
        (tree.Ahead, tree.Behind).Should().Be((2, 1));
        (tree.Staged, tree.Changed, tree.Untracked, tree.Conflicts).Should().Be((1, 1, 1, 1));
        tree.Tone.Should().Be(StatusTone.Danger);
        tree.ReviewText.Should().Be("Resolve conflicts");

        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();

        tree.IsClean.Should().BeTrue();
        tree.Summary.Should().Contain("Clean");
        tree.Tone.Should().Be(StatusTone.Success);
    }

    [Fact]
    public async Task Not_a_repository_offers_to_initialize_one_in_the_Git_tab()
    {
        _h.Status = null;
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();

        overview.WorkingTree.IsNotRepository.Should().BeTrue();
        overview.Commits.ShowNotRepository.Should().BeTrue();
        overview.WorkingTree.OpenGitCommand.Execute(null);

        _h.Navigations.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Git));
    }

    [Fact]
    public async Task Review_changes_opens_the_Git_tab()
    {
        _h.Status = TestData.Status(entries: [TestData.Modified("a.cs")]);
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();

        overview.WorkingTree.ReviewChangesCommand.Execute(null);

        _h.Navigations.Should().ContainSingle(n => n.Section == WorkspaceSection.Git);
    }

    // ----- Recent commits --------------------------------------------------------------

    [Fact]
    public async Task Clicking_a_commit_opens_the_Git_history_at_its_sha()
    {
        _h.Status = TestData.Status();
        _h.Commits.Add(OverviewHarness.Commit("abcdef1234567890abcdef1234567890abcdef12", "Fix the parser"));
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        await overview.ActivateAsync();
        var commit = overview.Commits.Commits.Single();

        overview.Commits.OpenCommitCommand.Execute(commit);

        commit.ShortSha.Should().Be("abcdef1");
        commit.Initials.Should().Be("AL");
        _h.Navigations.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Git, commit.Sha));
    }

    [Fact]
    public async Task Commits_reload_when_the_repository_changes_while_visible()
    {
        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        await overview.ActivateAsync();
        _h.Commits.Add(OverviewHarness.Commit("1234567890123456789012345678901234567890", "New commit"));

        await _h.Context.NotifyRepositoryChangedAsync();

        overview.Commits.Commits.Should().ContainSingle(c => c.Subject == "New commit");
    }

    [Fact]
    public async Task Repository_changes_while_hidden_reload_on_the_next_activation_only()
    {
        _h.Status = TestData.Status();
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();
        await overview.ActivateAsync();
        overview.Deactivate();
        _h.Commits.Add(OverviewHarness.Commit("1234567890123456789012345678901234567890", "While away"));

        await _h.Context.NotifyRepositoryChangedAsync();

        overview.Commits.Commits.Should().BeEmpty();
        overview.Commits.IsStale.Should().BeTrue();

        await overview.ActivateAsync();

        overview.Commits.Commits.Should().ContainSingle(c => c.Subject == "While away");
    }

    [Fact]
    public async Task A_repository_without_commits_says_so()
    {
        _h.Status = TestData.Status() with { IsUnborn = true };
        await _h.RefreshStatusAsync();
        using var overview = _h.Create();

        await overview.ActivateAsync();

        overview.Commits.IsEmpty.Should().BeTrue();
        await _h.Git.DidNotReceive().GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>());
    }

    // ----- Attention --------------------------------------------------------------------

    [Fact]
    public async Task Attention_lists_reasons_by_severity_and_opens_their_section()
    {
        _h.Snapshot = new ProjectSnapshot
        {
            ProjectId = _h.Project.Id,
            CapturedAt = DateTimeOffset.UtcNow,
            Attention =
            [
                new AttentionReason(AttentionLevel.Info, "3 commits not pushed.", "Git"),
                new AttentionReason(AttentionLevel.Critical, "CI is failing on main.", "GitHub"),
                new AttentionReason(AttentionLevel.Warning, "The last run of 'test' failed.", "Commands"),
                new AttentionReason(AttentionLevel.Critical, "The project folder was moved or deleted.", "Overview"),
            ],
        };
        using var overview = _h.Create();
        await overview.ActivateAsync();
        var items = overview.Attention.Items;

        items.Select(i => i.Level).Should().Equal(AttentionLevel.Critical, AttentionLevel.Critical, AttentionLevel.Warning, AttentionLevel.Info);
        items[0].Tone.Should().Be(StatusTone.Danger);
        items[0].Section.Should().Be(WorkspaceSection.GitHub);
        items[1].CanOpen.Should().BeFalse("the Overview is the tab showing the list");
        overview.Attention.Tone.Should().Be(StatusTone.Danger);

        overview.Attention.OpenCommand.Execute(items[2]);

        _h.Navigations.Should().ContainSingle().Which.Section.Should().Be(WorkspaceSection.Commands);
        await _h.StatusService.DidNotReceive().RefreshAsync(Arg.Any<Project>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attention_follows_snapshot_updates_of_this_project_only()
    {
        _h.Snapshot = new ProjectSnapshot { ProjectId = _h.Project.Id, CapturedAt = DateTimeOffset.UtcNow };
        using var overview = _h.Create();
        await overview.ActivateAsync();

        _h.StatusService.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_h.StatusService, new ProjectSnapshot
        {
            ProjectId = "another",
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            Attention = [new AttentionReason(AttentionLevel.Warning, "Other project", "Git")],
        });
        overview.Attention.HasItems.Should().BeFalse();

        _h.StatusService.SnapshotUpdated += Raise.Event<EventHandler<ProjectSnapshot>>(_h.StatusService, new ProjectSnapshot
        {
            ProjectId = _h.Project.Id,
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(2),
            Attention = [new AttentionReason(AttentionLevel.Warning, "2 commits behind origin/main.", "Git")],
        });

        overview.Attention.Items.Should().ContainSingle(i => i.Message == "2 commits behind origin/main.");
        overview.Attention.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_or_old_snapshot_is_recomputed_without_calling_GitHub()
    {
        _h.Snapshot = null;
        using var overview = _h.Create();

        await overview.ActivateAsync();

        await _h.StatusService.Received(1).RefreshAsync(_h.Project, false, Arg.Any<CancellationToken>());
    }

    // ----- Commands --------------------------------------------------------------------

    [Fact]
    public async Task Commands_card_offers_the_first_dev_build_test_and_lint_commands_with_their_last_result()
    {
        await LoadProfileAsync(
            Command("npm:dev", "dev", CommandCategory.Dev),
            Command("npm:build", "build", CommandCategory.Build),
            Command("npm:build2", "build:prod", CommandCategory.Build),
            Command("npm:test", "test", CommandCategory.Test),
            Command("npm:lint", "lint", CommandCategory.Lint),
            Command("npm:format", "format", CommandCategory.Format));
        _h.History.Add(Record("r1", "npm:test", RunStatus.Failed, TestData.Now.AddHours(-2), exitCode: 1));
        _h.History.Add(Record("r2", "npm:test", RunStatus.Succeeded, TestData.Now.AddHours(-1)));
        using var overview = _h.Create();

        await overview.ActivateAsync();

        var commands = overview.Commands.Commands;
        commands.Select(c => c.Id).Should().Equal("npm:dev", "npm:build", "npm:test", "npm:lint");
        commands[2].LastRun!.Id.Should().Be("r2");
        commands[2].LastTone.Should().Be(StatusTone.Success);
        commands[0].HasLastRun.Should().BeFalse();
        overview.Commands.AllCommandsText.Should().Be("All 6 commands");
    }

    [Fact]
    public async Task Starting_a_command_runs_it_in_its_folder_and_shows_the_run()
    {
        Directory.CreateDirectory(_h.Folder.Combine("web"));
        await LoadProfileAsync(Command("npm:build", "build", CommandCategory.Build, workingDirectory: "web"));
        var session = Substitute.For<IRunSession>();
        session.Id.Returns("run-42");
        session.Status.Returns(RunStatus.Running);
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        using var overview = _h.Create();
        await overview.ActivateAsync();
        var build = overview.Commands.Commands.Single();

        await overview.Commands.StartCommand.ExecuteAsync(build);

        await _h.Runs.Received(1).StartAsync(Arg.Is<RunRequest>(r =>
            r.ProjectId == _h.Project.Id
            && r.CommandLine == "npm run build"
            && r.CommandId == "npm:build"
            && r.Category == CommandCategory.Build
            && PathUtil.AreSame(r.WorkingDirectory, _h.Folder.Combine("web"))), Arg.Any<CancellationToken>());
        _h.Navigations.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Commands, "run-42"));
        build.IsRunning.Should().BeTrue();
        build.IsStarting.Should().BeFalse();
    }

    [Fact]
    public async Task A_command_that_cannot_start_is_reported()
    {
        await LoadProfileAsync(Command("npm:test", "test", CommandCategory.Test));
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.ToolNotFound, "npm was not found."));
        using var overview = _h.Create();
        await overview.ActivateAsync();

        await overview.Commands.StartCommand.ExecuteAsync(overview.Commands.Commands.Single());

        _h.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "npm was not found."), Arg.Any<NotificationAction?>());
        _h.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_completed_run_updates_the_last_result_live()
    {
        await LoadProfileAsync(Command("npm:test", "test", CommandCategory.Test));
        using var overview = _h.Create();
        await overview.ActivateAsync();

        _h.Runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_h.Runs,
            new RunCompletedEventArgs(Record("r9", "npm:test", RunStatus.Failed, TestData.Now, exitCode: 2)));

        var test = overview.Commands.Commands.Single();
        test.LastRunText.Should().Be("Failed (exit code 2)");
        test.LastTone.Should().Be(StatusTone.Danger);
    }

    [Fact]
    public async Task No_primary_command_shows_a_hint_once_detection_ran()
    {
        await LoadProfileAsync(Command("make:docs", "docs", CommandCategory.Other));
        using var overview = _h.Create();

        await overview.ActivateAsync();

        overview.Commands.ShowNoCommands.Should().BeTrue();
        overview.Commands.OpenCommandsCommand.Execute(null);
        _h.Navigations.Should().ContainSingle(n => n.Section == WorkspaceSection.Commands && n.Argument == null);
    }

    // ----- Tasks -------------------------------------------------------------------------

    [Fact]
    public async Task Tasks_card_counts_open_tasks_per_status_and_lists_the_most_important()
    {
        _h.Tasks.AddRange(
        [
            OverviewHarness.Item("a", 1, "Low todo", WorkItemStatus.Todo, WorkItemPriority.Low),
            OverviewHarness.Item("b", 2, "Urgent bug", WorkItemStatus.Todo, WorkItemPriority.Urgent),
            OverviewHarness.Item("c", 3, "Doing", WorkItemStatus.InProgress, WorkItemPriority.High),
            OverviewHarness.Item("d", 4, "Review me", WorkItemStatus.Review, WorkItemPriority.High),
            OverviewHarness.Item("e", 5, "Backlog idea", WorkItemStatus.Backlog),
            OverviewHarness.Item("f", 6, "Another", WorkItemStatus.Todo),
            OverviewHarness.Item("g", 7, "Done already", WorkItemStatus.Done, WorkItemPriority.Urgent),
        ]);
        using var overview = _h.Create();

        await overview.ActivateAsync();

        var tasks = overview.Tasks;
        tasks.OpenCount.Should().Be(6);
        tasks.StatusCounts.Select(c => (c.Label, c.Count)).Should().Equal(("In progress", 1), ("Review", 1), ("To do", 3), ("Backlog", 1));
        tasks.TopTasks.Select(t => t.Id).Should().Equal("b", "c", "d", "a", "f");
        tasks.TopTasks[0].PriorityTone.Should().Be(StatusTone.Danger);

        tasks.OpenTaskCommand.Execute(tasks.TopTasks[1]);
        _h.Navigations.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Tasks, "c"));
    }

    [Fact]
    public async Task New_task_asks_for_a_title_and_creates_it()
    {
        _h.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns(Task.FromResult<string?>("  Ship the release  "));
        _h.WorkItems.CreateAsync(Arg.Any<string>(), Arg.Any<WorkItemDraft>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(OverviewHarness.Item("new", 8, call.ArgAt<WorkItemDraft>(1).Title, WorkItemStatus.Todo)));
        using var overview = _h.Create();
        await overview.ActivateAsync();

        await overview.Tasks.NewTaskCommand.ExecuteAsync(null);

        await _h.WorkItems.Received(1).CreateAsync(_h.Project.Id, Arg.Is<WorkItemDraft>(d => d.Title == "Ship the release"), Arg.Any<CancellationToken>());
        _h.Notifications.Received(1).Show("Created task #8", "Ship the release", NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Cancelling_the_new_task_prompt_creates_nothing()
    {
        _h.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns(Task.FromResult<string?>(null));
        using var overview = _h.Create();

        await overview.Tasks.NewTaskCommand.ExecuteAsync(null);

        await _h.WorkItems.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<WorkItemDraft>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Task_changes_of_this_project_reload_the_card_while_visible()
    {
        using var overview = _h.Create();
        await overview.ActivateAsync();
        _h.Tasks.Add(OverviewHarness.Item("x", 1, "Added elsewhere", WorkItemStatus.Todo));

        _h.WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(_h.WorkItems, new WorkItemsChangedEventArgs("other", null));
        overview.Tasks.OpenCount.Should().Be(0);

        _h.WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(_h.WorkItems, new WorkItemsChangedEventArgs(_h.Project.Id, "x"));
        overview.Tasks.OpenCount.Should().Be(1);
    }

    // ----- CI ------------------------------------------------------------------------------

    [Fact]
    public async Task Ci_asks_to_sign_in_when_signed_out()
    {
        using var harness = new OverviewHarness(new GitHubRepoRef("acme", "forge-app")) { SignedIn = false };
        using var overview = harness.Create();

        await overview.ActivateAsync();
        await overview.Ci.HintActionCommand.ExecuteAsync(null);

        overview.Ci.Hint.Should().Be(CiHint.SignedOut);
        overview.Ci.HintActionText.Should().Be("Sign in to GitHub");
        harness.Navigation.Received(1).OpenSettings("GitHub");
    }

    [Fact]
    public async Task Ci_shows_the_latest_run_of_each_workflow_when_signed_in()
    {
        using var harness = new OverviewHarness(new GitHubRepoRef("acme", "forge-app")) { SignedIn = true, Status = TestData.Status() };
        await harness.RefreshStatusAsync();
        harness.GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CiSummary
        {
            State = CiState.Failure,
            Branch = "main",
            LatestRuns =
            [
                Run(1, "CI", CiState.Failure, TestData.Now),
                Run(2, "Release", CiState.Success, TestData.Now.AddHours(-3)),
            ],
        }));
        using var overview = harness.Create();

        await overview.ActivateAsync();
        overview.Ci.OpenRunCommand.Execute(overview.Ci.Runs[0]);

        overview.Ci.Hint.Should().Be(CiHint.None);
        overview.Ci.ShowRuns.Should().BeTrue();
        overview.Ci.State.Should().Be(CiState.Failure);
        overview.Ci.Runs.Select(r => r.Name).Should().Equal("CI", "Release");
        await harness.GitHub.Received().GetCiSummaryAsync(new GitHubRepoRef("acme", "forge-app"), "main", Arg.Any<CancellationToken>());
        harness.Shell.Received(1).OpenUrl("https://github.com/acme/forge-app/actions/runs/1");
    }

    [Fact]
    public async Task Ci_keeps_the_cached_state_when_GitHub_cannot_be_reached()
    {
        using var harness = new OverviewHarness(new GitHubRepoRef("acme", "forge-app")) { SignedIn = true };
        harness.Snapshot = new ProjectSnapshot
        {
            ProjectId = harness.Project.Id,
            CapturedAt = DateTimeOffset.UtcNow,
            Ci = new CiSummary { State = CiState.Success, Branch = "main", LatestRuns = [Run(5, "CI", CiState.Success, TestData.Now)] },
        };
        harness.GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.NetworkUnavailable, "GitHub cannot be reached."));
        using var overview = harness.Create();

        await overview.ActivateAsync();

        overview.Ci.HasError.Should().BeFalse();
        overview.Ci.State.Should().Be(CiState.Success);
        overview.Ci.Runs.Should().ContainSingle();
        overview.Ci.Message.Should().Contain("GitHub cannot be reached.");
    }

    [Fact]
    public async Task Ci_without_workflow_runs_explains_how_to_add_one()
    {
        using var harness = new OverviewHarness(new GitHubRepoRef("acme", "forge-app")) { SignedIn = true };
        harness.GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CiSummary { State = CiState.None }));
        using var overview = harness.Create();

        await overview.ActivateAsync();
        await overview.Ci.HintActionCommand.ExecuteAsync(null);

        overview.Ci.Hint.Should().Be(CiHint.NoRuns);
        harness.Navigations.Should().ContainSingle(n => n.Section == WorkspaceSection.GitHub);
    }

    [Fact]
    public async Task Ci_follows_a_branch_checkout()
    {
        using var harness = new OverviewHarness(new GitHubRepoRef("acme", "forge-app")) { SignedIn = true, Status = TestData.Status() };
        await harness.RefreshStatusAsync();
        harness.GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CiSummary { State = CiState.Success, Branch = call.ArgAt<string?>(1) }));
        using var overview = harness.Create();
        await overview.ActivateAsync();

        harness.Status = TestData.Status(branch: "feature/login", head: "2222222222222222222222222222222222222222");
        await harness.RefreshStatusAsync();

        overview.Ci.Branch.Should().Be("feature/login");
        await harness.GitHub.Received(1).GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), "feature/login", Arg.Any<CancellationToken>());
    }

    // ----- Recent activity ---------------------------------------------------------------

    [Fact]
    public async Task Recent_activity_inserts_new_entries_of_this_project_live()
    {
        _h.Entries.AddRange(Enumerable.Range(1, 8).Select(i => Entry(i, $"Entry {i}", TestData.Now.AddMinutes(-i))));
        using var overview = _h.Create();
        await overview.ActivateAsync();

        _h.Activity.EntryAdded += Raise.Event<EventHandler<ActivityEntry>>(_h.Activity, Entry(100, "Elsewhere", TestData.Now, projectId: "other"));
        _h.Activity.EntryAdded += Raise.Event<EventHandler<ActivityEntry>>(_h.Activity, Entry(101, "Pushed main", TestData.Now));

        overview.Activity.Entries.Should().HaveCount(8);
        overview.Activity.Entries[0].Title.Should().Be("Pushed main");
        overview.Activity.Entries.Should().NotContain(e => e.Title == "Entry 8");
    }

    [Fact]
    public async Task Recent_activity_follows_references_and_view_all_opens_the_activity_tab()
    {
        _h.Entries.Add(Entry(1, "Committed", TestData.Now) with { RefKind = "commit", RefValue = "abcdef1234567" });
        using var overview = _h.Create();
        await overview.ActivateAsync();

        overview.Activity.OpenCommand.Execute(overview.Activity.Entries[0]);
        overview.Activity.ViewAllCommand.Execute(null);

        _h.Navigations.Should().Equal(
            new WorkspaceNavigationRequest(WorkspaceSection.Git, "abcdef1234567"),
            new WorkspaceNavigationRequest(WorkspaceSection.Activity));
    }

    // ----- Hero ------------------------------------------------------------------------------

    [Fact]
    public async Task Hero_shows_the_readme_description_technologies_and_top_languages()
    {
        _h.Readme = "# Forge\n\n[![CI](https://x/badge.svg)](https://x)\n\nA **fast** desktop cockpit for [developers](https://example.com).\nIt runs everything.\n\n## Install";
        await LoadProfileAsync();
        using var overview = _h.Create();

        await overview.ActivateAsync();

        overview.Hero.Description.Should().Be("A fast desktop cockpit for developers. It runs everything.");
        overview.Hero.ReadmePath.Should().Be("README.md");
        overview.Hero.Technologies.Should().Equal("React", "Vite");
        overview.Hero.Languages.Select(l => l.Name).Should().Equal("TypeScript", "CSS", "HTML", "JavaScript", "Shell", "Other");
        overview.Hero.Languages.Sum(l => l.Percentage).Should().BeApproximately(100, 0.001);

        overview.Hero.OpenFolderCommand.Execute(null);
        _h.Shell.Received(1).OpenFolder(_h.Project.Path);
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private async Task LoadProfileAsync(params DetectedCommand[] commands)
    {
        var profile = new ProjectProfile
        {
            Commands = commands,
            Technologies =
            [
                new Technology("TypeScript", TechnologyKind.Language, "tsconfig.json"),
                new Technology("Vite", TechnologyKind.BuildTool, "vite.config.ts"),
                new Technology("React", TechnologyKind.Framework, "package.json"),
            ],
            Languages =
            [
                new LanguageShare("TypeScript", 10, 100, 50, "#3178C6"),
                new LanguageShare("CSS", 5, 50, 20, "#563D7C"),
                new LanguageShare("HTML", 5, 50, 10, "#E34C26"),
                new LanguageShare("JavaScript", 5, 50, 8, "#F1E05A"),
                new LanguageShare("Shell", 1, 10, 6, "#89E051"),
                new LanguageShare("Makefile", 1, 10, 4, "#427819"),
                new LanguageShare("Dockerfile", 1, 10, 2, "#384D54"),
            ],
        };
        _h.Profile = profile;
        await _h.Context.InitializeAsync();
        _h.Context.Profile.Should().NotBeNull();
    }

    private static DetectedCommand Command(string id, string script, CommandCategory category, string workingDirectory = "") => new()
    {
        Id = id,
        Name = script,
        CommandLine = $"npm run {script}",
        Category = category,
        Source = "package.json",
        WorkingDirectory = workingDirectory,
    };

    private RunRecord Record(string id, string commandId, RunStatus status, DateTimeOffset started, int? exitCode = null) => new()
    {
        Id = id,
        ProjectId = _h.Project.Id,
        CommandId = commandId,
        Label = commandId,
        CommandLine = "npm test",
        WorkingDirectory = _h.Project.Path,
        Status = status,
        StartedAt = started,
        EndedAt = started.AddSeconds(30),
        ExitCode = exitCode,
        LogPath = "log.txt",
    };

    private static WorkflowRunInfo Run(long id, string name, CiState state, DateTimeOffset at) => new()
    {
        Id = id,
        Name = name,
        Event = "push",
        Branch = "main",
        State = state,
        CreatedAt = at,
        UpdatedAt = at,
        HtmlUrl = $"https://github.com/acme/forge-app/actions/runs/{id}",
        RunNumber = (int)id,
    };

    private ActivityEntry Entry(long id, string title, DateTimeOffset? at = null, string? projectId = null) => new()
    {
        Id = id,
        ProjectId = projectId ?? _h.Project.Id,
        At = at ?? TestData.Now,
        Kind = ActivityKind.GitPush,
        Outcome = ActivityOutcome.Success,
        Title = title,
    };
}
