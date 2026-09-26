using ForgeDesk.Core.GitHub;

namespace ForgeDesk.Core.Tests.GitHub;

public class CiStatesTests
{
    [Theory]
    [InlineData("queued", null, CiState.Queued)]
    [InlineData("waiting", null, CiState.Queued)]
    [InlineData("requested", null, CiState.Queued)]
    [InlineData("pending", null, CiState.Queued)]
    [InlineData("in_progress", null, CiState.Running)]
    [InlineData("completed", "success", CiState.Success)]
    [InlineData("completed", "failure", CiState.Failure)]
    [InlineData("completed", "timed_out", CiState.Failure)]
    [InlineData("completed", "startup_failure", CiState.Failure)]
    [InlineData("completed", "action_required", CiState.Failure)]
    [InlineData("completed", "cancelled", CiState.Cancelled)]
    [InlineData("completed", "skipped", CiState.Success)]
    [InlineData("completed", "neutral", CiState.Success)]
    [InlineData("completed", "stale", CiState.Cancelled)]
    [InlineData("completed", null, CiState.Unknown)]
    [InlineData("COMPLETED", "SUCCESS", CiState.Success)]
    [InlineData("something_new", null, CiState.Unknown)]
    [InlineData(null, "failure", CiState.Failure)]
    public void Workflow_status_and_conclusion_map_to_ci_state(string? status, string? conclusion, CiState expected) =>
        CiStates.FromStatus(status, conclusion).Should().Be(expected);

    [Theory]
    [InlineData("success", CiState.Success)]
    [InlineData("failure", CiState.Failure)]
    [InlineData("error", CiState.Failure)]
    [InlineData("pending", CiState.Running)]
    [InlineData("weird", CiState.Unknown)]
    public void Commit_statuses_map_to_ci_state(string state, CiState expected) =>
        CiStates.FromCommitStatus(state).Should().Be(expected);

    [Theory]
    [InlineData(new CiState[0], CiState.None)]
    [InlineData(new[] { CiState.Success, CiState.Running, CiState.Failure }, CiState.Running)]
    [InlineData(new[] { CiState.Success, CiState.Queued }, CiState.Running)]
    [InlineData(new[] { CiState.Success, CiState.Failure, CiState.Cancelled }, CiState.Failure)]
    [InlineData(new[] { CiState.Success, CiState.Cancelled }, CiState.Success)]
    [InlineData(new[] { CiState.Cancelled }, CiState.Cancelled)]
    [InlineData(new[] { CiState.Unknown }, CiState.Unknown)]
    public void Branch_summary_aggregation_puts_running_first(CiState[] states, CiState expected) =>
        CiStates.Aggregate(states).Should().Be(expected);

    [Theory]
    [InlineData(new CiState[0], CiState.None)]
    [InlineData(new[] { CiState.Success, CiState.Running, CiState.Failure }, CiState.Failure)]
    [InlineData(new[] { CiState.Success, CiState.Running }, CiState.Running)]
    [InlineData(new[] { CiState.Success, CiState.Queued }, CiState.Queued)]
    [InlineData(new[] { CiState.Success, CiState.Cancelled }, CiState.Cancelled)]
    [InlineData(new[] { CiState.Success, CiState.Success }, CiState.Success)]
    public void Pull_request_checks_put_failures_first(CiState[] states, CiState expected) =>
        CiStates.CombineChecks(states).Should().Be(expected);

    [Fact]
    public void Summary_lists_at_most_three_failing_workflows()
    {
        var runs = Enumerable.Range(1, 5).Select(i => Run(i, $"wf{i}", CiState.Failure)).ToList();

        var summary = CiStates.Summarize("main", runs);

        summary.State.Should().Be(CiState.Failure);
        summary.Message.Should().Be("CI failing: wf1, wf2, wf3 +2 more");
    }

    [Fact]
    public void Summary_of_a_single_passing_workflow_is_singular()
    {
        CiStates.Summarize("main", [Run(1, "CI", CiState.Success)]).Message.Should().Be("1 workflow passing");
    }

    [Fact]
    public void Summary_uses_the_newest_run_when_a_workflow_ran_several_times()
    {
        var older = Run(1, "CI", CiState.Failure, workflowId: 7, createdAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Run(2, "CI", CiState.Success, workflowId: 7, createdAt: new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));

        var summary = CiStates.Summarize("main", [older, newer]);

        summary.State.Should().Be(CiState.Success);
        summary.LatestRuns.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    [Fact]
    public void Summary_of_cancelled_runs_names_them()
    {
        CiStates.Summarize(null, [Run(1, "Deploy", CiState.Cancelled)]).Message.Should().Be("CI cancelled: Deploy");
    }

    private static WorkflowRunInfo Run(long id, string name, CiState state, long? workflowId = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = id,
        Name = name,
        WorkflowId = workflowId ?? id,
        Event = "push",
        State = state,
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero),
        HtmlUrl = $"https://github.com/octo/app/actions/runs/{id}",
    };
}
