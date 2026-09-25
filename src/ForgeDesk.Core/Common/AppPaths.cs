namespace ForgeDesk.Core.Common;

/// <summary>Locations of everything ForgeDesk writes to disk.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
    string LogsDirectory { get; }
    string RunLogsDirectory { get; }
    string BackupsDirectory { get; }
    string CacheDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    public const string DataDirectoryEnvironmentVariable = "FORGEDESK_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "forgedesk.db");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string RunLogsDirectory => Path.Combine(DataDirectory, "runs");
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    /// <summary>
    /// %LOCALAPPDATA%\ForgeDesk on Windows (overridable through FORGEDESK_DATA_DIR,
    /// used by UI tests and portable setups).
    /// </summary>
    public static AppPaths CreateDefault()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return new AppPaths(Path.Combine(local, "ForgeDesk"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RunLogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
