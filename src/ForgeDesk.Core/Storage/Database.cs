using Dapper;
using ForgeDesk.Core.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Storage;

public sealed record DatabaseOpenResult(bool Recovered, string? QuarantinedPath, string? RestoredFromBackup);

/// <summary>
/// Owns the local SQLite database: connection factory, migrations, integrity checks,
/// corruption recovery and rotating backups. All repositories go through it.
/// </summary>
public sealed class Database
{
    private const int BackupsToKeep = 5;
    private readonly ILogger<Database> _logger;
    private readonly string _connectionString;
    private readonly IClock _clock;

    static Database()
    {
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Database(IAppPaths paths, IClock? clock = null, ILogger<Database>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<Database>.Instance;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();
    }

    public IAppPaths Paths { get; }

    public int CurrentVersion => Schema.Migrations.Count;

    /// <summary>
    /// Opens (creating if needed), verifies and migrates the database. A corrupted file is
    /// moved aside and replaced by the most recent healthy backup or a fresh database,
    /// so a damaged file never prevents ForgeDesk from starting.
    /// </summary>
    public async Task<DatabaseOpenResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.DatabasePath)!);
        Directory.CreateDirectory(Paths.BackupsDirectory);

        if (File.Exists(Paths.DatabasePath) && !await IsHealthyAsync(Paths.DatabasePath, cancellationToken).ConfigureAwait(false))
        {
            var quarantined = Quarantine();
            var restored = TryRestoreLatestBackup();
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Database was corrupted; moved to {Quarantined}, restored from {Backup}", quarantined, restored ?? "(none)");
            return new DatabaseOpenResult(true, quarantined, restored);
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseOpenResult(false, null, null);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;").ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw Translate(ex);
        }
    }

    /// <summary>Runs <paramref name="work"/> with an open connection, translating SQLite failures.</summary>
    public async Task<T> UseAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(connection).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw Translate(ex);
        }
    }

    public Task UseAsync(Func<SqliteConnection, Task> work, CancellationToken cancellationToken = default) =>
        UseAsync(async c =>
        {
            await work(c).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>Hot backup through the SQLite online backup API; keeps the newest few.</summary>
    public async Task<string> BackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Paths.BackupsDirectory);
        var target = Path.Combine(Paths.BackupsDirectory, $"forgedesk-{_clock.Now:yyyyMMdd-HHmmss}.db");
        await using (var source = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        PruneBackups();
        return target;
    }

    /// <summary>Creates a backup when the newest one is older than <paramref name="maxAge"/>.</summary>
    public async Task<string?> BackupIfStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var newest = ListBackups().FirstOrDefault();
        if (newest is not null && _clock.Now - File.GetLastWriteTime(newest) < maxAge)
        {
            return null;
        }

        return await BackupAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> ListBackups() =>
        Directory.Exists(Paths.BackupsDirectory)
            ? Directory.GetFiles(Paths.BackupsDirectory, "forgedesk-*.db").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
            : [];

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);
        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);

        if (version > Schema.Migrations.Count)
        {
            throw new ForgeException(ErrorKind.StorageFailure,
                "Your ForgeDesk data was created by a newer version of ForgeDesk.",
                "Update ForgeDesk to the latest version to open it.");
        }

        for (var v = (int)version; v < Schema.Migrations.Count; v++)
        {
            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(Schema.Migrations[v], transaction: tx).ConfigureAwait(false);
            await connection.ExecuteAsync($"PRAGMA user_version = {v + 1};", transaction: tx).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Migrated database to schema v{Version}", v + 1);
        }
    }

    private static async Task<bool> IsHealthyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var result = await connection.ExecuteScalarAsync<string>("PRAGMA quick_check;").ConfigureAwait(false);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private string Quarantine()
    {
        SqliteConnection.ClearAllPools();
        var target = Path.Combine(Paths.BackupsDirectory, $"corrupted-{_clock.Now:yyyyMMdd-HHmmss}.db");
        File.Move(Paths.DatabasePath, target, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = Paths.DatabasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Move(sidecar, target + suffix, overwrite: true);
            }
        }

        return target;
    }

    private string? TryRestoreLatestBackup()
    {
        foreach (var backup in ListBackups())
        {
            if (IsHealthyAsync(backup, CancellationToken.None).GetAwaiter().GetResult())
            {
                File.Copy(backup, Paths.DatabasePath, overwrite: true);
                return backup;
            }
        }

        return null;
    }

    private void PruneBackups()
    {
        foreach (var stale in ListBackups().Skip(BackupsToKeep))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete old backup {Backup}", stale);
            }
        }
    }

    private static ForgeException Translate(SqliteException ex) => ex.SqliteErrorCode switch
    {
        11 or 26 => new ForgeException(ErrorKind.StorageCorrupted, "ForgeDesk's local database is damaged.",
            "Restart ForgeDesk: it will restore the latest backup automatically.", ex.Message, ex),
        5 or 6 => new ForgeException(ErrorKind.StorageFailure, "ForgeDesk's local database is busy.",
            "Another ForgeDesk window may be running. Try again in a moment.", ex.Message, ex),
        13 => new ForgeException(ErrorKind.StorageFailure, "The disk is full.",
            "Free some disk space and try again.", ex.Message, ex),
        _ => new ForgeException(ErrorKind.StorageFailure, "Could not access ForgeDesk's local data.", null, ex.Message, ex),
    };

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => DateTimeOffset.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture);

        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override DateTimeOffset? Parse(object value) =>
            value is null or DBNull ? null : DateTimeOffset.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture);

        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset? value) =>
            parameter.Value = value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
    }
}
