using System.Globalization;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Recognizes progress indicators printed by common tools: "[ 45%]" (CMake, pytest),
/// "Progress: 45 %", "[12/40]" (ninja, Docker BuildKit), "(3 of 10)", "Step 3/7" (Docker),
/// Cargo's "Building [===>  ] 45/100" and bare percentages ("45% building", git's
/// "Receiving objects:  45%"). Percentages that describe something else ("100% of tests",
/// coverage tables, CPU usage) are ignored.
/// </summary>
internal static partial class ProgressParser
{
    /// <summary>Only the start of very long lines is inspected.</summary>
    private const int MaxInspectedLength = 400;

    /// <summary>Returns the progress (0..1) announced by <paramref name="line"/>, or null.</summary>
    public static double? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var text = line.Length > MaxInspectedLength ? line[..MaxInspectedLength] : line;
        var hasPercent = text.Contains('%', StringComparison.Ordinal);

        if (hasPercent)
        {
            if (Percentage(BracketPercentRegex().Match(text)) is { } bracketed)
            {
                return bracketed;
            }

            if (Percentage(ProgressWordRegex().Match(text)) is { } announced)
            {
                return announced;
            }
        }

        // Every output line goes through here: cheap character checks skip the regexes that cannot match.
        var hasSlash = text.Contains('/', StringComparison.Ordinal);
        if (hasSlash && Fraction(CargoBuildingRegex().Match(text)) is { } cargo)
        {
            return cargo;
        }

        if (Fraction(StepRegex().Match(text)) is { } step)
        {
            return step;
        }

        if (hasSlash && text.Contains('[', StringComparison.Ordinal) && Fraction(BracketFractionRegex().Match(text)) is { } bracketFraction)
        {
            return bracketFraction;
        }

        if (text.Contains('(', StringComparison.Ordinal) && Fraction(OfFractionRegex().Match(text)) is { } ofFraction)
        {
            return ofFraction;
        }

        return hasPercent ? PlainPercentage(text) : null;
    }

    private static double? PlainPercentage(string text)
    {
        var matches = PlainPercentRegex().Matches(text);
        if (matches.Count == 0 || matches.Count > 2 || NoiseRegex().IsMatch(text))
        {
            // Three or more percentages on one line is a table (coverage, benchmarks), not progress.
            return null;
        }

        return Percentage(matches[0]);
    }

    private static double? Percentage(Match match)
    {
        if (!match.Success || !double.TryParse(match.Groups[1].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value is >= 0 and <= 100 ? value / 100 : null;
    }

    private static double? Fraction(Match match)
    {
        if (!match.Success
            || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
            || !long.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return null;
        }

        if (total <= 0 || done > total || total > 10_000_000)
        {
            return null;
        }

        return (double)done / total;
    }

    [GeneratedRegex(@"\[\s*(\d{1,3}(?:\.\d+)?)\s*%\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketPercentRegex();

    [GeneratedRegex(@"\bprogress\b\s*[:=]?\s*(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProgressWordRegex();

    [GeneratedRegex(@"^\s*Building\s+\[[=>\s-]*\]\s+(\d+)\s*/\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CargoBuildingRegex();

    [GeneratedRegex(@"\bstep\s+(\d+)\s*(?:/|of)\s*(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StepRegex();

    [GeneratedRegex(@"\[\s*(\d+)\s*/\s*(\d+)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketFractionRegex();

    [GeneratedRegex(@"\(\s*(\d+)\s+of\s+(\d+)\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OfFractionRegex();

    [GeneratedRegex(@"(?<![\w.])(\d{1,3}(?:\.\d+)?)\s?%(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPercentRegex();

    [GeneratedRegex(
        @"%\s*of\b|\b(?:coverage|covered|uncovered|statements?|branch(?:es)?|functions?|cpu|mem(?:ory)?|heap|disk|battery|swap|faster|slower|smaller|larger|bigger|increased?|decreased?|reduc(?:ed|tion)|improve(?:d|ment)?|regress(?:ed|ion)?|hit\s+rate|cache\s+hits?|gzip(?:ped)?|compress(?:ed|ion)?|saved|similarity|confidence|accuracy|loss|discount|opacity|zoom|width|height)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();
}

/// <summary>
/// Smooths successive progress readings: progress only moves forward, except when a new phase
/// clearly starts (the previous one completed, or the value drops by half or more) — parallel
/// tasks reporting slightly different values would otherwise make the bar jitter.
/// </summary>
internal sealed class ProgressTracker
{
    private const double MinimumStep = 0.001;
    private const double CompletedThreshold = 0.99;
    private const double NewPhaseDrop = 0.5;

    public double? Current { get; private set; }

    /// <summary>Feeds an output line; returns true when <see cref="Current"/> changed.</summary>
    public bool Observe(string line) => ProgressParser.Parse(line) is { } value && Update(value);

    public bool Update(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (Current is not { } current)
        {
            Current = value;
            return true;
        }

        if (value >= current)
        {
            if (value - current < MinimumStep)
            {
                return false;
            }

            Current = value;
            return true;
        }

        if (current >= CompletedThreshold || current - value >= NewPhaseDrop)
        {
            Current = value;
            return true;
        }

        return false;
    }
}
