using System.Reflection;
using System.Runtime.InteropServices;

namespace ForgeDesk.App.Startup;

/// <summary>Version and environment facts used in logs, crash reports and the About page.</summary>
internal static class AppInfo
{
    /// <summary>Product version without build metadata ("1.2.0", not "1.2.0+3f2c…").</summary>
    public static string Version { get; } = ReadVersion();

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string OperatingSystem => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
