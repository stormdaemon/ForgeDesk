using System.Globalization;
using System.IO;
using System.Text;

namespace ForgeDesk.App.Startup;

/// <summary>Writes a plain-text crash report next to the logs when ForgeDesk dies unexpectedly.</summary>
internal static class CrashReport
{
    public static string FileName(DateTimeOffset at) =>
        $"crash-{at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt";

    public static string Format(Exception exception, DateTimeOffset at, string version, string runtime, string operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        builder.AppendLine("ForgeDesk crash report");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Time:     {at.ToString("O", CultureInfo.InvariantCulture)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Version:  {version}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime:  {runtime}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"OS:       {operatingSystem}");
        builder.AppendLine();
        builder.AppendLine(exception.ToString());
        return builder.ToString();
    }

    /// <summary>Best effort: returns the report path, or null when it could not be written.</summary>
    public static string? TryWrite(string directory, Exception exception)
    {
        try
        {
            var now = DateTimeOffset.Now;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName(now));
            File.WriteAllText(path, Format(exception, now, AppInfo.Version, AppInfo.Runtime, AppInfo.OperatingSystem), Encoding.UTF8);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
