using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ForgeDesk.Core.Tests.GitHub.Fakes;

/// <summary>A request as the fake API received it (the body is read eagerly).</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, byte[] RawBody, IReadOnlyDictionary<string, string> Headers)
{
    public string Path => Uri.AbsolutePath;

    public string Query => Uri.Query;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// In-process stand-in for api.github.com: routes (method + path) serve canned JSON, unknown
/// routes answer 404 like GitHub. Later registrations win, so a test can override a default.
/// </summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    public static readonly Uri BaseAddress = new("https://api.github.test/");

    private readonly List<Route> _routes = [];
    private readonly Lock _gate = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) =>
        Requests.Where(r => string.Equals(r.Path, path, StringComparison.Ordinal)).ToList();

    public FakeGitHubApi On(HttpMethod method, string path, Func<RecordedRequest, Task<HttpResponseMessage>> respond)
    {
        lock (_gate)
        {
            _routes.Add(new Route(method, path, respond));
        }

        return this;
    }

    public FakeGitHubApi On(HttpMethod method, string path, Func<RecordedRequest, HttpResponseMessage> respond) =>
        On(method, path, r => Task.FromResult(respond(r)));

    public FakeGitHubApi OnGet(string path, string json, params (string Name, string Value)[] headers) =>
        On(HttpMethod.Get, path, _ => Json(json, HttpStatusCode.OK, headers));

    public FakeGitHubApi OnPost(string path, string json, HttpStatusCode status = HttpStatusCode.Created) =>
        On(HttpMethod.Post, path, _ => Json(json, status));

    public FakeGitHubApi OnError(HttpMethod method, string path, HttpStatusCode status, string json, params (string Name, string Value)[] headers) =>
        On(method, path, _ => Json(json, status, headers));

    /// <summary>Requests to <paramref name="path"/> fail at the transport level (offline, DNS, timeout…).</summary>
    public FakeGitHubApi OnFailure(HttpMethod method, string path, Func<Exception> exception) =>
        On(method, path, _ => Task.FromException<HttpResponseMessage>(exception()));

    /// <summary>Every request fails at the transport level.</summary>
    public FakeGitHubApi FailAllWith(Func<Exception> exception)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Put, HttpMethod.Delete })
        {
            OnFailure(method, "*", exception);
        }

        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Empty(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, Encoding.UTF8.GetString(raw), raw, headers);
        Requests.Enqueue(recorded);

        Route? route;
        lock (_gate)
        {
            route = _routes.LastOrDefault(r => r.Method == request.Method && (r.Path == "*" || string.Equals(r.Path, recorded.Path, StringComparison.Ordinal)));
        }

        var response = route is null
            ? Json("""{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""", HttpStatusCode.NotFound)
            : await route.Respond(recorded);
        response.RequestMessage = request;
        return response;
    }

    private sealed record Route(HttpMethod Method, string Path, Func<RecordedRequest, Task<HttpResponseMessage>> Respond);
}
