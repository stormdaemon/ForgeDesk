using System.Globalization;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Settings;

/// <summary>
/// "Reset ForgeDesk": the request is a marker file in the data folder; at the next start, before the
/// database opens, <see cref="ApplyPending"/> deletes ForgeDesk's own data (database, run logs,
/// cache). Project folders are never touched; backups and application logs are kept.
/// </summary>
public static class DataReset
{
    public const string MarkerFileName = "reset-requested";

    public static string MarkerPath(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.DataDirectory, MarkerFileName);
    }

    public static bool IsPending(IAppPaths paths) => File.Exists(MarkerPath(paths));

    /// <summary>Asks for a reset at the next start.</summary>
    public static void Schedule(IAppPaths paths, DateTimeOffset now)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(MarkerPath(paths), $"Reset requested on {now.ToString("O", CultureInfo.InvariantCulture)}.\n");
    }

    public static void Cancel(IAppPaths paths)
    {
        var marker = MarkerPath(paths);
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Performs a pending reset. Returns true when one ran. When the database can't be deleted (in
    /// use), the request stays for the next start and the error is thrown.
    /// </summary>
    public static bool ApplyPending(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!IsPending(paths))
        {
            return false;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var file = paths.DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        EmptyDirectory(paths.RunLogsDirectory);
        EmptyDirectory(paths.CacheDirectory);
        Cancel(paths);
        return true;
    }

    private static void EmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo folder)
                {
                    folder.Delete(recursive: true);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover log or cache file is harmless; the database is what matters.
            }
        }
    }
}
