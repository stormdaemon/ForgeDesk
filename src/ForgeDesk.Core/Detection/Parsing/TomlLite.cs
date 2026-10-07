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

                // Counted per appended line (a delimiter cannot span a line break), so the scan stays linear.
                var delimiters = CountOccurrences(value, delimiter);
                while (delimiters < 2 && i + 1 < lines.Length)
                {
                    var next = lines[++i];
                    builder.Append('\n').Append(next);
                    delimiters += CountOccurrences(next, delimiter);
                }

                value = builder.ToString();
            }
            else if (value.StartsWith('['))
            {
                var builder = new StringBuilder(value);

                // The bracket scan resumes where it stopped (depth and quote state carried over), so it stays linear.
                var scan = default(BracketScan);
                scan.Feed(value);
                while (scan.Depth > 0 && i + 1 < lines.Length)
                {
                    var next = StripComment(lines[++i]);
                    builder.Append('\n');
                    scan.Feed("\n");
                    builder.Append(next);
                    scan.Feed(next);
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

    /// <summary>Bracket depth of text fed piece by piece, ignoring brackets inside quoted strings.</summary>
    private struct BracketScan
    {
        private char? _quote;
        private bool _escaped;

        public int Depth { get; private set; }

        public void Feed(string text)
        {
            foreach (var c in text)
            {
                if (_escaped)
                {
                    _escaped = false;
                }
                else if (_quote is not null)
                {
                    if (c == '\\' && _quote == '"')
                    {
                        _escaped = true;
                    }
                    else if (c == _quote)
                    {
                        _quote = null;
                    }
                }
                else if (c is '"' or '\'')
                {
                    _quote = c;
                }
                else if (c == '[')
                {
                    Depth++;
                }
                else if (c == ']')
                {
                    Depth--;
                }
            }
        }
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
