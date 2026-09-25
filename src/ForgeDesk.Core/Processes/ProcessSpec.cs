namespace ForgeDesk.Core.Processes;

/// <summary>Everything needed to start an external process.</summary>
public sealed record ProcessSpec
{
    public required string FileName { get; init; }

    /// <summary>Arguments passed individually (escaped by the runtime, never re-parsed by a shell).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Pre-formatted Windows command-line arguments. When set, <see cref="Arguments"/> is ignored.
    /// Needed for cmd.exe, whose quoting rules differ from the MSVCRT rules the runtime applies.
    /// </summary>
    public string? RawArguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Environment overrides. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Optional text written to stdin, which is then closed.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Kill the process tree when exceeded. Null = no timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Stops capturing output in memory past this size (streaming callbacks still fire).</summary>
    public int MaxCapturedChars { get; init; } = 32 * 1024 * 1024;

    public override string ToString() =>
        RawArguments is not null ? $"{FileName} {RawArguments}"
        : Arguments.Count == 0 ? FileName
        : $"{FileName} {string.Join(' ', Arguments.Select(QuoteForDisplay))}";

    private static string QuoteForDisplay(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public enum OutputStream
{
    StandardOutput,
    StandardError,
}

public readonly record struct OutputLine(OutputStream Stream, string Text);
