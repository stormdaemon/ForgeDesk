using System.Globalization;
using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Builds a single-hunk patch that "git apply --cached" accepts, from a parsed <see cref="FileDiff"/>
/// and one of its hunks (or a hunk the caller trimmed down: counts are recomputed from the lines).
/// </summary>
internal static class GitPatchBuilder
{
    private static readonly string[] KeptHeaderPrefixes =
        ["diff --git ", "old mode ", "new mode ", "new file mode ", "deleted file mode ", "index ", "--- ", "+++ "];

    public static string Build(FileDiff diff, DiffHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(hunk);
        Validate(diff, hunk);

        var patch = new StringBuilder();
        foreach (var header in HeaderFor(diff))
        {
            patch.Append(header).Append('\n');
        }

        var oldCount = hunk.Lines.Count(l => l.Kind is DiffLineKind.Context or DiffLineKind.Removed);
        var newCount = hunk.Lines.Count(l => l.Kind is DiffLineKind.Context or DiffLineKind.Added);
        patch.Append(CultureInfo.InvariantCulture, $"@@ -{hunk.OldStart},{oldCount} +{hunk.NewStart},{newCount} @@\n");

        foreach (var line in hunk.Lines)
        {
            patch.Append(line.Kind switch
            {
                DiffLineKind.Context => ' ',
                DiffLineKind.Added => '+',
                DiffLineKind.Removed => '-',
                _ => '\\',
            });

            if (line.Kind == DiffLineKind.NoNewlineMarker)
            {
                patch.Append(" No newline at end of file\n");
                continue;
            }

            patch.Append(line.Text);
            if (line.HasCarriageReturn)
            {
                patch.Append('\r');
            }

            patch.Append('\n');
        }

        return patch.ToString();
    }

    private static void Validate(FileDiff diff, DiffHunk hunk)
    {
        if (diff.IsBinary || diff.IsTooLarge)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Individual changes can't be staged in this file.",
                "Stage or unstage the whole file instead.");
        }

        if (hunk.Header.StartsWith("@@@", StringComparison.Ordinal) || diff.HeaderLines.FirstOrDefault()?.StartsWith("diff --cc", StringComparison.Ordinal) == true)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Changes in a conflicted file can't be staged one by one.",
                "Resolve the conflict first, then stage the whole file.");
        }

        if (!hunk.Lines.Any(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed))
        {
            throw ForgeException.InvalidInput("This part of the file has no changes to stage.");
        }

        // U+FFFD means the diff had bytes that aren't UTF-8 (a legacy code page): rebuilding the patch
        // from text would silently change them, so refuse rather than corrupt the file.
        if (hunk.Lines.Any(l => l.Text.Contains('�', StringComparison.Ordinal)))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "This file isn't UTF-8 text, so its changes can't be staged one by one.",
                "Stage or unstage the whole file instead.");
        }
    }

    /// <summary>
    /// Git's own header lines when possible. Renames and copies are rewritten as an in-place change of
    /// the new path: applying a hunk must not move the file back and forth in the index.
    /// </summary>
    private static IEnumerable<string> HeaderFor(FileDiff diff)
    {
        var gitHeader = diff.HeaderLines.Where(l => KeptHeaderPrefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal))).ToList();
        var hasSides = gitHeader.Any(l => l.StartsWith("--- ", StringComparison.Ordinal)) && gitHeader.Any(l => l.StartsWith("+++ ", StringComparison.Ordinal));
        if (diff.OldPath is null && hasSides)
        {
            return gitHeader;
        }

        var path = diff.Path;
        var header = new List<string> { $"diff --git {QuotePath("a/" + path, false)} {QuotePath("b/" + path, false)}" };
        header.AddRange(gitHeader.Where(l => l.StartsWith("new file mode ", StringComparison.Ordinal) || l.StartsWith("deleted file mode ", StringComparison.Ordinal)));
        header.Add("--- " + (diff.IsNewFile ? "/dev/null" : QuotePath("a/" + path, true)));
        header.Add("+++ " + (diff.IsDeletedFile ? "/dev/null" : QuotePath("b/" + path, true)));
        return header;
    }

    // Like git: C-quote special characters; in ---/+++ lines an unquoted name with spaces ends with a TAB.
    private static string QuotePath(string path, bool sideLine) =>
        GitQuoting.NeedsQuoting(path) ? GitQuoting.Quote(path)
        : sideLine && path.Contains(' ', StringComparison.Ordinal) ? path + "\t"
        : path;
}
