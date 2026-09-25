using Dapper;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ForgeDesk.Core.Tests.Storage;

public class DatabaseTests
{
    [Fact]
    public async Task Initialize_creates_schema_at_latest_version()
    {
        using var db = await TestDatabase.CreateAsync();

        await using var connection = await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;");
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'")).ToList();

        version.Should().Be(db.Database.CurrentVersion);
        tables.Should().Contain(["projects", "work_items", "activity", "runs", "settings", "custom_commands"]);
    }

    [Fact]
    public async Task Initialize_is_idempotent()
    {
        using var db = await TestDatabase.CreateAsync();
        var result = await db.Database.InitializeAsync(TestContext.Current.CancellationToken);
        result.Recovered.Should().BeFalse();
    }

    [Fact]
    public async Task Corrupted_database_is_quarantined_and_restored_from_backup()
    {
        using var dir = new TempDirectory();
        var paths = new AppPaths(dir.Path);
        paths.EnsureCreated();
        var database = new Database(paths);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var settings = new SettingsService(database);
        await settings.SaveAsync(AppSettings.Default with { OnboardingCompleted = true }, TestContext.Current.CancellationToken);
        await database.BackupAsync(TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(paths.DatabasePath, "this is definitely not a sqlite file, just garbage bytes ....", TestContext.Current.CancellationToken);

        var reopened = new Database(paths);
        var result = await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        result.Recovered.Should().BeTrue();
        result.QuarantinedPath.Should().NotBeNull();
        File.Exists(result.QuarantinedPath!).Should().BeTrue();
        result.RestoredFromBackup.Should().NotBeNull();

        var restoredSettings = new SettingsService(reopened);
        await restoredSettings.LoadAsync(TestContext.Current.CancellationToken);
        restoredSettings.Current.OnboardingCompleted.Should().BeTrue();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Corrupted_database_without_backup_starts_fresh()
    {
        using var dir = new TempDirectory();
        var paths = new AppPaths(dir.Path);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.DatabasePath, new string('x', 4096), TestContext.Current.CancellationToken);

        var database = new Database(paths);
        var result = await database.InitializeAsync(TestContext.Current.CancellationToken);

        result.Recovered.Should().BeTrue();
        result.RestoredFromBackup.Should().BeNull();
        var settings = new SettingsService(database);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.Current.Should().Be(AppSettings.Default);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Newer_schema_version_is_rejected_with_clear_message()
    {
        using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("PRAGMA user_version = 999;");
        }

        var act = () => db.Database.InitializeAsync(TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Message.Should().Contain("newer version");
    }

    [Fact]
    public async Task Backups_are_pruned_to_the_newest_five()
    {
        using var db = await TestDatabase.CreateAsync();
        for (var i = 0; i < 7; i++)
        {
            File.WriteAllText(Path.Combine(db.Paths.BackupsDirectory, $"forgedesk-2020010{i}-000000.db"), "x");
        }

        await db.Database.BackupAsync(TestContext.Current.CancellationToken);
        db.Database.ListBackups().Should().HaveCount(5);
    }
}
