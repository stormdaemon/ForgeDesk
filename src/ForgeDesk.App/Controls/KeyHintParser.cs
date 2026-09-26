namespace ForgeDesk.App.Controls;

/// <summary>Splits a shortcut ("Ctrl+Shift+P", "Ctrl++", "Alt+Left") into keycap labels.</summary>
internal static class KeyHintParser
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "Ctrl",
        ["Shift"] = "Shift",
        ["Alt"] = "Alt",
        ["Win"] = "Win",
        ["Enter"] = "Enter",
        ["Esc"] = "Esc",
        ["Tab"] = "Tab",
        ["Space"] = "Space",
        ["Del"] = "Del",
        ["Delete"] = "Delete",
        ["Home"] = "Home",
        ["End"] = "End",
        ["Control"] = "Ctrl",
        ["Escape"] = "Esc",
        ["Return"] = "Enter",
        ["Windows"] = "Win",
        ["Left"] = "←",
        ["Right"] = "→",
        ["Up"] = "↑",
        ["Down"] = "↓",
        ["Plus"] = "+",
        ["Comma"] = ",",
        ["Backtick"] = "`",
    };

    public static IReadOnlyList<string> Parse(string? keys)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(keys))
        {
            return result;
        }

        var text = keys.Trim();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            // A '+' separates keys unless it is the key itself ("Ctrl++" or a lone "+").
            if (text[i] == '+' && i > start && text[start..i].Trim().Length > 0)
            {
                result.Add(Display(text[start..i].Trim()));
                start = i + 1;
            }
        }

        var last = text[start..].Trim();
        if (last.Length > 0)
        {
            result.Add(Display(last));
        }

        return result;
    }

    // Single letters and function keys are shown upper-case ("k" → "K", "f5" → "F5").
    private static string Display(string key) =>
        DisplayNames.TryGetValue(key, out var display) ? display
        : key.Length <= 3 ? key.ToUpperInvariant()
        : key;
}
