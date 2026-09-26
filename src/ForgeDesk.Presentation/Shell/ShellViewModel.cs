using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// View model of the main window: title bar (breadcrumb, search), sidebar, page host, status bar
/// and the command palette, plus startup routing, window activation and the window-level shortcuts.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly ISettingsService _settings;
    private readonly IProjectRegistry _registry;
    private readonly IProjectActions _actions;
    private readonly ILogger<ShellViewModel> _logger;
    private Task? _initialization;
    private bool _activationRequested;
    private bool _disposed;

    public ShellViewModel(
        INavigationService navigation,
        IPageFactory pages,
        ISettingsService settings,
        IProjectRegistry registry,
        IProjectActions actions,
        SidebarViewModel sidebar,
        StatusBarViewModel statusBar,
        CommandPaletteViewModel palette,
        ILogger<ShellViewModel> logger)
    {
        _navigation = navigation;
        _pages = pages;
        _settings = settings;
        _registry = registry;
        _actions = actions;
        _logger = logger;
        Sidebar = sidebar;
        StatusBar = statusBar;
        Palette = palette;
        _navigation.Navigated += OnNavigated;
        UpdatePage();
    }

    public SidebarViewModel Sidebar { get; }

    public StatusBarViewModel StatusBar { get; }

    public CommandPaletteViewModel Palette { get; }

    /// <summary>The page on screen (resolved to its view by DataTemplate).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoPage))]
    public partial object? CurrentPage { get; private set; }

    [ObservableProperty]
    public partial PageKind CurrentKind { get; private set; }

    /// <summary>The open project on screen, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace), nameof(WindowTitle))]
    public partial ProjectWorkspaceViewModel? CurrentWorkspace { get; private set; }

    public bool HasWorkspace => CurrentWorkspace is not null;

    /// <summary>"my-app — ForgeDesk" (taskbar and Alt+Tab).</summary>
    public string WindowTitle => CurrentWorkspace is { } workspace ? $"{workspace.Name} — ForgeDesk" : "ForgeDesk";

    /// <summary>First breadcrumb segment: "Home", "Activity", "Settings", "Welcome" or "Projects".</summary>
    [ObservableProperty]
    public partial string PageTitle { get; private set; } = string.Empty;

    /// <summary>True after startup routing, so "nothing to show" is not flashed while starting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoPage))]
    public partial bool IsStarted { get; private set; }

    /// <summary>No page could be shown (the dashboard is not available): the shell offers to add a project.</summary>
    public bool ShowNoPage => IsStarted && CurrentPage is null;

    public bool CanClone => _actions.CanClone;

    /// <summary>Label of the clone entry points, null (hidden) when cloning is unavailable.</summary>
    public string? CloneActionText => CanClone ? "Clone repository…" : null;

    public bool CanOpenActivity => _pages.IsAvailable(PageKind.Activity);

    public bool CanOpenSettings => _pages.IsAvailable(PageKind.Settings);

    /// <summary>Loads the sidebar and shows the first page. Safe to call more than once.</summary>
    public Task InitializeAsync() => _initialization ??= StartAsync();

    /// <summary>
    /// Handles a request to show a project (jump list, toast, "--open-project &lt;id&gt;") or a folder
    /// ("ForgeDesk C:\dev\app"): opens it, registering the folder first when needed. Wins over
    /// restoring the last project at startup.
    /// </summary>
    public async Task HandleActivationAsync(string? projectId, string? folderPath)
    {
        if (projectId is null && string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        _activationRequested = true;
        var opened = false;
        if (projectId is not null)
        {
            opened = await _actions.OpenAsync(projectId).ConfigureAwait(true);
        }

        if (!opened && !string.IsNullOrWhiteSpace(folderPath))
        {
            opened = await _actions.AddOrOpenFolderAsync(folderPath).ConfigureAwait(true);
        }

        if (!opened && _navigation.CurrentPage is null)
        {
            _navigation.GoToDashboard();
        }
    }

    [RelayCommand]
    private void OpenPalette(string? prefix) => Palette.Open(prefix);

    /// <summary>Ctrl+P: the palette in file mode when a project is open.</summary>
    [RelayCommand]
    private void GoToFile() => Palette.Open(CurrentWorkspace is null ? null : "/");

    [RelayCommand]
    private Task AddLocalProjectAsync() => _actions.AddLocalProjectAsync();

    [RelayCommand(CanExecute = nameof(CanClone))]
    private Task CloneRepositoryAsync() => _actions.CloneRepositoryAsync();

    [RelayCommand]
    private void GoHome() => _navigation.GoToDashboard();

    [RelayCommand]
    private void OpenActivity() => _navigation.OpenActivity();

    [RelayCommand]
    private void OpenSettings() => _navigation.OpenSettings();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _navigation.GoBack();

    private bool CanGoBack() => _navigation.CanGoBack;

    [RelayCommand]
    private void ToggleSidebar() => Sidebar.IsCollapsed = !Sidebar.IsCollapsed;

    /// <summary>Ctrl+1 … Ctrl+0 (parameter: 0-based tab index, as a number or text).</summary>
    [RelayCommand]
    private Task SelectTabAsync(object? parameter)
    {
        if (CurrentWorkspace is not { } workspace || !TryParseIndex(parameter, out var index))
        {
            return Task.CompletedTask;
        }

        return workspace.SelectSectionByIndexCommand.ExecuteAsync(index);
    }

    /// <summary>Ctrl+`: the Terminal tab of the open project.</summary>
    [RelayCommand]
    private Task OpenTerminalAsync() =>
        CurrentWorkspace is { } workspace ? workspace.SelectSectionAsync(WorkspaceSection.Terminal) : Task.CompletedTask;

    /// <summary>F5: refreshes the current workspace section, or else the current page.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IRefreshable? target = CurrentPage switch
        {
            ProjectWorkspaceViewModel { CurrentSection: IRefreshable section } => section,
            IRefreshable page => page,
            _ => null,
        };

        if (target is null || !target.RefreshCommand.CanExecute(null))
        {
            return;
        }

        try
        {
            await target.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // Superseded or closed.
        }
        catch (Exception ex)
        {
            // The page shows its own errors; an escaping one must not take the window down.
            _logger.LogError(ex, "Refreshing {Page} failed", target.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigation.Navigated -= OnNavigated;
        CurrentWorkspace = null;
        Sidebar.Dispose();
        StatusBar.Dispose();
        Palette.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            await Sidebar.InitializeAsync().ConfigureAwait(true);
            await RouteStartupAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup routing failed");
            if (_navigation.CurrentPage is null)
            {
                _navigation.GoToDashboard();
            }
        }
        finally
        {
            IsStarted = true;
        }
    }

    /// <summary>
    /// Onboarding until it is completed, else the last opened project when the user wants it
    /// restored and it is still registered, else the dashboard.
    /// </summary>
    private async Task RouteStartupAsync()
    {
        if (_activationRequested)
        {
            return;
        }

        var settings = _settings.Current;
        if (!settings.OnboardingCompleted && _pages.IsAvailable(PageKind.Onboarding))
        {
            _navigation.OpenOnboarding();
            return;
        }

        if (settings.RestoreLastProjectOnStartup && settings.LastOpenedProjectId is { } lastId)
        {
            var project = await FindProjectAsync(lastId).ConfigureAwait(true);
            if (_activationRequested)
            {
                return;
            }

            if (project is not null && await _actions.OpenAsync(project.Id).ConfigureAwait(true))
            {
                return;
            }
        }

        if (!_activationRequested && _navigation.CurrentPage is null)
        {
            _navigation.GoToDashboard();
        }
    }

    private async Task<Project?> FindProjectAsync(string projectId)
    {
        try
        {
            return await _registry.GetAsync(projectId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look up the last opened project");
            return null;
        }
    }

    private void OnNavigated(object? sender, EventArgs e) => UpdatePage();

    partial void OnCurrentWorkspaceChanged(ProjectWorkspaceViewModel? oldValue, ProjectWorkspaceViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnWorkspacePropertyChanged;
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectWorkspaceViewModel.Name))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private void UpdatePage()
    {
        CurrentPage = _navigation.CurrentPage;
        CurrentKind = _navigation.CurrentKind;
        CurrentWorkspace = CurrentPage as ProjectWorkspaceViewModel;
        PageTitle = CurrentPage is null
            ? string.Empty
            : CurrentKind switch
            {
                PageKind.Dashboard => "Home",
                PageKind.Project => "Projects",
                PageKind.Activity => "Activity",
                PageKind.Settings => "Settings",
                PageKind.Onboarding => "Welcome",
                _ => string.Empty,
            };
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private static bool TryParseIndex(object? parameter, out int index)
    {
        switch (parameter)
        {
            case int value:
                index = value;
                return value >= 0;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                index = parsed;
                return parsed >= 0;
            default:
                index = -1;
                return false;
        }
    }
}
