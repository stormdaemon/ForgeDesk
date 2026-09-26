using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Builds the environment that hands credentials from <see cref="IGitCredentialProvider"/>s to git
/// through GIT_CONFIG_COUNT / GIT_CONFIG_KEY_n / GIT_CONFIG_VALUE_n, so secrets never appear on a
/// command line (visible to every process on the machine) nor in any config file.
/// </summary>
internal static class GitCredentialEnvironment
{
    public const string CountVariable = "GIT_CONFIG_COUNT";
    public const string KeyVariablePrefix = "GIT_CONFIG_KEY_";
    public const string ValueVariablePrefix = "GIT_CONFIG_VALUE_";

    public static async Task<IReadOnlyDictionary<string, string>> BuildAsync(
        IReadOnlyList<IGitCredentialProvider> providers,
        IEnumerable<string> remoteUrls,
        int inheritedConfigCount,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var entries = new List<KeyValuePair<string, string>>();
        if (providers.Count == 0)
        {
            return ToEnvironment(entries, inheritedConfigCount);
        }

        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in remoteUrls)
        {
            var scope = HttpScope(url);
            if (scope is null || !scopes.Add(scope))
            {
                continue;
            }

            var credential = await ResolveAsync(providers, url, logger, cancellationToken).ConfigureAwait(false);
            if (credential is null)
            {
                continue;
            }

            var key = $"http.{scope}.extraHeader";
            // An empty value first clears headers configured elsewhere for this host (a CI checkout, a
            // manual setup…): two Authorization headers make servers reject the request.
            entries.Add(new(key, string.Empty));
            entries.Add(new(key, AuthorizationHeader(credential)));
        }

        return ToEnvironment(entries, inheritedConfigCount);
    }

    public static string AuthorizationHeader(GitCredential credential) =>
        "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));

    /// <summary>The "scheme://host[:port]/" prefix git matches http.&lt;url&gt;.* settings against, or null for non-HTTP remotes.</summary>
    public static string? HttpScope(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        // Authority excludes the user info and default ports, so "https://token@github.com:443/o/r" → "https://github.com/".
        return $"{uri.Scheme}://{uri.Authority}/";
    }

    /// <summary>Number of GIT_CONFIG_* entries ForgeDesk itself inherited; ours are appended after them.</summary>
    public static int InheritedConfigCount(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : 0;

    internal static Dictionary<string, string> ToEnvironment(IReadOnlyList<KeyValuePair<string, string>> entries, int inheritedConfigCount)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entries.Count == 0)
        {
            return environment;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var index = (inheritedConfigCount + i).ToString(CultureInfo.InvariantCulture);
            environment[KeyVariablePrefix + index] = entries[i].Key;
            environment[ValueVariablePrefix + index] = entries[i].Value;
        }

        environment[CountVariable] = (inheritedConfigCount + entries.Count).ToString(CultureInfo.InvariantCulture);
        return environment;
    }

    private static async Task<GitCredential?> ResolveAsync(IReadOnlyList<IGitCredentialProvider> providers, string url, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            try
            {
                var credential = await provider.GetCredentialAsync(url, cancellationToken).ConfigureAwait(false);
                if (credential is not null && !string.IsNullOrEmpty(credential.Password))
                {
                    return credential;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken provider must not block git: its own credential helpers can still authenticate.
                logger.LogWarning(ex, "Git credential provider {Provider} failed", provider.GetType().Name);
            }
        }

        return null;
    }
}
