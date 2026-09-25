using System.Buffers;

namespace ForgeDesk.Core.Detection;

/// <summary>
/// Builds command lines that run unchanged through cmd.exe on Windows and /bin/sh elsewhere
/// (see <see cref="Processes.ShellCommand"/>).
/// </summary>
internal static class CommandLines
{
    private static readonly SearchValues<char> SafeArgumentChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.:/\\@+=,");

    /// <summary>Quotes an argument when it contains characters a shell would interpret.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.AsSpan().ContainsAnyExcept(SafeArgumentChars))
        {
            return argument;
        }

        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Invokes a script stored in the project, relative to the working directory. cmd.exe reads
    /// "vendor/bin/phpunit" as "vendor" followed by a "/bin" switch, so Windows needs backslashes;
    /// sh needs an explicit "./" to look in the current folder.
    /// </summary>
    public static string ProjectScript(string relativePath) =>
        OperatingSystem.IsWindows()
            ? Quote(relativePath.Replace('/', '\\'))
            : "./" + Quote(relativePath);

    /// <summary>A relative path passed as an argument (tools accept forward slashes on every OS).</summary>
    public static string PathArgument(string relativePath) => Quote(relativePath.Length == 0 ? "." : relativePath);
}
