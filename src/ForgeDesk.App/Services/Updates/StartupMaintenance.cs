using System.IO;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Services.Updates;

/// <summary>
/// Startup work owned by Settings: a reset requested in Settings › Data &amp; storage runs before the
/// database opens, and the automatic update check is scheduled 10 seconds after startup.
/// </summary>
internal static class StartupMaintenance
{
    public static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(10);

    /// <summary>Call after the container is built and before the database is initialized.</summary>
    public static void Run(IServiceProvider services, IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ForgeDesk.StartupMaintenance");

        try
        {
            if (DataReset.ApplyPending(paths))
            {
                logger.LogWarning("ForgeDesk was reset as requested: its database, run logs and cache were deleted");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The request stays in place and is retried at the next start.
            logger.LogError(ex, "The requested reset could not be completed");
        }

        try
        {
            // Resolved when the timer fires, so nothing here is created before the window exists.
            _ = ScheduleUpdateCheckAsync(services, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The automatic update check could not be scheduled");
        }
    }

    private static async Task ScheduleUpdateCheckAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            await Task.Delay(UpdateCheckDelay).ConfigureAwait(true);
            await services.GetRequiredService<UpdateCoordinator>().CheckAutomaticallyAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // ForgeDesk closed within the delay.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The automatic update check failed");
        }
    }
}
