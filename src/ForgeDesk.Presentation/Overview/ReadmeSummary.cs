using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Presentation.Overview;

/// <summary>
/// Extracts the first meaningful paragraph of a README as plain text: skips front matter, headings,
/// badges, images, HTML blocks, code fences, tables and rules, then strips inline Markdown.
/// </summary>
public static partial class ReadmeSummary
{
    public const int MaxLength = 420;

    public static string? Extract(string? markdown, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            // YAML front matter.
            var end = Array.FindIndex(lines, 1, l => l.Trim() is "---" or "...");
            index = end < 0 ? 0 : end + 1;
        }

        var paragraph = new List<string>();
        var inFence = false;
        var inHtmlComment = false;
        for (; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (inHtmlComment)
            {
                inHtmlComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                inHtmlComment = !line.Contains("-->", StringComparison.Ordinal);
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (line.Length == 0)
            {
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (IsSkippable(line, lines, index))
            {
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            var text = StripInline(line);
            if (text.Length == 0)
            {
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            paragraph.Add(text);
        }

        if (paragraph.Count == 0)
        {
            return null;
        }

        var result = WhitespaceRegex().Replace(string.Join(' ', paragraph), " ").Trim();
        return Truncate(result, maxLength);
    }

    private static bool IsSkippable(string line, string[] lines, int index)
    {
        if (line.StartsWith('#') || line.StartsWith('>') || line.StartsWith('|') || line.StartsWith("[!", StringComparison.Ordinal)
            || line.StartsWith("![", StringComparison.Ordinal) || line.StartsWith("[![", StringComparison.Ordinal))
        {
            return true;
        }

        // Rules ("---", "***") and setext heading underlines.
        if (RuleRegex().IsMatch(line) || SetextRegex().IsMatch(line))
        {
            return true;
        }

        // A setext heading: the next line underlines this one.
        if (index + 1 < lines.Length && SetextRegex().IsMatch(lines[index + 1].Trim()))
        {
            return true;
        }

        // List items and link reference definitions are not a description.
        if (ListRegex().IsMatch(line) || LinkDefinitionRegex().IsMatch(line))
        {
            return true;
        }

        // HTML blocks (<p align="center">, <img …>, <div>…), unless the tag only wraps prose.
        return line.StartsWith('<') && (StripInline(line).Length == 0 || HtmlHeadingRegex().IsMatch(line));
    }

    internal static string StripInline(string line)
    {
        var text = ImageRegex().Replace(line, string.Empty);
        text = LinkRegex().Replace(text, "$1");
        text = AutoLinkRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = EmphasisRegex().Replace(text, "$2");
        text = text.Replace("`", string.Empty, StringComparison.Ordinal);
        text = System.Net.WebUtility.HtmlDecode(text);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Max(0, maxLength - 1));
        if (cut < maxLength / 2)
        {
            cut = maxLength - 1;
        }

        var builder = new StringBuilder(text, 0, cut, cut + 1);
        return builder.ToString().TrimEnd(',', ';', ':', ' ', '.') + "…";
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<(https?://[^>]+)>")]
    private static partial Regex AutoLinkRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^(=+|-+)$")]
    private static partial Regex SetextRegex();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^<h[1-6][\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"^\[[^\]]+\]:\s*\S+")]
    private static partial Regex LinkDefinitionRegex();
}
