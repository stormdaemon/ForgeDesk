using System.Globalization;
using System.Text;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Git has no diff for untracked files, so it is built here: the whole file as added lines, with the
/// same binary heuristic (a NUL byte among the first 8 000 bytes) and size limits as tracked diffs.
/// </summary>
internal static class UntrackedFileDiff
{
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Git's buffer_is_binary() looks at this many leading bytes.</summary>
    public const int BinarySniffBytes = 8000;

    public static async Task<FileDiff> CreateAsync(string fullPath, string relativePath, int maxLines, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        var aPath = "a/" + relativePath;
        var bPath = "b/" + relativePath;
        var gitLine = $"diff --git {GitQuoting.Quote(aPath)} {GitQuoting.Quote(bPath)}";
        var plusLine = "+++ " + (GitQuoting.NeedsQuoting(bPath) ? GitQuoting.Quote(bPath) : bPath.Contains(' ', StringComparison.Ordinal) ? bPath + "\t" : bPath);

        byte[] content;
        string mode;
        if (info.LinkTarget is { } linkTarget)
        {
            // Git stores a symbolic link as a blob holding the target path, without a trailing newline.
            content = Encoding.UTF8.GetBytes(linkTarget.Replace('\\', '/'));
            mode = "120000";
        }
        else
        {
            if (!info.Exists)
            {
                return new FileDiff { Path = relativePath };
            }

            mode = IsExecutable(info) ? "100755" : "100644";
            content = info.Length > MaxBytes ? [] : await ReadSharedAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (info.Length > MaxBytes || content.Length > MaxBytes)
            {
                return new FileDiff
                {
                    Path = relativePath,
                    IsNewFile = true,
                    IsTooLarge = true,
                    HeaderLines = [gitLine, $"new file mode {mode}"],
                };
            }
        }

        var header = new List<string> { gitLine, $"new file mode {mode}" };
        if (content.AsSpan(0, Math.Min(content.Length, BinarySniffBytes)).Contains((byte)0))
        {
            header.Add($"Binary files /dev/null and {GitQuoting.Quote(bPath)} differ");
            return new FileDiff { Path = relativePath, IsNewFile = true, IsBinary = true, HeaderLines = header };
        }

        if (content.Length == 0)
        {
            return new FileDiff { Path = relativePath, IsNewFile = true, HeaderLines = header };
        }

        header.Add("--- /dev/null");
        header.Add(plusLine);
        var lines = BuildLines(content);
        var lineCount = lines.Count(l => l.Kind == DiffLineKind.Added);
        if (lines.Count > maxLines)
        {
            return new FileDiff { Path = relativePath, IsNewFile = true, IsTooLarge = true, HeaderLines = header };
        }

        var hunk = new DiffHunk
        {
            Header = lineCount == 1 ? "@@ -0,0 +1 @@" : string.Create(CultureInfo.InvariantCulture, $"@@ -0,0 +1,{lineCount} @@"),
            OldStart = 0,
            OldCount = 0,
            NewStart = 1,
            NewCount = lineCount,
            Lines = lines,
        };

        return new FileDiff { Path = relativePath, IsNewFile = true, HeaderLines = header, Hunks = [hunk] };
    }

    private static List<DiffLine> BuildLines(byte[] content)
    {
        var lines = new List<DiffLine>();
        var remaining = content.AsSpan();
        var number = 1;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf((byte)'\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            var carriageReturn = !line.IsEmpty && line[^1] == (byte)'\r';
            if (carriageReturn)
            {
                line = line[..^1];
            }

            lines.Add(new DiffLine(DiffLineKind.Added, Encoding.UTF8.GetString(line), null, number++) { HasCarriageReturn = carriageReturn });
            if (newline < 0)
            {
                lines.Add(new DiffLine(DiffLineKind.NoNewlineMarker, "No newline at end of file", null, null));
                break;
            }

            remaining = remaining[(newline + 1)..];
        }

        return lines;
    }

    private static bool IsExecutable(FileInfo info) =>
        !OperatingSystem.IsWindows() && (info.UnixFileMode & UnixFileMode.UserExecute) != 0;

    /// <summary>Reads at most <see cref="MaxBytes"/> + 1 bytes; the file may change while it is read.</summary>
    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        // Editors keep files open while the user types: read without blocking them.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while (memory.Length <= MaxBytes && (read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
