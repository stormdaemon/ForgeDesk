using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Dashboard;

public enum CloneSource
{
    /// <summary>A repository of the signed-in GitHub account.</summary>
    GitHub,

    /// <summary>Any git address typed or pasted by the user.</summary>
    Url,
}

/// <summary>A tab of the clone dialog.</summary>
public sealed record CloneSourceTab(CloneSource Source, string Title, string Icon, string AutomationId);

/// <summary>
/// "Clone a repository": pick one of your GitHub repositories or paste any git address, choose the
/// destination (remembered for next time), then clone with live progress and cancellation. On
/// success the dialog closes with <see cref="ClonedProject"/> set.
/// </summary>
public sealed partial class CloneRepositoryDialogViewModel : ViewModelBase, IDialogViewModel, IDisposable
{
    private readonly IGitHubService _github;
    private readonly IGitHubAccountService _accounts;
    private readonly IProjectCloneService _clones;
    private readonly IProjectRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IShellIntegration _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<CloneRepositoryDialogViewModel> _logger;
    private readonly List<RemoteRepositoryItemViewModel> _allRepositories = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _cloneCts;
    private bool _folderNameEdited;
    private bool _settingFolderName;
    private bool _started;
    private bool _disposed;

    public CloneRepositoryDialogViewModel(
        IGitHubService github,
        IGitHubAccountService accounts,
        IProjectCloneService clones,
        IProjectRegistry registry,
        ISettingsService settings,
        IDialogService dialogs,
        INavigationService navigation,
        IShellIntegration shell,
        IUiDispatcher dispatcher,
        ILogger<CloneRepositoryDialogViewModel> logger)
    {
        _github = github;
        _accounts = accounts;
        _clones = clones;
        _registry = registry;
        _settings = settings;
        _dialogs = dialogs;
        _navigation = navigation;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;

        Tabs =
        [
            new CloneSourceTab(CloneSource.GitHub, "Your GitHub repositories", "Globe16", "Clone.GitHubTab"),
            new CloneSourceTab(CloneSource.Url, "URL", "Link16", "Clone.UrlTab"),
        ];

        Account = accounts.Current;
        BaseFolder = CloneDestination.BaseFolderFrom(settings.Current.DefaultCloneDirectory);
        SelectedTab = Account is null ? Tabs[1] : Tabs[0];
        _accounts.AccountChanged += OnAccountChanged;
    }

    public string Title => "Clone a repository";

    public double PreferredWidth => 760;

    public double? PreferredHeight => 700;

    public event EventHandler<bool?>? CloseRequested;

    public IReadOnlyList<CloneSourceTab> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHubSource), nameof(IsUrlSource), nameof(SourceUrl), nameof(CanClone), nameof(SourceSummary))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial CloneSourceTab SelectedTab { get; set; }

    public bool IsGitHubSource => SelectedTab.Source == CloneSource.GitHub;

    public bool IsUrlSource => SelectedTab.Source == CloneSource.Url;

    // ----- GitHub repositories ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(SignedInText), nameof(ShowSignInPrompt), nameof(ShowRepositoryList),
        nameof(ShowNoRepositories), nameof(ShowNoRepositoryMatches), nameof(ShowRepositorySkeleton))]
    public partial GitHubAccount? Account { get; private set; }

    public bool IsSignedIn => Account is not null;

    public string? SignedInText => Account is { } account ? $"Signed in as {account.User.Login}" : null;

    public bool ShowSignInPrompt => !IsSignedIn;

    /// <summary>The repositories matching <see cref="RepositoryFilter"/>, most recently updated first.</summary>
    public ObservableCollection<RemoteRepositoryItemViewModel> Repositories { get; } = [];

    [ObservableProperty]
    public partial string RepositoryFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceUrl), nameof(CanClone), nameof(SourceSummary))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial RemoteRepositoryItemViewModel? SelectedRepository { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRepositorySkeleton), nameof(ShowNoRepositories), nameof(ShowRepositoryList))]
    public partial bool IsLoadingRepositories { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRepositories), nameof(ShowRepositoryList), nameof(HasRepositoriesError))]
    public partial ErrorInfo? RepositoriesError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRepositories))]
    public partial bool HasLoadedRepositories { get; private set; }

    public bool HasRepositoriesError => RepositoriesError is not null;

    public bool ShowRepositorySkeleton => IsSignedIn && IsLoadingRepositories && _allRepositories.Count == 0;

    public bool ShowNoRepositories => IsSignedIn && HasLoadedRepositories && !IsLoadingRepositories && RepositoriesError is null && _allRepositories.Count == 0;

    public bool ShowNoRepositoryMatches => IsSignedIn && _allRepositories.Count > 0 && Repositories.Count == 0;

    public bool ShowRepositoryList => IsSignedIn && RepositoriesError is null && Repositories.Count > 0;

    public int RepositoryCount => _allRepositories.Count;

    // ----- URL --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlError), nameof(ParsedGitHubRepo), nameof(HasParsedGitHubRepo), nameof(SourceUrl), nameof(CanClone), nameof(SourceSummary))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string Url { get; set; } = string.Empty;

    /// <summary>Why the typed address can't be cloned; null while empty or valid.</summary>
    public string? UrlError => string.IsNullOrWhiteSpace(Url) ? null : CloneDestination.ValidateUrl(Url);

    /// <summary>The GitHub repository the address points to, when it is one.</summary>
    public GitHubRepoRef? ParsedGitHubRepo => UrlError is null ? CloneDestination.ParseGitHub(Url) : null;

    public bool HasParsedGitHubRepo => ParsedGitHubRepo is not null;

    // ----- Destination ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(DestinationError), nameof(CanClone))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string BaseFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(DestinationError), nameof(CanClone))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string FolderName { get; set; } = string.Empty;

    /// <summary>Full path of the new folder (preview).</summary>
    public string TargetPath
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(FolderName) ? BaseFolder : Path.Combine(BaseFolder, FolderName.Trim());
            }
            catch (ArgumentException)
            {
                return BaseFolder;
            }
        }
    }

    /// <summary>Why the destination can't be used; null when it can (or while no repository is chosen).</summary>
    public string? DestinationError => SourceUrl is null && string.IsNullOrWhiteSpace(FolderName)
        ? null
        : CloneDestination.ValidateTarget(BaseFolder, FolderName);

    // ----- Clone ------------------------------------------------------------------------------

    /// <summary>The address that will be cloned, or null when none is chosen or it is invalid.</summary>
    public string? SourceUrl => IsGitHubSource
        ? SelectedRepository?.CloneUrl
        : !string.IsNullOrWhiteSpace(Url) && UrlError is null ? Url.Trim() : null;

    /// <summary>"owner/repo" or the address, for the footer summary.</summary>
    public string? SourceSummary => IsGitHubSource
        ? SelectedRepository?.FullName
        : ParsedGitHubRepo?.FullName ?? (SourceUrl is null ? null : SourceUrl);

    public bool CanClone => !IsCloning && SourceUrl is not null && CloneDestination.ValidateTarget(BaseFolder, FolderName) is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone), nameof(CancelText), nameof(CanEditInputs))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand), nameof(ChangeBaseFolderCommand))]
    public partial bool IsCloning { get; private set; }

    public bool CanEditInputs => !IsCloning;

    public string CancelText => IsCloning ? "Cancel clone" : "Cancel";

    /// <summary>"Receiving objects".</summary>
    [ObservableProperty]
    public partial string? ProgressStage { get; private set; }

    /// <summary>Raw git progress line ("Receiving objects: 45% (450/1000), 1.20 MiB | 2.00 MiB/s").</summary>
    [ObservableProperty]
    public partial string? ProgressMessage { get; private set; }

    /// <summary>0–100.</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    /// <summary>Why the last clone failed (shown inline, with details).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloneError), nameof(IsAuthenticationError))]
    public partial ErrorInfo? CloneError { get; private set; }

    public bool HasCloneError => CloneError is not null;

    /// <summary>The failure looks like missing credentials: the error offers to sign in.</summary>
    public bool IsAuthenticationError => CloneError?.Kind is ErrorKind.AuthenticationFailed or ErrorKind.AuthenticationRequired;

    /// <summary>"Clone cancelled." after a cancellation.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>The project created by a successful clone.</summary>
    public Project? ClonedProject { get; private set; }

    /// <summary>Called when the dialog opens: starts loading the repositories of the signed-in account.</summary>
    public void Start()
    {
        _started = true;
        LoadRepositoriesIfNeeded();
    }

    private void LoadRepositoriesIfNeeded()
    {
        if (_started && !_disposed && IsSignedIn && IsGitHubSource && !HasLoadedRepositories && !IsLoadingRepositories)
        {
            _ = LoadRepositoriesAsync();
        }
    }

    [RelayCommand]
    private async Task LoadRepositoriesAsync()
    {
        if (!IsSignedIn || _disposed)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoadingRepositories = true;
        RepositoriesError = null;
        try
        {
            var repositories = await _github.GetMyRepositoriesAsync(cts.Token).ConfigureAwait(true);
            var cloned = await ReadClonedRepositoriesAsync(cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _allRepositories.Clear();
            _allRepositories.AddRange(repositories
                .OrderByDescending(r => r.PushedAt ?? r.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new RemoteRepositoryItemViewModel(r, cloned.Contains(r.FullName))));
            HasLoadedRepositories = true;
            ApplyRepositoryFilter();
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the GitHub repositories");
            RepositoriesError = ErrorInfo.From(ex, "Could not load your repositories");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoadingRepositories = false;
            }
        }
    }

    private async Task<HashSet<string>> ReadClonedRepositoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(true);
            return projects.Where(p => p.GitHub is not null).Select(p => p.GitHub!.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "Could not read the registered projects");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    partial void OnRepositoryFilterChanged(string value) => ApplyRepositoryFilter();

    private void ApplyRepositoryFilter()
    {
        var terms = DashboardOrdering.SearchTerms(RepositoryFilter);
        Repositories.SyncWith(_allRepositories.Where(r => r.Matches(terms)).ToList());
        if (SelectedRepository is { } selected && !Repositories.Contains(selected))
        {
            SelectedRepository = null;
        }

        OnPropertyChanged(nameof(ShowRepositoryList));
        OnPropertyChanged(nameof(ShowNoRepositories));
        OnPropertyChanged(nameof(ShowNoRepositoryMatches));
        OnPropertyChanged(nameof(ShowRepositorySkeleton));
        OnPropertyChanged(nameof(RepositoryCount));
    }

    partial void OnSelectedTabChanged(CloneSourceTab oldValue, CloneSourceTab newValue)
    {
        // Ctrl+click can deselect the tab: keep the one that was shown.
        if (newValue is null)
        {
            SelectedTab = oldValue ?? Tabs[1];
            return;
        }

        CloneError = null;
        SuggestFolderName();
        OnPropertyChanged(nameof(DestinationError));
        LoadRepositoriesIfNeeded();
    }

    partial void OnSelectedRepositoryChanged(RemoteRepositoryItemViewModel? value)
    {
        CloneError = null;
        SuggestFolderName();
        OnPropertyChanged(nameof(DestinationError));
    }

    partial void OnUrlChanged(string value)
    {
        CloneError = null;
        SuggestFolderName();
        OnPropertyChanged(nameof(DestinationError));
    }

    partial void OnFolderNameChanged(string value)
    {
        if (!_settingFolderName)
        {
            // Typing a name keeps it; clearing the box lets the repository name come back.
            _folderNameEdited = !string.IsNullOrWhiteSpace(value);
        }
    }

    private void SuggestFolderName()
    {
        if (_folderNameEdited)
        {
            return;
        }

        var suggestion = IsGitHubSource
            ? SelectedRepository?.Name ?? string.Empty
            : UrlError is null ? CloneDestination.SuggestedFolderName(Url) : string.Empty;
        _settingFolderName = true;
        try
        {
            FolderName = suggestion;
        }
        finally
        {
            _settingFolderName = false;
        }
    }

    [RelayCommand]
    private void UseUrlInstead() => SelectedTab = Tabs[1];

    [RelayCommand]
    private void OpenRepositoryInBrowser(RemoteRepositoryItemViewModel? repository)
    {
        if (repository is null)
        {
            return;
        }

        try
        {
            _shell.OpenUrl(repository.Repository.HtmlUrl);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not open {Url}", repository.Repository.HtmlUrl);
        }
    }

    /// <summary>Closes the dialog and shows Settings › GitHub.</summary>
    [RelayCommand]
    private void SignIn()
    {
        CancelWork();
        CloseRequested?.Invoke(this, false);
        _navigation.OpenSettings("GitHub");
    }

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task ChangeBaseFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync("Choose where to clone repositories", Directory.Exists(BaseFolder) ? BaseFolder : null).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            CloneError = ErrorInfo.From(ex, "Could not open the folder picker");
            return;
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            BaseFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        var url = SourceUrl;
        if (url is null || _disposed)
        {
            return;
        }

        var target = TargetPath;
        CloneError = null;
        StatusText = null;
        ProgressStage = "Connecting…";
        ProgressMessage = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        IsCloning = true;
        _cloneCts?.Dispose();
        var cts = _cloneCts = new CancellationTokenSource();
        try
        {
            var project = await _clones.CloneAsync(url, target, new UiProgress<GitProgress>(_dispatcher, OnProgress), cts.Token).ConfigureAwait(true);
            ClonedProject = project;
            await RememberBaseFolderAsync().ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            StatusText = "Clone cancelled. Nothing was left behind.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloning {Url} failed", url);
            CloneError = Describe(ex);
        }
        finally
        {
            IsCloning = false;
            ProgressStage = null;
            ProgressMessage = null;
        }
    }

    /// <summary>Cancels a running clone, or closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsCloning)
        {
            _cloneCts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, false);
    }

    /// <summary>The window is closing (Esc, title bar): stops any clone or loading in flight.</summary>
    public void OnDialogClosed() => CancelWork();

    internal static ErrorInfo Describe(Exception exception)
    {
        var error = ErrorInfo.From(exception, "Could not clone the repository");
        return error.Kind switch
        {
            ErrorKind.AuthenticationFailed or ErrorKind.AuthenticationRequired => error with
            {
                Hint = "If the repository is private, sign in to GitHub in ForgeDesk (Settings › GitHub) so git can use your account, "
                    + "or sign in when Git Credential Manager asks, then try again.",
            },
            ErrorKind.NotFound => error with
            {
                Hint = error.Hint ?? "Check the address. If the repository is private, sign in to GitHub first: without access it looks like it doesn't exist.",
            },
            ErrorKind.NetworkUnavailable => error with { Hint = error.Hint ?? "Check your internet connection, then try again." },
            _ => error,
        };
    }

    private void OnProgress(GitProgress progress)
    {
        if (!IsCloning)
        {
            return;
        }

        ProgressStage = progress.Stage;
        ProgressMessage = progress.Message;
        if (progress.Percent is { } percent)
        {
            ProgressPercent = Math.Clamp(percent, 0, 100);
            IsProgressIndeterminate = false;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private async Task RememberBaseFolderAsync()
    {
        var folder = BaseFolder.Trim();
        if (string.Equals(_settings.Current.DefaultCloneDirectory, folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { DefaultCloneDirectory = folder }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not remember the clone folder");
        }
    }

    private void OnAccountChanged(object? sender, GitHubAccount? account) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        var wasSignedIn = IsSignedIn;
        Account = account;
        if (account is null)
        {
            _allRepositories.Clear();
            HasLoadedRepositories = false;
            ApplyRepositoryFilter();
        }
        else if (!wasSignedIn)
        {
            LoadRepositoriesIfNeeded();
        }
    });

    private void CancelWork()
    {
        _cloneCts?.Cancel();
        _loadCts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accounts.AccountChanged -= OnAccountChanged;
        CancelWork();
        _cloneCts?.Dispose();
        _loadCts?.Dispose();
    }
}
