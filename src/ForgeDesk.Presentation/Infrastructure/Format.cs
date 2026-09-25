using System.Globalization;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Consistent human-friendly formatting used across views.</summary>
public static class Format
{
    public static string RelativeTime(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is null)
        {
            return "never";
        }

        var delta = (now ?? DateTimeOffset.Now) - value.Value;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        return delta.TotalSeconds switch
        {
            < 45 => "just now",
            < 90 => "1 min ago",
            _ when delta.TotalMinutes < 60 => $"{(int)delta.TotalMinutes} min ago",
            _ when delta.TotalHours < 2 => "1 hour ago",
            _ when delta.TotalHours < 24 => $"{(int)delta.TotalHours} hours ago",
            _ when delta.TotalDays < 2 => "yesterday",
            _ when delta.TotalDays < 7 => $"{(int)delta.TotalDays} days ago",
            _ when delta.TotalDays < 14 => "last week",
            _ when delta.TotalDays < 31 => $"{(int)(delta.TotalDays / 7)} weeks ago",
            _ when delta.TotalDays < 365 => value.Value.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture),
            _ => value.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } d)
        {
            return "—";
        }

        if (d.TotalSeconds < 1)
        {
            return $"{Math.Max(0, (int)d.TotalMilliseconds)} ms";
        }

        if (d.TotalMinutes < 1)
        {
            return $"{d.TotalSeconds:0.0} s";
        }

        if (d.TotalHours < 1)
        {
            return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        }

        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    public static string Timestamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {plural ?? singular + "s"}";

    public static string Bytes(long bytes) => Core.Common.PathUtil.FormatBytes(bytes);
}
