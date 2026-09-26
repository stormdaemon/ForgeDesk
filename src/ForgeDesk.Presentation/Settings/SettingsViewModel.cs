using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>
/// The Settings page: a list of sections on the left, the selected section on the right. Every
/// change is saved as it is made (text boxes after a short pause). Navigation argument: a section
/// name ("GitHub", "Updates"…).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, INavigationAware, IRefreshable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly SettingsStore _store;
    private bool _disposed;

    public SettingsViewModel(
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        INavigationService navigation,
        IShellIntegration shell,
        IGitService git,
        IGitHubAccountService accounts,
        IGitHubService github,
        IShellDiscovery shells,
        IAppPaths paths,
        Database database,
        IActivityLog activity,
        IUpdateService updates,
        UpdateCoordinator updateCoordinator,
        IUiDispatcher dispatcher,
        ILogger<SettingsViewModel> logger)
        : this(settings, dialogs, notifications, navigation, shell, git, accounts, github, shells, paths, database, activity, updates,
            updateCoordinator, dispatcher, logger, TimeSpan.FromMilliseconds(600))
    {
    }

    internal SettingsViewModel(
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        INavigationService navigation,
        IShellIntegration shell,
        IGitService git,
        IGitHubAccountService accounts,
        IGitHubService github,
        IShellDiscovery shells,
        IAppPaths paths,
        Database database,
        IActivityLog activity,
        IUpdateService updates,
        UpdateCoordinator updateCoordinator,
        IUiDispatcher dispatcher,
        ILogger<SettingsViewModel> logger,
        TimeSpan textSaveDelay)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        _store = new SettingsStore(settings, notifications, dispatcher, logger, textSaveDelay);

        General = new GeneralSettingsViewModel(_store, dialogs, navigation, notifications);
        Appearance = new AppearanceSettingsViewModel(_store);
        Git = new GitSettingsViewModel(_store, git, dialogs, notifications, shell, logger);
        GitHub = new GitHubSettingsViewModel(_store, accounts, github, shell, dialogs, notifications, dispatcher, logger);
        Terminal = new TerminalSettingsViewModel(_store, shells);
        Commands = new CommandsSettingsViewModel(_store);
        Editor = new EditorSettingsViewModel(_store, shell, logger);
        Updates = new UpdatesSettingsViewModel(_store, updateCoordinator);
        Data = new DataSettingsViewModel(paths, database, activity, accounts, dialogs, notifications, shell, logger);
        About = new AboutSettingsViewModel(updates, shell, notifications);
        Sections = [General, Appearance, Git, GitHub, Terminal, Commands, Editor, Updates, Data, About];
        SelectedSection = General;

        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    public GeneralSettingsViewModel General { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    public GitSettingsViewModel Git { get; }

    public GitHubSettingsViewModel GitHub { get; }

    public TerminalSettingsViewModel Terminal { get; }

    public CommandsSettingsViewModel Commands { get; }

    public EditorSettingsViewModel Editor { get; }

    public UpdatesSettingsViewModel Updates { get; }

    public DataSettingsViewModel Data { get; }

    public AboutSettingsViewModel About { get; }

    [ObservableProperty]
    public partial SettingsSectionViewModel SelectedSection { get; set; }

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (argument is string name && FindSection(name) is { } section)
        {
            SelectedSection = section;
        }

        await ActivateSelectedAsync().ConfigureAwait(true);
    }

    public void OnNavigatedFrom()
    {
    }

    /// <summary>The section matching a navigation name ("github", "Data & storage", "data").</summary>
    public SettingsSectionViewModel? FindSection(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return Sections.FirstOrDefault(s => string.Equals(s.Key, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Title, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>F5: reloads the section on screen (git detection, shells, rate limit, backups…).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        switch (SelectedSection)
        {
            case GitSettingsViewModel git:
                await git.DetectCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case TerminalSettingsViewModel terminal:
                await terminal.LoadShellsCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case GitHubSettingsViewModel github:
                await github.RefreshRateLimitCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            default:
                await SelectedSection.ActivateAsync().ConfigureAwait(true);
                break;
        }
    }

    partial void OnSelectedSectionChanged(SettingsSectionViewModel oldValue, SettingsSectionViewModel newValue)
    {
        // Ctrl+click can deselect the entry: keep showing the section that was selected.
        if (newValue is null)
        {
            SelectedSection = oldValue ?? General;
            return;
        }

        if (!_disposed)
        {
            _ = ActivateSelectedAsync();
        }
    }

    private async Task ActivateSelectedAsync()
    {
        var section = SelectedSection;
        try
        {
            await section.ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "The {Section} settings failed to load", section.Key);
            section.Error = ErrorInfo.From(ex, $"Could not load the {section.Title} settings");
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        foreach (var section in Sections)
        {
            section.ApplySettings(settings);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        GitHub.Dispose();
        _store.Dispose();
    }
}
