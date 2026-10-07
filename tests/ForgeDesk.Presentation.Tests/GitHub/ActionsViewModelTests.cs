using ForgeDesk.Core.Activity;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub;

public sealed class ActionsViewModelTests : IDisposable
{
    private readonly GitHubHarness _harness = new();
    private readonly List<TaskCompletionSource> _delays = [];

    public ActionsViewModelTests()
    {
        _harness.GitHub.GetWorkflowsAsync(GitHubHarness.Repo, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowInfo>>([GitHubData.Workflow(2, "Release"), GitHubData.Workflow(1, "CI")]));
        Runs(GitHubData.Run(3, CiState.Failure), GitHubData.Run(2), GitHubData.Run(1));
        _harness.GitHub.GetWorkflowJobsAsync(GitHubHarness.Repo, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowJobInfo>>([GitHubData.Job(11, "build", CiState.Failure), GitHubData.Job(12, "lint", CiState.Success)]));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Workflows_list_all_first_and_runs_of_the_current_branch()
    {
        var actions = await OpenAsync();

        actions.Workflows.Select(w => w.Name).Should().Equal("All workflows", "CI", "Release");
        actions.SelectedWorkflow!.IsAll.Should().BeTrue();
        actions.Runs.Select(r => r.Id).Should().Equal(3, 2, 1);
        actions.RunsCaption.Should().Be("Runs of every workflow on main");
        await _harness.GitHub.Received().GetWorkflowRunsAsync(GitHubHarness.Repo, "main", null, ActionsViewModel.MaxRuns, Arg.Any<CancellationToken>());

        actions.SelectedWorkflow = actions.Workflows[1];
        await _harness.GitHub.Received(1).GetWorkflowRunsAsync(GitHubHarness.Repo, "main", 1, ActionsViewModel.MaxRuns, Arg.Any<CancellationToken>());

        actions.SelectedScope = BranchScopeOption.For(RunBranchScope.All);
        await _harness.GitHub.Received(1).GetWorkflowRunsAsync(GitHubHarness.Repo, null, 1, ActionsViewModel.MaxRuns, Arg.Any<CancellationToken>());
        actions.RunsCaption.Should().Be("Runs of CI on all branches");
    }

    [Fact]
    public async Task No_runs_on_the_branch_offers_every_branch()
    {
        Runs();

        var actions = await OpenAsync();

        actions.IsEmpty.Should().BeTrue();
        actions.EmptyTitle.Should().Be("No runs on main");
        actions.ShowAllBranchesCommand.Execute(null);
        actions.SelectedScope.Value.Should().Be(RunBranchScope.All);
    }

    [Fact]
    public async Task Selecting_a_run_shows_its_jobs_failed_ones_expanded()
    {
        var actions = await OpenAsync();

        actions.SelectedRun = actions.Runs[0];

        var detail = actions.Detail!;
        detail.Jobs.Select(j => j.Name).Should().Equal("build", "lint");
        detail.Jobs[0].IsExpanded.Should().BeTrue();
        detail.Jobs[1].IsExpanded.Should().BeFalse();
        detail.Jobs[0].Steps.Should().HaveCount(2);
        detail.Run.CanRerunFailed.Should().BeTrue();
        detail.Run.CanCancel.Should().BeFalse();
    }

    [Fact]
    public async Task Auto_refresh_runs_every_15_seconds_while_a_run_is_in_progress()
    {
        Runs(GitHubData.Run(4, CiState.Running), GitHubData.Run(3));
        var actions = await OpenAsync();
        actions.IsAutoRefreshing.Should().BeTrue();
        _delays.Should().ContainSingle();
        _harness.GitHub.ClearReceivedCalls();

        Runs(GitHubData.Run(4, CiState.Running), GitHubData.Run(3));
        _delays[0].SetResult();

        await _harness.GitHub.Received(1).GetWorkflowRunsAsync(GitHubHarness.Repo, "main", null, ActionsViewModel.MaxRuns, Arg.Any<CancellationToken>());
        _harness.GitHub.Received(1).InvalidateCache(GitHubHarness.Repo);
        actions.IsAutoRefreshing.Should().BeTrue();
        _delays.Should().HaveCount(2);

        Runs(GitHubData.Run(4, CiState.Success), GitHubData.Run(3));
        _delays[1].SetResult();

        actions.Runs[0].StateText.Should().Be("Succeeded");
        actions.IsAutoRefreshing.Should().BeFalse("nothing is in progress any more");
        _delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task Auto_refresh_stops_when_the_view_is_hidden()
    {
        Runs(GitHubData.Run(4, CiState.Queued));
        var section = await _harness.OpenGitHubAsync();
        section.Actions.Delay = Delay;
        await section.SelectViewAsync(GitHubView.Actions);
        section.Actions.IsAutoRefreshing.Should().BeTrue();

        await section.SelectViewAsync(GitHubView.Overview);

        section.Actions.IsAutoRefreshing.Should().BeFalse();
        _harness.GitHub.ClearReceivedCalls();
        _delays[0].TrySetResult();
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GetWorkflowRunsAsync(default!, default, default, default, default);

        section.Deactivate();
        await section.SelectViewAsync(GitHubView.Actions);
        await section.ActivateAsync();
        section.Actions.IsAutoRefreshing.Should().BeTrue("the run is still queued");
    }

    [Fact]
    public async Task Rerun_records_activity_and_reloads_without_cache()
    {
        var actions = await OpenAsync();
        _harness.GitHub.ClearReceivedCalls();

        await actions.RerunCommand.ExecuteAsync(actions.Runs[1]);

        await _harness.GitHub.Received(1).RerunWorkflowAsync(GitHubHarness.Repo, 2, false, Arg.Any<CancellationToken>());
        _harness.GitHub.Received(1).InvalidateCache(GitHubHarness.Repo);
        await _harness.GitHub.Received(1).GetWorkflowRunsAsync(GitHubHarness.Repo, "main", null, ActionsViewModel.MaxRuns, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.WorkflowRerun && e.RefValue!.EndsWith("/runs/2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rerun_failed_jobs_is_only_for_failed_runs()
    {
        var actions = await OpenAsync();

        await actions.RerunFailedCommand.ExecuteAsync(actions.Runs[1]);
        await _harness.GitHub.DidNotReceiveWithAnyArgs().RerunWorkflowAsync(default!, default, default, default);

        await actions.RerunFailedCommand.ExecuteAsync(actions.Runs[0]);
        await _harness.GitHub.Received(1).RerunWorkflowAsync(GitHubHarness.Repo, 3, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_asks_for_confirmation()
    {
        Runs(GitHubData.Run(5, CiState.Running));
        var actions = await OpenAsync();
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);

        await actions.CancelRunCommand.ExecuteAsync(actions.Runs[0]);
        await _harness.GitHub.DidNotReceiveWithAnyArgs().CancelWorkflowRunAsync(default!, default, default);

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);
        await actions.CancelRunCommand.ExecuteAsync(actions.Runs[0]);

        await _harness.Dialogs.Received().ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Cancel run"));
        await _harness.GitHub.Received(1).CancelWorkflowRunAsync(GitHubHarness.Repo, 5, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.WorkflowCancelled), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deep_link_to_a_run_of_another_branch_looks_everywhere()
    {
        _harness.GitHub.GetWorkflowRunsAsync(GitHubHarness.Repo, null, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowRunInfo>>([GitHubData.Run(40, branch: "feature/x"), GitHubData.Run(3)]));
        var section = await _harness.OpenGitHubAsync();

        await section.NavigateToAsync(GitHubNavigation.Run(40));

        section.Actions.SelectedScope.Value.Should().Be(RunBranchScope.All);
        section.Actions.SelectedRun!.Id.Should().Be(40);
        section.Actions.Detail!.Jobs.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Detached_head_lists_runs_of_every_branch()
    {
        _harness.Status = TestData.Status(branch: null);

        var actions = await OpenAsync();

        actions.SelectedScope.Value.Should().Be(RunBranchScope.All);
        await _harness.GitHub.Received().GetWorkflowRunsAsync(GitHubHarness.Repo, null, null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private Task Delay(TimeSpan interval, CancellationToken token)
    {
        interval.Should().Be(ActionsViewModel.AutoRefreshInterval);
        var delay = new TaskCompletionSource(TaskCreationOptions.None);
        token.Register(() => delay.TrySetCanceled(token));
        _delays.Add(delay);
        return delay.Task;
    }

    private void Runs(params WorkflowRunInfo[] runs) =>
        _harness.GitHub.GetWorkflowRunsAsync(GitHubHarness.Repo, Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowRunInfo>>(runs));

    private async Task<ActionsViewModel> OpenAsync()
    {
        var section = await _harness.OpenGitHubAsync();
        section.Actions.Delay = Delay;
        await section.SelectViewAsync(GitHubView.Actions);
        return section.Actions;
    }
}
