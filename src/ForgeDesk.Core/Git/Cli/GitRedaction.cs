using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Masks credentials before text is shown or logged: user info in URLs ("https://user:token@host/…"),
/// Authorization header values (git hands ForgeDesk's token to git through its environment, which
/// hooks inherit and may print) and GitHub tokens.
/// </summary>
internal static partial class GitRedaction
{
    public const string Mask = "***";

    /// <summary>Replaces every credential found in <paramref name="text"/> with <see cref="Mask"/>.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = text.Contains('@', StringComparison.Ordinal) ? UrlUserInfo().Replace(text, "${scheme}" + Mask + "@") : text;
        redacted = AuthorizationValue().Replace(redacted, "${prefix}" + Mask);
        return GitHubToken().Replace(redacted, Mask);
    }

    public static string RedactCommand(IEnumerable<string> arguments) =>
        "git " + string.Join(' ', arguments.Select(a => Quote(Redact(a))));

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    [GeneratedRegex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s""']+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    /// <summary>"Authorization: basic …" / "bearer …" / "token …", as in an http.extraHeader value or a trace.</summary>
    [GeneratedRegex(@"(?<prefix>\bauthorization\s*[:=]\s*(?:basic|bearer|token)\s+)[^\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationValue();

    /// <summary>Personal access, OAuth, app and fine-grained GitHub tokens.</summary>
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();
}
