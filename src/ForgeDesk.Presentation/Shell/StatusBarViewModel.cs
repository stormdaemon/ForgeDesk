using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// The 26 px status bar: current project's branch and changes (with the auto-fetch state), running
/// commands, background operations, GitHub account and update availability.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly IRunService _runs;
    private readonly IBackgroundOperations _operations;
    private readonly IGitHubAccountService _github;
    private readonly IProjectActions _actions;
    private readonly IUiDispatcher _dispatcher;
    private string? _latestRunProjectId;
    private bool _disposed;

    public StatusBarViewModel(
        INavigationService navigation,
        IRunService runs,
        IBackgroundOperations operations,
        IGitHubAccountService github,
        IProjectActions actions,
        IUiDispatcher dispatcher)
    {
        _navigation = navigation;
        _runs = runs;
        _operations = operations;
        _github = github;
        _actions = actions;
        _dispatcher = dispatcher;

        _navigation.Navigated += OnNavigated;
        _runs.RunStarted += OnRunStarted;
        _runs.RunCompleted += OnRunCompleted;
        _github.AccountChanged += OnAccountChanged;
        ((INotifyCollectionChanged)_operations.Operations).CollectionChanged += OnOperationsChanged;
        foreach (var operation in _operations.Operations)
        {
            operation.PropertyChanged += OnOperationPropertyChanged;
        }

        Workspace = _navigation.CurrentPage as ProjectWorkspaceViewModel;
        UpdateRuns();
        UpdateOperations();
        UpdateAccount(_github.Current);
    }

    /// <summary>The open project on screen; its header properties feed the left part of the bar.</summary>
    [ObservableProperty]
    public partial ProjectWorkspaceViewModel? Workspace { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning))]
    public partial int RunningCount { get; private set; }

    /// <summary>"npm run dev" — the most recently started command still running.</summary>
    [ObservableProperty]
    public partial string? LatestRunLabel { get; private set; }

    [ObservableProperty]
    public partial string? RunningToolTip { get; private set; }

    public bool HasRunning => RunningCount > 0;

    /// <summary>The first background operation (clone, analysis…), shown with its progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperations))]
    public partial BackgroundOperation? CurrentOperation { get; private set; }

    /// <summary>"+2" when more operations run behind <see cref="CurrentOperation"/>.</summary>
    [ObservableProperty]
    public partial string? MoreOperationsText { get; private set; }

    [ObservableProperty]
    public partial string? OperationsToolTip { get; private set; }

    public bool HasOperations => CurrentOperation is not null;

    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    [ObservableProperty]
    public partial string? GitHubLogin { get; private set; }

    [ObservableProperty]
    public partial string? GitHubAvatarUrl { get; private set; }

    /// <summary>Login when signed in, "Sign in" otherwise.</summary>
    [ObservableProperty]
    public partial string GitHubText { get; private set; } = "Sign in";

    /// <summary>Version of an available update, set by the updater; null when up to date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    public partial string? UpdateAvailableVersion { get; set; }

    public bool HasUpdate => !string.IsNullOrEmpty(UpdateAvailableVersion);

    public string? UpdateText => HasUpdate ? $"Update {UpdateAvailableVersion} available" : null;

    /// <summary>Opens the Commands tab of the project whose command started last.</summary>
    [RelayCommand]
    private Task OpenRunningCommandsAsync() =>
        _latestRunProjectId is { } projectId ? _actions.OpenAsync(projectId, WorkspaceSection.Commands) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenGitChangesAsync() =>
        Workspace is { } workspace ? workspace.SelectSectionAsync(WorkspaceSection.Git) : Task.CompletedTask;

    [RelayCommand]
    private void OpenGitHubAccount() => _navigation.OpenSettings("GitHub");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigation.Navigated -= OnNavigated;
        _runs.RunStarted -= OnRunStarted;
        _runs.RunCompleted -= OnRunCompleted;
        _github.AccountChanged -= OnAccountChanged;
        ((INotifyCollectionChanged)_operations.Operations).CollectionChanged -= OnOperationsChanged;
        foreach (var operation in _operations.Operations)
        {
            operation.PropertyChanged -= OnOperationPropertyChanged;
        }
    }

    private void OnNavigated(object? sender, EventArgs e) => Workspace = _navigation.CurrentPage as ProjectWorkspaceViewModel;

    private void OnRunStarted(object? sender, IRunSession session) => _dispatcher.Post(UpdateRuns);

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e) => _dispatcher.Post(UpdateRuns);

    private void OnAccountChanged(object? sender, GitHubAccount? account) => _dispatcher.Post(() => UpdateAccount(account));

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Follow each operation's message and progress so the tooltip stays current.
        foreach (var removed in e.OldItems?.OfType<BackgroundOperation>() ?? [])
        {
            removed.PropertyChanged -= OnOperationPropertyChanged;
        }

        foreach (var added in e.NewItems?.OfType<BackgroundOperation>() ?? [])
        {
            added.PropertyChanged += OnOperationPropertyChanged;
        }

        UpdateOperations();
    }

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e) => _dispatcher.Post(UpdateOperations);

    private void UpdateRuns()
    {
        if (_disposed)
        {
            return;
        }

        var running = _runs.ActiveRuns
            .Where(r => r.Status is RunStatus.Running or RunStatus.Queued)
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        RunningCount = running.Count;
        var latest = running.FirstOrDefault();
        LatestRunLabel = latest?.Request.Label;
        _latestRunProjectId = latest?.Request.ProjectId;
        RunningToolTip = running.Count == 0
            ? null
            : string.Join('\n', running.Take(8).Select(r => r.Request.Label)) + (running.Count > 8 ? $"\n+{running.Count - 8} more" : string.Empty);
    }

    private void UpdateOperations()
    {
        var operations = _operations.Operations;
        CurrentOperation = operations.Count > 0 ? operations[0] : null;
        MoreOperationsText = operations.Count > 1 ? $"+{operations.Count - 1}" : null;
        OperationsToolTip = operations.Count == 0 ? null : string.Join('\n', operations.Select(o => o.Message is null ? o.Title : $"{o.Title} — {o.Message}"));
    }

    private void UpdateAccount(GitHubAccount? account)
    {
        IsSignedIn = account is not null;
        GitHubLogin = account?.User.Login;
        GitHubAvatarUrl = account?.User.AvatarUrl is { Length: > 0 } url ? url : null;
        GitHubText = account?.User.Login ?? "Sign in";
    }
}
