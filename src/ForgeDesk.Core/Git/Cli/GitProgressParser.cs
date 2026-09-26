using System.Globalization;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Parses the progress lines git writes to stderr with --progress, for example
/// "Receiving objects:  45% (450/1000), 1.20 MiB | 2.00 MiB/s" or "remote: Counting objects: 12, done.".
/// </summary>
internal static partial class GitProgressParser
{
    public static bool TryParse(string line, out GitProgress progress)
    {
        progress = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ProgressLine().Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        int? percent = match.Groups["percent"].Success
            ? Math.Clamp(int.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture), 0, 100)
            : null;
        var stage = match.Groups["stage"].Value.Trim();
        var message = match.Groups["remote"].Success ? $"remote: {match.Groups["body"].Value.Trim()}" : match.Groups["body"].Value.Trim();
        progress = new GitProgress(stage, percent, message);
        return true;
    }

    /// <summary>True for any line that is progress chatter rather than a real message.</summary>
    public static bool IsProgressLine(string line) => TryParse(line, out _);

    // Stage: capitalized words ("Receiving objects", "Resolving deltas", "Updating files"), then either a
    // percentage or a plain count ("Enumerating objects: 5, done.").
    [GeneratedRegex(@"^(?<remote>remote:\s*)?(?<body>(?<stage>[A-Z][a-z]+(?: [a-z]+)*):\s+(?:(?<percent>\d{1,3})%|\d+(?:,|$|\s)).*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();
}
