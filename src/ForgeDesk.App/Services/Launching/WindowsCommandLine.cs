using System.Text;

namespace ForgeDesk.App.Services.Launching;

/// <summary>
/// Builds and parses Windows command lines with the rules of CommandLineToArgvW / the MSVC
/// runtime, which is how virtually every Windows program splits its arguments.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>Quotes one argument so that it survives CommandLineToArgvW unchanged.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote must be doubled, plus one to escape the quote itself.
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Trailing backslashes precede the closing quote, so they are doubled too.
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    /// <summary>Splits a command line into arguments exactly like CommandLineToArgvW (for arguments after argv[0]).</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    i++;
                }

                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', count);
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                // Inside quotes, a doubled quote produces a literal quote.
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }

                inQuotes = !inQuotes;
                hasToken = true;
                i++;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            hasToken = true;
            i++;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
