using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection.Parsing;

/// <summary>A key/value pair of a TOML table; <see cref="RawValue"/> keeps the value text as written.</summary>
internal sealed record TomlEntry(string Key, string RawValue);

/// <summary>
/// Tolerant, read-only view of a TOML file, good enough for manifests (Cargo.toml, pyproject.toml,
/// Pipfile): table headers, keys, strings and arrays of strings. Malformed lines are skipped.
/// </summary>
internal sealed partial class TomlLite
{
    private readonly Dictionary<string, List<TomlEntry>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tableOrder = [];

    private TomlLite()
    {
        GetOrAddTable(string.Empty);
    }

    /// <summary>Table names in file order ("" = root, "dependencies", "tool.poetry", "bin" for [[bin]]…).</summary>
    public IReadOnlyList<string> Tables => _tableOrder;

    public static TomlLite Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var document = new TomlLite();
        var current = document.GetOrAddTable(string.Empty);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                var header = ParseHeader(line);
                if (header is not null)
                {
                    current = document.GetOrAddTable(header);
                }

                continue;
            }

            var equals = IndexOutsideQuotes(line, '=');
            if (equals <= 0)
            {
                continue;
            }

            var key = NormalizeKey(line[..equals]);
            var value = line[(equals + 1)..].Trim();

            // Multi-line arrays and strings continue until brackets balance / the string closes.
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                var builder = new StringBuilder(value);
                while (CountOccurrences(builder.ToString(), delimiter) < 2 && i + 1 < lines.Length)
                {
                    builder.Append('\n').Append(lines[++i]);
                }

                value = builder.ToString();
            }
            else if (value.StartsWith('['))
            {
                var builder = new StringBuilder(value);
                while (BracketBalance(builder.ToString()) > 0 && i + 1 < lines.Length)
                {
                    builder.Append('\n').Append(StripComment(lines[++i]));
                }

                value = builder.ToString();
            }

            if (key.Length > 0)
            {
                current.Add(new TomlEntry(key, value));
            }
        }

        return document;
    }

    public bool HasTable(string name) => _tables.ContainsKey(name);

    /// <summary>True when a table or one of its sub-tables exists ("tool.poetry" matches "tool.poetry.dependencies").</summary>
    public bool HasTableOrChild(string name) =>
        _tableOrder.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase) || t.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<TomlEntry> Entries(string table) =>
        _tables.TryGetValue(table, out var entries) ? entries : [];

    /// <summary>Keys of a table (first dotted segment, so "tokio.workspace = true" yields "tokio").</summary>
    public IEnumerable<string> KeysOf(string table) =>
        Entries(table).Select(e => FirstSegment(e.Key)).Distinct(StringComparer.OrdinalIgnoreCase);

    public string? GetString(string table, string key)
    {
        var entry = Entries(table).LastOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : Unquote(entry.RawValue);
    }

    public IReadOnlyList<string> GetStringArray(string table, string key)
    {
        var entry = Entries(table).LastOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return entry is null ? [] : StringsIn(entry.RawValue);
    }

    /// <summary>Every quoted string inside a raw value (arrays of strings, inline tables…).</summary>
    public static IReadOnlyList<string> StringsIn(string rawValue) =>
        QuotedString().Matches(rawValue).Select(m => m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["s"].Value).ToList();

    public static string Unquote(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 6 && (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal)))
        {
            return value[3..^3].Trim('\n');
        }

        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
        {
            return value[1..^1];
        }

        return value;
    }

    private List<TomlEntry> GetOrAddTable(string name)
    {
        if (!_tables.TryGetValue(name, out var entries))
        {
            entries = [];
            _tables[name] = entries;
            _tableOrder.Add(name);
        }

        return entries;
    }

    private static string? ParseHeader(string line)
    {
        var isArray = line.StartsWith("[[", StringComparison.Ordinal);
        var close = isArray ? line.LastIndexOf("]]", StringComparison.Ordinal) : line.LastIndexOf(']');
        var start = isArray ? 2 : 1;
        if (close < start)
        {
            return null;
        }

        return NormalizeKey(line[start..close]);
    }

    /// <summary>Dotted key with quotes and blanks removed: <c>target.'cfg(windows)'.dependencies</c> → <c>target.cfg(windows).dependencies</c>.</summary>
    private static string NormalizeKey(string key)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        char? quote = null;
        foreach (var c in key.Trim())
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    builder.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '.')
            {
                parts.Add(builder.ToString().Trim());
                builder.Clear();
            }
            else
            {
                builder.Append(c);
            }
        }

        parts.Add(builder.ToString().Trim());
        return string.Join('.', parts);
    }

    private static string FirstSegment(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? key : key[..dot];
    }

    private static string StripComment(string line)
    {
        var index = IndexOutsideQuotes(line, '#');
        return index < 0 ? line : line[..index];
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

    private static int BracketBalance(string text)
    {
        var depth = 0;
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
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
        }

        return depth;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [GeneratedRegex("\"(?<d>(?:[^\"\\\\\\n]|\\\\.)*)\"|'(?<s>[^'\\n]*)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedString();
}
