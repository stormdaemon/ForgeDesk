using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>What a project card asks its page to do (open, pin, locate…).</summary>
internal interface IProjectCardHost
{
    /// <summary>Name of the detected code editor, or null.</summary>
    string? EditorName { get; }

    Task OpenAsync(ProjectCardViewModel card, WorkspaceSection? section);

    void OpenInExplorer(ProjectCardViewModel card);

    void OpenInEditor(ProjectCardViewModel card);

    void OpenTerminal(ProjectCardViewModel card);

    void CopyPath(ProjectCardViewModel card);

    Task TogglePinAsync(ProjectCardViewModel card);

    Task RenameAsync(ProjectCardViewModel card);

    Task SetGroupAsync(ProjectCardViewModel card);

    Task RemoveAsync(ProjectCardViewModel card);

    Task LocateAsync(ProjectCardViewModel card);
}

/// <summary>
/// A project on the dashboard: identity, git state, CI, tasks, last commit and the reasons it
/// needs attention, from the latest <see cref="ProjectSnapshot"/> (cached, then refreshed live).
/// </summary>
public sealed partial class ProjectCardViewModel : ObservableObject
{
    /// <summary>Attention reasons listed on a card; the rest are summarized ("+2 more").</summary>
    public const int MaxAttentionItems = 3;

    public const string UngroupedHeader = "Other projects";

    private static readonly string[] SnapshotProperties =
    [
        nameof(HasSnapshot), nameof(IsChecking), nameof(IsFolderMissing), nameof(IsGitRepository), nameof(Branch), nameof(BranchText),
        nameof(HasBranch), nameof(Ahead), nameof(Behind), nameof(HasAhead), nameof(HasBehind), nameof(SyncToolTip), nameof(ChangedFiles),
        nameof(HasChanges), nameof(ChangedFilesText), nameof(CiState), nameof(HasCi), nameof(OpenTasks), nameof(HasOpenTasks),
        nameof(OpenTasksText), nameof(LastCommitAt), nameof(LastCommitSubject), nameof(HasLastCommit), nameof(PrimaryLanguage),
        nameof(HasPrimaryLanguage), nameof(Technologies), nameof(AttentionLevel), nameof(NeedsAttention), nameof(AttentionRank),
        nameof(AttentionItems), nameof(TopAttention), nameof(HasAttention), nameof(MoreAttentionText), nameof(AttentionToolTip),
        nameof(Status), nameof(StatusText), nameof(IsHealthy), nameof(Problem), nameof(ToolTip),
    ];

    private readonly IProjectCardHost _host;

    internal ProjectCardViewModel(Project project, IProjectCardHost host)
    {
        ArgumentNullException.ThrowIfNull(project);
        _host = host;
        Project = project;
    }

    public string Id => Project.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Path), nameof(Color), nameof(IsPinned), nameof(PinMenuText), nameof(Group),
        nameof(GroupHeader), nameof(HasGroup), nameof(GitHubFullName), nameof(HasGitHub), nameof(LastOpenedAt), nameof(ToolTip))]
    public partial Project Project { get; private set; }

    /// <summary>The latest status, or null until one is known.</summary>
    [ObservableProperty]
    public partial ProjectSnapshot? Snapshot { get; private set; }

    /// <summary>At least one command of the project is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusText))]
    public partial bool IsRunning { get; set; }

    // ----- Identity ---------------------------------------------------------------------------

    public string Name => Project.Name;

    public string Path => Project.Path;

    public string? Color => Project.Color;

    public bool IsPinned => Project.IsPinned;

    public string PinMenuText => IsPinned ? "Unpin" : "Pin";

    public string? Group => string.IsNullOrWhiteSpace(Project.Group) ? null : Project.Group.Trim();

    public bool HasGroup => Group is not null;

    /// <summary>Header of the card's group when the dashboard shows groups.</summary>
    public string GroupHeader => Group ?? UngroupedHeader;

    public string? GitHubFullName => Project.GitHub?.FullName;

    public bool HasGitHub => Project.GitHub is not null;

    public DateTimeOffset? LastOpenedAt => Project.LastOpenedAt;

    public DateTimeOffset AddedAt => Project.AddedAt;

    public string OpenInEditorText => _host.EditorName is { } editor ? $"Open in {editor}" : "Open in editor";

    public bool CanOpenInEditor => _host.EditorName is not null;

    // ----- Status -----------------------------------------------------------------------------

    public bool HasSnapshot => Snapshot is not null;

    /// <summary>No status is known yet: the card says it is checking.</summary>
    public bool IsChecking => Snapshot is null;

    public bool IsFolderMissing => Snapshot is { FolderExists: false };

    public bool IsGitRepository => Snapshot is { IsGitRepository: true };

    public string? Branch => Snapshot?.Branch;

    /// <summary>"main", "Detached HEAD", "Not a Git repository", or null before the first status.</summary>
    public string? BranchText => Snapshot switch
    {
        null => null,
        { FolderExists: false } => null,
        { IsGitRepository: false } => "Not a Git repository",
        { Branch: { } branch } => branch,
        { IsDetachedHead: true } => "Detached HEAD",
        _ => "No commits yet",
    };

    public bool HasBranch => BranchText is not null;

    public int Ahead => Snapshot?.Ahead ?? 0;

    public int Behind => Snapshot?.Behind ?? 0;

    public bool HasAhead => Ahead > 0;

    public bool HasBehind => Behind > 0;

    public string? SyncToolTip
    {
        get
        {
            if (Snapshot is not { IsGitRepository: true } snapshot)
            {
                return null;
            }

            var parts = new List<string>(2);
            if (snapshot.Ahead > 0)
            {
                parts.Add($"{Format.Count(snapshot.Ahead, "commit")} to push");
            }

            if (snapshot.Behind > 0)
            {
                parts.Add($"{Format.Count(snapshot.Behind, "commit")} to pull");
            }

            var tracking = snapshot.Upstream is { } upstream ? $"Tracking {upstream}" : "No upstream branch";
            return parts.Count == 0 ? $"{tracking} · in sync" : $"{tracking} · {string.Join(", ", parts)}";
        }
    }

    public int ChangedFiles => Snapshot?.ChangedFiles ?? 0;

    public bool HasChanges => ChangedFiles > 0;

    public string ChangedFilesText => ChangedFiles > 0 ? Format.Count(ChangedFiles, "change") : "Clean";

    public CiState CiState => Snapshot?.Ci?.State ?? CiState.Unknown;

    public bool HasCi => Snapshot?.Ci is { State: not CiState.Unknown and not CiState.None };

    public int OpenTasks => Snapshot?.OpenWorkItems ?? 0;

    public bool HasOpenTasks => OpenTasks > 0;

    public string OpenTasksText => Format.Count(OpenTasks, "open task");

    public DateTimeOffset? LastCommitAt => Snapshot?.LastCommitAt;

    public string? LastCommitSubject => Snapshot?.LastCommitSubject;

    public bool HasLastCommit => LastCommitAt is not null;

    public string? PrimaryLanguage => string.IsNullOrWhiteSpace(Snapshot?.PrimaryLanguage) ? null : Snapshot.PrimaryLanguage;

    public bool HasPrimaryLanguage => PrimaryLanguage is not null;

    /// <summary>Top three technologies besides the primary language ("React", "Vite", "Docker").</summary>
    public IReadOnlyList<string> Technologies => Snapshot is null
        ? []
        : Snapshot.Technologies
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, PrimaryLanguage, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

    public AttentionLevel AttentionLevel => IsFolderMissing ? AttentionLevel.Critical : Snapshot?.AttentionLevel ?? AttentionLevel.None;

    /// <summary>The project has a warning or a critical problem (info-level notes do not count).</summary>
    public bool NeedsAttention => AttentionLevel >= AttentionLevel.Warning;

    /// <summary>Sort key of the Attention order: missing folders, critical, warning, info, unknown, fine.</summary>
    public int AttentionRank => Snapshot switch
    {
        null => 1,
        { FolderExists: false } => 5,
        _ => AttentionLevel switch
        {
            AttentionLevel.Critical => 4,
            AttentionLevel.Warning => 3,
            AttentionLevel.Info => 2,
            _ => 0,
        },
    };

    /// <summary>The most important reasons, most severe first (at most <see cref="MaxAttentionItems"/>).</summary>
    public IReadOnlyList<AttentionItemViewModel> AttentionItems
    {
        get
        {
            if (Snapshot is null || IsFolderMissing)
            {
                return [];
            }

            var items = Snapshot.Attention.Select(AttentionItemViewModel.From).ToList();
            if (Problem is { } problem)
            {
                items.Add(new AttentionItemViewModel(AttentionLevel.Warning, problem, null));
            }

            return items.Take(MaxAttentionItems).ToArray();
        }
    }

    /// <summary>The single most important reason (list layout).</summary>
    public AttentionItemViewModel? TopAttention => AttentionItems.FirstOrDefault();

    public bool HasAttention => AttentionItems.Count > 0;

    public string? MoreAttentionText
    {
        get
        {
            var total = (Snapshot?.Attention.Count ?? 0) + (Problem is null ? 0 : 1);
            return IsFolderMissing || total <= MaxAttentionItems ? null : $"+{total - MaxAttentionItems} more";
        }
    }

    public string? AttentionToolTip => Snapshot is null || Snapshot.Attention.Count == 0
        ? null
        : string.Join('\n', Snapshot.Attention.Select(a => a.Message));

    /// <summary>Error met while computing the status (git missing, unreadable folder…).</summary>
    public string? Problem => IsFolderMissing || string.IsNullOrWhiteSpace(Snapshot?.Problem) ? null : Snapshot.Problem;

    public StatusTone Status
    {
        get
        {
            if (IsRunning)
            {
                return StatusTone.Running;
            }

            if (Snapshot is null)
            {
                return StatusTone.None;
            }

            if (IsFolderMissing)
            {
                return StatusTone.Warning;
            }

            return AttentionLevel switch
            {
                AttentionLevel.Critical => StatusTone.Danger,
                AttentionLevel.Warning => StatusTone.Warning,
                AttentionLevel.Info => StatusTone.Info,
                _ => StatusTone.Success,
            };
        }
    }

    public string StatusText
    {
        get
        {
            if (IsRunning)
            {
                return "Running";
            }

            if (Snapshot is null)
            {
                return "Checking…";
            }

            if (IsFolderMissing)
            {
                return "Folder not found";
            }

            return AttentionLevel switch
            {
                AttentionLevel.Critical => "Needs attention",
                AttentionLevel.Warning => "Needs attention",
                AttentionLevel.Info => "Work in progress",
                _ => "All clear",
            };
        }
    }

    public bool IsHealthy => Snapshot is not null && !IsFolderMissing && AttentionLevel == AttentionLevel.None && Problem is null;

    public string ToolTip
    {
        get
        {
            var lines = new List<string>(4) { Name, Path };
            if (IsFolderMissing)
            {
                lines.Add("The folder no longer exists. Locate it or remove the project.");
            }
            else if (LastCommitSubject is { } subject)
            {
                lines.Add($"Last commit: {subject}");
            }

            return string.Join('\n', lines);
        }
    }

    // ----- Search -----------------------------------------------------------------------------

    /// <summary>True when every term appears in the name, path, branch, language, technologies, group or GitHub repository.</summary>
    public bool Matches(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0)
        {
            return true;
        }

        var haystack = string.Join('\n', new[] { Name, Path, Branch, PrimaryLanguage, Group, GitHubFullName }
            .Concat(Snapshot?.Technologies ?? [])
            .Where(s => !string.IsNullOrEmpty(s)));
        return terms.All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    // ----- Commands ---------------------------------------------------------------------------

    [RelayCommand]
    private Task OpenAsync() => _host.OpenAsync(this, null);

    /// <summary>Opens the project on the tab an attention reason points to.</summary>
    [RelayCommand]
    private Task OpenAttentionAsync(AttentionItemViewModel? item) => _host.OpenAsync(this, item?.Section);

    [RelayCommand]
    private void OpenInExplorer() => _host.OpenInExplorer(this);

    [RelayCommand]
    private void OpenInEditor() => _host.OpenInEditor(this);

    [RelayCommand]
    private void OpenTerminal() => _host.OpenTerminal(this);

    [RelayCommand]
    private void CopyPath() => _host.CopyPath(this);

    [RelayCommand]
    private Task TogglePinAsync() => _host.TogglePinAsync(this);

    [RelayCommand]
    private Task RenameAsync() => _host.RenameAsync(this);

    [RelayCommand]
    private Task SetGroupAsync() => _host.SetGroupAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _host.RemoveAsync(this);

    [RelayCommand]
    private Task LocateAsync() => _host.LocateAsync(this);

    // ----- Updates ----------------------------------------------------------------------------

    internal void Update(Project project)
    {
        if (project != Project)
        {
            Project = project;
        }
    }

    /// <summary>Applies a snapshot unless an equal or newer one is already shown. Returns true when it changed.</summary>
    internal bool Apply(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Snapshot is { } current && (ReferenceEquals(current, snapshot) || snapshot.CapturedAt < current.CapturedAt))
        {
            return false;
        }

        Snapshot = snapshot;
        return true;
    }

    /// <summary>The detected editor changed: refresh the "Open in …" entry.</summary>
    internal void NotifyEditorChanged()
    {
        OnPropertyChanged(nameof(OpenInEditorText));
        OnPropertyChanged(nameof(CanOpenInEditor));
    }

    partial void OnSnapshotChanged(ProjectSnapshot? value)
    {
        foreach (var name in SnapshotProperties)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(name));
        }
    }
}
