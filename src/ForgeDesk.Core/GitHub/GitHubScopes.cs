namespace ForgeDesk.Core.GitHub;

/// <summary>OAuth scope helpers for classic tokens (fine-grained tokens have no scopes).</summary>
internal static class GitHubScopes
{
    public const string Repo = "repo";
    public const string Workflow = "workflow";

    // A scope grants its narrower "children": holding "repo" satisfies an endpoint asking for "public_repo".
    private static readonly Dictionary<string, string> Parents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public_repo"] = "repo",
        ["repo:status"] = "repo",
        ["repo_deployment"] = "repo",
        ["repo:invite"] = "repo",
        ["security_events"] = "repo",
        ["read:org"] = "write:org",
        ["write:org"] = "admin:org",
        ["read:public_key"] = "write:public_key",
        ["write:public_key"] = "admin:public_key",
        ["read:repo_hook"] = "write:repo_hook",
        ["write:repo_hook"] = "admin:repo_hook",
        ["read:user"] = "user",
        ["user:email"] = "user",
        ["user:follow"] = "user",
        ["read:packages"] = "write:packages",
        ["read:discussion"] = "write:discussion",
        ["read:gpg_key"] = "write:gpg_key",
        ["write:gpg_key"] = "admin:gpg_key",
    };

    /// <summary>Parses a scope header value ("repo, workflow, read:org").</summary>
    public static IReadOnlyList<string> Parse(string? header) =>
        string.IsNullOrWhiteSpace(header)
            ? []
            : header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when <paramref name="granted"/> contains <paramref name="required"/> or a broader scope implying it.</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required)
    {
        var set = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
        for (var scope = required; scope is not null; scope = Parents.GetValueOrDefault(scope))
        {
            if (set.Contains(scope))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first scope GitHub accepts for an endpoint that the token doesn't hold (directly or
    /// through a broader scope), or null when nothing is missing. <paramref name="granted"/> is
    /// null when GitHub sent no X-OAuth-Scopes header (fine-grained tokens): scopes don't apply.
    /// </summary>
    public static string? FindMissing(IReadOnlyList<string> accepted, IReadOnlyList<string>? granted)
    {
        if (accepted.Count == 0 || granted is null)
        {
            return null;
        }

        return accepted.Any(a => Satisfies(granted, a)) ? null : accepted[0];
    }
}
