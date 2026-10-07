using ForgeDesk.Core.Settings;

namespace ForgeDesk.Presentation.Tests.Settings.Support;

/// <summary>In-memory settings: changes apply at once and raise <see cref="Changed"/> synchronously.</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(AppSettings? initial = null) => Current = initial ?? AppSettings.Default;

    public AppSettings Current { get; private set; }

    public int SaveCount { get; private set; }

    /// <summary>Makes the next saves fail with this exception.</summary>
    public Exception? FailWith { get; set; }

    public event EventHandler<AppSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        Current = settings;
        SaveCount++;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return SaveAsync(change(Current), cancellationToken);
    }

    /// <summary>Simulates a change made elsewhere in the app (palette, another page).</summary>
    public void ChangeExternally(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        Changed?.Invoke(this, Current);
    }
}
