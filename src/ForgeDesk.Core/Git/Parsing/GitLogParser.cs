using System.Globalization;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Custom "git log --format" with ASCII record (0x1E) and unit (0x1F) separators, which never occur in
/// names or messages, so any message parses unambiguously. The raw message (%B) comes last: should it
/// ever contain 0x1F, the extra fields are joined back.
/// </summary>
internal static class GitLogParser
{
    public const string Format = "--format=%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%B";

    private const int FieldCount = 10;

    public static IReadOnlyList<GitCommit> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\x1e'))
        {
            var fields = record.Split('\x1f');
            if (fields.Length < FieldCount || fields[0].Trim().Length == 0)
            {
                continue;
            }

            var (subject, body) = SplitMessage(string.Join('\x1f', fields[9..]));
            commits.Add(new GitCommit
            {
                Sha = fields[0].Trim(),
                Parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Author = new GitSignature(fields[2], fields[3], ParseDate(fields[4])),
                Committer = new GitSignature(fields[5], fields[6], ParseDate(fields[7])),
                Refs = ParseDecorations(fields[8]),
                Subject = subject,
                Body = body,
            });
        }

        return commits;
    }

    /// <summary>
    /// Subject = first line, body = the rest, as GitHub and most tools present a message (git's own %s
    /// would join the whole first paragraph into one line).
    /// </summary>
    public static (string Subject, string Body) SplitMessage(string message)
    {
        var text = message.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0
            ? (text.TrimEnd(), string.Empty)
            : (text[..newline].TrimEnd(), text[(newline + 1)..].TrimStart('\n').TrimEnd());
    }

    /// <summary>"HEAD -> main, origin/main, tag: v1.0" → ["HEAD -> main", "origin/main", "tag: v1.0"].</summary>
    public static IReadOnlyList<string> ParseDecorations(string decorations) =>
        decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue;
}
