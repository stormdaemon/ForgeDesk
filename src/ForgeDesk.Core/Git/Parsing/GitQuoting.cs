using System.Text;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Git's C-style path quoting ("tab\tname.txt", "we\"ird.txt", octal escapes for raw bytes), used in
/// diff headers and non -z listings when a path contains special characters.
/// </summary>
internal static class GitQuoting
{
    /// <summary>Unquotes a C-quoted token; returns the value unchanged when it isn't quoted.</summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        return UnquoteAt(value, 0, out _) ?? value;
    }

    /// <summary>
    /// Reads a quoted string starting at <paramref name="start"/> (which must be a '"'). Returns null
    /// when it isn't well formed; <paramref name="end"/> is the index just after the closing quote.
    /// </summary>
    public static string? UnquoteAt(string text, int start, out int end)
    {
        end = start;
        if (start >= text.Length || text[start] != '"')
        {
            return null;
        }

        // Escapes can encode raw bytes of a multi-byte UTF-8 sequence, so decode through a byte buffer.
        var bytes = new List<byte>(text.Length);
        var runStart = start + 1;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '"' && c != '\\')
            {
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(text[runStart..i]));
            if (c == '"')
            {
                end = i + 1;
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            runStart = i + 2;
            if (++i >= text.Length)
            {
                return null;
            }

            var escaped = text[i];
            switch (escaped)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 't': bytes.Add(0x09); break;
                case 'n': bytes.Add(0x0A); break;
                case 'v': bytes.Add(0x0B); break;
                case 'f': bytes.Add(0x0C); break;
                case 'r': bytes.Add(0x0D); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case >= '0' and <= '3' when i + 2 < text.Length && IsOctal(text[i + 1]) && IsOctal(text[i + 2]):
                    bytes.Add((byte)(((escaped - '0') << 6) | ((text[i + 1] - '0') << 3) | (text[i + 2] - '0')));
                    i += 2;
                    runStart = i + 1;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    public static bool NeedsQuoting(string path) => path.Any(c => c is '"' or '\\' || c < 0x20 || c == 0x7F);

    /// <summary>Quotes a path the way git does with core.quotepath=false (non-ASCII left as is).</summary>
    public static string Quote(string path)
    {
        if (!NeedsQuoting(path))
        {
            return path;
        }

        var builder = new StringBuilder(path.Length + 8).Append('"');
        foreach (var c in path)
        {
            switch (c)
            {
                case '\a': builder.Append("\\a"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\v': builder.Append("\\v"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case < ' ' or '\x7F':
                    builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    break;
                default: builder.Append(c); break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';
}
