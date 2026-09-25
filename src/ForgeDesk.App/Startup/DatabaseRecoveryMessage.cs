using System.Globalization;
using System.IO;
using ForgeDesk.Core.Storage;

namespace ForgeDesk.App.Startup;

/// <summary>Explains to the user what happened when the local database had to be recovered.</summary>
internal static class DatabaseRecoveryMessage
{
    public const string RestoredTitle = "Local data restored";
    public const string ResetTitle = "Local data reset";

    public static (string Title, string Message) For(DatabaseOpenResult result, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RestoredFromBackup is not { } backup)
        {
            return (ResetTitle, "ForgeDesk's local data was damaged and has been reset.");
        }

        return TryParseBackupTime(backup, out var time)
            ? (RestoredTitle, $"ForgeDesk's local data was damaged and has been restored from the backup of {time.ToString("f", culture)}.")
            : (RestoredTitle, "ForgeDesk's local data was damaged and has been restored from the latest backup.");
    }

    /// <summary>Backups are named forgedesk-yyyyMMdd-HHmmss.db in local time.</summary>
    internal static bool TryParseBackupTime(string backupPath, out DateTime time)
    {
        time = default;
        var name = Path.GetFileNameWithoutExtension(backupPath);
        const string prefix = "forgedesk-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(name[prefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out time);
    }
}
