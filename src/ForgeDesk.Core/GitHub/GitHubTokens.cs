using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.GitHub;

/// <summary>Token format checks and scope warnings. Never log or echo a token.</summary>
internal static partial class GitHubTokens
{
    /// <summary>
    /// Trims what users commonly paste around a token (spaces, quotes, line breaks) and checks
    /// the shape loosely: classic (ghp_), fine-grained (github_pat_), OAuth (gho_), GitHub App
    /// user tokens (ghu_) or a legacy 40-character hex token.
    /// </summary>
    public static string Normalize(string? token)
    {
        var trimmed = token?.Trim().Trim('"', '\'').Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Paste a GitHub token to sign in.",
                "Create one at https://github.com/settings/tokens.");
        }

        if (!TokenShape().IsMatch(trimmed))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "This doesn't look like a GitHub token.",
                "Personal access tokens start with 'ghp_' (classic) or 'github_pat_' (fine-grained). Copy the whole token from https://github.com/settings/tokens.");
        }

        return trimmed;
    }

    /// <summary>
    /// Classic personal access tokens and OAuth tokens carry scopes; fine-grained and GitHub App
    /// tokens use per-repository permissions instead. Unknown shapes (from credential helpers)
    /// count as scoped when GitHub reported scopes.
    /// </summary>
    public static bool UsesScopes(string token, IReadOnlyCollection<string> scopes)
    {
        if (token.StartsWith("github_pat_", StringComparison.Ordinal) || token.StartsWith("ghu_", StringComparison.Ordinal))
        {
            return false;
        }

        return token.StartsWith("ghp_", StringComparison.Ordinal)
            || token.StartsWith("gho_", StringComparison.Ordinal)
            || LegacyToken().IsMatch(token)
            || scopes.Count > 0;
    }

    /// <summary>A warning for the UI when a scoped token lacks "repo" or "workflow"; null otherwise.</summary>
    public static string? DescribeMissingScopes(string token, IReadOnlyList<string> scopes)
    {
        if (!UsesScopes(token, scopes))
        {
            return null;
        }

        var missingRepo = !GitHubScopes.Satisfies(scopes, GitHubScopes.Repo);
        var missingWorkflow = !GitHubScopes.Satisfies(scopes, GitHubScopes.Workflow);
        return (missingRepo, missingWorkflow) switch
        {
            (true, true) => "This token can't access private repositories (missing the 'repo' scope) and can't push changes to GitHub Actions workflow files (missing the 'workflow' scope).",
            (true, false) => "This token can't access private repositories: it lacks the 'repo' scope.",
            (false, true) => "Pushing changes to GitHub Actions workflow files will be rejected: the token lacks the 'workflow' scope.",
            _ => null,
        };
    }

    [GeneratedRegex(@"^(?:(?:ghp|gho|ghu)_[A-Za-z0-9]{20,251}|github_pat_[A-Za-z0-9_]{20,244}|[0-9A-Fa-f]{40})$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    [GeneratedRegex("^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyToken();
}
