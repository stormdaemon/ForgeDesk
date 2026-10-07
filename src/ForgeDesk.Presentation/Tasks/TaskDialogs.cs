using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>Base of the Tasks tab's pickers: title, Confirm / Cancel and the close request.</summary>
public abstract partial class TaskDialogViewModel : ObservableObject, IDialogViewModel
{
    public abstract string Title { get; }

    public virtual double PreferredWidth => 560;

    public virtual double? PreferredHeight => null;

    public event EventHandler<bool?>? CloseRequested;

    [ObservableProperty]
    public partial string? ValidationMessage { get; protected set; }

    [RelayCommand]
    protected void Confirm()
    {
        ValidationMessage = Validate();
        if (ValidationMessage is null)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected abstract string? Validate();
}

/// <summary>A commit of the "Link commit" picker.</summary>
public sealed record CommitChoice(GitCommit Commit)
{
    public string Sha => Commit.Sha;

    public string ShortSha => Commit.ShortSha;

    public string Subject => Commit.Subject;

    public string Author => Commit.Author.Name;

    public DateTimeOffset When => Commit.Author.When;
}

/// <summary>"Link commit": the last 50 commits of the current branch, filtered by subject, author or SHA.</summary>
public sealed partial class PickCommitDialogViewModel : TaskDialogViewModel
{
    public const int CommitCount = 50;

    private readonly IReadOnlyList<CommitChoice> _all;

    public PickCommitDialogViewModel(IReadOnlyList<GitCommit> commits, string taskKey)
    {
        ArgumentNullException.ThrowIfNull(commits);
        _all = commits.Select(c => new CommitChoice(c)).ToList();
        TaskKey = taskKey;
        Filter();
    }

    public override string Title => "Link a commit";

    public override double? PreferredHeight => 560;

    public string TaskKey { get; }

    public string Explanation => $"Pick a commit of the current branch to link to {TaskKey}.";

    public ObservableCollection<CommitChoice> Commits { get; } = [];

    public bool HasCommits => _all.Count > 0;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommitChoice? SelectedCommit { get; set; }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnSelectedCommitChanged(CommitChoice? value) => ValidationMessage = null;

    /// <summary>Double-click / Enter on a commit.</summary>
    [RelayCommand]
    private void Choose(CommitChoice? choice)
    {
        if (choice is not null)
        {
            SelectedCommit = choice;
            Confirm();
        }
    }

    protected override string? Validate() => SelectedCommit is null ? "Select a commit." : null;

    private void Filter()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _all.Where(c => terms.All(t =>
            c.Subject.Contains(t, StringComparison.OrdinalIgnoreCase)
            || c.Author.Contains(t, StringComparison.OrdinalIgnoreCase)
            || c.Sha.StartsWith(t, StringComparison.OrdinalIgnoreCase))).ToList();
        Commits.SyncWith(matches);
        if (SelectedCommit is null || !matches.Contains(SelectedCommit))
        {
            SelectedCommit = matches.FirstOrDefault();
        }
    }
}

/// <summary>A file of the "Link file" picker.</summary>
public sealed record FileChoice(string RelativePath)
{
    public string Name => RelativePath.Contains('/', StringComparison.Ordinal) ? RelativePath[(RelativePath.LastIndexOf('/') + 1)..] : RelativePath;

    public string Folder => RelativePath.Contains('/', StringComparison.Ordinal) ? RelativePath[..RelativePath.LastIndexOf('/')] : string.Empty;
}

/// <summary>"Link file": fuzzy search over the project's file index.</summary>
public sealed partial class PickFileDialogViewModel : TaskDialogViewModel
{
    public const int MaxResults = 100;

    private readonly IFileIndex _index;
    private readonly string _root;
    private FileIndexSnapshot? _snapshot;

    public PickFileDialogViewModel(IFileIndex index, string projectRoot, string taskKey)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _root = projectRoot;
        TaskKey = taskKey;
    }

    public override string Title => "Link a file";

    public override double? PreferredHeight => 560;

    public string TaskKey { get; }

    public ObservableCollection<FileChoice> Files { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FileChoice? SelectedFile { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Reads the file index (called when the dialog opens).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            _snapshot = await _index.GetAsync(_root, cancellationToken: cancellationToken).ConfigureAwait(true);
            LoadError = null;
            Search();
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            LoadError = ErrorInfo.From(ex, "Could not list the project's files").Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value) => Search();

    partial void OnSelectedFileChanged(FileChoice? value) => ValidationMessage = null;

    [RelayCommand]
    private void Choose(FileChoice? choice)
    {
        if (choice is not null)
        {
            SelectedFile = choice;
            Confirm();
        }
    }

    protected override string? Validate() => SelectedFile is null ? "Select a file." : null;

    private void Search()
    {
        if (_snapshot is null)
        {
            return;
        }

        var query = SearchText.Trim();
        var paths = query.Length == 0
            ? _snapshot.Files.Take(MaxResults).ToList()
            : _index.Search(_snapshot, query, MaxResults).Select(m => m.RelativePath).ToList();
        var choices = paths.Select(p => new FileChoice(p)).ToList();
        Files.Clear();
        foreach (var choice in choices)
        {
            Files.Add(choice);
        }

        SelectedFile = Files.FirstOrDefault();
        Summary = query.Length == 0
            ? $"{Format.Count(_snapshot.Files.Count, "file")} in the project — type to search"
            : choices.Count == 0 ? "No file matches" : Format.Count(choices.Count, "match", "matches");
    }
}

/// <summary>What to do with uncommitted changes before switching to a task's branch.</summary>
public enum DirtyTreeChoice
{
    Cancel,
    StashAndSwitch,
    CreateWithoutSwitching,
}

/// <summary>"You have uncommitted changes": stash and switch, or create the branch without switching.</summary>
public sealed partial class DirtyTreeDialogViewModel : ObservableObject, IDialogViewModel
{
    public DirtyTreeDialogViewModel(string branch, int changedFiles, bool branchExists)
    {
        Branch = branch;
        ChangedFiles = changedFiles;
        BranchExists = branchExists;
    }

    public string Title => "You have uncommitted changes";

    public double PreferredWidth => 520;

    public double? PreferredHeight => null;

    public string Branch { get; }

    public int ChangedFiles { get; }

    public bool BranchExists { get; }

    public string Explanation =>
        $"{Format.Count(ChangedFiles, "file")} {(ChangedFiles == 1 ? "has" : "have")} uncommitted changes. Stash them to switch to {Branch} "
        + "with a clean working tree (restore them later from Git › Stashes), or "
        + (BranchExists ? "stay on the current branch." : "create the branch without switching to it.");

    public string SecondaryText => BranchExists ? "Stay on this branch" : "Create without switching";

    public DirtyTreeChoice Choice { get; private set; } = DirtyTreeChoice.Cancel;

    public event EventHandler<bool?>? CloseRequested;

    [RelayCommand]
    private void StashAndSwitch() => Close(DirtyTreeChoice.StashAndSwitch);

    [RelayCommand]
    private void CreateWithoutSwitching() => Close(DirtyTreeChoice.CreateWithoutSwitching);

    [RelayCommand]
    private void Cancel() => Close(DirtyTreeChoice.Cancel);

    private void Close(DirtyTreeChoice choice)
    {
        Choice = choice;
        CloseRequested?.Invoke(this, choice != DirtyTreeChoice.Cancel);
    }
}
