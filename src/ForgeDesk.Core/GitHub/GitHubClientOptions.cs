using System.Net;
using System.Reflection;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// How ForgeDesk talks HTTP to GitHub. The defaults target github.com; tests replace the
/// handler (canned responses) and the base address.
/// </summary>
internal sealed class GitHubClientOptions
{
    public const string ProductName = "ForgeDesk";

    public static readonly Uri GitHubApiAddress = new("https://api.github.com/");

    public Uri BaseAddress { get; init; } = GitHubApiAddress;

    /// <summary>Creates the transport handler. Called once per client factory.</summary>
    public Func<HttpMessageHandler> CreateHandler { get; init; } = CreateDefaultHandler;

    public string ProductVersion { get; init; } = GetProductVersion();

    /// <summary>
    /// Pooled sockets with a bounded connection lifetime (DNS changes are picked up), the
    /// system proxy with the user's Windows credentials (corporate proxies), and no automatic
    /// redirects: Octokit follows redirects itself so it can keep the Authorization header
    /// on same-host redirects only.
    /// </summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    };

    private static string GetProductVersion()
    {
        var assembly = typeof(GitHubClientOptions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Drop build metadata ("1.0.0+3f2a…"): the User-Agent only needs the version.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
