using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>"Working tree" card: branch, upstream, ahead/behind and change counts from the shared git status.</summary>
public sealed partial class OverviewWorkingTreeViewModel : OverviewCardViewModel
{
    public OverviewWorkingTreeViewModel(ProjectContext context)
        : base(context)
    {
        Context.GitStatusChanged += OnGitStatusChanged;
        Update();
    }

    /// <summary>Git status has not been read yet.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    public partial bool IsRepository { get; private set; }

    /// <summary>Folder checked and it is not a repository (and git itself works).</summary>
    [ObservableProperty]
    public partial bool IsNotRepository { get; private set; }

    /// <summary>Git status could not be read (git missing, repository locked…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitError))]
    public partial ErrorInfo? GitError { get; private set; }

    public bool HasGitError => GitError is not null;

    [ObservableProperty]
    public partial string? BranchText { get; private set; }

    [ObservableProperty]
    public partial bool IsDetached { get; private set; }

    [ObservableProperty]
    public partial bool IsUnborn { get; private set; }

    [ObservableProperty]
    public partial string UpstreamText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasUpstream { get; private set; }

    [ObservableProperty]
    public partial int Ahead { get; private set; }

    [ObservableProperty]
    public partial int Behind { get; private set; }

    [ObservableProperty]
    public partial int Staged { get; private set; }

    [ObservableProperty]
    public partial int Changed { get; private set; }

    [ObservableProperty]
    public partial int Untracked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    public partial int Conflicts { get; private set; }

    public bool HasConflicts => Conflicts > 0;

    [ObservableProperty]
    public partial int Stashes { get; private set; }

    [ObservableProperty]
    public partial bool IsClean { get; private set; }

    /// <summary>"Merging", "Rebasing"… when an operation is in progress, else null.</summary>
    [ObservableProperty]
    public partial string? OperationText { get; private set; }

    /// <summary>One line summarizing the tree ("Clean — nothing to commit", "12 files changed").</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusTone Tone { get; private set; }

    [ObservableProperty]
    public partial string ReviewText { get; private set; } = "Review changes";

    protected override string ErrorTitle => "Could not read Git status";

    protected override Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        Update();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ReviewChanges() => Context.RequestNavigation(WorkspaceSection.Git);

    /// <summary>"Initialize repository" lives in the Git tab, which explains it and offers the action.</summary>
    [RelayCommand]
    private void OpenGit() => Context.RequestNavigation(WorkspaceSection.Git);

    private void OnGitStatusChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var status = Context.GitStatus;
        IsChecking = !Context.IsRepositoryChecked;
        GitError = Context.FolderExists ? Context.GitError : null;
        IsRepository = status is not null;
        IsNotRepository = Context.IsRepositoryChecked && status is null && GitError is null && Context.FolderExists;

        if (status is null)
        {
            BranchText = null;
            Summary = string.Empty;
            Tone = StatusTone.None;
            Ahead = Behind = Staged = Changed = Untracked = Conflicts = Stashes = 0;
            IsClean = false;
            HasUpstream = false;
            UpstreamText = string.Empty;
            OperationText = null;
            return;
        }

        IsDetached = status.IsDetached;
        IsUnborn = status.IsUnborn;
        BranchText = status.IsDetached
            ? $"{(status.HeadSha is { Length: > 7 } sha ? sha[..7] : status.HeadSha)} (detached)"
            : status.Branch;
        HasUpstream = status.Upstream is not null;
        UpstreamText = status.Upstream ?? (status.IsDetached ? "No branch" : "Not published");
        Ahead = status.Ahead;
        Behind = status.Behind;
        Staged = status.Staged.Count();
        Conflicts = status.Conflicted.Count();
        Untracked = status.Entries.Count(e => e.IsUntracked);
        Changed = status.Entries.Count(e => !e.IsUntracked && !e.IsConflicted && e.WorkTreeState is not (GitFileState.Unmodified or GitFileState.Ignored));
        Stashes = status.StashCount;
        IsClean = status.IsClean;
        OperationText = status.State switch
        {
            GitRepositoryState.Merging => "Merge in progress",
            GitRepositoryState.Rebasing => "Rebase in progress",
            GitRepositoryState.CherryPicking => "Cherry-pick in progress",
            GitRepositoryState.Reverting => "Revert in progress",
            GitRepositoryState.Bisecting => "Bisect in progress",
            _ => null,
        };

        var files = status.Entries.Count;
        (Summary, Tone) = (Conflicts, files) switch
        {
            ( > 0, _) => (Format.Count(Conflicts, "file has", "files have") + " conflicts", StatusTone.Danger),
            (_, 0) when status.IsUnborn => ("No commits yet — make the first one", StatusTone.Info),
            (_, 0) => ("Clean — nothing to commit", StatusTone.Success),
            _ => (Format.Count(files, "file") + " changed", StatusTone.Warning),
        };
        ReviewText = Conflicts > 0 ? "Resolve conflicts" : files > 0 ? "Review changes" : "Open Git";
    }

    protected override void OnDispose() => Context.GitStatusChanged -= OnGitStatusChanged;
}
