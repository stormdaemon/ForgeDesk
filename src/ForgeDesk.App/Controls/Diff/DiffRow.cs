using System.Globalization;
using System.Text;
using ForgeDesk.Core.Git;

namespace ForgeDesk.App.Controls.Diff;

public enum DiffRowKind
{
    HunkHeader,
    Context,
    Added,
    Removed,
    NoNewline,
}

/// <summary>
/// One visual row of a <see cref="DiffView"/>. Every string is precomputed so that scrolling a
/// 20 000-line diff only recycles containers and never formats anything.
/// </summary>
public sealed class DiffRow
{
    private DiffRow(int index, DiffRowKind kind, string oldNumber, string newNumber, string marker, string text, DiffHunk hunk)
    {
        Index = index;
        Kind = kind;
        OldNumber = oldNumber;
        NewNumber = newNumber;
        Marker = marker;
        Text = text;
        Hunk = hunk;
    }

    /// <summary>Position in the diff (used to copy selected rows in order).</summary>
    public int Index { get; }

    public DiffRowKind Kind { get; }

    /// <summary>Line number in the old file, or empty.</summary>
    public string OldNumber { get; }

    /// <summary>Line number in the new file, or empty.</summary>
    public string NewNumber { get; }

    /// <summary>"+", "-" or a space; empty for headers.</summary>
    public string Marker { get; }

    /// <summary>Display text: tabs expanded, trailing CR removed, very long lines shortened.</summary>
    public string Text { get; }

    /// <summary>The hunk this row belongs to (the command parameter of hunk actions).</summary>
    public DiffHunk Hunk { get; }

    public bool IsHunkHeader => Kind == DiffRowKind.HunkHeader;

    internal static DiffRow Header(int index, DiffHunk hunk) =>
        new(index, DiffRowKind.HunkHeader, string.Empty, string.Empty, string.Empty, DiffRowBuilder.Clean(hunk.Header), hunk);

    internal static DiffRow Line(int index, DiffLine line, DiffHunk hunk)
    {
        var (kind, marker) = line.Kind switch
        {
            DiffLineKind.Added => (DiffRowKind.Added, "+"),
            DiffLineKind.Removed => (DiffRowKind.Removed, "-"),
            DiffLineKind.NoNewlineMarker => (DiffRowKind.NoNewline, string.Empty),
            _ => (DiffRowKind.Context, " "),
        };

        var text = kind == DiffRowKind.NoNewline ? "No newline at end of file" : DiffRowBuilder.Clean(line.Text);
        return new DiffRow(index, kind, Number(line.OldLineNumber), Number(line.NewLineNumber), marker, text, hunk);
    }

    private static string Number(int? value) => value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>The rows of a diff plus the measurements the view needs for a stable layout.</summary>
public sealed record DiffRows(IReadOnlyList<DiffRow> Rows, int LongestLine, int LineNumberDigits);

/// <summary>Flattens a <see cref="FileDiff"/> into display rows.</summary>
public static class DiffRowBuilder
{
    /// <summary>Longer lines are cut for display (minified files would otherwise explode the layout).</summary>
    public const int MaxDisplayedLineLength = 2000;

    private const int TabSize = 4;

    public static DiffRows Build(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var rows = new List<DiffRow>(diff.Hunks.Sum(h => h.Lines.Count + 1));
        var longest = 0;
        var largestNumber = 0;
        foreach (var hunk in diff.Hunks)
        {
            var header = DiffRow.Header(rows.Count, hunk);
            rows.Add(header);
            longest = Math.Max(longest, header.Text.Length);
            foreach (var line in hunk.Lines)
            {
                var row = DiffRow.Line(rows.Count, line, hunk);
                rows.Add(row);
                longest = Math.Max(longest, row.Text.Length);
                largestNumber = Math.Max(largestNumber, Math.Max(line.OldLineNumber ?? 0, line.NewLineNumber ?? 0));
            }
        }

        var digits = Math.Max(3, largestNumber.ToString(CultureInfo.InvariantCulture).Length);
        return new DiffRows(rows, longest, digits);
    }

    /// <summary>Expands tabs to 4-column stops, drops a trailing CR and caps the length.</summary>
    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var length = text.Length;
        if (text[length - 1] == '\r')
        {
            length--;
        }

        if (text.AsSpan(0, length).IndexOf('\t') < 0 && length <= MaxDisplayedLineLength)
        {
            return length == text.Length ? text : text[..length];
        }

        var builder = new StringBuilder(Math.Min(length + 16, MaxDisplayedLineLength + 4));
        var consumed = 0;
        while (consumed < length && builder.Length < MaxDisplayedLineLength)
        {
            var c = text[consumed++];
            if (c == '\t')
            {
                builder.Append(' ', TabSize - (builder.Length % TabSize));
            }
            else
            {
                builder.Append(c);
            }
        }

        if (consumed < length || builder.Length > MaxDisplayedLineLength)
        {
            builder.Length = Math.Min(builder.Length, MaxDisplayedLineLength);
            builder.Append('…');
        }

        return builder.ToString();
    }
}
