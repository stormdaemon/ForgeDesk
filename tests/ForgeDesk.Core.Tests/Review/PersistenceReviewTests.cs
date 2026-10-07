using System.Text;
using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Parsing;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Review;

/// <summary>Regression tests for the persistence review findings.</summary>
public class PersistenceReviewTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static T WithinBudget<T>(Func<T> work)
    {
        var task = Task.Run(work, TestContext.Current.CancellationToken);
        task.Wait(Budget, TestContext.Current.CancellationToken).Should().BeTrue("parsing must stay linear in the input size");
        return task.Result;
    }

    [Fact]
    public void F1_toml_long_multiline_array_parses_in_linear_time()
    {
        var builder = new StringBuilder("[project]\nname = \"demo\"\ndependencies = [\n");
        for (var i = 0; i < 40_000; i++)
        {
            builder.Append("  \"pkg").Append(i).Append("\",\n");
        }

        builder.Append("]\n[tool.other]\nx = 1\n");
        var text = builder.ToString();

        var toml = WithinBudget(() => TomlLite.Parse(text));

        toml.GetStringArray("project", "dependencies").Should().HaveCount(40_000);
        toml.HasTable("tool.other").Should().BeTrue();
    }

    [Fact]
    public void F1_toml_unterminated_multiline_string_parses_in_linear_time()
    {
        var builder = new StringBuilder("[project]\ndescription = \"\"\"\n");
        for (var i = 0; i < 40_000; i++)
        {
            builder.Append("line ").Append(i).Append(" [x] 'q' \"\n");
        }

        var text = builder.ToString();

        var toml = WithinBudget(() => TomlLite.Parse(text));

        toml.Entries("project").Should().ContainSingle();
    }

    [Fact]
    public void F1_toml_incremental_scan_keeps_quote_state_across_lines()
    {
        var toml = TomlLite.Parse("a = [\"x\", '[\n', \"y\"]\nb = \"after\"\n");

        toml.GetString(string.Empty, "b").Should().Be("after");
        toml.Entries(string.Empty).Should().HaveCount(2);
    }

    [Fact]
    public void F2_workflow_unbalanced_flow_list_parses_in_linear_time()
    {
        var builder = new StringBuilder("name: x\non: [\n");
        for (var i = 0; i < 80_000; i++)
        {
            builder.Append("a,\n");
        }

        var text = builder.ToString();

        var workflow = WithinBudget(() => WorkflowFileParser.Parse(".github/workflows/x.yml", text));

        workflow.Triggers.Should().Contain("a");
    }

    [Fact]
    public void F2_workflow_multiline_flow_map_still_parses()
    {
        var workflow = WorkflowFileParser.Parse(".github/workflows/x.yml", "name: x\non: {\n  push: { branches: [main] },\n  'pull_request': {}\n}\njobs: {}\n");

        workflow.Triggers.Should().Equal("push", "pull_request");
    }

    [Fact]
    public async Task F3_timestamps_are_stored_as_utc_iso_text_and_compare_in_time_order()
    {
        using var db = await TestDatabase.CreateAsync();
        var log = new ActivityLog(db.Database, new AdjustableClock());
        var ct = TestContext.Current.CancellationToken;
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        await log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPush, Title = "a", At = at }, ct);

        var raw = await db.Database.UseAsync(c => c.ExecuteScalarAsync<string>("SELECT at FROM activity"), ct);
        raw.Should().Be("2026-01-01T10:00:00.0000000+00:00");
        var before = await log.QueryAsync(new ActivityQuery { Before = new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero) }, ct);
        before.Should().ContainSingle().Which.At.Should().Be(at);
    }

    [Fact]
    public async Task F3_migration_rewrites_legacy_local_offset_timestamps_as_utc()
    {
        using var db = await TestDatabase.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await db.Database.UseAsync(async c =>
        {
            await c.ExecuteAsync("INSERT INTO activity (at, kind, outcome, title) VALUES ('2026-01-01 12:00:00.5+02:00', 0, 0, 'legacy')");
            await c.ExecuteAsync("INSERT INTO activity (at, kind, outcome, title) VALUES ('2026-01-01T09:00:00.1234567+00:00', 0, 0, 'current')");
            await c.ExecuteAsync("PRAGMA user_version = 1;");
        }, ct);

        await db.Database.InitializeAsync(ct);

        var stored = await db.Database.UseAsync(c => c.QueryAsync<string>("SELECT at FROM activity ORDER BY id"), ct);
        stored.Should().Equal("2026-01-01T10:00:00.5000000+00:00", "2026-01-01T09:00:00.1234567+00:00");
    }

    [Fact]
    public async Task F6_index_only_corruption_is_repaired_on_the_next_start()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await TestDatabase.CreateAsync();
        var projectId = await db.InsertProjectRowAsync();
        await CorruptPathIndexAsync(db);

        // quick_check alone does not see it: this is what made "restart to recover" a false promise.
        await using (var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Paths.DatabasePath};Pooling=False"))
        {
            await check.OpenAsync(ct);
            (await check.ExecuteScalarAsync<string>("PRAGMA quick_check;")).Should().Be("ok");
        }

        var delete = () => db.Database.UseAsync(c => c.ExecuteAsync("DELETE FROM projects WHERE id = @Id", new { Id = projectId }), ct);
        (await delete.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.StorageCorrupted);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var restarted = new ForgeDesk.Core.Storage.Database(db.Paths);
        await restarted.InitializeAsync(ct);

        var deleted = await restarted.UseAsync(c => c.ExecuteAsync("DELETE FROM projects WHERE id = @Id", new { Id = projectId }), ct);
        deleted.Should().Be(1);
        var integrity = await restarted.UseAsync(c => c.ExecuteScalarAsync<string>("PRAGMA integrity_check;"), ct);
        integrity.Should().Be("ok");
    }

    /// <summary>Makes ux_projects_path hold the project's name instead of its path, leaving the schema text intact.</summary>
    private static async Task CorruptPathIndexAsync(TestDatabase db)
    {
        const string Original = "CREATE UNIQUE INDEX ux_projects_path ON projects(path COLLATE NOCASE)";
        const string Wrong = "CREATE UNIQUE INDEX ux_projects_path ON projects(name COLLATE NOCASE)";
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA writable_schema = ON;");
        await connection.ExecuteAsync("UPDATE sqlite_master SET sql = @Wrong WHERE name = 'ux_projects_path'", new { Wrong });
        await connection.ExecuteAsync("PRAGMA writable_schema = RESET;");
        await connection.ExecuteAsync("REINDEX ux_projects_path;");
        await connection.ExecuteAsync("PRAGMA writable_schema = ON;");
        await connection.ExecuteAsync("UPDATE sqlite_master SET sql = @Original WHERE name = 'ux_projects_path'", new { Original });
        await connection.ExecuteAsync("PRAGMA writable_schema = RESET;");
    }

    [Fact]
    public async Task F3_entries_across_an_offset_change_are_ordered_newest_first()
    {
        using var db = await TestDatabase.CreateAsync();
        var log = new ActivityLog(db.Database, new AdjustableClock());
        var ct = TestContext.Current.CancellationToken;
        var older = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)); // 00:30Z
        var newer = new DateTimeOffset(2026, 10, 25, 2, 10, 0, TimeSpan.FromHours(1)); // 01:10Z

        await log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPush, Title = "older", At = older }, ct);
        await log.RecordAsync(new ActivityEntry { Kind = ActivityKind.GitPush, Title = "newer", At = newer }, ct);

        var entries = await log.QueryAsync(new ActivityQuery(), ct);
        entries.Select(e => e.Title).Should().Equal("newer", "older");
    }
}
