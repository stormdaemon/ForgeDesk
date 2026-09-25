using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Settings;

public sealed class SettingsService : ISettingsService
{
    private const string Key = "app";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly Database _database;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.Default;

    public SettingsService(Database database, ILogger<SettingsService>? logger = null)
    {
        _database = database;
        _logger = logger ?? NullLogger<SettingsService>.Instance;
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await _database.UseAsync(c =>
            c.ExecuteScalarAsync<string?>("SELECT value_json FROM settings WHERE key = @Key", new { Key }), cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is not null)
            {
                Volatile.Write(ref _current, loaded);
            }
        }
        catch (JsonException ex)
        {
            // Unreadable settings must never block startup: fall back to defaults.
            _logger.LogWarning(ex, "Settings could not be parsed; using defaults");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        SafeEvent.Raise(Changed, this, settings);
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = change(Current);
            await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        SafeEvent.Raise(Changed, this, updated);
    }

    private async Task PersistAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await _database.UseAsync(c => c.ExecuteAsync(
            "INSERT INTO settings(key, value_json) VALUES (@Key, @Json) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json",
            new { Key, Json = json }), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, settings);
    }
}
