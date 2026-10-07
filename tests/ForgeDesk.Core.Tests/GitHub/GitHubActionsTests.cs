using System.Net;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubActionsTests
{
    private const string RunsPath = "/repos/octo/app/actions/runs";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Workflow_runs_are_mapped_with_their_ci_state()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(
            WorkflowRun(30, "CI", 1, "in_progress", null),
            WorkflowRun(29, "CI", 1, "completed", "failure", runNumber: 7),
            WorkflowRun(28, "Deploy", 2, "waiting", null)));

        var runs = await h.Service.GetWorkflowRunsAsync(GitHubServiceHarness.Repo, "main", cancellationToken: Ct);

        runs.Select(r => r.State).Should().Equal(CiState.Running, CiState.Failure, CiState.Queued);
        var failed = runs[1];
        failed.Id.Should().Be(29);
        failed.Name.Should().Be("CI");
        failed.WorkflowId.Should().Be(1);
        failed.RunNumber.Should().Be(7);
        failed.RunAttempt.Should().Be(1);
        failed.Event.Should().Be("push");
        failed.Branch.Should().Be("main");
        failed.HeadSha.Should().Be("abc123");
        failed.CommitMessage.Should().Be("Fix the build");
        failed.Actor.Should().Be("octocat");
        failed.Status.Should().Be("completed");
        failed.Conclusion.Should().Be("failure");
        failed.Duration.Should().Be(TimeSpan.FromMinutes(5));
        failed.HtmlUrl.Should().Be("https://github.com/octo/app/actions/runs/29");
        runs[0].Duration.Should().BeNull("a running workflow has no final duration");
        h.Api.Requests.Single().Query.Should().Contain("branch=main");
    }

    [Fact]
    public async Task Workflow_runs_of_one_workflow_use_the_workflow_endpoint()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/actions/workflows/161335/runs", WorkflowRuns(WorkflowRun(1, "CI", 161335, "completed", "success")));

        var runs = await h.Service.GetWorkflowRunsAsync(GitHubServiceHarness.Repo, workflowId: 161335, max: 20, cancellationToken: Ct);

        runs.Should().ContainSingle().Which.State.Should().Be(CiState.Success);
        h.Api.Requests.Single().Query.Should().Contain("per_page=20");
    }

    [Fact]
    public async Task Workflows_are_listed_with_their_state()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/actions/workflows", Workflows());

        var workflows = await h.Service.GetWorkflowsAsync(GitHubServiceHarness.Repo, Ct);

        workflows.Should().Equal(
            new WorkflowInfo(161335, "CI", ".github/workflows/ci.yml", "active", "https://github.com/octo/app/blob/main/.github/workflows/ci.yml"),
            new WorkflowInfo(161336, "Release", ".github/workflows/release.yml", "disabled_manually", "https://github.com/octo/app/blob/main/.github/workflows/release.yml"));
    }

    [Fact]
    public async Task Jobs_include_their_steps_in_order()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/actions/runs/29679449/jobs", Jobs());

        var jobs = await h.Service.GetWorkflowJobsAsync(GitHubServiceHarness.Repo, 29679449, Ct);

        var job = jobs.Should().ContainSingle().Subject;
        job.Name.Should().Be("build");
        job.State.Should().Be(CiState.Running);
        job.StartedAt.Should().Be(new DateTimeOffset(2026, 9, 20, 10, 0, 10, TimeSpan.Zero));
        job.CompletedAt.Should().BeNull();
        job.Steps.Select(s => (s.Number, s.Name, s.State)).Should().Equal(
            (1, "Set up job", CiState.Success),
            (2, "Run tests", CiState.Running));
    }

    [Theory]
    [InlineData(true, "/repos/octo/app/actions/runs/42/rerun-failed-jobs")]
    [InlineData(false, "/repos/octo/app/actions/runs/42/rerun")]
    public async Task Rerun_posts_to_the_matching_endpoint(bool failedOnly, string expectedPath)
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Post, expectedPath, _ => FakeGitHubApi.Empty(HttpStatusCode.Created));

        await h.Service.RerunWorkflowAsync(GitHubServiceHarness.Repo, 42, failedOnly, Ct);

        h.Api.Requests.Single().Path.Should().Be(expectedPath);
    }

    [Fact]
    public async Task Cancel_posts_to_the_cancel_endpoint_and_invalidates_ci_data()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(WorkflowRun(42, "CI", 1, "in_progress", null)));
        h.Api.On(HttpMethod.Post, "/repos/octo/app/actions/runs/42/cancel", _ => FakeGitHubApi.Empty(HttpStatusCode.Accepted));

        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        await h.Service.CancelWorkflowRunAsync(GitHubServiceHarness.Repo, 42, Ct);
        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        h.Api.RequestsTo(RunsPath).Should().HaveCount(2, "the cancel must invalidate the cached summary");
    }

    [Fact]
    public async Task Ci_summary_keeps_the_latest_run_of_each_workflow()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(
            WorkflowRun(3, "CI", 1, "completed", "success", "2026-09-20T12:00:00Z", headSha: "new"),
            WorkflowRun(2, "CI", 1, "completed", "failure", "2026-09-20T11:00:00Z", headSha: "old"),
            WorkflowRun(1, "Lint", 2, "completed", "success", "2026-09-20T10:00:00Z", headSha: "old")));

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        summary.State.Should().Be(CiState.Success);
        summary.Message.Should().Be("2 workflows passing");
        summary.Branch.Should().Be("main");
        summary.HeadSha.Should().Be("new");
        summary.UpdatedAt.Should().Be(new DateTimeOffset(2026, 9, 20, 12, 5, 0, TimeSpan.Zero));
        summary.LatestRuns.Select(r => r.Id).Should().Equal(3, 1);
    }

    [Fact]
    public async Task Ci_summary_ignores_pull_request_runs_from_forks_with_the_same_branch_name()
    {
        // GH-1: branch= matches head_branch, so a fork's "main" PR run is listed with the repository's main runs.
        using var h = new GitHubServiceHarness();
        var forkRun = WorkflowRun(5, "CI", 1, "completed", "failure", "2026-09-20T12:00:00Z", headSha: "fork");
        forkRun = forkRun.Insert(forkRun.LastIndexOf('}'), """
            , "event": "pull_request", "repository": { "id": 100, "name": "app", "full_name": "octo/app" },
              "head_repository": { "id": 200, "name": "app", "full_name": "contributor/app" }
            """).Replace("\"event\": \"push\",", string.Empty);
        var ownRun = WorkflowRun(4, "CI", 1, "completed", "success", "2026-09-20T11:00:00Z", headSha: "own");
        ownRun = ownRun.Insert(ownRun.LastIndexOf('}'), """
            , "repository": { "id": 100, "name": "app", "full_name": "octo/app" },
              "head_repository": { "id": 100, "name": "app", "full_name": "octo/app" }
            """);
        h.Api.OnGet(RunsPath, WorkflowRuns(forkRun, ownRun));

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        summary.State.Should().Be(CiState.Success);
        summary.HeadSha.Should().Be("own");
    }

    [Fact]
    public async Task Ci_summary_reports_failing_workflows_by_name()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(
            WorkflowRun(3, "build", 1, "completed", "failure"),
            WorkflowRun(2, "lint", 2, "completed", "success")));

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        summary.State.Should().Be(CiState.Failure);
        summary.Message.Should().Be("CI failing: build");
    }

    [Fact]
    public async Task Ci_summary_is_running_while_any_workflow_runs()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(
            WorkflowRun(3, "build", 1, "queued", null),
            WorkflowRun(2, "lint", 2, "completed", "failure")));

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        summary.State.Should().Be(CiState.Running);
        summary.Message.Should().Be("1 workflow running, 1 failing");
    }

    [Fact]
    public async Task Ci_summary_without_runs_is_none()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns());

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "feature/x", Ct);

        summary.State.Should().Be(CiState.None);
        summary.LatestRuns.Should().BeEmpty();
        summary.Message.Should().Be("No workflow runs on 'feature/x' yet");
    }

    [Fact]
    public async Task Ci_summary_defaults_to_the_repository_default_branch()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app", Repository(defaultBranch: "trunk"));
        h.Api.OnGet(RunsPath, WorkflowRuns(WorkflowRun(1, "CI", 1, "completed", "success", branch: "trunk")));

        var summary = await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, null, Ct);

        summary.Branch.Should().Be("trunk");
        h.Api.RequestsTo(RunsPath).Single().Query.Should().Contain("branch=trunk");
    }

    [Fact]
    public async Task Ci_summary_is_cached_until_its_time_to_live_expires()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(WorkflowRun(1, "CI", 1, "completed", "success")));

        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        h.Clock.Advance(GitHubService.CiTimeToLive - TimeSpan.FromSeconds(1));
        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        h.Api.RequestsTo(RunsPath).Should().HaveCount(1);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        h.Api.RequestsTo(RunsPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task Explicit_refresh_bypasses_the_cache()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(WorkflowRun(1, "CI", 1, "completed", "success")));

        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        ((IGitHubService)h.Service).InvalidateCache(new GitHubRepoRef("OCTO", "App"));
        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        h.Api.RequestsTo(RunsPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_call()
    {
        using var h = new GitHubServiceHarness();
        var release = new TaskCompletionSource();
        h.Api.On(HttpMethod.Get, RunsPath, async _ =>
        {
            await release.Task;
            return FakeGitHubApi.Json(WorkflowRuns(WorkflowRun(1, "CI", 1, "completed", "success")));
        });

        var first = h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        var second = h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results[0].Should().BeSameAs(results[1]);
        h.Api.RequestsTo(RunsPath).Should().HaveCount(1);
    }

    [Fact]
    public async Task Signing_in_as_someone_else_drops_cached_data()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(RunsPath, WorkflowRuns(WorkflowRun(1, "CI", 1, "completed", "success")));

        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);
        h.SignIn(OtherClassicToken, "hubot");
        await h.Service.GetCiSummaryAsync(GitHubServiceHarness.Repo, "main", Ct);

        var requests = h.Api.RequestsTo(RunsPath);
        requests.Should().HaveCount(2);
        requests[1].Header("Authorization").Should().Be($"Bearer {OtherClassicToken}", "the client is rebuilt for the new token");
    }
}
