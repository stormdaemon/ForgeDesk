namespace ForgeDesk.Core.Detection.Parsing;

/// <summary>One meaningful line of a YAML file: indentation, optional list dash, optional "key:" and the rest.</summary>
internal sealed record YamlLine(int Index, int Indent, bool IsListItem, string? Key, string Value)
{
    /// <summary>Indentation of the content (after "- " for list items).</summary>
    public int ContentIndent { get; init; }
}

/// <summary>
/// Indentation-based outline of a YAML document. It does not implement YAML; it recognizes the
/// "key: value" / "- item" structure that manifests (Taskfile, pubspec, compose, workflows) use,
/// which is enough to list keys without pulling a YAML dependency. Comments are removed.
/// </summary>
internal static class YamlOutline
{
    public static IReadOnlyList<YamlLine> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<YamlLine>();
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int? blockScalarIndent = null;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i].TrimEnd();
            var indent = CountIndent(raw);
            var content = StripComment(raw[indent..]).TrimEnd();

            // Skip the body of block scalars ("run: |") so script lines are never read as keys.
            if (blockScalarIndent is { } scalarIndent)
            {
                if (content.Length == 0 || indent > scalarIndent)
                {
                    continue;
                }

                blockScalarIndent = null;
            }

            if (content.Length == 0 || content is "---" or "...")
            {
                continue;
            }

            var isListItem = content == "-" || content.StartsWith("- ", StringComparison.Ordinal);
            var contentIndent = indent;
            if (isListItem)
            {
                var afterDash = content.Length > 1 ? content[1..] : string.Empty;
                var extra = CountIndent(afterDash);
                contentIndent = indent + 1 + extra;
                content = afterDash.Trim();
            }

            var (key, value) = SplitKey(content);
            result.Add(new YamlLine(i, indent, isListItem, key, value) { ContentIndent = contentIndent });

            if (key is not null && (value.StartsWith('|') || value.StartsWith('>')))
            {
                blockScalarIndent = indent;
            }
        }

        return result;
    }

    /// <summary>Value of a key at the root of the document (quotes removed), or null.</summary>
    public static string? TopLevelScalar(IReadOnlyList<YamlLine> lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.Indent == 0 && !l.IsListItem && string.Equals(l.Key, key, StringComparison.Ordinal));
        return line is null || line.Value.Length == 0 ? null : Unquote(line.Value);
    }

    /// <summary>Position of a root-level key, or -1.</summary>
    public static int FindTopLevel(IReadOnlyList<YamlLine> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Indent == 0 && !lines[i].IsListItem && string.Equals(lines[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Every line nested under the line at <paramref name="parentPosition"/>.</summary>
    public static IReadOnlyList<YamlLine> Descendants(IReadOnlyList<YamlLine> lines, int parentPosition)
    {
        if (parentPosition < 0 || parentPosition >= lines.Count)
        {
            return [];
        }

        var parent = lines[parentPosition];
        var result = new List<YamlLine>();
        for (var i = parentPosition + 1; i < lines.Count; i++)
        {
            var line = lines[i];

            // A list may sit at the same indentation as its parent key ("key:\n- item").
            if (line.Indent < parent.ContentIndent || (line.Indent == parent.ContentIndent && !line.IsListItem))
            {
                break;
            }

            if (line.Indent == parent.ContentIndent && line.IsListItem && result.Count > 0 && !result[0].IsListItem)
            {
                break;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>The first level of lines nested under the line at <paramref name="parentPosition"/>.</summary>
    public static IReadOnlyList<YamlLine> Children(IReadOnlyList<YamlLine> lines, int parentPosition)
    {
        var descendants = Descendants(lines, parentPosition);
        if (descendants.Count == 0)
        {
            return [];
        }

        var level = descendants[0].Indent;
        return descendants.Where(l => l.Indent == level).ToList();
    }

    /// <summary>Keys directly under a root-level key ("tasks:", "dependencies:"…).</summary>
    public static IReadOnlyList<string> ChildKeys(IReadOnlyList<YamlLine> lines, string topLevelKey)
    {
        var position = FindTopLevel(lines, topLevelKey);
        return position < 0
            ? []
            : Children(lines, position).Where(l => !l.IsListItem && l.Key is not null).Select(l => l.Key!).ToList();
    }

    public static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && (trimmed[0] == '"' || trimmed[0] == '\'') && trimmed[^1] == trimmed[0]
            ? trimmed[1..^1]
            : trimmed;
    }

    private static (string? Key, string Value) SplitKey(string content)
    {
        if (content.Length == 0 || content[0] is '{' or '[' or '|' or '>' or '&' or '*' or '!')
        {
            return (null, content);
        }

        char? quote = null;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if ((c is '"' or '\'') && i == 0)
            {
                quote = c;
                continue;
            }

            if (c == ':' && (i == content.Length - 1 || content[i + 1] is ' ' or '\t'))
            {
                var key = Unquote(content[..i]);
                return key.Length == 0 ? (null, content) : (key, content[(i + 1)..].Trim());
            }
        }

        return (null, content);
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count;
    }

    private static string StripComment(string content)
    {
        char? quote = null;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'' && (i == 0 || content[i - 1] is ' ' or '\t' or ':' or '[' or '{' or ',' or '-'))
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || content[i - 1] is ' ' or '\t'))
            {
                return content[..i];
            }
        }

        return content;
    }
}
