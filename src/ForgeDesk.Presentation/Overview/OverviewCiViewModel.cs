using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>Why the CI card shows a hint instead of runs.</summary>
public enum CiHint
{
    None,
    NotLinked,
    SignedOut,
    NoRuns,
    Unavailable,
}

public sealed record OverviewWorkflowRunViewModel(string Name, CiState State, string? Branch, string? CommitMessage, string Event, DateTimeOffset At,
    TimeSpan? Duration, string HtmlUrl, int RunNumber)
{
    public string Subtitle => string.IsNullOrWhiteSpace(CommitMessage) ? $"#{RunNumber} · {Event}" : $"#{RunNumber} · {CommitMessage}";

    public string DurationText => Format.Duration(Duration);

    public bool HasDuration => Duration is not null;

    public string ToolTip => $"{Name} #{RunNumber} ({State}) on {Branch ?? "?"}\n{CommitMessage}\nClick to open the run on GitHub";

    public static OverviewWorkflowRunViewModel From(WorkflowRunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new OverviewWorkflowRunViewModel(run.Name, run.State, run.Branch, run.CommitMessage, run.Event, run.UpdatedAt ?? run.CreatedAt,
            run.Duration, run.HtmlUrl, run.RunNumber);
    }
}

/// <summary>"Continuous integration" card: the CI state of the current branch and the latest run of each workflow.</summary>
public sealed partial class OverviewCiViewModel : OverviewCardViewModel
{
    internal const int MaxRuns = 5;

    private readonly IProjectStatusService _status;
    private readonly IGitHubService _gitHub;
    private readonly IGitHubAccountService _accounts;
    private readonly IShellIntegration _shell;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private string? _loadedBranch;

    public OverviewCiViewModel(
        ProjectContext context,
        IProjectStatusService status,
        IGitHubService gitHub,
        IGitHubAccountService accounts,
        IShellIntegration shell,
        INavigationService navigation,
        INotificationService notifications,
        IUiDispatcher dispatcher)
        : base(context)
    {
        _status = status;
        _gitHub = gitHub;
        _accounts = accounts;
        _shell = shell;
        _navigation = navigation;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _status.SnapshotUpdated += OnSnapshotUpdated;
        _accounts.AccountChanged += OnAccountChanged;
        Context.PropertyChanged += OnContextPropertyChanged;
        Context.GitStatusChanged += OnGitStatusChanged;
    }

    public ObservableCollection<OverviewWorkflowRunViewModel> Runs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    [NotifyPropertyChangedFor(nameof(ShowRuns))]
    [NotifyPropertyChangedFor(nameof(HintTitle))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    [NotifyPropertyChangedFor(nameof(HintActionText))]
    [NotifyPropertyChangedFor(nameof(HintIcon))]
    public partial CiHint Hint { get; private set; }

    public bool HasHint => Hint != CiHint.None;

    public bool ShowRuns => Hint == CiHint.None && HasLoaded;

    protected override void OnLoadStateChanged() => OnPropertyChanged(nameof(ShowRuns));

    [ObservableProperty]
    public partial CiState State { get; private set; }

    [ObservableProperty]
    public partial string? Branch { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>A note shown under the runs (e.g. GitHub unreachable, showing the last known state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HintText))]
    public partial string? Message { get; private set; }

    public string? RepositoryName => Context.Project.GitHub?.FullName;

    public string HintTitle => Hint switch
    {
        CiHint.NotLinked => "Not linked to GitHub",
        CiHint.SignedOut => "Sign in to see CI",
        CiHint.NoRuns => "No workflow runs yet",
        CiHint.Unavailable => "CI status unavailable",
        _ => string.Empty,
    };

    public string HintText => Hint switch
    {
        CiHint.NotLinked => "This project has no GitHub remote. Push it to GitHub to follow its builds here.",
        CiHint.SignedOut => "ForgeDesk needs your GitHub account to read the workflow runs of this repository.",
        CiHint.NoRuns => "GitHub Actions has not run for this repository. Add a workflow under .github/workflows to build every push.",
        CiHint.Unavailable => Message ?? "GitHub could not be reached. Check your connection and try again.",
        _ => string.Empty,
    };

    public string HintActionText => Hint switch
    {
        CiHint.SignedOut => "Sign in to GitHub",
        CiHint.Unavailable => "Retry",
        _ => "Open GitHub tab",
    };

    public string HintIcon => Hint switch
    {
        CiHint.SignedOut => "PersonAccounts24",
        CiHint.Unavailable => "CloudOff24",
        CiHint.NoRuns => "Flow20",
        _ => "Globe20",
    };

    protected override string ErrorTitle => "Could not read the CI status";

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (Context.Project.GitHub is not { } repo)
        {
            ShowHint(CiHint.NotLinked);
            return;
        }

        if (!_gitHub.IsSignedIn)
        {
            ShowHint(CiHint.SignedOut);
            return;
        }

        _loadedBranch = Context.GitStatus?.Branch;
        var cached = await _status.GetCachedAsync(Context.ProjectId, cancellationToken).ConfigureAwait(true);
        if (cached?.Ci is { } known)
        {
            Apply(known);
        }

        try
        {
            var fresh = await _gitHub.GetCiSummaryAsync(repo, Context.GitStatus?.Branch, cancellationToken).ConfigureAwait(true);
            Apply(fresh);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            var info = ErrorInfo.From(ex);
            if (cached?.Ci is { State: not CiState.Unknown })
            {
                Message = $"Showing the last known state: {info.Message}";
            }
            else
            {
                Message = info.Message;
                ShowHint(CiHint.Unavailable);
            }
        }
    }

    private void Apply(CiSummary ci)
    {
        State = ci.State;
        Branch = ci.Branch;
        UpdatedAt = ci.UpdatedAt;
        Message = ci.State == CiState.Unknown ? ci.Message : null;
        var runs = ci.LatestRuns.OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt).Take(MaxRuns).Select(OverviewWorkflowRunViewModel.From).ToList();
        if (!runs.SequenceEqual(Runs))
        {
            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(run);
            }
        }

        Hint = ci.State switch
        {
            CiState.None => CiHint.NoRuns,
            CiState.Unknown when Runs.Count == 0 => CiHint.Unavailable,
            _ => CiHint.None,
        };
    }

    private void ShowHint(CiHint hint)
    {
        Runs.Clear();
        State = CiState.Unknown;
        Branch = null;
        UpdatedAt = null;
        Hint = hint;
    }

    [RelayCommand]
    private Task HintActionAsync()
    {
        switch (Hint)
        {
            case CiHint.SignedOut:
                _navigation.OpenSettings("GitHub");
                return Task.CompletedTask;
            case CiHint.Unavailable:
                _gitHub.InvalidateCache(Context.Project.GitHub);
                return LoadAsync();
            default:
                Context.RequestNavigation(WorkspaceSection.GitHub);
                return Task.CompletedTask;
        }
    }

    [RelayCommand]
    private void OpenGitHubTab() => Context.RequestNavigation(WorkspaceSection.GitHub);

    [RelayCommand]
    private void OpenRun(OverviewWorkflowRunViewModel? run)
    {
        if (run is null)
        {
            return;
        }

        try
        {
            _shell.OpenUrl(run.HtmlUrl);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    private void OnSnapshotUpdated(object? sender, ProjectSnapshot snapshot)
    {
        if (!string.Equals(snapshot.ProjectId, Context.ProjectId, StringComparison.Ordinal) || snapshot.Ci is not { } ci)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!IsDisposed && HasLoaded && Context.Project.GitHub is not null && _gitHub.IsSignedIn)
            {
                Apply(ci);
            }
        });
    }

    /// <summary>CI is per branch: a checkout (or the first git status) shows the runs of the new branch.</summary>
    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        if (HasLoaded && Context.Project.GitHub is not null && _gitHub.IsSignedIn
            && Context.GitStatus?.Branch is { } branch && !string.Equals(branch, _loadedBranch, StringComparison.Ordinal))
        {
            RequestReload();
        }
    }

    private void OnAccountChanged(object? sender, GitHubAccount? account) => _dispatcher.Post(RequestReload);

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectContext.Project))
        {
            OnPropertyChanged(nameof(RepositoryName));
            RequestReload();
        }
    }

    protected override void OnDispose()
    {
        _status.SnapshotUpdated -= OnSnapshotUpdated;
        _accounts.AccountChanged -= OnAccountChanged;
        Context.PropertyChanged -= OnContextPropertyChanged;
        Context.GitStatusChanged -= OnGitStatusChanged;
    }
}
