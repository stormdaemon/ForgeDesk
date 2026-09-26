using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeDesk.Core.Common;
using Octokit;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Turns Octokit and transport failures into <see cref="ForgeException"/>s with messages written
/// for users. <c>Detail</c> keeps the HTTP status and an excerpt of GitHub's response body.
/// </summary>
internal static class GitHubErrorTranslator
{
    private const int BodyExcerptLength = 2000;
    private const string DefaultSubject = "This GitHub resource";

    public static ForgeException NotSignedIn() =>
        new(ErrorKind.AuthenticationRequired, "You're not signed in to GitHub.", "Sign in to GitHub in Settings.");

    /// <summary>
    /// Translates <paramref name="exception"/> when it is a GitHub or network failure. Returns
    /// false for cancellation requested through <paramref name="cancellationToken"/> and for
    /// unrelated exceptions, which callers let propagate unchanged.
    /// </summary>
    /// <param name="subject">What was requested, used in "not found" messages ("The repository 'octo/app'").</param>
    public static bool TryTranslate(Exception exception, string? subject, CancellationToken cancellationToken, [NotNullWhen(true)] out ForgeException? translated)
    {
        ArgumentNullException.ThrowIfNull(exception);
        translated = exception switch
        {
            ForgeException forge => forge,
            OperationCanceledException when cancellationToken.IsCancellationRequested => null,
            ApiException api => FromApiException(api, subject),
            _ when IsNetworkFailure(exception) => Unreachable(exception),
            _ => null,
        };
        return translated is not null;
    }

    /// <summary>Same as <see cref="TryTranslate"/> but always returns an exception (the original one when untranslatable).</summary>
    public static Exception Translate(Exception exception, string? subject = null) =>
        TryTranslate(exception, subject, CancellationToken.None, out var translated) ? translated : exception;

    /// <summary>Translates a raw HTTP error response (requests made without Octokit, e.g. uploads).</summary>
    public static ForgeException FromResponse(HttpStatusCode status, string? body, IReadOnlyDictionary<string, string>? headers, string? subject, Octokit.Internal.IJsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ApiError? error = null;
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                error = serializer.Deserialize<ApiError>(body);
            }
            catch (Exception)
            {
                // Not GitHub's JSON error shape (a proxy page…): the raw body still goes to Detail.
            }
        }

        return FromStatus(status, error, body, headers, subject);
    }

    private static ForgeException FromApiException(ApiException api, string? subject)
    {
        var body = api.HttpResponse?.Body as string;
        var headers = api.HttpResponse?.Headers;
        return api switch
        {
            RateLimitExceededException rateLimit => PrimaryRateLimit(rateLimit.Reset, Describe(api.StatusCode, body)),
            SecondaryRateLimitExceededException or AbuseException => SecondaryRateLimit(headers, Describe(api.StatusCode, body)),
            _ => FromStatus(api.StatusCode, api.ApiError, body, headers, subject),
        };
    }

    internal static ForgeException FromStatus(HttpStatusCode status, ApiError? error, string? body, IReadOnlyDictionary<string, string>? headers, string? subject)
    {
        var detail = Describe(status, body);
        var gitHubMessage = string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message.Trim();
        var what = string.IsNullOrWhiteSpace(subject) ? DefaultSubject : subject;
        var code = (int)status;

        switch (code)
        {
            case 401:
                return new ForgeException(ErrorKind.AuthenticationFailed, "Your GitHub token is invalid or expired.",
                    "Sign in to GitHub again in Settings.", detail);

            case 403:
                return Forbidden(error, body, headers, detail);

            case 404:
                var missingForNotFound = MissingScope(headers);
                return new ForgeException(ErrorKind.NotFound, $"{what} doesn't exist or your token can't access it.",
                    missingForNotFound is not null
                        ? $"GitHub hides resources your token can't reach: it lacks the '{missingForNotFound}' scope. Sign in again with a token that includes it."
                        : "Check the name, and that your token can access this repository (private repositories need the 'repo' scope, or explicit access for fine-grained tokens).",
                    detail);

            case 410:
                return new ForgeException(ErrorKind.NotFound, gitHubMessage is null ? $"{what} is no longer available." : EnsurePeriod(gitHubMessage),
                    "The feature may be disabled for this repository.", detail);

            case 422:
                return new ForgeException(ErrorKind.InvalidInput, DescribeValidation(error), "Correct the values and try again.", detail);

            case 429:
                return SecondaryRateLimit(headers, detail);

            case >= 500:
                return new ForgeException(ErrorKind.RemoteRejected, "GitHub is having problems right now.",
                    "Try again in a few minutes. Ongoing incidents are listed on https://www.githubstatus.com.", detail);

            default:
                return new ForgeException(ErrorKind.RemoteRejected,
                    gitHubMessage is null ? $"GitHub rejected the request (HTTP {code})." : $"GitHub rejected the request: {EnsurePeriod(gitHubMessage)}",
                    null, detail);
        }
    }

    private static ForgeException Forbidden(ApiError? error, string? body, IReadOnlyDictionary<string, string>? headers, string detail)
    {
        if (Header(headers, "X-RateLimit-Remaining") == "0"
            && long.TryParse(Header(headers, "X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
        {
            return PrimaryRateLimit(DateTimeOffset.FromUnixTimeSeconds(resetEpoch), detail);
        }

        var message = error?.Message ?? string.Empty;
        if (message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
            || (body?.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return SecondaryRateLimit(headers, detail);
        }

        if (Header(headers, "X-GitHub-SSO") is { } sso && sso.StartsWith("required", StringComparison.OrdinalIgnoreCase))
        {
            return new ForgeException(ErrorKind.PermissionDenied,
                "This organization requires single sign-on (SAML) authorization for your token.",
                "On GitHub, open Settings › Developer settings › Tokens, choose \"Configure SSO\" and authorize the organization, then try again.",
                detail);
        }

        if (MissingScope(headers) is { } scope)
        {
            return new ForgeException(ErrorKind.PermissionDenied,
                $"Your GitHub token doesn't have the '{scope}' scope needed for this action.",
                $"Sign in again with a token that includes the '{scope}' scope.",
                detail);
        }

        var reason = string.IsNullOrWhiteSpace(message) ? string.Empty : $" GitHub says: {EnsurePeriod(message.Trim())}";
        return new ForgeException(ErrorKind.PermissionDenied,
            $"GitHub denied access to this action.{reason}",
            "Check that your account can perform this action on the repository. Fine-grained tokens also need the matching repository permission.",
            detail);
    }

    private static string? MissingScope(IReadOnlyDictionary<string, string>? headers)
    {
        var granted = Header(headers, "X-OAuth-Scopes");
        return GitHubScopes.FindMissing(
            GitHubScopes.Parse(Header(headers, "X-Accepted-OAuth-Scopes")),
            granted is null ? null : GitHubScopes.Parse(granted));
    }

    private static ForgeException PrimaryRateLimit(DateTimeOffset reset, string? detail)
    {
        if (reset <= DateTimeOffset.UnixEpoch.AddDays(1))
        {
            // No reset header: GitHub resets the primary limit hourly.
            return new ForgeException(ErrorKind.RateLimited, "You've used up your GitHub API requests for now.",
                "The limit resets within the hour. Try again later.", detail);
        }

        var when = FormatLocalTime(reset);
        return new ForgeException(ErrorKind.RateLimited,
            $"You've used up your GitHub API requests for now. The limit resets at {when}.",
            $"Wait until {when} and try again.",
            detail);
    }

    private static ForgeException SecondaryRateLimit(IReadOnlyDictionary<string, string>? headers, string? detail)
    {
        var retryAfter = int.TryParse(Header(headers, "Retry-After"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? $"Wait about {(seconds < 90 ? $"{seconds} seconds" : $"{(int)Math.Ceiling(seconds / 60.0)} minutes")} and try again."
            : "Wait a minute and try again.";
        return new ForgeException(ErrorKind.RateLimited, "GitHub is limiting how fast ForgeDesk can send requests.", retryAfter, detail);
    }

    private static ForgeException Unreachable(Exception exception) =>
        new(ErrorKind.NetworkUnavailable, "GitHub is unreachable. Check your connection.",
            "ForgeDesk keeps working offline; GitHub data refreshes once you're back online.",
            DescribeChain(exception), exception);

    /// <summary>Local wall-clock time of <paramref name="instant"/>, with the date when it isn't today.</summary>
    internal static string FormatLocalTime(DateTimeOffset instant)
    {
        var local = instant.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    private static bool IsNetworkFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // A cancellation the caller didn't ask for is an HTTP timeout.
            if (current is HttpRequestException or HttpIOException or SocketException or TimeoutException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeValidation(ApiError? error)
    {
        var messages = (error?.Errors ?? [])
            .Select(DescribeValidationError)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (messages.Count > 0)
        {
            return string.Join(" ", messages);
        }

        var message = error?.Message;
        return string.IsNullOrWhiteSpace(message) || message.Equals("Validation Failed", StringComparison.OrdinalIgnoreCase)
            ? "GitHub rejected the request as invalid."
            : EnsurePeriod(message.Trim());
    }

    private static string DescribeValidationError(ApiErrorDetail detail)
    {
        var resource = detail.Resource ?? string.Empty;
        var field = Humanize(detail.Field);
        switch (detail.Code)
        {
            case "already_exists" when resource == "Release" && detail.Field == "tag_name":
                return "A release with this tag already exists.";
            case "already_exists" when resource == "ReleaseAsset":
                return "A file with this name is already attached to the release.";
            case "already_exists":
                return $"{Article(Humanize(resource))} with this {field} already exists.";
            case "missing_field":
                return $"The {field} is required.";
            case "invalid" when string.IsNullOrWhiteSpace(detail.Message):
                return $"The {field} is invalid.";
            case "missing" when string.IsNullOrWhiteSpace(detail.Message):
                return $"{Article(Humanize(resource))} referenced by the request doesn't exist.";
            case "unprocessable" when string.IsNullOrWhiteSpace(detail.Message):
                return $"The {field} can't be processed.";
        }

        if (!string.IsNullOrWhiteSpace(detail.Message))
        {
            return EnsurePeriod(detail.Message.Trim());
        }

        return string.IsNullOrEmpty(detail.Field) ? detail.Code ?? string.Empty : $"{detail.Field}: {detail.Code}";
    }

    private static string Humanize(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "value";
        }

        // "tag_name" → "tag name", "ReleaseAsset" → "release asset".
        var builder = new StringBuilder(identifier.Length + 4);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (c == '_')
            {
                builder.Append(' ');
            }
            else if (char.IsUpper(c))
            {
                if (i > 0 && identifier[i - 1] != '_')
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Article(string noun) =>
        noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0]), StringComparison.Ordinal) ? $"An {noun}" : $"A {noun}";

    private static string EnsurePeriod(string text) =>
        text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private static string Describe(HttpStatusCode status, string? body)
    {
        var reason = Enum.IsDefined(status) ? $" {status}" : string.Empty;
        var header = $"HTTP {(int)status}{reason}";
        if (string.IsNullOrWhiteSpace(body))
        {
            return header;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= BodyExcerptLength
            ? $"{header}{Environment.NewLine}{trimmed}"
            : $"{header}{Environment.NewLine}{trimmed[..BodyExcerptLength]}…";
    }

    private static string DescribeChain(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 5; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        if (headers.TryGetValue(name, out var exact))
        {
            return exact;
        }

        // HTTP/2 lower-cases header names; Octokit keeps whatever casing it received.
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
