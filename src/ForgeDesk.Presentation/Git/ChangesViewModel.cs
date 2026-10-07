using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// The Changes view: conflicted, staged and unstaged files built from the shared git status, the
/// diff of the selected file with per-hunk staging, file actions, operation banners and the commit box.
/// </summary>
public sealed partial class ChangesViewModel : GitSubViewModel
{
    private const int MaxListedFiles = 8;

    private readonly Dictionary<(ChangeGroup Group, string Path), ChangeItemViewModel> _items = [];
    private readonly ChangeGroupHeader _conflictHeader = new(ChangeGroup.Conflicted);
    private readonly ChangeGroupHeader _stagedHeader = new(ChangeGroup.Staged);
    private readonly ChangeGroupHeader _unstagedHeader = new(ChangeGroup.Unstaged);
    private IReadOnlyList<ChangeItemViewModel> _selection = [];
    private CancellationTokenSource? _diffLoad;
    private bool _rebuilding;
    private bool _statusPending = true;
    private bool _selectFirstOnNextStatus = true;

    public ChangesViewModel(GitSectionContext section)
        : base(section)
    {
        IsDescriptionExpanded = section.Preferences.CommitDescriptionExpanded;
    }

    public override GitView View => GitView.Changes;

    /// <summary>Group headers and files, flat (one virtualized list).</summary>
    public ObservableCollection<ChangeListRow> Rows { get; } = [];

    /// <summary>Selected files in list order. The view pushes its selection with <see cref="SetSelection"/>.</summary>
    public IReadOnlyList<ChangeItemViewModel> SelectedItems => _selection;

    public bool HasSelection => _selection.Count > 0;

    public bool HasMultipleSelection => _selection.Count > 1;

    public int SelectedStagedCount => _selection.Count(i => i.IsStaged);

    public int SelectedUnstagedCount => _selection.Count(i => !i.IsStaged);

    /// <summary>"3 files selected".</summary>
    public string SelectionTitle => $"{Format.Count(_selection.Count, "file")} selected";

    /// <summary>"Stage 2 files" for a multiple selection, null when none of it can be staged.</summary>
    public string? SelectionStageText => SelectedUnstagedCount > 0 ? $"Stage {Format.Count(SelectedUnstagedCount, "file")}" : null;

    /// <summary>"Unstage 1 file" for a multiple selection, null when none of it is staged.</summary>
    public string? SelectionUnstageText => SelectedStagedCount > 0 ? $"Unstage {Format.Count(SelectedStagedCount, "file")}" : null;

    /// <summary>"2 staged · 1 not staged".</summary>
    public string SelectionDetail
    {
        get
        {
            var parts = new List<string>();
            if (SelectedStagedCount > 0)
            {
                parts.Add($"{SelectedStagedCount} staged");
            }

            var conflicted = _selection.Count(i => i.IsConflicted);
            var unstaged = SelectedUnstagedCount - conflicted;
            if (unstaged > 0)
            {
                parts.Add($"{unstaged} not staged");
            }

            if (conflicted > 0)
            {
                parts.Add($"{conflicted} conflicted");
            }

            return string.Join(" · ", parts);
        }
    }

    // ----- Counts and repository state --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText), nameof(CommitToolTip), nameof(TotalCount), nameof(IsClean), nameof(ShowList))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand), nameof(UnstageAllCommand))]
    public partial int StagedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText), nameof(CommitToolTip), nameof(TotalCount), nameof(IsClean), nameof(ShowList))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand), nameof(StageAllCommand))]
    public partial int UnstagedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCount), nameof(IsClean), nameof(ShowList), nameof(HasConflicts), nameof(CommitToolTip))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand), nameof(StageAllCommand))]
    public partial int ConflictCount { get; private set; }

    /// <summary>Number of changed paths.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClean), nameof(ShowList), nameof(ChangedFilesText))]
    [NotifyCanExecuteChangedFor(nameof(DiscardAllCommand))]
    public partial int FileCount { get; private set; }

    public int TotalCount => StagedCount + UnstagedCount + ConflictCount;

    public bool HasConflicts => ConflictCount > 0;

    /// <summary>"5 changed files".</summary>
    public string ChangedFilesText => Format.Count(FileCount, "changed file");

    /// <summary>The status was read and there is nothing to commit.</summary>
    public bool IsClean => HasStatus && FileCount == 0;

    public bool ShowList => HasStatus && FileCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClean), nameof(ShowList))]
    public partial bool HasStatus { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText), nameof(CommitAndPushText), nameof(CommitToolTip), nameof(CommitAndPushToolTip))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    public partial string? BranchName { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitAndPushText), nameof(CommitAndPushToolTip))]
    public partial string? Upstream { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAmend), nameof(CleanTitle), nameof(CleanDescription), nameof(CleanActionText))]
    public partial bool IsUnborn { get; private set; }

    [ObservableProperty]
    public partial bool IsDetached { get; private set; }

    /// <summary>"You're not on a branch" banner text, with the commit HEAD points at.</summary>
    [ObservableProperty]
    public partial string? DetachedMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMerging), nameof(IsOtherOperationInProgress), nameof(OperationTitle), nameof(OperationMessage))]
    public partial GitRepositoryState RepositoryState { get; private set; }

    public bool IsMerging => RepositoryState == GitRepositoryState.Merging;

    /// <summary>A rebase, cherry-pick, revert or bisect is in progress (resolved from a terminal).</summary>
    public bool IsOtherOperationInProgress => RepositoryState is not GitRepositoryState.Normal and not GitRepositoryState.Merging;

    public string OperationTitle => RepositoryState switch
    {
        GitRepositoryState.Merging => "Merge in progress",
        GitRepositoryState.Rebasing => "Rebase in progress",
        GitRepositoryState.CherryPicking => "Cherry-pick in progress",
        GitRepositoryState.Reverting => "Revert in progress",
        GitRepositoryState.Bisecting => "Bisect in progress",
        _ => string.Empty,
    };

    public string OperationMessage => RepositoryState switch
    {
        GitRepositoryState.Merging => HasConflicts
            ? "Resolve the conflicted files, stage them, then commit to complete the merge."
            : "All conflicts are resolved. Commit to complete the merge.",
        GitRepositoryState.Rebasing => "Resolve any conflicts and stage the files, then run 'git rebase --continue' (or '--abort') in a terminal.",
        GitRepositoryState.CherryPicking => "Resolve any conflicts and stage the files, then run 'git cherry-pick --continue' (or '--abort') in a terminal.",
        GitRepositoryState.Reverting => "Resolve any conflicts and stage the files, then run 'git revert --continue' (or '--abort') in a terminal.",
        GitRepositoryState.Bisecting => "Mark commits with 'git bisect good' or 'git bisect bad' in a terminal, and finish with 'git bisect reset'.",
        _ => string.Empty,
    };

    /// <summary>The latest commit of HEAD (clean state, amend), null before the first commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanDescription))]
    public partial GitCommit? LastCommit { get; private set; }

    public string CleanTitle => IsUnborn ? "No commits yet" : "No local changes";

    public string CleanDescription => IsUnborn
        ? "Add or create files in this folder; they will appear here, ready for the first commit."
        : LastCommit is { } last
            ? $"Everything is committed. Last commit: {last.ShortSha} · {last.Subject} · {Format.RelativeTime(last.Author.When, Section.Services.Time.GetLocalNow())}."
            : "Everything is committed. Changes you make to files will appear here.";

    public string CleanActionText => IsUnborn ? "Open folder" : "Open history";

    // ----- Diff -------------------------------------------------------------------------

    /// <summary>The single selected file whose diff is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HunkActionText), nameof(DiffTargetText), nameof(ShowConflictPanel), nameof(ShowDiff), nameof(ShowDiffView))]
    public partial ChangeItemViewModel? DiffItem { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffAdditions), nameof(DiffDeletions), nameof(HasLineCounts), nameof(HunkActionText), nameof(ShowDiffView),
        nameof(IsDiffPending))]
    public partial FileDiff? Diff { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffView), nameof(IsDiffPending))]
    public partial bool IsDiffLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiff), nameof(ShowDiffView))]
    public partial ErrorInfo? DiffError { get; private set; }

    /// <summary>The first diff of a newly selected file is loading (nothing to show yet).</summary>
    public bool IsDiffPending => IsDiffLoading && Diff is null;

    /// <summary>The diff control is visible: one file (not conflicted) selected, loaded, without error.</summary>
    public bool ShowDiffView => ShowDiff && !IsDiffPending;

    public int DiffAdditions => Diff?.Additions ?? 0;

    public int DiffDeletions => Diff?.Deletions ?? 0;

    public bool HasLineCounts => Diff is { IsBinary: false, IsTooLarge: false } && (DiffAdditions > 0 || DiffDeletions > 0);

    /// <summary>"Stage hunk" / "Unstage hunk" on each hunk header; null hides the button.</summary>
    public string? HunkActionText => DiffItem is null || DiffItem.IsConflicted || Diff is null or { IsBinary: true } or { IsTooLarge: true }
        ? null
        : DiffItem.IsStaged ? "Unstage hunk" : "Stage hunk";

    public string DiffTargetText => DiffItem switch
    {
        null => string.Empty,
        { IsConflicted: true } => "Conflicted",
        { IsStaged: true } => "Staged",
        { IsUntracked: true } => "Untracked",
        _ => "Not staged",
    };

    /// <summary>A conflicted file: explain how to resolve it instead of a combined diff.</summary>
    public bool ShowConflictPanel => DiffItem?.IsConflicted == true;

    public bool ShowDiff => !HasMultipleSelection && !ShowConflictPanel && DiffError is null;

    // ----- Loading and status -------------------------------------------------------------

    public override async Task LoadAsync()
    {
        _statusPending = false;
        ApplyStatus();
        await LoadLastCommitAsync().ConfigureAwait(true);
    }

    /// <summary>The shared status changed (called by the section on the UI thread).</summary>
    internal void OnStatusChanged()
    {
        if (IsActive)
        {
            ApplyStatus();
        }
        else
        {
            _statusPending = true;
        }
    }

    protected override void OnReactivated()
    {
        if (_statusPending)
        {
            _statusPending = false;
            ApplyStatus();
        }
    }

    internal bool ContainsPath(string path) => _items.Keys.Any(k => string.Equals(k.Path, path, StringComparison.Ordinal));

    /// <summary>Rebuilds the list from <see cref="ProjectContext.GitStatus"/>, keeping item instances, selection and scroll.</summary>
    internal void ApplyStatus()
    {
        var status = Context.GitStatus;
        HasStatus = status is not null;
        var previousRows = Rows.ToList();
        var previousSelection = _selection;

        var conflicted = Order(status?.Conflicted);
        var staged = Order(status?.Staged);
        var unstaged = Order(status?.Unstaged);

        var rows = new List<ChangeListRow>(conflicted.Count + staged.Count + unstaged.Count + 3);
        var live = new HashSet<(ChangeGroup, string)>();
        AddGroup(rows, live, _conflictHeader, conflicted);
        AddGroup(rows, live, _stagedHeader, staged);
        AddGroup(rows, live, _unstagedHeader, unstaged);
        foreach (var key in _items.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _items.Remove(key);
        }

        _rebuilding = true;
        try
        {
            Rows.SyncWith(rows);
        }
        finally
        {
            _rebuilding = false;
        }

        ConflictCount = conflicted.Count;
        StagedCount = staged.Count;
        UnstagedCount = unstaged.Count;
        FileCount = status?.Entries.Count ?? 0;
        BranchName = status?.Branch;
        Upstream = status?.Upstream;
        IsUnborn = status?.IsUnborn == true;
        IsDetached = status?.IsDetached == true;
        DetachedMessage = status is { IsDetached: true }
            ? $"HEAD points at commit {Short(status.HeadSha)}, not at a branch. Commits you make here won't belong to any branch until you create one."
            : null;
        RepositoryState = status?.State ?? GitRepositoryState.Normal;
        OnPropertyChanged(nameof(OperationMessage));

        var selection = RestoreSelection(previousSelection, previousRows, rows);
        if (selection.Count == 0 && _selectFirstOnNextStatus)
        {
            selection = rows.OfType<ChangeItemViewModel>().Take(1).ToList();
        }

        if (status is not null)
        {
            _selectFirstOnNextStatus = false;
        }

        ApplySelection(selection, reloadDiff: true, fromView: false);
    }

    /// <summary>Selection made in the view (list order is restored here).</summary>
    public void SetSelection(IEnumerable<ChangeItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_rebuilding)
        {
            return;
        }

        var chosen = items.ToHashSet();
        var ordered = Rows.OfType<ChangeItemViewModel>().Where(chosen.Contains).ToList();
        if (ordered.SequenceEqual(_selection))
        {
            return;
        }

        ApplySelection(ordered, reloadDiff: false, fromView: true);
    }

    /// <summary>Raised when the view model changed the selection itself (restore after refresh, navigation): the view mirrors it.</summary>
    public event EventHandler? SelectionRestored;

    /// <summary>Asks the view to put the keyboard focus in the commit summary.</summary>
    public event EventHandler? FocusSummaryRequested;

    /// <summary>
    /// A focus request the view has not handled yet (the view may be created after the request,
    /// e.g. when the palette opens the Git tab for the first time).
    /// </summary>
    public bool IsSummaryFocusPending { get; private set; }

    /// <summary>The view focused the summary box.</summary>
    public void AcknowledgeSummaryFocus() => IsSummaryFocusPending = false;

    internal override Task NavigateAsync(GitNavigation navigation)
    {
        if (navigation.Path is { } path)
        {
            var matches = Rows.OfType<ChangeItemViewModel>().Where(i => string.Equals(i.Path, path, StringComparison.Ordinal)).Take(1).ToList();
            if (matches.Count > 0)
            {
                ApplySelection(matches, reloadDiff: false, fromView: false);
            }
        }

        if (navigation.CommitMessage is { Length: > 0 } message && string.IsNullOrWhiteSpace(Summary))
        {
            var (subject, body) = SplitMessage(message);
            Summary = subject;
            Description = body;
        }

        if (navigation.FocusCommitMessage)
        {
            IsSummaryFocusPending = true;
            FocusSummaryRequested?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    private void AddGroup(List<ChangeListRow> rows, HashSet<(ChangeGroup, string)> live, ChangeGroupHeader header, IReadOnlyList<GitStatusEntry> entries)
    {
        header.Count = entries.Count;
        if (entries.Count == 0)
        {
            return;
        }

        rows.Add(header);
        foreach (var entry in entries)
        {
            var key = (header.Group, entry.Path);
            if (_items.TryGetValue(key, out var item))
            {
                item.Update(entry);
            }
            else
            {
                item = new ChangeItemViewModel(header.Group, entry);
                _items[key] = item;
            }

            live.Add(key);
            rows.Add(item);
        }
    }

    private static List<GitStatusEntry> Order(IEnumerable<GitStatusEntry>? entries) =>
        entries?.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal).ToList() ?? [];

    /// <summary>
    /// Keeps the rows that still exist, follows a file that moved to another group (staged ↔ not
    /// staged), and when every selected file is gone selects the row that took the place of the first one.
    /// </summary>
    private static List<ChangeItemViewModel> RestoreSelection(IReadOnlyList<ChangeItemViewModel> previous, List<ChangeListRow> previousRows, List<ChangeListRow> rows)
    {
        if (previous.Count == 0)
        {
            return [];
        }

        var items = rows.OfType<ChangeItemViewModel>().ToList();
        var present = items.ToHashSet();
        var restored = new List<ChangeItemViewModel>();
        foreach (var old in previous)
        {
            if (present.Contains(old))
            {
                restored.Add(old);
                continue;
            }

            var moved = items.FirstOrDefault(i => string.Equals(i.Path, old.Path, StringComparison.Ordinal) && !previous.Contains(i));
            if (moved is not null)
            {
                restored.Add(moved);
            }
        }

        if (restored.Count > 0)
        {
            return items.Where(restored.Contains).ToList();
        }

        var index = previousRows.IndexOf(previous[0]);
        if (index < 0 || items.Count == 0)
        {
            return [];
        }

        var replacement = rows.Skip(index).OfType<ChangeItemViewModel>().FirstOrDefault() ?? items[^1];
        return [replacement];
    }

    private void ApplySelection(IReadOnlyList<ChangeItemViewModel> items, bool reloadDiff, bool fromView)
    {
        var changed = !items.SequenceEqual(_selection);
        _selection = items;
        if (changed)
        {
            OnPropertyChanged(nameof(SelectedItems));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasMultipleSelection));
            OnPropertyChanged(nameof(SelectedStagedCount));
            OnPropertyChanged(nameof(SelectedUnstagedCount));
            OnPropertyChanged(nameof(SelectionTitle));
            OnPropertyChanged(nameof(SelectionDetail));
            OnPropertyChanged(nameof(SelectionStageText));
            OnPropertyChanged(nameof(SelectionUnstageText));
            OnPropertyChanged(nameof(ShowDiff));
            OnPropertyChanged(nameof(ShowDiffView));
        }

        if (!fromView)
        {
            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }

        var primary = items.Count == 1 ? items[0] : null;
        if (!ReferenceEquals(primary, DiffItem))
        {
            DiffItem = primary;
            LoadDiff(primary, keepCurrent: false);
        }
        else if (reloadDiff && primary is not null)
        {
            LoadDiff(primary, keepCurrent: true);
        }
    }

    private IReadOnlyList<ChangeItemViewModel> Targets(ChangeItemViewModel? item) =>
        item is null ? _selection : _selection.Contains(item) ? _selection : [item];

    // ----- Diff loading -----------------------------------------------------------------

    /// <summary>Loads the diff of <paramref name="item"/>, cancelling any load in flight so a fast selection never shows a stale diff.</summary>
    private void LoadDiff(ChangeItemViewModel? item, bool keepCurrent)
    {
        _diffLoad?.Cancel();
        _diffLoad = null;
        if (item is null || item.IsConflicted || IsDisposed)
        {
            Diff = null;
            DiffError = null;
            IsDiffLoading = false;
            return;
        }

        var load = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _diffLoad = load;
        _ = LoadDiffAsync(item, keepCurrent, load);
    }

    private async Task LoadDiffAsync(ChangeItemViewModel item, bool keepCurrent, CancellationTokenSource load)
    {
        if (!keepCurrent)
        {
            Diff = null;
        }

        DiffError = null;
        IsDiffLoading = true;
        try
        {
            var target = item.IsStaged ? DiffTarget.Staged : DiffTarget.WorkingTree;
            var diff = await Git.GetFileDiffAsync(Root, item.Path, target, load.Token).ConfigureAwait(true);
            if (load.IsCancellationRequested || !ReferenceEquals(_diffLoad, load))
            {
                return;
            }

            if (!DiffContent.Same(Diff, diff))
            {
                Diff = diff;
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // Replaced by a newer selection, or the project was closed.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_diffLoad, load))
            {
                Diff = null;
                DiffError = ErrorInfo.From(ex, "Could not load the diff");
            }
        }
        finally
        {
            if (ReferenceEquals(_diffLoad, load))
            {
                _diffLoad = null;
                IsDiffLoading = false;
            }

            load.Dispose();
        }
    }

    [RelayCommand]
    private void RetryDiff() => LoadDiff(DiffItem, keepCurrent: false);

    /// <summary>Stages a hunk of an unstaged diff, or unstages a hunk of a staged diff.</summary>
    [RelayCommand]
    private async Task ApplyHunkAsync(DiffHunk? hunk)
    {
        if (hunk is null || Diff is not { } diff || DiffItem is not { IsConflicted: false } item)
        {
            return;
        }

        var reverse = item.IsStaged;
        await RunActionAsync(async () =>
        {
            await Git.ApplyHunkAsync(Root, diff, hunk, reverse, Section.Lifetime).ConfigureAwait(true);
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
        }, reverse ? "Could not unstage the hunk" : "Could not stage the hunk").ConfigureAwait(true);
    }

    // ----- File actions -----------------------------------------------------------------

    [RelayCommand]
    private Task StageAsync(ChangeItemViewModel? item) => StageItemsAsync(Targets(item).Where(i => !i.IsStaged).ToList());

    [RelayCommand]
    private Task UnstageAsync(ChangeItemViewModel? item) => UnstageItemsAsync(Targets(item).Where(i => i.IsStaged).ToList());

    /// <summary>Space: unstages the selection when all of it is staged, stages it otherwise.</summary>
    [RelayCommand]
    private Task ToggleStagingAsync()
    {
        var targets = _selection;
        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        return targets.All(i => i.IsStaged)
            ? UnstageItemsAsync(targets)
            : StageItemsAsync(targets.Where(i => !i.IsStaged).ToList());
    }

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private Task StageAllAsync() => RunActionAsync(async () =>
    {
        await Git.StageAllAsync(Root, Section.Lifetime).ConfigureAwait(true);
        await Context.RefreshGitStatusAsync().ConfigureAwait(true);
    }, "Could not stage the changes");

    private bool CanStageAll() => UnstagedCount + ConflictCount > 0;

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    private Task UnstageAllAsync() => RunActionAsync(async () =>
    {
        await Git.UnstageAllAsync(Root, Section.Lifetime).ConfigureAwait(true);
        await Context.RefreshGitStatusAsync().ConfigureAwait(true);
    }, "Could not unstage the changes");

    private bool CanUnstageAll() => StagedCount > 0;

    [RelayCommand]
    private async Task DiscardAsync(ChangeItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        var paths = targets.Select(t => t.Path).Distinct(StringComparer.Ordinal).ToList();
        var entries = EntriesFor(paths);
        var created = entries.Count(IsNewFile);
        var title = paths.Count == 1 ? $"Discard changes to {GitFileStates.FileName(paths[0])}?" : $"Discard changes to {paths.Count} files?";
        var message = (paths.Count == 1
                ? $"All changes to {paths[0]}, staged or not, will be lost."
                : $"All changes to these files, staged or not, will be lost:\n{ListPaths(paths)}")
            + (created == 0 ? string.Empty : created == 1 && paths.Count == 1 ? "\n\nThis new file will be deleted." : $"\n\n{Format.Count(created, "new file")} will be deleted.")
            + "\n\nThis can't be undone.";

        if (!await ConfirmAsync(title, message, "Discard changes").ConfigureAwait(true))
        {
            return;
        }

        await DiscardPathsAsync(paths, paths.Count == 1 ? $"Discarded changes to {paths[0]}" : $"Discarded changes to {paths.Count} files").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDiscardAll))]
    private async Task DiscardAllAsync()
    {
        var entries = Context.GitStatus?.Entries ?? [];
        if (entries.Count == 0)
        {
            return;
        }

        var created = entries.Count(IsNewFile);
        var deleted = entries.Count(e => !IsNewFile(e) && (e.IndexState == GitFileState.Deleted || e.WorkTreeState == GitFileState.Deleted));
        var conflicted = entries.Count(e => e.IsConflicted);
        var modified = entries.Count - created - deleted - conflicted;
        var parts = new List<string>();
        if (modified > 0)
        {
            parts.Add($"{modified} modified (restored)");
        }

        if (created > 0)
        {
            parts.Add($"{created} new (deleted)");
        }

        if (deleted > 0)
        {
            parts.Add($"{deleted} deleted (brought back)");
        }

        if (conflicted > 0)
        {
            parts.Add($"{conflicted} conflicted");
        }

        var message = $"Every change in this repository will be lost: {Format.Count(entries.Count, "file")}, {string.Join(", ", parts)}."
            + (StagedCount > 0 ? " Staged changes are discarded too." : string.Empty)
            + "\n\nThis can't be undone. To keep the changes aside instead, stash them.";
        if (!await ConfirmAsync("Discard all changes?", message, $"Discard {Format.Count(entries.Count, "file")}").ConfigureAwait(true))
        {
            return;
        }

        await DiscardPathsAsync(entries.Select(e => e.Path).Distinct(StringComparer.Ordinal).ToList(),
            $"Discarded all changes ({Format.Count(entries.Count, "file")})").ConfigureAwait(true);
    }

    private bool CanDiscardAll() => FileCount > 0;

    [RelayCommand]
    private void OpenFile(ChangeItemViewModel? item)
    {
        var target = item ?? DiffItem ?? _selection.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        TryShell(() =>
        {
            var path = PathUtil.ResolveUnder(Root, target.Path);
            if (!File.Exists(path))
            {
                Notify("This file no longer exists", $"{target.Path} was deleted, so there is nothing to open.", NotificationSeverity.Info);
                return;
            }

            Section.Shell.OpenInEditor(path);
        }, "Could not open the file");
    }

    [RelayCommand]
    private void Reveal(ChangeItemViewModel? item)
    {
        var target = item ?? _selection.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        TryShell(() =>
        {
            var path = PathUtil.ResolveUnder(Root, target.Path);
            if (File.Exists(path) || System.IO.Directory.Exists(path))
            {
                Section.Shell.RevealInExplorer(path);
            }
            else
            {
                Section.Shell.OpenFolder(ExistingFolder(Path.GetDirectoryName(path)) ?? Root);
            }
        }, "Could not open File Explorer");
    }

    [RelayCommand]
    private void CopyPath(ChangeItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            var paths = targets.Select(t => PathUtil.ResolveUnder(Root, t.Path)).Distinct(StringComparer.Ordinal);
            Copy(string.Join(Environment.NewLine, paths), targets.Count == 1 ? "Path" : "Paths");
        }
        catch (Exception ex)
        {
            ShowError(ex, "Could not copy the path");
        }
    }

    [RelayCommand]
    private void CopyRelativePath(ChangeItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count > 0)
        {
            Copy(string.Join(Environment.NewLine, targets.Select(t => t.Path).Distinct(StringComparer.Ordinal)),
                targets.Count == 1 ? "Relative path" : "Relative paths");
        }
    }

    [RelayCommand]
    private void ShowInFiles(ChangeItemViewModel? item)
    {
        var target = item ?? _selection.FirstOrDefault();
        if (target is not null)
        {
            Context.RequestNavigation(WorkspaceSection.Files, target.Path);
        }
    }

    [RelayCommand]
    private Task ViewFileHistoryAsync(ChangeItemViewModel? item)
    {
        var target = item ?? _selection.FirstOrDefault();
        return target is null ? Task.CompletedTask : Section.Navigate(GitNavigation.FileHistory(target.Path));
    }

    [RelayCommand]
    private Task StashChangesAsync() => Section.Navigate(GitNavigation.StashChanges());

    [RelayCommand]
    private void OpenRepositoryFolder() => TryShell(() => Section.Shell.OpenFolder(Root), "Could not open the folder");

    [RelayCommand]
    private Task CleanActionAsync()
    {
        if (IsUnborn)
        {
            OpenRepositoryFolder();
            return Task.CompletedTask;
        }

        return Section.Navigate(GitNavigation.History());
    }

    // ----- Banners ----------------------------------------------------------------------

    [RelayCommand]
    private async Task AbortMergeAsync()
    {
        var confirmed = await ConfirmAsync("Abort the merge?",
            "The merge stops and the branch goes back to how it was before it started. Conflict resolutions you made are lost.",
            "Abort merge").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await Git.AbortMergeAsync(Root, Section.Lifetime).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.WarningAsync(ActivityKind.GitMerge, "Aborted a merge", null, BranchName).ConfigureAwait(true);
            ClearCommitBox();
            Notify("Merge aborted", BranchName is null ? null : $"{BranchName} is back to how it was before the merge.");
        }, "Could not abort the merge").ConfigureAwait(true);
    }

    /// <summary>Detached HEAD banner: creates a branch at the current commit and switches to it.</summary>
    [RelayCommand]
    private async Task CreateBranchHereAsync()
    {
        string? name;
        try
        {
            var existing = await Git.GetBranchesAsync(Root, includeRemote: false, Section.Lifetime).ConfigureAwait(true);
            var names = existing.Select(b => b.Name).ToArray();
            name = await Section.Dialogs.PromptAsync(new PromptOptions
            {
                Title = "Create branch here",
                Message = $"The new branch starts at {Short(Context.GitStatus?.HeadSha)} and becomes the current branch.",
                Placeholder = "feature/short-description",
                ConfirmText = "Create branch",
                Validate = value => BranchNames.Validate(value, names),
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            ShowError(ex, "Could not create the branch");
            return;
        }
        catch (Exception)
        {
            return;
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await Git.CreateBranchAsync(Root, trimmed, startPoint: null, checkout: true, Section.Lifetime).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitBranchCreated, $"Created branch {trimmed}", "From a detached HEAD.", trimmed)
                .ConfigureAwait(true);
            Notify($"Created and switched to {trimmed}", "Commits you make now belong to this branch.");
        }, "Could not create the branch").ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenTerminal() => TryShell(() => Section.Shell.OpenExternalTerminal(Root), "Could not open a terminal");

    // ----- Helpers ----------------------------------------------------------------------

    private async Task StageItemsAsync(IReadOnlyList<ChangeItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var paths = items.Select(i => i.Path).Distinct(StringComparer.Ordinal).ToList();
        await RunActionAsync(async () =>
        {
            await Git.StageAsync(Root, paths, Section.Lifetime).ConfigureAwait(true);
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
        }, paths.Count == 1 ? $"Could not stage {GitFileStates.FileName(paths[0])}" : "Could not stage the files").ConfigureAwait(true);
    }

    private async Task UnstageItemsAsync(IReadOnlyList<ChangeItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var paths = items.Select(i => i.Path).Distinct(StringComparer.Ordinal).ToList();
        await RunActionAsync(async () =>
        {
            await Git.UnstageAsync(Root, paths, Section.Lifetime).ConfigureAwait(true);
            await Context.RefreshGitStatusAsync().ConfigureAwait(true);
        }, paths.Count == 1 ? $"Could not unstage {GitFileStates.FileName(paths[0])}" : "Could not unstage the files").ConfigureAwait(true);
    }

    private Task DiscardPathsAsync(IReadOnlyList<string> paths, string title) => RunActionAsync(async () =>
    {
        await Git.DiscardAsync(Root, paths, Section.Lifetime).ConfigureAwait(true);
        await Section.Activity.SucceededAsync(ActivityKind.GitDiscard, title, paths.Count == 1 ? null : ListPaths(paths), BranchName)
            .ConfigureAwait(true);
        await Context.RefreshGitStatusAsync().ConfigureAwait(true);
        Notify(title);
    }, "Could not discard the changes");

    private List<GitStatusEntry> EntriesFor(IReadOnlyList<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        return (Context.GitStatus?.Entries ?? []).Where(e => wanted.Contains(e.Path)).ToList();
    }

    /// <summary>Discarding a file that does not exist in HEAD deletes it.</summary>
    private static bool IsNewFile(GitStatusEntry entry) =>
        entry.IsUntracked || (!entry.IsConflicted && entry.IndexState is GitFileState.Added or GitFileState.Renamed or GitFileState.Copied);

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        try
        {
            return await Section.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = title,
                Message = message,
                ConfirmText = confirmText,
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            ShowError(ex, "Could not ask for confirmation");
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task LoadLastCommitAsync()
    {
        if (Context.GitStatus is not { } status || status.IsUnborn)
        {
            LastCommit = null;
            return;
        }

        try
        {
            var log = await Git.GetLogAsync(Root, new GitLogQuery { Take = 1 }, Section.Lifetime).ConfigureAwait(true);
            LastCommit = log.Count > 0 ? log[0] : null;
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // Only decorates the clean state; the rest of the view works without it.
            LastCommit = null;
            System.Diagnostics.Trace.TraceWarning($"Could not read the last commit: {ex.Message}");
        }
        catch (Exception)
        {
            // The project was closed.
        }
    }

    private void TryShell(Action action, string errorTitle)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ShowError(ex, errorTitle);
        }
    }

    internal static string ListPaths(IReadOnlyList<string> paths)
    {
        var listed = paths.Take(MaxListedFiles).Select(p => "  • " + p);
        var text = string.Join('\n', listed);
        return paths.Count > MaxListedFiles ? $"{text}\n  … and {paths.Count - MaxListedFiles} more" : text;
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "an unknown commit" : sha[..Math.Min(7, sha.Length)];

    private static string? ExistingFolder(string? path)
    {
        while (!string.IsNullOrEmpty(path) && !System.IO.Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path);
        }

        return string.IsNullOrEmpty(path) ? null : path;
    }

    protected override void OnDisposed()
    {
        _diffLoad?.Cancel();
        _diffLoad = null;
    }
}
