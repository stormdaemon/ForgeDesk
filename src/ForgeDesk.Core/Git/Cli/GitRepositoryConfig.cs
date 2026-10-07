namespace ForgeDesk.Core.Git;

/// <summary>
/// Reads which configuration keys a repository sets itself (.git/config, config.worktree and the
/// files they include). A folder the user downloads or unzips carries that configuration, so
/// ForgeDesk never lets it run programs during automatic reads, nor redirect TLS for a token.
/// </summary>
internal static class GitRepositoryConfig
{
    /// <summary>Lists every key with the scope it comes from (included files report their includer's scope).</summary>
    public static readonly IReadOnlyList<string> ListArguments = ["config", "--show-scope", "--name-only", "-z", "--list"];

    /// <summary>Settings that weaken or redirect HTTPS; a repository setting one of them never receives a token.</summary>
    private static readonly HashSet<string> TransportKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sslverify", "sslcainfo", "sslcapath", "sslbackend", "schannelusesslcainfo", "proxy", "proxysslcainfo", "curloptresolve",
    };

    /// <summary>Names of the keys set in the repository's own (local or worktree) configuration.</summary>
    public static IReadOnlyList<string> ParseRepositoryKeys(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var fields = output.Split('\0');
        var keys = new List<string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var scope = fields[i].Trim('\n', '\r');
            if (scope is "local" or "worktree")
            {
                keys.Add(fields[i + 1]);
            }
        }

        return keys;
    }

    /// <summary>
    /// Overrides that switch off the repository's own filter drivers (filter.&lt;driver&gt;.clean/smudge/process):
    /// git runs them on reads such as status and diff whenever a file's stat data is stale. Drivers from
    /// the user's global or system configuration (Git LFS…) keep working.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FilterOverrides(IEnumerable<string> repositoryKeys) =>
        repositoryKeys
            .Where(IsFilterCommand)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => new KeyValuePair<string, string>(key, string.Empty))
            .ToList();

    /// <summary>True when the repository's own configuration changes how HTTPS connections are made or verified.</summary>
    public static bool OverridesTransport(IEnumerable<string> repositoryKeys) =>
        repositoryKeys.Any(key =>
            (key.StartsWith("http.", StringComparison.OrdinalIgnoreCase) && TransportKeys.Contains(LastSegment(key)))
            || (key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) && LastSegment(key).Equals("proxy", StringComparison.OrdinalIgnoreCase)));

    private static bool IsFilterCommand(string key)
    {
        if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || key.LastIndexOf('.') <= "filter".Length)
        {
            return false;
        }

        var name = LastSegment(key);
        return name.Equals("clean", StringComparison.OrdinalIgnoreCase)
            || name.Equals("smudge", StringComparison.OrdinalIgnoreCase)
            || name.Equals("process", StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string key) => key[(key.LastIndexOf('.') + 1)..];
}
