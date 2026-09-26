using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Git;

/// <summary>Masks credentials embedded in URLs ("https://user:token@host/…") before text is shown or logged.</summary>
internal static partial class GitRedaction
{
    public const string Mask = "***";

    /// <summary>Replaces the user-info part of every URL in <paramref name="text"/> (a token may be the user name alone).</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('@', StringComparison.Ordinal) ? UrlUserInfo().Replace(text, "${scheme}" + Mask + "@") : text;
    }

    public static string RedactCommand(IEnumerable<string> arguments) =>
        "git " + string.Join(' ', arguments.Select(a => Quote(Redact(a))));

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    [GeneratedRegex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s""']+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();
}
