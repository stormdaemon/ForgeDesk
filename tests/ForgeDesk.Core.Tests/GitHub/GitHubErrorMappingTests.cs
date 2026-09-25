using System.Net;
using System.Net.Sockets;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;

namespace ForgeDesk.Core.Tests.GitHub;

/// <summary>End to end: HTTP responses from the fake API → the ForgeException kind callers see.</summary>
public class GitHubErrorMappingTests
{
    private const string RepoPath = "/repos/octo/app";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, string, ErrorKind> StatusCases() => new()
    {
        { 401, """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest"}""", ErrorKind.AuthenticationFailed },
        { 403, """{"message":"Resource not accessible by personal access token"}""", ErrorKind.PermissionDenied },
        { 403, """{"message":"API rate limit exceeded for user ID 583231."}""", ErrorKind.RateLimited },
        { 403, """{"message":"You have exceeded a secondary rate limit and have been temporarily blocked from content creation."}""", ErrorKind.RateLimited },
        { 404, """{"message":"Not Found"}""", ErrorKind.NotFound },
        { 422, """{"message":"Validation Failed","errors":[{"resource":"Repository","code":"invalid","field":"name"}]}""", ErrorKind.InvalidInput },
        { 500, """{"message":"Server Error"}""", ErrorKind.RemoteRejected },
        { 503, "Service Unavailable", ErrorKind.RemoteRejected },
    };

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task Http_errors_map_to_error_kinds(int status, string body, ErrorKind expected)
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnError(HttpMethod.Get, RepoPath, (HttpStatusCode)status, body,
            ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", status == 403 && body.Contains("API rate limit", StringComparison.Ordinal) ? "0" : "4000"), ("X-RateLimit-Reset", "1790000000"));

        var act = () => h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(expected);
        error.Detail.Should().StartWith($"HTTP {status}");
    }

    [Fact]
    public async Task Not_found_mentions_the_repository()
    {
        using var h = new GitHubServiceHarness();

        var act = () => h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Message
            .Should().Be("The repository 'octo/app' doesn't exist or your token can't access it.");
    }

    [Fact]
    public async Task Missing_scope_is_named_for_403()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnError(HttpMethod.Post, "/repos/octo/app/actions/runs/1/rerun", HttpStatusCode.Forbidden, """{"message":"Must have admin rights to Repository."}""",
            ("X-OAuth-Scopes", "read:org"), ("X-Accepted-OAuth-Scopes", "repo"));

        var act = () => h.Service.RerunWorkflowAsync(GitHubServiceHarness.Repo, 1, failedJobsOnly: false, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.PermissionDenied);
        error.Message.Should().Contain("'repo'");
    }

    public static TheoryData<string> TransportFailures() => new() { "dns", "timeout", "socket" };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Transport_failures_are_network_unavailable(string failure)
    {
        using var h = new GitHubServiceHarness();
        h.Api.FailAllWith(() => failure switch
        {
            "dns" => new HttpRequestException("No such host is known. (api.github.test:443)", new SocketException((int)SocketError.HostNotFound)),
            "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()),
            _ => new SocketException((int)SocketError.ConnectionReset),
        });

        var act = () => h.Service.GetIssuesAsync(GitHubServiceHarness.Repo, IssueStateFilter.Open, 10, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        error.Message.Should().Be("GitHub is unreachable. Check your connection.");
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnError(HttpMethod.Get, RepoPath, HttpStatusCode.BadGateway, "{}");
        var first = () => h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);
        await first.Should().ThrowAsync<ForgeException>();

        h.Api.OnGet(RepoPath, GitHubPayloads.Repository());
        var repo = await h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, Ct);

        repo.Name.Should().Be("app");
        h.Api.RequestsTo(RepoPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation()
    {
        using var h = new GitHubServiceHarness();
        var never = new TaskCompletionSource<HttpResponseMessage>();
        h.Api.On(HttpMethod.Get, RepoPath, _ => never.Task);
        using var cts = new CancellationTokenSource();

        var pending = h.Service.GetRepositoryAsync(GitHubServiceHarness.Repo, cts.Token);
        await cts.CancelAsync();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
        never.SetResult(FakeGitHubApi.Json(GitHubPayloads.Repository()));
    }
}
