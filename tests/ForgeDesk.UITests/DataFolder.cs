using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;

namespace ForgeDesk.UITests;

/// <summary>Creates an isolated ForgeDesk data folder, optionally pre-seeded with settings.</summary>
public static class DataFolder
{
    public static string Create(string name) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "forgedesk-ui-data", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)])).FullName;

    public static async Task<string> CreateWithSettingsAsync(string name, Func<AppSettings, AppSettings> configure)
    {
        var dir = Create(name);
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        var database = new Database(paths);
        await database.InitializeAsync();
        var settings = new SettingsService(database);
        await settings.UpdateAsync(configure);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return dir;
    }
}
