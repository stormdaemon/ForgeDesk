using ForgeDesk.Core.Common;
using ForgeDesk.Core.Storage;

namespace ForgeDesk.Core.Tests.Infrastructure;

/// <summary>A migrated database in its own temp data directory.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _dir = new("data");

    private TestDatabase()
    {
        Paths = new AppPaths(_dir.Path);
        Paths.EnsureCreated();
        Database = new Database(Paths);
    }

    public AppPaths Paths { get; }

    public Database Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase();
        await db.Database.InitializeAsync();
        return db;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }
}
