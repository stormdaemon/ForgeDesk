using System.Text;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// Tolerant reader for the subset of TOML that dependency manifests use (Cargo.toml,
/// pyproject.toml, libs.versions.toml): tables, dotted/quoted keys, strings, inline tables and
/// multi-line arrays. Values are kept as raw text and decoded on demand. Malformed lines are skipped.
/// </summary>
internal static class TomlLite
{
    /// <summary>A key/value pair with the table it belongs to (both split into unquoted segments).</summary>
    public sealed record Entry(IReadOnlyList<string> Table, IReadOnlyList<string> Key, string Value)
    {
        public string TableName => string.Join('.', Table);
    }

    public static IReadOnlyList<Entry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<Entry>();
        IReadOnlyList<string> table = [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                var isArrayTable = line.StartsWith("[[", StringComparison.Ordinal);
                var close = line.LastIndexOf(isArrayTable ? "]]" : "]", StringComparison.Ordinal);
                var open = isArrayTable ? 2 : 1;
                if (close > open)
                {
                    table = SplitKey(line[open..close]);
                }

                continue;
            }

            var equals = IndexOutsideQuotes(line, '=');
            if (equals <= 0)
            {
                continue;
            }

            var key = SplitKey(line[..equals]);
            var value = line[(equals + 1)..].Trim();

            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                // Multi-line strings never hold dependencies: skip to their end so their lines are not parsed as keys.
                var delimiter = value[..3];
                var rest = value[3..];
                while (!rest.Contains(delimiter, StringComparison.Ordinal) && i + 1 < lines.Length)
                {
                    rest = lines[++i];
                }

                entries.Add(new Entry(table, key, string.Empty));
                continue;
            }

            // Arrays (and TOML 1.1 inline tables) may span several lines.
            while (!IsBalanced(value) && i + 1 < lines.Length)
            {
                value += " " + StripComment(lines[++i]).Trim();
            }

            if (key.Count > 0)
            {
                entries.Add(new Entry(table, key, value));
            }
        }

        return entries;
    }

    /// <summary>Decodes a basic ("…") or literal ('…') string value.</summary>
    public static bool TryGetString(string value, out string result)
    {
        result = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        if (trimmed[0] == '\'')
        {
            var end = trimmed.IndexOf('\'', 1);
            if (end < 0)
            {
                return false;
            }

            result = trimmed[1..end];
            return true;
        }

        if (trimmed[0] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var i = 1; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '"')
            {
                result = builder.ToString();
                return true;
            }

            if (c == '\\' && i + 1 < trimmed.Length)
            {
                var escaped = trimmed[++i];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }

            builder.Append(c);
        }

        return false;
    }

    /// <summary>Keys and raw values of an inline table <c>{ a = "1", b.c = true }</c>; dotted keys are joined with '.'.</summary>
    public static IReadOnlyDictionary<string, string> GetInlineTable(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return result;
        }

        foreach (var pair in SplitTopLevel(trimmed[1..^1]))
        {
            var equals = IndexOutsideQuotes(pair, '=');
            if (equals > 0)
            {
                result[string.Join('.', SplitKey(pair[..equals]))] = pair[(equals + 1)..].Trim();
            }
        }

        return result;
    }

    /// <summary>Raw elements of an array value <c>[ "a", "b" ]</c>.</summary>
    public static IReadOnlyList<string> GetArray(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? SplitTopLevel(trimmed[1..^1]).Where(e => e.Length > 0).ToList()
            : [];
    }

    /// <summary>String elements of an array value (other elements are ignored).</summary>
    public static IReadOnlyList<string> GetStringArray(string value) =>
        GetArray(value).Select(e => TryGetString(e, out var s) ? s : null).OfType<string>().ToList();

    /// <summary>Splits "a.'b.c'.d" into ["a", "b.c", "d"].</summary>
    public static IReadOnlyList<string> SplitKey(string key)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in key)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '.')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString().Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static bool IsBalanced(string value)
    {
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
        }

        return depth <= 0;
    }

    private static int IndexOutsideQuotes(string text, char target)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripComment(string line)
    {
        var hash = IndexOutsideQuotes(line, '#');
        return hash < 0 ? line : line[..hash];
    }
}
