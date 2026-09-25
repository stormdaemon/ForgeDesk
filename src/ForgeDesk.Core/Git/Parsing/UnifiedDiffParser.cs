using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Parses git's unified diff output (including combined "diff --cc" diffs of conflicted files) from raw
/// bytes. Working on bytes rather than on captured text keeps CR LF line endings intact, which patches
/// rebuilt from the diff need to apply.
/// </summary>
internal static partial class UnifiedDiffParser
{
    public const int DefaultMaxLines = 25_000;

    /// <summary>Parses every file section. A file whose hunks exceed <paramref name="maxLines"/> lines is returned with <see cref="FileDiff.IsTooLarge"/> and no hunks.</summary>
    public static IReadOnlyList<FileDiff> Parse(ReadOnlySpan<byte> data, int maxLines = DefaultMaxLines)
    {
        var lines = SplitLines(data);
        var files = new List<FileDiff>();
        FileSection? current = null;
        var i = 0;
        while (i < lines.Count)
        {
            var text = lines[i].Text;
            if (IsFileHeader(text))
            {
                if (current is not null)
                {
                    files.Add(current.Build());
                }

                current = new FileSection(text);
                i++;
            }
            else if (current is null)
            {
                i++;
            }
            else if (current.IsTooLarge)
            {
                i++;
            }
            else if (text.StartsWith("@@@", StringComparison.Ordinal))
            {
                current.AddHunk(ParseCombinedHunk(lines, ref i), maxLines);
            }
            else if (text.StartsWith("@@ ", StringComparison.Ordinal))
            {
                current.AddHunk(ParseHunk(lines, ref i), maxLines);
            }
            else
            {
                if (current.Hunks.Count == 0)
                {
                    current.AddHeaderLine(text);
                }

                i++;
            }
        }

        if (current is not null)
        {
            files.Add(current.Build());
        }

        return files;
    }

    private static bool IsFileHeader(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal)
        || line.StartsWith("diff --cc ", StringComparison.Ordinal)
        || line.StartsWith("diff --combined ", StringComparison.Ordinal);

    private static DiffHunk? ParseHunk(List<RawLine> lines, ref int i)
    {
        var header = lines[i].Text;
        var match = HunkHeader().Match(header);
        i++;
        if (!match.Success)
        {
            return null;
        }

        var oldStart = ParseInt(match.Groups["oldStart"].Value);
        var oldCount = match.Groups["oldCount"].Success ? ParseInt(match.Groups["oldCount"].Value) : 1;
        var newStart = ParseInt(match.Groups["newStart"].Value);
        var newCount = match.Groups["newCount"].Success ? ParseInt(match.Groups["newCount"].Value) : 1;

        var diffLines = new List<DiffLine>();
        int oldLine = oldStart, newLine = newStart, oldRemaining = oldCount, newRemaining = newCount;
        // Lines are consumed by count, so content that looks like a header ("--- x", "diff --git") stays content.
        while (i < lines.Count)
        {
            var (text, carriageReturn) = lines[i];
            if (text.StartsWith('\\'))
            {
                diffLines.Add(Marker(text));
                i++;
                continue;
            }

            if (oldRemaining <= 0 && newRemaining <= 0)
            {
                break;
            }

            var marker = text.Length == 0 ? ' ' : text[0];
            var content = text.Length == 0 ? string.Empty : text[1..];
            if (marker == ' ' && oldRemaining > 0 && newRemaining > 0)
            {
                diffLines.Add(new DiffLine(DiffLineKind.Context, content, oldLine++, newLine++) { HasCarriageReturn = carriageReturn });
                oldRemaining--;
                newRemaining--;
            }
            else if (marker == '-' && oldRemaining > 0)
            {
                diffLines.Add(new DiffLine(DiffLineKind.Removed, content, oldLine++, null) { HasCarriageReturn = carriageReturn });
                oldRemaining--;
            }
            else if (marker == '+' && newRemaining > 0)
            {
                diffLines.Add(new DiffLine(DiffLineKind.Added, content, null, newLine++) { HasCarriageReturn = carriageReturn });
                newRemaining--;
            }
            else
            {
                break;
            }

            i++;
        }

        return new DiffHunk
        {
            Header = header,
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
            Lines = diffLines,
        };
    }

    // "@@@ -1,3 -1,3 +1,7 @@@": one column of +/-/space per parent. Only shown (conflicts), never applied,
    // so lines are read until the next hunk or file instead of by count.
    private static DiffHunk? ParseCombinedHunk(List<RawLine> lines, ref int i)
    {
        var header = lines[i].Text;
        var match = CombinedHunkHeader().Match(header);
        i++;
        if (!match.Success)
        {
            return null;
        }

        var parents = match.Groups["at"].Value.Length - 1;
        var ranges = match.Groups["ranges"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (oldStart, oldCount) = ParseRange(ranges.FirstOrDefault(r => r.StartsWith('-')));
        var (newStart, newCount) = ParseRange(ranges.LastOrDefault(r => r.StartsWith('+')));

        var diffLines = new List<DiffLine>();
        var newLine = newStart;
        while (i < lines.Count && !lines[i].Text.StartsWith("@@", StringComparison.Ordinal) && !IsFileHeader(lines[i].Text))
        {
            var (text, carriageReturn) = lines[i];
            i++;
            if (text.StartsWith('\\'))
            {
                diffLines.Add(Marker(text));
                continue;
            }

            var columns = text.Length >= parents ? text[..parents] : text.PadRight(parents);
            var content = text.Length >= parents ? text[parents..] : string.Empty;
            var kind = columns.Contains('-', StringComparison.Ordinal) ? DiffLineKind.Removed
                : columns.Contains('+', StringComparison.Ordinal) ? DiffLineKind.Added
                : DiffLineKind.Context;
            int? number = kind == DiffLineKind.Removed ? null : newLine++;
            diffLines.Add(new DiffLine(kind, content, null, number) { HasCarriageReturn = carriageReturn });
        }

        return new DiffHunk
        {
            Header = header,
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
            Lines = diffLines,
        };
    }

    private static DiffLine Marker(string text) =>
        new(DiffLineKind.NoNewlineMarker, text.TrimStart('\\').Trim(), null, null);

    private static (int Start, int Count) ParseRange(string? range)
    {
        if (string.IsNullOrEmpty(range))
        {
            return (0, 0);
        }

        var parts = range[1..].Split(',');
        return (ParseInt(parts[0]), parts.Length > 1 ? ParseInt(parts[1]) : 1);
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>Splits on LF; a CR right before the LF is removed from the text and remembered.</summary>
    private static List<RawLine> SplitLines(ReadOnlySpan<byte> data)
    {
        var lines = new List<RawLine>();
        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            var line = newline < 0 ? data : data[..newline];
            var carriageReturn = !line.IsEmpty && line[^1] == (byte)'\r';
            if (carriageReturn)
            {
                line = line[..^1];
            }

            lines.Add(new RawLine(Encoding.UTF8.GetString(line), carriageReturn));
            data = newline < 0 ? [] : data[(newline + 1)..];
        }

        return lines;
    }

    private readonly record struct RawLine(string Text, bool CarriageReturn);

    /// <summary>Accumulates one "diff --git" section.</summary>
    private sealed class FileSection
    {
        private readonly List<string> _headerLines = [];
        private string? _gitOld;
        private string? _gitNew;
        private string? _minus;
        private string? _plus;
        private bool _minusSeen;
        private bool _plusSeen;
        private string? _renameFrom;
        private string? _renameTo;
        private bool _isBinary;
        private bool _isNew;
        private bool _isDeleted;
        private int _lineCount;

        public FileSection(string headerLine)
        {
            _headerLines.Add(headerLine);
            if (headerLine.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                (_gitOld, _gitNew) = ParseGitHeaderPaths(headerLine["diff --git ".Length..]);
            }
            else
            {
                var path = GitQuoting.Unquote(headerLine[(headerLine.IndexOf(' ', 5) + 1)..]);
                _gitOld = path;
                _gitNew = path;
            }
        }

        public List<DiffHunk> Hunks { get; } = [];

        public bool IsTooLarge { get; private set; }

        public void AddHeaderLine(string line)
        {
            _headerLines.Add(line);
            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                _isNew = true;
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                _isDeleted = true;
            }
            else if (TryValue(line, "rename from ", out var from) || TryValue(line, "copy from ", out from))
            {
                _renameFrom = GitQuoting.Unquote(from);
            }
            else if (TryValue(line, "rename to ", out var to) || TryValue(line, "copy to ", out to))
            {
                _renameTo = GitQuoting.Unquote(to);
            }
            else if (TryValue(line, "--- ", out var minus))
            {
                _minusSeen = true;
                _minus = ParseSidePath(minus, "a/");
            }
            else if (TryValue(line, "+++ ", out var plus))
            {
                _plusSeen = true;
                _plus = ParseSidePath(plus, "b/");
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                _isBinary = true;
            }
        }

        public void AddHunk(DiffHunk? hunk, int maxLines)
        {
            if (hunk is null)
            {
                return;
            }

            _lineCount += hunk.Lines.Count;
            if (_lineCount > maxLines)
            {
                IsTooLarge = true;
                Hunks.Clear();
                return;
            }

            Hunks.Add(hunk);
        }

        public FileDiff Build()
        {
            var oldName = _renameFrom ?? (_minusSeen ? _minus : null) ?? _gitOld;
            var newName = _renameTo ?? (_plusSeen ? _plus : null) ?? _gitNew;
            var isMoved = _renameFrom is not null || _renameTo is not null;
            return new FileDiff
            {
                Path = (_isDeleted ? oldName : newName ?? oldName) ?? string.Empty,
                OldPath = isMoved ? oldName : null,
                IsBinary = _isBinary,
                IsNewFile = _isNew,
                IsDeletedFile = _isDeleted,
                IsTooLarge = IsTooLarge,
                HeaderLines = _headerLines,
                Hunks = IsTooLarge ? [] : Hunks.ToList(),
            };
        }

        private static bool TryValue(string line, string prefix, out string value)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = line[prefix.Length..];
                return true;
            }

            value = string.Empty;
            return false;
        }

        /// <summary>"a/dir/file.txt" (git appends a TAB to names containing spaces), "/dev/null" or a quoted name.</summary>
        private static string? ParseSidePath(string value, string prefix)
        {
            if (value == "/dev/null")
            {
                return null;
            }

            var name = value.StartsWith('"') ? GitQuoting.Unquote(value.TrimEnd('\t')) : value.TrimEnd('\t');
            return StripPrefix(name, prefix);
        }

        /// <summary>
        /// "a/x b/y" from a "diff --git" line. Unquoted names may contain spaces, so the unambiguous
        /// symmetric form ("a/P b/P") is tried first; ---/+++ and rename lines refine the result anyway.
        /// </summary>
        internal static (string? Old, string? New) ParseGitHeaderPaths(string rest)
        {
            if (rest.StartsWith('"'))
            {
                var first = GitQuoting.UnquoteAt(rest, 0, out var end);
                if (first is not null && end + 1 < rest.Length)
                {
                    return (StripPrefix(first, "a/"), StripPrefix(GitQuoting.Unquote(rest[(end + 1)..]), "b/"));
                }
            }

            if (rest.EndsWith('"'))
            {
                var split = rest.LastIndexOf(" \"b/", StringComparison.Ordinal);
                if (split > 0)
                {
                    return (StripPrefix(rest[..split], "a/"), StripPrefix(GitQuoting.Unquote(rest[(split + 1)..]), "b/"));
                }
            }

            var length = (rest.Length - 5) / 2;
            if (rest.Length >= 7 && (rest.Length - 5) % 2 == 0 && rest.StartsWith("a/", StringComparison.Ordinal)
                && string.CompareOrdinal(rest, 2 + length, " b/", 0, 3) == 0
                && string.CompareOrdinal(rest, 2, rest, 5 + length, length) == 0)
            {
                var path = rest.Substring(2, length);
                return (path, path);
            }

            var separator = rest.IndexOf(" b/", StringComparison.Ordinal);
            return separator > 0
                ? (StripPrefix(rest[..separator], "a/"), rest[(separator + 3)..])
                : (null, null);
        }

        private static string StripPrefix(string name, string prefix) =>
            name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"^(?<at>@{3,}) (?<ranges>[-+0-9, ]+?) \k<at>", RegexOptions.CultureInvariant)]
    private static partial Regex CombinedHunkHeader();
}
