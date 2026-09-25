using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using Octokit;
using Octokit.Internal;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubErrorTranslatorTests
{
    [Fact]
    public void Unauthorized_means_the_token_is_invalid_or_expired()
    {
        var error = Translate(new AuthorizationException(Response(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""")));

        error.Kind.Should().Be(ErrorKind.AuthenticationFailed);
        error.Message.Should().Be("Your GitHub token is invalid or expired.");
        error.Detail.Should().Be("HTTP 401 Unauthorized" + Environment.NewLine + """{"message":"Bad credentials"}""");
    }

    [Fact]
    public void Primary_rate_limit_mentions_the_local_reset_time()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(20);
        var response = Response(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded for user ID 1."}""",
            ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        var error = Translate(new RateLimitExceededException(response));

        error.Kind.Should().Be(ErrorKind.RateLimited);
        var expectedTime = GitHubErrorTranslator.FormatLocalTime(DateTimeOffset.FromUnixTimeSeconds(reset.ToUnixTimeSeconds()));
        error.Message.Should().Contain(expectedTime);
        error.Hint.Should().Contain(expectedTime);
    }

    [Fact]
    public void Secondary_rate_limit_uses_retry_after()
    {
        var response = Response(HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", ("Retry-After", "60"));

        var error = Translate(new SecondaryRateLimitExceededException(response));

        error.Kind.Should().Be(ErrorKind.RateLimited);
        error.Hint.Should().Be("Wait about 60 seconds and try again.");
    }

    [Fact]
    public void Too_many_requests_is_a_rate_limit()
    {
        var error = Translate(new ApiException(Response((HttpStatusCode)429, """{"message":"Too many requests"}""", ("Retry-After", "300"))));

        error.Kind.Should().Be(ErrorKind.RateLimited);
        error.Hint.Should().Be("Wait about 5 minutes and try again.");
    }

    [Fact]
    public void Forbidden_with_a_missing_scope_names_the_scope()
    {
        var response = Response(HttpStatusCode.Forbidden, """{"message":"Must have admin rights to Repository."}""",
            ("X-OAuth-Scopes", "gist, read:org"), ("X-Accepted-OAuth-Scopes", "repo"));

        var error = Translate(new ForbiddenException(response));

        error.Kind.Should().Be(ErrorKind.PermissionDenied);
        error.Message.Should().Contain("'repo'");
        error.Hint.Should().Contain("'repo'");
    }

    [Fact]
    public void Broader_scopes_satisfy_narrower_requirements()
    {
        var response = Response(HttpStatusCode.Forbidden, """{"message":"Must have admin rights to Repository."}""",
            ("x-oauth-scopes", "repo, workflow"), ("x-accepted-oauth-scopes", "public_repo"));

        var error = Translate(new ForbiddenException(response));

        error.Kind.Should().Be(ErrorKind.PermissionDenied);
        error.Message.Should().Be("GitHub denied access to this action. GitHub says: Must have admin rights to Repository.");
    }

    [Fact]
    public void Fine_grained_token_denial_keeps_githubs_reason()
    {
        var response = Response(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}""", ("X-Accepted-GitHub-Permissions", "issues=write"));

        var error = Translate(new ForbiddenException(response));

        error.Kind.Should().Be(ErrorKind.PermissionDenied);
        error.Message.Should().Contain("Resource not accessible by personal access token.");
    }

    [Fact]
    public void Sso_enforcement_explains_how_to_authorize_the_token()
    {
        var response = Response(HttpStatusCode.Forbidden, """{"message":"Resource protected by organization SAML enforcement."}""",
            ("X-GitHub-SSO", "required; url=https://github.com/orgs/acme/sso?authorization_request=abc"));

        var error = Translate(new ForbiddenException(response));

        error.Kind.Should().Be(ErrorKind.PermissionDenied);
        error.Message.Should().Contain("single sign-on");
    }

    [Fact]
    public void Not_found_names_the_subject()
    {
        var error = Translate(new NotFoundException(Response(HttpStatusCode.NotFound, """{"message":"Not Found"}""")), "The repository 'octo/secret'");

        error.Kind.Should().Be(ErrorKind.NotFound);
        error.Message.Should().Be("The repository 'octo/secret' doesn't exist or your token can't access it.");
    }

    [Fact]
    public void Validation_errors_are_turned_into_sentences()
    {
        var response = Response((HttpStatusCode)422,
            """{"message":"Validation Failed","errors":[{"resource":"Release","code":"already_exists","field":"tag_name"},{"resource":"Issue","code":"missing_field","field":"title"}]}""");

        var error = Translate(new ApiValidationException(response));

        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Be("A release with this tag already exists. The title is required.");
    }

    [Fact]
    public void Validation_error_without_details_uses_githubs_message()
    {
        var error = Translate(new ApiValidationException(Response((HttpStatusCode)422, """{"message":"No commits between main and feature"}""")));

        error.Message.Should().Be("No commits between main and feature.");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Server_errors_say_github_is_having_problems(int status)
    {
        var error = Translate(new ApiException(Response((HttpStatusCode)status, "<html>Unicorn!</html>")));

        error.Kind.Should().Be(ErrorKind.RemoteRejected);
        error.Message.Should().Be("GitHub is having problems right now.");
        error.Detail.Should().StartWith($"HTTP {status}").And.Contain("Unicorn!");
    }

    [Fact]
    public void Other_client_errors_keep_githubs_message()
    {
        var error = Translate(new ApiException(Response(HttpStatusCode.Conflict, """{"message":"Git Repository is empty"}""")));

        error.Kind.Should().Be(ErrorKind.RemoteRejected);
        error.Message.Should().Be("GitHub rejected the request: Git Repository is empty.");
    }

    [Fact]
    public void Long_bodies_are_truncated_in_the_detail()
    {
        var body = new string('x', 10_000);

        var error = Translate(new ApiException(Response(HttpStatusCode.InternalServerError, body)));

        error.Detail!.Length.Should().BeLessThan(2_100);
        error.Detail.Should().EndWith("…");
    }

    public static TheoryData<Exception> NetworkFailures() => new()
    {
        new HttpRequestException("No such host is known. (api.github.com:443)", new SocketException((int)SocketError.HostNotFound)),
        new HttpRequestException("The SSL connection could not be established"),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()),
        new SocketException((int)SocketError.NetworkUnreachable),
    };

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public void Transport_failures_mean_github_is_unreachable(Exception failure)
    {
        var error = Translate(failure);

        error.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        error.Message.Should().Be("GitHub is unreachable. Check your connection.");
        error.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cancellation_requested_by_the_caller_is_not_translated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        GitHubErrorTranslator.TryTranslate(new OperationCanceledException(cts.Token), null, cts.Token, out var translated).Should().BeFalse();
        translated.Should().BeNull();
    }

    [Fact]
    public void Unrelated_exceptions_are_left_alone()
    {
        var bug = new InvalidOperationException("bug");

        GitHubErrorTranslator.TryTranslate(bug, null, CancellationToken.None, out _).Should().BeFalse();
        GitHubErrorTranslator.Translate(bug).Should().BeSameAs(bug);
    }

    [Fact]
    public void Raw_upload_responses_are_translated_like_octokit_ones()
    {
        var error = GitHubErrorTranslator.FromResponse((HttpStatusCode)422,
            """{"message":"Validation Failed","errors":[{"resource":"ReleaseAsset","code":"already_exists","field":"name"}]}""",
            null, "The release", new SimpleJsonSerializer());

        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Be("A file with this name is already attached to the release.");
    }

    [Fact]
    public void Raw_responses_detect_the_primary_rate_limit_from_headers()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset };

        var error = GitHubErrorTranslator.FromResponse(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", headers, null, new SimpleJsonSerializer());

        error.Kind.Should().Be(ErrorKind.RateLimited);
    }

    [Fact]
    public void Raw_responses_that_are_not_json_still_produce_a_message()
    {
        var error = GitHubErrorTranslator.FromResponse(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", null, null, new SimpleJsonSerializer());

        error.Kind.Should().Be(ErrorKind.RemoteRejected);
        error.Detail.Should().Contain("Bad gateway");
    }

    private static ForgeException Translate(Exception exception, string? subject = null)
    {
        GitHubErrorTranslator.TryTranslate(exception, subject, CancellationToken.None, out var translated).Should().BeTrue();
        return translated!;
    }

    private static FakeResponse Response(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
        new(status, body, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>What Octokit hands to its exceptions: status, raw body, headers and parsed rate-limit info.</summary>
    private sealed class FakeResponse(HttpStatusCode status, string body, Dictionary<string, string> headers) : IResponse
    {
        public object Body => body;

        public IReadOnlyDictionary<string, string> Headers => headers;

        public ApiInfo ApiInfo { get; } = new(
            new Dictionary<string, Uri>(),
            [],
            [],
            string.Empty,
            new RateLimit(
                int.Parse(headers.GetValueOrDefault("X-RateLimit-Limit", "5000"), CultureInfo.InvariantCulture),
                int.Parse(headers.GetValueOrDefault("X-RateLimit-Remaining", "5000"), CultureInfo.InvariantCulture),
                long.Parse(headers.GetValueOrDefault("X-RateLimit-Reset", "0"), CultureInfo.InvariantCulture)),
            TimeSpan.Zero);

        public HttpStatusCode StatusCode => status;

        public string ContentType => "application/json";
    }
}
