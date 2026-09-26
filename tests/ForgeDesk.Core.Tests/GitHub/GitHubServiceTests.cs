using System.Net;
using System.Text.Json;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Calls_require_a_signed_in_account()
    {
        using var h = new GitHubServiceHarness(signedIn: false);

        h.Service.IsSignedIn.Should().BeFalse();
        var act = () => h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AuthenticationRequired);
        error.Hint.Should().Be("Sign in to GitHub in Settings.");
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Requests_carry_the_token_and_the_ForgeDesk_user_agent()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app", Repository());

        await h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        var request = h.Api.Requests.Single();
        request.Header("Authorization").Should().Be($"Bearer {ClassicToken}");
        request.Header("User-Agent").Should().StartWith("ForgeDesk/9.9.9");
    }

    [Fact]
    public async Task Repository_is_mapped_from_the_api_payload()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app", Repository(isPrivate: true, defaultBranch: "develop"));

        var repo = await h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        repo.Owner.Should().Be("octo");
        repo.Name.Should().Be("app");
        repo.FullName.Should().Be("octo/app");
        repo.IsPrivate.Should().BeTrue();
        repo.DefaultBranch.Should().Be("develop");
        repo.CloneUrl.Should().Be("https://github.com/octo/app.git");
        repo.HtmlUrl.Should().Be("https://github.com/octo/app");
        repo.Stars.Should().Be(42);
        repo.Forks.Should().Be(3);
        repo.OpenIssues.Should().Be(5);
        repo.Language.Should().Be("C#");
        repo.CanPush.Should().BeTrue();
        repo.PushedAt.Should().Be(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task My_repositories_include_org_and_collaborator_repos_sorted_by_push()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/user/repos", Array([
            Repository("octo", "old", pushedAt: "2025-01-01T00:00:00Z"),
            Repository("acme", "platform", pushedAt: "2026-09-24T00:00:00Z", push: false),
            Repository("octo", "app", pushedAt: "2026-09-20T00:00:00Z"),
        ]));

        var repos = await h.Service.GetMyRepositoriesAsync(Ct);

        repos.Select(r => r.FullName).Should().Equal("acme/platform", "octo/app", "octo/old");
        repos[0].CanPush.Should().BeFalse();
        var query = Uri.UnescapeDataString(h.Api.Requests.Single().Query);
        query.Should().Contain("affiliation=").And.Contain("owner").And.Contain("collaborator").And.Contain("organization_member");
        query.Should().Contain("sort=pushed").And.Contain("direction=desc").And.Contain("per_page=100");
    }

    [Fact]
    public async Task Issues_exclude_pull_requests_and_map_labels()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/issues", Array([
            Issue(12, "Crash on start", labels: $"[{Label("bug", "d73a4a")}, {Label("odd", "not-a-color")}]"),
            Issue(11, "Add login page", isPullRequest: true),
            Issue(10, "Docs typo", open: false),
        ]));

        var issues = await h.Service.GetIssuesAsync(GitHubServiceHarness.Repo, IssueStateFilter.All, 50, Ct);

        issues.Select(i => i.Number).Should().Equal(12, 10);
        var crash = issues[0];
        crash.Title.Should().Be("Crash on start");
        crash.IsOpen.Should().BeTrue();
        crash.Author.Should().Be("octocat");
        crash.AuthorAvatarUrl.Should().StartWith("https://avatars.githubusercontent.com/");
        crash.Comments.Should().Be(2);
        crash.Assignees.Should().Equal("hubot");
        crash.Labels.Should().Equal(new GitHubLabel("bug", "#d73a4a"), new GitHubLabel("odd", "#ededed"));
        crash.HtmlUrl.Should().Be("https://github.com/octo/app/issues/12");
        issues[1].IsOpen.Should().BeFalse();
        issues[1].ClosedAt.Should().NotBeNull();
        h.Api.Requests.Single().Query.Should().Contain("state=all");
    }

    [Fact]
    public async Task Issues_keep_paging_when_pull_requests_fill_a_page()
    {
        using var h = new GitHubServiceHarness();
        var firstPage = Array(Enumerable.Range(1, 100).Select(n => Issue(1000 - n, isPullRequest: n > 2)));
        var secondPage = Array([Issue(5), Issue(4)]);
        h.Api.On(HttpMethod.Get, "/repos/octo/app/issues", r =>
            FakeGitHubApi.Json(r.Query.Contains("page=2", StringComparison.Ordinal) ? secondPage : firstPage));

        var issues = await h.Service.GetIssuesAsync(GitHubServiceHarness.Repo, IssueStateFilter.Open, 3, Ct);

        issues.Select(i => i.Number).Should().Equal(999, 998, 5);
        h.Api.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Issue_comments_are_mapped()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/issues/12/comments", Array([Comment(7, "Me too"), Comment(8, "Fixed in #13")]));

        var comments = await h.Service.GetIssueCommentsAsync(GitHubServiceHarness.Repo, 12, Ct);

        comments.Should().HaveCount(2);
        comments[0].Should().Be(new GitHubComment(7, "octocat", "https://avatars.githubusercontent.com/u/2?v=4", "Me too",
            new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero), "https://github.com/octo/app/issues/1#issuecomment-7"));
    }

    [Fact]
    public async Task Creating_an_issue_posts_title_and_body_and_invalidates_the_cached_list()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/issues", Array([Issue(1)]));
        h.Api.OnPost("/repos/octo/app/issues", Issue(2, "New bug"));

        await h.Service.GetIssuesAsync(GitHubServiceHarness.Repo, IssueStateFilter.Open, 30, Ct);
        var created = await h.Service.CreateIssueAsync(GitHubServiceHarness.Repo, "  New bug  ", "Details", Ct);
        await h.Service.GetIssuesAsync(GitHubServiceHarness.Repo, IssueStateFilter.Open, 30, Ct);

        created.Number.Should().Be(2);
        using var body = JsonDocument.Parse(h.Api.RequestsTo("/repos/octo/app/issues").Single(r => r.Method == HttpMethod.Post).Body);
        body.RootElement.GetProperty("title").GetString().Should().Be("New bug");
        body.RootElement.GetProperty("body").GetString().Should().Be("Details");
        h.Api.RequestsTo("/repos/octo/app/issues").Count(r => r.Method == HttpMethod.Get).Should().Be(2);
    }

    [Fact]
    public async Task Creating_an_issue_without_title_is_rejected_locally()
    {
        using var h = new GitHubServiceHarness();

        var act = () => h.Service.CreateIssueAsync(GitHubServiceHarness.Repo, "   ", "body", Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_an_issue_only_sends_the_state_change()
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Patch, "/repos/octo/app/issues/12", _ => FakeGitHubApi.Json(Issue(12, open: false)));

        var issue = await h.Service.SetIssueOpenAsync(GitHubServiceHarness.Repo, 12, open: false, Ct);

        issue.IsOpen.Should().BeFalse();
        using var body = JsonDocument.Parse(h.Api.Requests.Single().Body);
        body.RootElement.GetProperty("state").GetString().Should().Be("closed");
        body.RootElement.GetProperty("state_reason").GetString().Should().Be("completed");
        body.RootElement.TryGetProperty("milestone", out _).Should().BeFalse("a null milestone would detach the issue from its milestone");
    }

    [Fact]
    public async Task Reopening_an_issue_sends_open_state()
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Patch, "/repos/octo/app/issues/12", _ => FakeGitHubApi.Json(Issue(12)));

        var issue = await h.Service.SetIssueOpenAsync(GitHubServiceHarness.Repo, 12, open: true, Ct);

        issue.IsOpen.Should().BeTrue();
        h.Api.Requests.Single().Body.Should().Be("""{"state":"open"}""");
    }

    [Fact]
    public async Task Adding_a_comment_posts_its_body()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnPost("/repos/octo/app/issues/12/comments", Comment(99, "Thanks!"));

        var comment = await h.Service.AddIssueCommentAsync(GitHubServiceHarness.Repo, 12, "Thanks!", Ct);

        comment.Id.Should().Be(99);
        h.Api.Requests.Single().Body.Should().Be("""{"body":"Thanks!"}""");
    }

    [Fact]
    public async Task Pull_request_states_distinguish_draft_merged_and_closed()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/pulls", Array([
            PullRequest(4),
            PullRequest(3, draft: true),
            PullRequest(2, state: "closed", merged: true),
            PullRequest(1, state: "closed"),
        ]));

        var pulls = await h.Service.GetPullRequestsAsync(GitHubServiceHarness.Repo, IssueStateFilter.All, 10, Ct);

        pulls.Select(p => p.State).Should().Equal(PullRequestState.Open, PullRequestState.Draft, PullRequestState.Merged, PullRequestState.Closed);
        pulls[0].HeadBranch.Should().Be("feature/login");
        pulls[0].BaseBranch.Should().Be("main");
        pulls[0].Reviewers.Should().Equal("reviewer");
        pulls[0].Labels.Should().Equal(new GitHubLabel("enhancement", "#a2eeef"));
        pulls[0].Checks.Should().Be(CiState.Unknown);
        h.Api.Requests.Single().Query.Should().Contain("state=all").And.Contain("per_page=10");
    }

    [Fact]
    public async Task Pull_request_details_include_diff_stats_and_combined_checks()
    {
        using var h = new GitHubServiceHarness();
        const string sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
        h.Api.OnGet("/repos/octo/app/pulls/7", PullRequest(7, additions: 120, deletions: 30, changedFiles: 5, mergeable: false));
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/check-runs", CheckRuns(("completed", "success"), ("completed", "skipped")));
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/status", CombinedStatus("failure"));

        var pr = await h.Service.GetPullRequestAsync(GitHubServiceHarness.Repo, 7, Ct);

        pr.Additions.Should().Be(120);
        pr.Deletions.Should().Be(30);
        pr.ChangedFiles.Should().Be(5);
        pr.Mergeable.Should().BeFalse();
        pr.HeadSha.Should().Be(sha);
        pr.Checks.Should().Be(CiState.Failure);
    }

    [Theory]
    [InlineData("in_progress", null, CiState.Running)]
    [InlineData("queued", null, CiState.Queued)]
    [InlineData("completed", "success", CiState.Success)]
    [InlineData("completed", "timed_out", CiState.Failure)]
    public async Task Pull_request_checks_reflect_check_runs(string status, string? conclusion, CiState expected)
    {
        using var h = new GitHubServiceHarness();
        const string sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
        h.Api.OnGet("/repos/octo/app/pulls/7", PullRequest(7));
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/check-runs", CheckRuns((status, conclusion)));
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/status", CombinedStatus());

        var pr = await h.Service.GetPullRequestAsync(GitHubServiceHarness.Repo, 7, Ct);

        pr.Checks.Should().Be(expected);
    }

    [Fact]
    public async Task Pull_request_without_any_check_reports_none()
    {
        using var h = new GitHubServiceHarness();
        const string sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
        h.Api.OnGet("/repos/octo/app/pulls/7", PullRequest(7));
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/check-runs", CheckRuns());
        h.Api.OnGet($"/repos/octo/app/commits/{sha}/status", CombinedStatus());

        var pr = await h.Service.GetPullRequestAsync(GitHubServiceHarness.Repo, 7, Ct);

        pr.Checks.Should().Be(CiState.None, "an empty combined status reads 'pending' but means no status at all");
    }

    [Fact]
    public async Task Pull_request_checks_are_unknown_when_the_token_cannot_read_them()
    {
        using var h = new GitHubServiceHarness();
        const string sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
        const string denied = """{"message":"Resource not accessible by personal access token"}""";
        h.Api.OnGet("/repos/octo/app/pulls/7", PullRequest(7));
        h.Api.OnError(HttpMethod.Get, $"/repos/octo/app/commits/{sha}/check-runs", HttpStatusCode.Forbidden, denied);
        h.Api.OnError(HttpMethod.Get, $"/repos/octo/app/commits/{sha}/status", HttpStatusCode.Forbidden, denied);

        var pr = await h.Service.GetPullRequestAsync(GitHubServiceHarness.Repo, 7, Ct);

        pr.Number.Should().Be(7);
        pr.Checks.Should().Be(CiState.Unknown);
    }

    [Fact]
    public async Task Creating_a_pull_request_sends_draft_flag()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnPost("/repos/octo/app/pulls", PullRequest(8, draft: true));

        var pr = await h.Service.CreatePullRequestAsync(GitHubServiceHarness.Repo, "Add login", "Body", "feature/login", "main", draft: true, Ct);

        pr.State.Should().Be(PullRequestState.Draft);
        using var body = JsonDocument.Parse(h.Api.Requests.Single().Body);
        body.RootElement.GetProperty("head").GetString().Should().Be("feature/login");
        body.RootElement.GetProperty("base").GetString().Should().Be("main");
        body.RootElement.GetProperty("draft").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_pull_request_reports_githubs_explanation()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnError(HttpMethod.Post, "/repos/octo/app/pulls", (HttpStatusCode)422,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for octo:feature/login."}],"documentation_url":"https://docs.github.com/rest/pulls/pulls#create-a-pull-request"}""");

        var act = () => h.Service.CreatePullRequestAsync(GitHubServiceHarness.Repo, "Add login", "", "feature/login", "main", false, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Be("A pull request already exists for octo:feature/login.");
        error.Detail.Should().StartWith("HTTP 422");
    }

    [Fact]
    public async Task Branches_and_tags_are_listed()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet("/repos/octo/app/branches", """[{"name":"main","commit":{"sha":"a1","url":""},"protected":true},{"name":"dev","commit":{"sha":"b2","url":""},"protected":false}]""");
        h.Api.OnGet("/repos/octo/app/tags", """[{"name":"v1.0.0","commit":{"sha":"c3","url":""},"zipball_url":"","tarball_url":"","node_id":"x"}]""");

        var branches = await h.Service.GetBranchesAsync(GitHubServiceHarness.Repo, Ct);
        var tags = await h.Service.GetTagsAsync(GitHubServiceHarness.Repo, Ct);

        branches.Should().Equal("main", "dev");
        tags.Should().Equal(new GitHubTag("v1.0.0", "c3"));
    }

    [Fact]
    public async Task Rate_limit_comes_from_the_rate_limit_endpoint()
    {
        using var h = new GitHubServiceHarness();
        var reset = new DateTimeOffset(2026, 9, 25, 13, 0, 0, TimeSpan.Zero);
        h.Api.OnGet("/rate_limit", RateLimit(5000, 4321, reset.ToUnixTimeSeconds()));

        var limit = await h.Service.GetRateLimitAsync(Ct);

        limit.Should().Be(new RateLimitInfo(5000, 4321, reset));
    }

    [Fact]
    public async Task Rate_limit_is_null_when_signed_out()
    {
        using var h = new GitHubServiceHarness(signedIn: false);

        (await h.Service.GetRateLimitAsync(Ct)).Should().BeNull();
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Rate_limit_falls_back_to_the_last_response_headers_when_offline()
    {
        using var h = new GitHubServiceHarness();
        var reset = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        h.Api.OnGet("/repos/octo/app", Repository(),
            ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "4000"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        h.Api.OnFailure(HttpMethod.Get, "/rate_limit", () => new HttpRequestException("No such host is known."));
        await h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        var limit = await h.Service.GetRateLimitAsync(Ct);

        limit.Should().Be(new RateLimitInfo(5000, 4000, reset));
    }
}
