using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Git;

/// <summary>What the Git tab can show for the project folder.</summary>
public enum GitSectionState
{
    /// <summary>The first git status has not been read yet.</summary>
    Checking,

    /// <summary>Git is not installed (or not found).</summary>
    GitMissing,

    /// <summary>Git status could not be read (folder missing, repository locked…).</summary>
    Unavailable,

    /// <summary>The folder is not under version control.</summary>
    NotRepository,

    Ready,
}

/// <summary>An entry of the Git tab's sub-navigation (Changes · History · Branches · Stashes · Tags).</summary>
public sealed partial class GitSubViewTab : ObservableObject
{
    internal GitSubViewTab(GitView view, string title, string icon, GitSubViewModel viewModel)
    {
        View = view;
        Title = title;
        Icon = icon;
        ViewModel = viewModel;
    }

    public GitView View { get; }

    public string Title { get; }

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon { get; }

    public GitSubViewModel ViewModel { get; }

    /// <summary>UI automation id of the tab ("Git.ChangesTab").</summary>
    public string AutomationId => $"Git.{View}Tab";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial string? BadgeText { get; internal set; }

    [ObservableProperty]
    public partial StatusTone BadgeTone { get; internal set; }

    [ObservableProperty]
    public partial string? ToolTip { get; internal set; }

    public bool HasBadge => BadgeText is not null;

    /// <summary>The view on screen.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }
}

/// <summary>
/// The Git tab of a project workspace: a sub-navigation over Changes, History, Branches, Stashes
/// and Tags (remembering the last one), and the states of a folder that is not a usable repository
/// (git missing, not a repository, status unreadable). Views load when first shown and reload on
/// status and ref changes while visible; hidden ones only remember that they are stale.
/// </summary>
public sealed partial class GitSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    /// <summary>Where Git for Windows is downloaded.</summary>
    public const string GitDownloadUrl = "https://git-scm.com/download/win";

    private readonly WorkspaceServices _services;
    private readonly GitViewPreferences _preferences;
    private readonly ILogger _logger;
    private GitSubViewTab? _current;
    private bool _suppressTabSelection;
    private bool _disposed;
    private int? _stashCount;

    public GitSectionViewModel(ProjectContext context, WorkspaceServices services, GitViewPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preferences);
        Context = context;
        _services = services;
        _preferences = preferences;
        _logger = services.LoggerFactory.CreateLogger<GitSectionViewModel>();

        var section = new GitSectionContext(context, services, preferences, NavigateAsync);
        Changes = new ChangesViewModel(section);
        History = new HistoryViewModel(section);
        Branches = new BranchesViewModel(section);
        Stashes = new StashesViewModel(section);
        Tags = new TagsViewModel(section);
        Tabs =
        [
            new GitSubViewTab(GitView.Changes, "Changes", "DocumentEdit16", Changes),
            new GitSubViewTab(GitView.History, "History", "History16", History),
            new GitSubViewTab(GitView.Branches, "Branches", "BranchFork16", Branches),
            new GitSubViewTab(GitView.Stashes, "Stashes", "Archive16", Stashes),
            new GitSubViewTab(GitView.Tags, "Tags", "Tag16", Tags),
        ];

        Context.GitStatusChanged += OnGitStatusChanged;
        Context.RepositoryChanged += OnRepositoryChanged;
        Context.PropertyChanged += OnContextPropertyChanged;

        _stashCount = context.GitStatus?.StashCount;
        UpdateState();
        UpdateBadges();
        Show(Tabs[(int)preferences.LastView]);
    }

    public WorkspaceSection Section => WorkspaceSection.Git;

    public ProjectContext Context { get; }

    public ChangesViewModel Changes { get; }

    public HistoryViewModel History { get; }

    public BranchesViewModel Branches { get; }

    public StashesViewModel Stashes { get; }

    public TagsViewModel Tags { get; }

    public ObservableCollection<GitSubViewTab> Tabs { get; }

    /// <summary>Views shown at least once; the view keeps them alive so lists keep their scroll and selection.</summary>
    public ObservableCollection<GitSubViewTab> OpenedTabs { get; } = [];

    /// <summary>The selected sub-navigation entry (two-way with the segmented control).</summary>
    [ObservableProperty]
    public partial GitSubViewTab? SelectedTab { get; set; }

    [ObservableProperty]
    public partial GitView CurrentView { get; private set; }

    public GitSubViewModel? CurrentSubView => _current?.ViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsGitMissing), nameof(IsUnavailable), nameof(IsNotRepository), nameof(IsReady))]
    public partial GitSectionState State { get; private set; }

    public bool IsChecking => State == GitSectionState.Checking;

    public bool IsGitMissing => State == GitSectionState.GitMissing;

    public bool IsUnavailable => State == GitSectionState.Unavailable;

    public bool IsNotRepository => State == GitSectionState.NotRepository;

    public bool IsReady => State == GitSectionState.Ready;

    /// <summary>Why git status could not be read (Unavailable), with a retry.</summary>
    [ObservableProperty]
    public partial ErrorInfo? RepositoryError { get; private set; }

    /// <summary>Why Git can't be used (not installed, too old, wrong path in Settings) and what to do.</summary>
    [ObservableProperty]
    public partial string? GitMissingDescription { get; private set; }

    public string NotRepositoryDescription => $"{Context.Project.Name} isn't under version control yet. Initialize a repository to track changes, "
        + "commit and push. Nothing in the folder is modified.";

    public bool IsActive { get; private set; }

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = true;
        UpdateState();
        if (IsReady && _current is { } current)
        {
            await current.ViewModel.ActivateAsync().ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        _current?.ViewModel.Deactivate();
    }

    /// <summary>
    /// Deep link: a <see cref="GitNavigation"/>, a commit SHA or a repository-relative path (see
    /// <see cref="GitNavigation.From"/>). A path without changes shows that file's history.
    /// </summary>
    public async Task NavigateToAsync(object argument)
    {
        if (GitNavigation.From(argument) is { } navigation)
        {
            await NavigateAsync(navigation).ConfigureAwait(true);
        }
    }

    /// <summary>Shows a view of the tab and applies the rest of the link to it.</summary>
    public async Task NavigateAsync(GitNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (_disposed)
        {
            return;
        }

        if (navigation is { View: GitView.Changes, Path: { } path, FocusCommitMessage: false }
            && Context.GitStatus?.Entries.Any(e => string.Equals(e.Path, path, StringComparison.Ordinal)) != true)
        {
            navigation = GitNavigation.FileHistory(path);
        }

        var target = Tabs[(int)navigation.View];
        target.ViewModel.PrepareNavigation(navigation);
        await SelectViewAsync(navigation.View).ConfigureAwait(true);
        if (IsReady && IsActive)
        {
            await target.ViewModel.NavigateAsync(navigation).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task SelectViewAsync(GitView view)
    {
        if (_disposed)
        {
            return;
        }

        var tab = Tabs[(int)view];
        if (ReferenceEquals(_current, tab))
        {
            SelectTabSilently(tab);
            return;
        }

        _current?.ViewModel.Deactivate();
        Show(tab);
        if (IsActive && IsReady)
        {
            await tab.ViewModel.ActivateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>F5: re-reads the status and reloads the view on screen.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
            if (IsReady && _current?.ViewModel is { HasLoaded: true } view)
            {
                await view.LoadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
    }

    [RelayCommand]
    private Task InitializeRepositoryAsync() => RunAsync(async () =>
    {
        await _services.Git.InitAsync(Context.Root, Context.Lifetime).ConfigureAwait(true);
        await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
        _services.Notifications.Show("Repository initialized", $"{Context.Project.Name} is now tracked by Git on branch main. Commit your files to start its history.",
            NotificationSeverity.Success);
    }, "Initializing…", "Could not initialize the repository", ErrorMode.Toast, _services.Notifications);

    /// <summary>"Check again" after installing Git, or retry after a status error.</summary>
    [RelayCommand]
    private Task CheckAgainAsync() => RunAsync(() => Context.RefreshGitStatusAsync(), "Checking…", "Could not read the repository",
        ErrorMode.Toast, _services.Notifications);

    [RelayCommand]
    private void InstallGit()
    {
        try
        {
            _services.Shell.OpenUrl(GitDownloadUrl);
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    partial void OnSelectedTabChanged(GitSubViewTab? value)
    {
        if (!_suppressTabSelection && value is not null)
        {
            _ = SelectViewAsync(value.View);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsActive = false;
        Context.GitStatusChanged -= OnGitStatusChanged;
        Context.RepositoryChanged -= OnRepositoryChanged;
        Context.PropertyChanged -= OnContextPropertyChanged;
        foreach (var tab in Tabs)
        {
            try
            {
                tab.ViewModel.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The {View} view failed to dispose", tab.View);
            }
        }
    }

    private void Show(GitSubViewTab tab)
    {
        if (_current is { } previous)
        {
            previous.IsCurrent = false;
        }

        _current = tab;
        tab.IsCurrent = true;
        if (!OpenedTabs.Contains(tab))
        {
            OpenedTabs.Add(tab);
        }

        _preferences.LastView = tab.View;
        CurrentView = tab.View;
        OnPropertyChanged(nameof(CurrentSubView));
        SelectTabSilently(tab);
    }

    private void SelectTabSilently(GitSubViewTab tab)
    {
        _suppressTabSelection = true;
        try
        {
            SelectedTab = tab;
        }
        finally
        {
            _suppressTabSelection = false;
        }
    }

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var wasReady = IsReady;
        UpdateState();
        UpdateBadges();
        if (!IsReady)
        {
            return;
        }

        Changes.OnStatusChanged();
        Stashes.OnStatusChanged();
        var stashCount = Context.GitStatus?.StashCount;
        if (_stashCount is not null && stashCount != _stashCount)
        {
            Stashes.Invalidate();
        }

        _stashCount = stashCount;
        if (!wasReady && IsActive && _current is { } current)
        {
            // The folder just became a repository (initialized, or git found): show the current view.
            _ = current.ViewModel.ActivateAsync();
        }
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        if (_disposed || !IsReady)
        {
            return;
        }

        Changes.Invalidate();
        History.Invalidate();
        Branches.Invalidate();
        Stashes.Invalidate();
        Tags.Invalidate();
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectContext.Project))
        {
            OnPropertyChanged(nameof(NotRepositoryDescription));
        }
    }

    private void UpdateState()
    {
        var error = Context.GitError;
        State = !Context.IsRepositoryChecked ? GitSectionState.Checking
            : error is { Kind: ErrorKind.GitNotFound or ErrorKind.ToolNotFound } ? GitSectionState.GitMissing
            : error is not null ? GitSectionState.Unavailable
            : Context.GitStatus is null ? GitSectionState.NotRepository
            : GitSectionState.Ready;
        RepositoryError = State == GitSectionState.Unavailable ? error : null;
        GitMissingDescription = State == GitSectionState.GitMissing
            ? $"{error!.Message} {error.Hint ?? "Install Git for Windows, then check again."}".Trim()
            : null;
    }

    private void UpdateBadges()
    {
        var status = Context.GitStatus;
        var changes = Tabs[(int)GitView.Changes];
        var conflicts = status?.Conflicted.Count() ?? 0;
        var count = status?.Entries.Count ?? 0;
        changes.BadgeText = count > 0 ? (count > 999 ? "999+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture)) : null;
        changes.BadgeTone = conflicts > 0 ? StatusTone.Danger : StatusTone.Neutral;
        changes.ToolTip = conflicts > 0 ? $"{Format.Count(conflicts, "conflicted file")} to resolve"
            : count > 0 ? Format.Count(count, "changed file") : "No local changes";

        var stashes = Tabs[(int)GitView.Stashes];
        var stashCount = status?.StashCount ?? 0;
        stashes.BadgeText = stashCount > 0 ? stashCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;
        stashes.BadgeTone = StatusTone.Neutral;
        stashes.ToolTip = stashCount > 0 ? Format.Count(stashCount, "stash", "stashes") : "No stashed changes";

        Tabs[(int)GitView.History].ToolTip = "Commits, with their files and diffs";
        Tabs[(int)GitView.Branches].ToolTip = "Local and remote branches";
        Tabs[(int)GitView.Tags].ToolTip = "Tags marking versions and releases";
    }
}
