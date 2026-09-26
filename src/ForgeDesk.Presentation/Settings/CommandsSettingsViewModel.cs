using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Settings › Commands: notifications, run history and stalled-run warnings.</summary>
public sealed partial class CommandsSettingsViewModel : SettingsSectionViewModel
{
    public const int MinHistory = 10;
    public const int MaxHistory = 5000;
    public const int MinStalledMinutes = 1;
    public const int MaxStalledMinutes = 240;

    private readonly SettingsStore _store;

    internal CommandsSettingsViewModel(SettingsStore store)
        : base("Commands", "Commands", "Play20", "Notifications and history of commands", "commands runs scripts notify notification history stalled build test")
    {
        _store = store;
        ApplySettings(store.Current);
    }

    [ObservableProperty]
    public partial bool NotifyWhenFinished { get; set; }

    [ObservableProperty]
    public partial double? HistoryPerProject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryError))]
    public partial string? HistoryError { get; private set; }

    public bool HasHistoryError => HistoryError is not null;

    [ObservableProperty]
    public partial double? StalledMinutes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStalledError))]
    public partial string? StalledError { get; private set; }

    public bool HasStalledError => StalledError is not null;

    partial void OnNotifyWhenFinishedChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { NotifyWhenRunCompletes = value });
        }
    }

    partial void OnHistoryPerProjectChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        HistoryError = SettingsNumbers.ValidateWhole(value, MinHistory, MaxHistory, "runs");
        if (HistoryError is null && value is { } runs)
        {
            _ = _store.SaveAsync(s => s with { RunHistoryPerProject = (int)Math.Round(runs) });
        }
    }

    partial void OnStalledMinutesChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        StalledError = SettingsNumbers.ValidateWhole(value, MinStalledMinutes, MaxStalledMinutes, "minutes");
        if (StalledError is null && value is { } minutes)
        {
            _ = _store.SaveAsync(s => s with { StalledRunWarningMinutes = (int)Math.Round(minutes) });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        NotifyWhenFinished = settings.NotifyWhenRunCompletes;
        HistoryPerProject = settings.RunHistoryPerProject;
        HistoryError = null;
        StalledMinutes = settings.StalledRunWarningMinutes;
        StalledError = null;
    }
}
