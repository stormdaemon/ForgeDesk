namespace ForgeDesk.Core.Git;

/// <summary>
/// Maps lines selected in the diff of an untracked file (built from its raw bytes) onto the diff git
/// produces once the file is "intent to add", which went through clean filters and end-of-line
/// conversion. Patches built from git's lines store what "git add" would store.
/// </summary>
internal static class UntrackedHunkSelection
{
    /// <summary>True when <paramref name="hunk"/> only adds lines (what any selection of an untracked file is).</summary>
    public static bool IsAdditionOnly(DiffHunk hunk) =>
        hunk.Lines.All(l => l.Kind is DiffLineKind.Added or DiffLineKind.NoNewlineMarker)
        && hunk.Lines.Any(l => l.Kind == DiffLineKind.Added);

    /// <summary>True when <paramref name="hunk"/> adds every line of the untracked file.</summary>
    public static bool SelectsWholeFile(FileDiff raw, DiffHunk hunk) =>
        IsAdditionOnly(hunk) && hunk.Lines.Count(l => l.Kind == DiffLineKind.Added) == AddedLines(raw).Count;

    /// <summary>
    /// The selection rebuilt from <paramref name="fresh"/>'s lines, or null when git's content isn't the
    /// raw content up to line endings (a filter changed it, or the file changed since it was read).
    /// </summary>
    public static (FileDiff Source, DiffHunk Hunk)? Convert(FileDiff raw, DiffHunk hunk, FileDiff fresh)
    {
        if (!IsAdditionOnly(hunk) || fresh.IsBinary || fresh.IsTooLarge || fresh.Hunks is not [var freshHunk])
        {
            return null;
        }

        var rawAdded = AddedLines(raw);
        var freshAdded = freshHunk.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        if (rawAdded.Count != freshAdded.Count
            || !rawAdded.Zip(freshAdded).All(p => string.Equals(p.First.Text, p.Second.Text, StringComparison.Ordinal))
            || freshHunk.Lines.Any(l => l.Kind is DiffLineKind.Context or DiffLineKind.Removed))
        {
            return null;
        }

        var selected = hunk.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.NewLineNumber).ToHashSet();
        if (selected.Contains(null) || !selected.All(n => freshAdded.Any(f => f.NewLineNumber == n)))
        {
            return null;
        }

        var lines = new List<DiffLine>();
        var previousKept = false;
        foreach (var line in freshHunk.Lines)
        {
            if (line.Kind == DiffLineKind.Added)
            {
                previousKept = selected.Contains(line.NewLineNumber);
                if (previousKept)
                {
                    lines.Add(line);
                }
            }
            else if (line.Kind == DiffLineKind.NoNewlineMarker && previousKept)
            {
                lines.Add(line);
            }
        }

        return (fresh with { Path = raw.Path }, freshHunk with { Lines = lines });
    }

    private static List<DiffLine> AddedLines(FileDiff diff) =>
        diff.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).ToList();
}
