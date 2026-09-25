using Octokit;
using Octokit.Internal;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Creates Octokit clients for a given token. All clients share one HTTP transport (connection
/// pooling); a client is only a cheap Connection + credentials wrapper, so building one per
/// token — or per validation attempt — costs nothing.
/// </summary>
internal sealed class GitHubClientFactory : IDisposable
{
    private readonly GitHubClientOptions _options;
    private readonly HttpClientAdapter _transport;
    private readonly Lazy<HttpClient> _uploadClient;
    private readonly SimpleJsonSerializer _serializer = new();

    public GitHubClientFactory(GitHubClientOptions? options = null)
    {
        _options = options ?? new GitHubClientOptions();
        _transport = new HttpClientAdapter(_options.CreateHandler);

        // Asset uploads bypass Octokit, which buffers the whole request body in memory to be
        // able to replay it on redirects — unusable for large files and for progress reporting.
        _uploadClient = new Lazy<HttpClient>(() => new HttpClient(_options.CreateHandler(), disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });
    }

    public string ProductVersion => _options.ProductVersion;

    /// <summary>Plain HTTP client (no timeout: callers bound each request) for streaming uploads.</summary>
    public HttpClient UploadClient => _uploadClient.Value;

    public IJsonSerializer Serializer => _serializer;

    public GitHubClient CreateClient(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var connection = new Connection(
            new ProductHeaderValue(GitHubClientOptions.ProductName, _options.ProductVersion),
            _options.BaseAddress,
            new InMemoryCredentialStore(new Credentials(token, AuthenticationType.Bearer)),
            _transport,
            _serializer);
        return new GitHubClient(connection);
    }

    public void Dispose()
    {
        _transport.Dispose();
        if (_uploadClient.IsValueCreated)
        {
            _uploadClient.Value.Dispose();
        }
    }
}
