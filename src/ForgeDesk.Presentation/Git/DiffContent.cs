using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Git;

/// <summary>Content comparison of diffs, so a refresh that changes nothing keeps the diff (and its scroll position) on screen.</summary>
public static class DiffContent
{
    public static bool Same(FileDiff? a, FileDiff? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (!string.Equals(a.Path, b.Path, StringComparison.Ordinal)
            || !string.Equals(a.OldPath, b.OldPath, StringComparison.Ordinal)
            || a.IsBinary != b.IsBinary
            || a.IsNewFile != b.IsNewFile
            || a.IsDeletedFile != b.IsDeletedFile
            || a.IsTooLarge != b.IsTooLarge
            || a.Hunks.Count != b.Hunks.Count
            || !a.HeaderLines.SequenceEqual(b.HeaderLines, StringComparer.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < a.Hunks.Count; i++)
        {
            var left = a.Hunks[i];
            var right = b.Hunks[i];
            if (!string.Equals(left.Header, right.Header, StringComparison.Ordinal) || !left.Lines.SequenceEqual(right.Lines))
            {
                return false;
            }
        }

        return true;
    }
}
