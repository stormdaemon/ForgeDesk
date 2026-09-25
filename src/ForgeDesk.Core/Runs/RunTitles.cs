using System.Globalization;

namespace ForgeDesk.Core.Runs;

/// <summary>User-facing wording for finished runs (activity journal, notifications).</summary>
internal static class RunTitles
{
    public static string Completed(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Status switch
        {
            RunStatus.Succeeded => record.Duration is { } duration
                ? $"{record.Label} succeeded in {FormatDuration(duration)}"
                : $"{record.Label} succeeded",
            RunStatus.Failed => record.ExitCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"{record.Label} failed (exit {code})")
                : $"{record.Label} could not start",
            RunStatus.Cancelled => $"{record.Label} was cancelled",
            RunStatus.Interrupted => $"{record.Label} was interrupted",
            _ => $"{record.Label} is running",
        };
    }

    /// <summary>"0.4 s", "12.3 s", "2 min 5 s", "1 h 3 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 60)
        {
            // Truncate rather than round so 59.96 s never reads "60.0 s".
            var tenths = Math.Floor(duration.TotalSeconds * 10) / 10;
            return string.Create(CultureInfo.InvariantCulture, $"{tenths:0.0} s");
        }

        if (duration.TotalHours < 1)
        {
            return duration.Seconds == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes} min")
                : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes} min {duration.Seconds} s");
        }

        var hours = (int)duration.TotalHours;
        return duration.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {duration.Minutes} min");
    }
}
