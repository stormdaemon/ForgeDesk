using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Picks the most relevant error lines of a run as output streams by: compiler diagnostics
/// ("error CS1002", "error TS2345", "error[E0425]"), npm/Maven/Gradle failures, test failures
/// (FAILED, ✗, pytest "E   " lines), Python tracebacks, Rust panics, "fatal:"… each with a
/// little context. Bounded to ~20 lines / 4 KB whatever the output size. When a failed run
/// printed nothing recognizable, the last lines of output are used instead.
/// </summary>
internal sealed partial class ErrorSummaryExtractor
{
    public const int MaxLines = 20;
    public const int MaxChars = 4096;
    public const int FallbackLines = 15;

    private const int ContextBefore = 1;
    private const int ContextAfter = 2;
    private const int MaxLineLength = 400;

    private readonly List<string> _selected = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _before = new();
    private readonly Queue<string> _tail = new();
    private int _afterRemaining;
    private int _chars;
    private bool _full;

    /// <summary>Convenience for a complete list of lines.</summary>
    public static string? Extract(IEnumerable<string> lines, bool failed)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var extractor = new ErrorSummaryExtractor();
        foreach (var line in lines)
        {
            extractor.Observe(line);
        }

        return extractor.Build(failed);
    }

    public static bool IsErrorLine(string line) =>
        !string.IsNullOrWhiteSpace(line) && ErrorRegex().IsMatch(line) && !NotAnErrorRegex().IsMatch(line);

    public void Observe(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!string.IsNullOrWhiteSpace(line))
        {
            _tail.Enqueue(line);
            if (_tail.Count > FallbackLines)
            {
                _tail.Dequeue();
            }
        }

        if (_full || IsBoilerplate(line))
        {
            return;
        }

        if (IsErrorLine(line))
        {
            if (_seen.Contains(Key(line)))
            {
                // A repeated diagnostic (final summaries) brings no new context either.
                _before.Clear();
                _afterRemaining = 0;
                return;
            }

            foreach (var context in _before)
            {
                Select(context);
            }

            _before.Clear();
            Select(line);
            _afterRemaining = ContextAfter;
        }
        else if (_afterRemaining > 0)
        {
            _afterRemaining--;
            Select(line);
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            _before.Enqueue(line);
            if (_before.Count > ContextBefore)
            {
                _before.Dequeue();
            }
        }
    }

    /// <summary>The summary, or null when there is nothing worth showing (successful runs never get one).</summary>
    public string? Build(bool failed)
    {
        if (!failed)
        {
            return null;
        }

        if (_selected.Count > 0)
        {
            return string.Join('\n', _selected);
        }

        if (_tail.Count == 0)
        {
            return null;
        }

        // Keep the end of the output: that is where tools print their final complaint.
        var lines = new List<string>();
        var chars = 0;
        foreach (var line in _tail.Reverse())
        {
            var trimmed = Shorten(line.TrimEnd());
            if (chars + trimmed.Length + 1 > MaxChars)
            {
                break;
            }

            lines.Add(trimmed);
            chars += trimmed.Length + 1;
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    private void Select(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var text = Shorten(line.TrimEnd());

        // Tools such as dotnet build repeat every diagnostic in a final summary.
        if (!_seen.Add(Key(line)))
        {
            return;
        }

        if (_selected.Count >= MaxLines || _chars + text.Length + 1 > MaxChars)
        {
            _full = true;
            return;
        }

        _selected.Add(text);
        _chars += text.Length + 1;
    }

    private static bool IsBoilerplate(string line) => BoilerplateRegex().IsMatch(line);

    private static string Key(string line) => Shorten(line.Trim());

    private static string Shorten(string line) =>
        line.Length <= MaxLineLength ? line : new StringBuilder(line, 0, MaxLineLength - 1, MaxLineLength).Append('…').ToString();

    [GeneratedRegex(
        """
        \berror(?:\s+[A-Z]{1,6}\d{2,6}|\[[A-Za-z]*\d+\])?\s*:
        |\b[A-Za-z]*(?:Error|Exception)\b
        |\bERROR\b
        |\bnpm\s+(?:ERR!|error)\s
        |\bFAIL(?:ED|URE)?\b
        |(?i:\bfailed\b)
        |^\s*[✗✘×❌]
        |Traceback\s+\(most\s+recent\s+call\s+last\)
        |\bpanicked\s+at\b
        |(?i:\bfatal(?:\s+error)?\s*:)
        |\bFATAL\b
        |^E\s{2,}\S
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ErrorRegex();

    /// <summary>Success counters such as "0 Error(s)", "Failed: 0" or "0 failed".</summary>
    [GeneratedRegex(
        @"(?i)(?:\b0\s+(?:errors?|error\(s\)|failed|failures?|failing)\b|\b(?:errors?|failures?|failed)\s*[:=]\s*0\b|\bno\s+errors?\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NotAnErrorRegex();

    /// <summary>Lines that never help: npm's pointers to its own log files, MSBuild's counters.</summary>
    [GeneratedRegex(
        @"^npm\s+(?:ERR!|error)\s+(?:A complete log of this run can be found in|This is probably not a problem with npm|Additional logging details can be found in|Log files were not written|You can rerun the command with)"
        + @"|^npm\s+(?:ERR!|error)\s*$"
        + @"|^npm\s+(?:ERR!|error)\s+\S*[/\\]\S*\.log\s*$"
        + @"|^\s*\d+\s+(?:Warning|Error)\(s\)\s*$"
        + @"|^\s*Time Elapsed\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex BoilerplateRegex();
}
