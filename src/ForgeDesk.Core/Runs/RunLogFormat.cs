using System.Globalization;
using System.Text;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// On-disk format of run logs: one line per output line,
/// <c>{index} {UTC timestamp, ISO-8601} {O|E} {text}</c> ("O" standard output, "E" standard error).
/// The index makes a tail read self-describing, so the viewer can show line numbers without
/// scanning the whole file.
/// </summary>
internal static class RunLogFormat
{
    public const char OutputMarker = 'O';
    public const char ErrorMarker = 'E';

    private const int TailChunkSize = 64 * 1024;

    public static string Format(RunLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var timestamp = line.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{line.Index} {timestamp} {(line.IsError ? ErrorMarker : OutputMarker)} {line.Text}");
    }

    /// <summary>
    /// Parses a formatted line. Lines that do not follow the format (a file edited by hand, an
    /// older format) are kept as plain text continuing from <paramref name="previous"/>.
    /// </summary>
    public static RunLogLine Parse(string raw, RunLogLine? previous)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (TryParse(raw, out var line))
        {
            return line;
        }

        return new RunLogLine((previous?.Index ?? -1) + 1, previous?.At ?? DateTimeOffset.MinValue, false, raw);
    }

    public static bool TryParse(string raw, out RunLogLine line)
    {
        line = null!;
        var span = raw.AsSpan();

        var firstSpace = span.IndexOf(' ');
        if (firstSpace <= 0 || !long.TryParse(span[..firstSpace], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return false;
        }

        var rest = span[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ');
        if (secondSpace <= 0 || !DateTimeOffset.TryParse(rest[..secondSpace], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return false;
        }

        rest = rest[(secondSpace + 1)..];
        if (rest.Length < 2 || rest[1] != ' ' || (rest[0] != OutputMarker && rest[0] != ErrorMarker))
        {
            return false;
        }

        line = new RunLogLine(index, at, rest[0] == ErrorMarker, rest[2..].ToString());
        return true;
    }

    /// <summary>Reads the last <paramref name="maxLines"/> lines of a log without scanning the whole file.</summary>
    public static async Task<IReadOnlyList<RunLogLine>> ReadTailAsync(string path, int maxLines, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (maxLines <= 0 || !File.Exists(path))
        {
            return [];
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            var start = await FindTailStartAsync(stream, maxLines, cancellationToken).ConfigureAwait(false);
            stream.Position = start;

            var lines = new List<RunLogLine>(Math.Min(maxLines, 4096));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            RunLogLine? previous = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } raw)
            {
                previous = Parse(raw, previous);
                lines.Add(previous);
            }

            // The file may have grown while reading a live run.
            return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }
    }

    private static async Task<long> FindTailStartAsync(FileStream stream, int maxLines, CancellationToken cancellationToken)
    {
        var end = stream.Length;
        var buffer = new byte[TailChunkSize];
        var position = end;
        var newlines = 0;
        while (position > 0)
        {
            var count = (int)Math.Min(buffer.Length, position);
            position -= count;
            stream.Position = position;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            for (var i = count - 1; i >= 0; i--)
            {
                // The newline terminating the last line does not start a new one.
                if (buffer[i] == (byte)'\n' && position + i != end - 1 && ++newlines == maxLines)
                {
                    return position + i + 1;
                }
            }
        }

        return 0;
    }
}
