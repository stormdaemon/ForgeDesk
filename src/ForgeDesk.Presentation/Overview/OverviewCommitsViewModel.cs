using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

public sealed record OverviewCommitViewModel(string Sha, string ShortSha, string Subject, string Author, DateTimeOffset When)
{
    public string Initials => InitialsOf(Author);

    public string ToolTip => $"{Subject}\n{Author} · {When.ToLocalTime():g}\n{Sha}\nClick to show it in Git history";

    public static OverviewCommitViewModel From(GitCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return new OverviewCommitViewModel(commit.Sha, commit.ShortSha, commit.Subject, commit.Author.Name, commit.Author.When);
    }

    internal static string InitialsOf(string? name)
    {
        var parts = (name ?? string.Empty).Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0])),
        };
    }
}

/// <summary>"Recent commits" card: the last commits of HEAD; a click opens the commit in the Git history.</summary>
public sealed partial class OverviewCommitsViewModel : OverviewCardViewModel
{
    internal const int Count = 6;

    private readonly IGitService _git;
    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;

    public OverviewCommitsViewModel(ProjectContext context, IGitService git, IShellIntegration shell, INotificationService notifications)
        : base(context)
    {
        _git = git;
        _shell = shell;
        _notifications = notifications;
        Context.RepositoryChanged += OnRepositoryChanged;
        Context.GitStatusChanged += OnGitStatusChanged;
    }

    public ObservableCollection<OverviewCommitViewModel> Commits { get; } = [];

    [ObservableProperty]
    public partial bool IsRepository { get; private set; }

    /// <summary>Git status was read and the folder is not a repository.</summary>
    public bool ShowNotRepository => Context.IsRepositoryChecked && !Context.IsGitRepository;

    /// <summary>A repository without commits yet.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    protected override string ErrorTitle => "Could not read the history";

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var status = Context.GitStatus;
        IsRepository = status is not null;
        if (status is null || status.IsUnborn)
        {
            Commits.Clear();
            IsEmpty = status is not null;
            return;
        }

        var log = await _git.GetLogAsync(Context.Root, new GitLogQuery { Take = Count }, cancellationToken).ConfigureAwait(true);
        var commits = log.Take(Count).Select(OverviewCommitViewModel.From).ToList();
        if (!commits.SequenceEqual(Commits))
        {
            Commits.Clear();
            foreach (var commit in commits)
            {
                Commits.Add(commit);
            }
        }

        IsEmpty = Commits.Count == 0;
    }

    [RelayCommand]
    private void OpenCommit(OverviewCommitViewModel? commit)
    {
        if (commit is not null)
        {
            Context.RequestNavigation(WorkspaceSection.Git, commit.Sha);
        }
    }

    [RelayCommand]
    private void CopySha(OverviewCommitViewModel? commit)
    {
        if (commit is null)
        {
            return;
        }

        try
        {
            _shell.CopyToClipboard(commit.Sha);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not copy the SHA"));
        }
    }

    [RelayCommand]
    private void OpenHistory() => Context.RequestNavigation(WorkspaceSection.Git, Commits.FirstOrDefault()?.Sha);

    private void OnRepositoryChanged(object? sender, EventArgs e) => RequestReload();

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShowNotRepository));

        // Becoming (or no longer being) a repository changes what the card shows.
        if (IsRepository != Context.IsGitRepository)
        {
            RequestReload();
        }
    }

    protected override void OnDispose()
    {
        Context.RepositoryChanged -= OnRepositoryChanged;
        Context.GitStatusChanged -= OnGitStatusChanged;
    }
}
