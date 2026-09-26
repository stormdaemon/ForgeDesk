using System.Text;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection;

/// <summary>
/// Reads the display name and the triggers of a GitHub Actions workflow. The "on:" key comes in
/// scalar ("on: push"), list ("on: [push, pull_request]" or a block list) and map forms
/// ("on:\n  push:\n    branches: [main]"), including flow maps ("on: { push: {}, workflow_dispatch: {} }").
/// </summary>
internal static class WorkflowFileParser
{
    public static WorkflowFile Parse(string relativePath, string text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(text);

        var lines = YamlOutline.Read(text);
        var name = YamlOutline.TopLevelScalar(lines, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(RelativePaths.FileName(relativePath));
        }

        return new WorkflowFile(name.Trim(), relativePath, ReadTriggers(text, lines));
    }

    private static IReadOnlyList<string> ReadTriggers(string text, IReadOnlyList<YamlLine> lines)
    {
        var position = YamlOutline.FindTopLevel(lines, "on");
        if (position < 0)
        {
            // YAML 1.1 parsers turn a bare "on" key into the boolean true; some files are written that way.
            position = YamlOutline.FindTopLevel(lines, "true");
        }

        if (position < 0)
        {
            return [];
        }

        var onLine = lines[position];
        var triggers = new List<string>();
        if (onLine.Value.Length > 0)
        {
            var value = onLine.Value;
            if (value[0] is '[' or '{')
            {
                value = CompleteFlowCollection(text, onLine.Index, value);
                triggers.AddRange(value[0] == '[' ? FlowListItems(value) : FlowMapKeys(value));
            }
            else
            {
                triggers.Add(YamlOutline.Unquote(value));
            }
        }
        else
        {
            foreach (var child in YamlOutline.Children(lines, position))
            {
                if (child.IsListItem)
                {
                    triggers.Add(YamlOutline.Unquote(child.Key ?? child.Value));
                }
                else if (child.Key is not null)
                {
                    triggers.Add(child.Key);
                }
            }
        }

        return triggers
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Appends the following raw lines until the brackets of a flow collection are balanced.</summary>
    private static string CompleteFlowCollection(string text, int lineIndex, string firstValue)
    {
        var builder = new StringBuilder(firstValue);
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var next = lineIndex + 1;
        while (Balance(builder.ToString()) > 0 && next < rawLines.Length)
        {
            builder.Append(' ').Append(rawLines[next++].Trim());
        }

        return builder.ToString();
    }

    private static IEnumerable<string> FlowListItems(string value)
    {
        var inner = value.Trim().TrimStart('[');
        var close = inner.LastIndexOf(']');
        if (close >= 0)
        {
            inner = inner[..close];
        }

        return SplitTopLevel(inner).Select(YamlOutline.Unquote);
    }

    private static IEnumerable<string> FlowMapKeys(string value)
    {
        var inner = value.Trim().TrimStart('{');
        var close = inner.LastIndexOf('}');
        if (close >= 0)
        {
            inner = inner[..close];
        }

        foreach (var entry in SplitTopLevel(inner))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            yield return YamlOutline.Unquote(colon < 0 ? entry : entry[..colon]);
        }
    }

    /// <summary>Splits on commas that are not nested inside brackets or quotes.</summary>
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
                if (c == quote)
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
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static int Balance(string text)
    {
        var depth = 0;
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote)
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

        return depth;
    }
}
