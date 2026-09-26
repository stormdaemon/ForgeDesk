using ForgeDesk.Core.Activity;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Projects;

/// <summary>Records journal entries without ever letting journaling break the operation it describes.</summary>
internal static class ActivityRecorder
{
    public static async Task TryRecordAsync(IActivityLog activity, ActivityEntry entry, ILogger logger)
    {
        try
        {
            // The operation already happened: do not cancel its journal entry halfway.
            await activity.RecordAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record activity {Kind}", entry.Kind);
        }
    }
}
