using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Runs;

namespace ForgeDesk.Presentation.Commands;

/// <summary>One line of a run's output in the log viewer.</summary>
public sealed partial class LogLineViewModel : ObservableObject
{
    public LogLineViewModel(RunLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Index = line.Index;
        Text = line.Text;
        IsStdErr = line.IsError;
        IsErrorLine = LogPatterns.IsError(line.Text);
    }

    public long Index { get; }

    public string Text { get; }

    /// <summary>Written to the error stream (tinted).</summary>
    public bool IsStdErr { get; }

    /// <summary>Looks like an error message ("error CS1002", "FAILED", "Exception"…): emphasized.</summary>
    public bool IsErrorLine { get; }

    /// <summary>Contains the log search text.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; internal set; }

    /// <summary>The search result currently shown (next / previous).</summary>
    [ObservableProperty]
    public partial bool IsCurrentMatch { get; internal set; }
}

/// <summary>Recognizes error lines in command output, across common toolchains.</summary>
public static partial class LogPatterns
{
    public static bool IsError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
        {
            return false;
        }

        try
        {
            return ErrorPattern().IsMatch(text) && !NoErrorPattern().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // "error CS1002", "error:", "ERR!", "FAILED", "fatal:", "panicked at", "Exception", "✖"…
    [GeneratedRegex(@"\berror\b|\berrors?:|\bfail(ed|ure)?\b|\bfatal\b|\bpanic(ked)?\b|exception\b|\bERR!|✖|✗|\btraceback\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ErrorPattern();

    // Summaries that report success: "0 Error(s)", "0 failed", "no errors", "Failed: 0".
    [GeneratedRegex(@"\b0\s+(error|errors|error\(s\)|failed|failures?)\b|\bno\s+errors?\b|\bfail(ed|ures?)?\s*[:=]\s*0\b|\berrors?\s*[:=]\s*0\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NoErrorPattern();
}
