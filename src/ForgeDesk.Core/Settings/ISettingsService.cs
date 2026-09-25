namespace ForgeDesk.Core.Settings;

public interface ISettingsService
{
    /// <summary>The latest settings (always non-null; defaults until loaded).</summary>
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Atomically applies a change to the current settings and persists it.</summary>
    Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default);
}
