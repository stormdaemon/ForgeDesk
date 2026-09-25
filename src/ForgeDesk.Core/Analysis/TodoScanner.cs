using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Analysis;

/// <summary>
/// Finds TODO/FIXME/HACK/XXX/BUG markers written as the first word of a comment
/// ("// TODO: …", "# FIXME(ana) …", "&lt;!-- HACK …"). Requiring the comment prefix and the
/// first-word position keeps prose ("fix the bug") and identifiers (todoList) out.
/// </summary>
internal static class TodoScanner
{
    private const int MaxLineLength = 1000;

    private static readonly ConcurrentDictionary<CommentSyntax, Regex> Patterns = new();

    /// <summary>Cheap pre-filter run on every line before the regular expression.</summary>
    public static bool MayContainMarker(ReadOnlySpan<char> line) =>
        line.Contains("TODO", StringComparison.OrdinalIgnoreCase)
        || line.Contains("FIXME", StringComparison.OrdinalIgnoreCase)
        || line.Contains("HACK", StringComparison.Ordinal)
        || line.Contains("XXX", StringComparison.Ordinal)
        || line.Contains("BUG", StringComparison.Ordinal);

    /// <summary>The marker on <paramref name="line"/>, or null.</summary>
    public static (string Tag, string Text)? Match(string line, CommentSyntax comments)
    {
        // Comments are short; very long lines are data or minified code.
        if (comments == CommentSyntax.None || line.Length > MaxLineLength || !MayContainMarker(line))
        {
            return null;
        }

        Match match;
        try
        {
            match = Patterns.GetOrAdd(comments, Build).Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        return (match.Groups["tag"].Value.ToUpperInvariant(), CleanText(match.Groups["text"].Value));
    }

    private static string CleanText(string text)
    {
        var cleaned = text.Trim();
        foreach (var closer in (string[])["*/", "-->", "*@", "#>"])
        {
            if (cleaned.EndsWith(closer, StringComparison.Ordinal))
            {
                cleaned = cleaned[..^closer.Length].TrimEnd();
            }
        }

        return cleaned.Length <= AnalysisLimits.MaxTodoTextLength
            ? cleaned
            : string.Concat(cleaned.AsSpan(0, AnalysisLimits.MaxTodoTextLength - 1), "…");
    }

    private static Regex Build(CommentSyntax comments)
    {
        var prefixes = new List<string>();
        if (comments.HasFlag(CommentSyntax.DoubleSlash))
        {
            prefixes.Add("//+!?");
        }

        if (comments.HasFlag(CommentSyntax.SlashStar))
        {
            prefixes.Add(@"/\*+!?");
            prefixes.Add(@"^\s*\*+");
        }

        if (comments.HasFlag(CommentSyntax.Hash))
        {
            prefixes.Add("#+");
        }

        if (comments.HasFlag(CommentSyntax.DoubleDash))
        {
            prefixes.Add("--+");
        }

        if (comments.HasFlag(CommentSyntax.Markup))
        {
            prefixes.Add("<!--");
            prefixes.Add(@"@\*");
        }

        if (comments.HasFlag(CommentSyntax.Semicolon))
        {
            prefixes.Add(";+");
        }

        if (comments.HasFlag(CommentSyntax.Percent))
        {
            prefixes.Add("%+");
        }

        if (comments.HasFlag(CommentSyntax.Apostrophe))
        {
            prefixes.Add("'");
        }

        if (comments.HasFlag(CommentSyntax.Rem))
        {
            prefixes.Add(@"^\s*@?(?i:rem)\b");
            prefixes.Add("::");
        }

        // TODO and FIXME are accepted in any case ("// todo:"); HACK, XXX and BUG only in capitals,
        // because "hack" and "bug" are ordinary words at the start of a comment.
        var pattern = new StringBuilder()
            .Append("(?:").AppendJoin('|', prefixes).Append(')')
            .Append(@"[ \t]*@?(?<tag>(?i:TODO|FIXME)|HACK|XXX|BUG)(?![A-Za-z0-9_])")
            .Append(@"(?:[ \t]*\([^)]*\))?[ \t]*[:\-]?(?<text>.*)$")
            .ToString();
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(250));
    }
}
