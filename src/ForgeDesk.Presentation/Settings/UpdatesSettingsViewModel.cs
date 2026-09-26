using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Settings › Updates: current version, manual check, automatic checks.</summary>
public sealed partial class UpdatesSettingsViewModel : SettingsSectionViewModel
{
    private readonly SettingsStore _store;

    internal UpdatesSettingsViewModel(SettingsStore store, UpdateCoordinator updates)
        : base("Updates", "Updates", "ArrowSync20", "Version and automatic updates", "update upgrade version release new check download install restart")
    {
        _store = store;
        Updates = updates;
        ApplySettings(store.Current);
    }

    /// <summary>The app-wide update state (shared with the status bar and notifications).</summary>
    public UpdateCoordinator Updates { get; }

    [ObservableProperty]
    public partial bool CheckAutomatically { get; set; }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => Updates.CheckAsync();

    [RelayCommand]
    private Task InstallUpdateAsync() => Updates.DownloadAndRestartAsync();

    partial void OnCheckAutomaticallyChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { CheckForUpdatesAutomatically = value });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings) => CheckAutomatically = settings.CheckForUpdatesAutomatically;
}
