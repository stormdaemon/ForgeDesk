using System.Text;
using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Files;

/// <summary>
/// The compiled form of a <see cref="ContentSearchQuery"/>: validates the pattern (so both engines
/// reject the same input with the same message), finds match positions inside a line and shapes
/// what a result shows.
/// </summary>
internal sealed class SearchPattern
{
    /// <summary>Longest line text returned; longer lines (minified files) are cut around the match.</summary>
    public const int MaxLineTextLength = 500;

    private const int ContextBeforeMatch = 120;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private SearchPattern(ContentSearchQuery query, Regex regex)
    {
        Query = query;
        Regex = regex;
    }

    public ContentSearchQuery Query { get; }

    public Regex Regex { get; }

    public static SearchPattern Create(ContentSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(query.Pattern))
        {
            throw ForgeException.InvalidInput("Type something to search for.");
        }

        if (query.Pattern.Contains('\n', StringComparison.Ordinal))
        {
            throw ForgeException.InvalidInput("Search works line by line: remove the line break from the search text.");
        }

        var pattern = query.IsRegex ? query.Pattern : Regex.Escape(query.Pattern);
        if (query.WholeWord)
        {
            pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        }

        var options = RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new SearchPattern(query, new Regex(pattern, options, MatchTimeout));
        }
        catch (ArgumentException ex)
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"The search pattern is not a valid regular expression: {ex.Message}",
                "Fix the expression, or turn off regular expressions to search for the text as typed.");
        }
    }

    /// <summary>Position (0-based char index) and length of the first match in a line, or null.</summary>
    public (int Index, int Length)? FindFirst(string line)
    {
        try
        {
            var match = Regex.Match(line);
            return match.Success ? (match.Index, match.Length) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the result for a matching line. <paramref name="fallbackByteColumn"/> is git's 1-based
    /// byte column, used when the .NET pattern cannot locate the match itself.
    /// </summary>
    public ContentMatch CreateMatch(string relativePath, int lineNumber, string lineText, int? fallbackByteColumn = null)
    {
        lineText = lineText.TrimEnd('\r', '\n');
        int index;
        int length;
        if (FindFirst(lineText) is { } found)
        {
            (index, length) = found;
        }
        else
        {
            index = fallbackByteColumn is { } byteColumn ? ByteColumnToCharIndex(lineText, byteColumn) : 0;
            length = 0;
        }

        if (lineText.Length <= MaxLineTextLength)
        {
            return new ContentMatch(relativePath, lineNumber, index + 1, length, lineText);
        }

        var start = Math.Max(0, index - ContextBeforeMatch);
        if (start > 0 && char.IsLowSurrogate(lineText[start]))
        {
            start--;
        }

        var end = Math.Min(lineText.Length, start + MaxLineTextLength);
        if (end < lineText.Length && char.IsHighSurrogate(lineText[end - 1]))
        {
            end--;
        }

        var builder = new StringBuilder(end - start + 2);
        if (start > 0)
        {
            builder.Append('…');
        }

        builder.Append(lineText, start, end - start);
        if (end < lineText.Length)
        {
            builder.Append('…');
        }

        var column = index - start + (start > 0 ? 1 : 0) + 1;
        return new ContentMatch(relativePath, lineNumber, column, Math.Min(length, end - index), builder.ToString());
    }

    internal static int ByteColumnToCharIndex(string text, int byteColumn)
    {
        var targetBytes = Math.Max(0, byteColumn - 1);
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (bytes >= targetBytes)
            {
                return i;
            }

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
            {
                bytes += 4;
                i++;
            }
            else
            {
                bytes += text[i] switch
                {
                    < '\u0080' => 1,
                    < 'ࠀ' => 2,
                    _ => 3,
                };
            }
        }

        return text.Length;
    }
}
