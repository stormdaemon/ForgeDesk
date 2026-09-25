using System.Globalization;
using System.IO;
using ForgeDesk.Core.Common;
using Serilog;
using Serilog.Events;

namespace ForgeDesk.App.Startup;

/// <summary>Serilog configuration: one file per day in the logs folder, 7 files, 10 MB each.</summary>
internal static class AppLogging
{
    public const int RetainedFileCount = 7;
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static Serilog.Core.Logger Create(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "forgedesk-.log"),
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCount,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
    }
}
