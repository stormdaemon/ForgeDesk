using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Settings › General: clone folder, startup, closing, onboarding.</summary>
public sealed partial class GeneralSettingsViewModel : SettingsSectionViewModel
{
    private readonly SettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;

    internal GeneralSettingsViewModel(SettingsStore store, IDialogService dialogs, INavigationService navigation, INotificationService notifications)
        : base("General", "General", "Settings20", "Default folders, startup and closing", "clone folder startup restore close confirm onboarding welcome")
    {
        _store = store;
        _dialogs = dialogs;
        _navigation = navigation;
        _notifications = notifications;
        ApplySettings(store.Current);
    }

    /// <summary>The folder chosen by the user, or null for the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloneFolderText), nameof(HasCustomCloneFolder))]
    [NotifyCanExecuteChangedFor(nameof(ResetCloneFolderCommand))]
    public partial string? CloneFolder { get; private set; }

    public string CloneFolderText => CloneFolder ?? CloneDestination.DefaultBaseFolder();

    public bool HasCustomCloneFolder => CloneFolder is not null;

    public string DefaultCloneFolder => CloneDestination.DefaultBaseFolder();

    [ObservableProperty]
    public partial bool RestoreLastProject { get; set; }

    [ObservableProperty]
    public partial bool ConfirmBeforeClosing { get; set; }

    [RelayCommand]
    private async Task ChangeCloneFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync("Choose where new clones go", CloneFolderText).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the folder picker"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(folder) && await _store.SaveAsync(s => s with { DefaultCloneDirectory = folder }).ConfigureAwait(true))
        {
            CloneFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(HasCustomCloneFolder))]
    private async Task ResetCloneFolderAsync()
    {
        if (await _store.SaveAsync(s => s with { DefaultCloneDirectory = null }).ConfigureAwait(true))
        {
            CloneFolder = null;
        }
    }

    /// <summary>Shows the welcome steps again (the current settings are kept).</summary>
    [RelayCommand]
    private void RunOnboarding() => _navigation.OpenOnboarding();

    partial void OnRestoreLastProjectChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { RestoreLastProjectOnStartup = value });
        }
    }

    partial void OnConfirmBeforeClosingChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { ConfirmBeforeClosingWithRunningTasks = value });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        CloneFolder = string.IsNullOrWhiteSpace(settings.DefaultCloneDirectory) ? null : settings.DefaultCloneDirectory;
        RestoreLastProject = settings.RestoreLastProjectOnStartup;
        ConfirmBeforeClosing = settings.ConfirmBeforeClosingWithRunningTasks;
    }
}
