using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>
/// Saves settings for the Settings page: immediately for toggles and choices, after a short pause
/// for text boxes (one pending save per field, the last value wins). Failures become error
/// notifications; nothing throws.
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Debouncer> _debouncers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public SettingsStore(ISettingsService settings, INotificationService notifications, IUiDispatcher dispatcher, ILogger logger, TimeSpan textDelay)
    {
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        TextDelay = textDelay;
    }

    /// <summary>Pause after the last keystroke before a text field is saved (zero saves at once).</summary>
    public TimeSpan TextDelay { get; }

    public AppSettings Current => _settings.Current;

    public ISettingsService Service => _settings;

    /// <summary>Applies and persists a change now. Returns false (after notifying) when it failed.</summary>
    public async Task<bool> SaveAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save a setting");
            _notifications.ShowError(ErrorInfo.From(ex, "Could not save the setting"));
            return false;
        }
    }

    /// <summary>Saves once the user stops typing in the field named <paramref name="key"/>.</summary>
    public void SaveLater(string key, Func<AppSettings, AppSettings> change) => RunLater(key, () => SaveAsync(change));

    /// <summary>Runs <paramref name="work"/> on the UI thread once the field named <paramref name="key"/> is quiet.</summary>
    public void RunLater(string key, Func<Task> work)
    {
        if (TextDelay <= TimeSpan.Zero)
        {
            _ = work();
            return;
        }

        if (!_debouncers.TryGetValue(key, out var debouncer))
        {
            debouncer = new Debouncer(TextDelay);
            _debouncers.Add(key, debouncer);
        }

        _pending.Add(key);
        debouncer.Trigger(async () =>
        {
            var inner = await _dispatcher.InvokeAsync<Task>(async () =>
            {
                try
                {
                    await work().ConfigureAwait(true);
                }
                finally
                {
                    _pending.Remove(key);
                }
            }).ConfigureAwait(false);
            await inner.ConfigureAwait(false);
        });
    }

    /// <summary>A save is waiting for the user to stop typing (external changes must not overwrite the field).</summary>
    public bool IsPending(string key) => _pending.Contains(key);

    public void Dispose()
    {
        foreach (var debouncer in _debouncers.Values)
        {
            debouncer.Dispose();
        }

        _debouncers.Clear();
    }
}
