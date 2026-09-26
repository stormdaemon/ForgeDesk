using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// Details of one commit (or stash): message, author and committer, parents, changed files with
/// their line counts, and the read-only diff of the selected file. Loads are cancelled when the
/// details are replaced, so switching commits quickly never shows a stale file list or diff.
/// </summary>
public sealed partial class CommitDetailsViewModel : ObservableObject, IDisposable
{
    private readonly GitSectionContext _section;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _diffLoad;
    private bool _disposed;

    public CommitDetailsViewModel(GitSectionContext section, string revision, GitCommit? commit = null, IReadOnlyList<RefPill>? refs = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        _section = section;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(section.Lifetime);
        Revision = revision;
        Commit = commit;
        Refs = refs ?? [];
    }

    /// <summary>Branches and tags pointing at the commit.</summary>
    public IReadOnlyList<RefPill> Refs { get; }

    public bool HasRefs => Refs.Count > 0;

    /// <summary>What is shown: a commit SHA or a stash name ("stash@{0}").</summary>
    public string Revision { get; }

    /// <summary>Stash contents: the commit information (message, author, parents) is git's bookkeeping, not shown.</summary>
    public bool IsStash => Revision.StartsWith("stash@", StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sha), nameof(ShortSha), nameof(Subject), nameof(Body), nameof(HasBody), nameof(AuthorName), nameof(AuthorEmail),
        nameof(AuthorDate), nameof(AuthorDateText), nameof(CommitterText), nameof(HasDistinctCommitter), nameof(Parents), nameof(HasParents),
        nameof(FullMessage))]
    public partial GitCommit? Commit { get; private set; }

    public string Sha => Commit?.Sha ?? Revision;

    public string ShortSha => Commit?.ShortSha ?? Revision;

    public string Subject => Commit?.Subject ?? string.Empty;

    public string Body => Commit?.Body ?? string.Empty;

    public bool HasBody => Body.Length > 0;

    public string FullMessage => HasBody ? $"{Subject}\n\n{Body}" : Subject;

    public string AuthorName => Commit?.Author.Name ?? string.Empty;

    public string AuthorEmail => Commit?.Author.Email ?? string.Empty;

    public DateTimeOffset? AuthorDate => Commit?.Author.When;

    public string AuthorDateText => Commit is { } c ? Format.Timestamp(c.Author.When) : string.Empty;

    /// <summary>Shown when someone else committed (rebase, cherry-pick, web merge) or much later.</summary>
    public bool HasDistinctCommitter => Commit is { Committer: { } committer } c
        && (!string.Equals(committer.Name, c.Author.Name, StringComparison.Ordinal)
            || !string.Equals(committer.Email, c.Author.Email, StringComparison.OrdinalIgnoreCase)
            || (committer.When - c.Author.When).Duration() > TimeSpan.FromMinutes(1));

    public string CommitterText => Commit?.Committer is { } committer
        ? $"Committed by {committer.Name} · {Format.Timestamp(committer.When)}"
        : string.Empty;

    public IReadOnlyList<ParentLink> Parents => Commit?.Parents.Select(p => new ParentLink(p)).ToList() ?? [];

    public bool HasParents => Commit?.Parents.Count > 0;

    public ObservableCollection<CommitFileViewModel> Files { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingFiles { get; private set; }

    [ObservableProperty]
    public partial ErrorInfo? FilesError { get; private set; }

    /// <summary>"3 files changed · +120 −45".</summary>
    [ObservableProperty]
    public partial string? StatsText { get; private set; }

    [ObservableProperty]
    public partial CommitFileViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial FileDiff? Diff { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingDiff { get; private set; }

    [ObservableProperty]
    public partial ErrorInfo? DiffError { get; private set; }

    public bool HasNoFiles => !IsLoadingFiles && FilesError is null && Files.Count == 0;

    partial void OnIsLoadingFilesChanged(bool value) => OnPropertyChanged(nameof(HasNoFiles));

    partial void OnFilesErrorChanged(ErrorInfo? value) => OnPropertyChanged(nameof(HasNoFiles));

    partial void OnSelectedFileChanged(CommitFileViewModel? value) => LoadDiff(value);

    /// <summary>Loads the commit and its changed files, then selects the first file.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsLoadingFiles = true;
        FilesError = null;
        try
        {
            var details = await _section.Git.GetCommitDetailsAsync(_section.Root, Revision, _lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            Commit = details.Commit;
            var largest = details.Files.Count == 0 ? 0 : details.Files.Max(f => f.Additions + f.Deletions);
            Files.SyncWith(details.Files.Select(f => new CommitFileViewModel(f, largest)).ToList());
            var additions = details.Files.Sum(f => f.Additions);
            var deletions = details.Files.Sum(f => f.Deletions);
            StatsText = $"{Format.Count(details.Files.Count, "file")} changed · +{additions} −{deletions}";
            SelectedFile = Files.FirstOrDefault();
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // Another commit was selected, or the project was closed.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                FilesError = ErrorInfo.From(ex, "Could not load the commit");
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsLoadingFiles = false;
                OnPropertyChanged(nameof(HasNoFiles));
            }
        }
    }

    [RelayCommand]
    private void RetryDiff() => LoadDiff(SelectedFile);

    [RelayCommand]
    private void CopySha() => Copy(Sha, "Commit SHA");

    [RelayCommand]
    private void CopyMessage() => Copy(FullMessage, "Commit message");

    [RelayCommand]
    private void OpenFileInEditor(CommitFileViewModel? file)
    {
        var target = file ?? SelectedFile;
        if (target is null)
        {
            return;
        }

        try
        {
            var path = PathUtil.ResolveUnder(_section.Root, target.Path);
            if (!File.Exists(path))
            {
                _section.Notifications.Show("This file no longer exists", $"{target.Path} isn't in the working tree anymore.", NotificationSeverity.Info);
                return;
            }

            _section.Shell.OpenInEditor(path);
        }
        catch (Exception ex)
        {
            _section.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the file"));
        }
    }

    [RelayCommand]
    private Task ViewFileHistoryAsync(CommitFileViewModel? file)
    {
        var target = file ?? SelectedFile;
        return target is null ? Task.CompletedTask : _section.Navigate(GitNavigation.FileHistory(target.Path));
    }

    /// <summary>A parent link: shows that commit in History.</summary>
    [RelayCommand]
    private Task ShowParentAsync(ParentLink? parent) =>
        parent is null ? Task.CompletedTask : _section.Navigate(GitNavigation.History(parent.Sha));

    [RelayCommand]
    private void CopyFilePath(CommitFileViewModel? file)
    {
        var target = file ?? SelectedFile;
        if (target is not null)
        {
            Copy(target.Path, "Relative path");
        }
    }

    private void LoadDiff(CommitFileViewModel? file)
    {
        _diffLoad?.Cancel();
        _diffLoad = null;
        DiffError = null;
        if (file is null || _disposed)
        {
            Diff = null;
            IsLoadingDiff = false;
            return;
        }

        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diffLoad = load;
        _ = LoadDiffAsync(file, load);
    }

    private async Task LoadDiffAsync(CommitFileViewModel file, CancellationTokenSource load)
    {
        Diff = null;
        IsLoadingDiff = true;
        try
        {
            var diff = await _section.Git.GetCommitFileDiffAsync(_section.Root, Revision, file.Path, load.Token).ConfigureAwait(true);
            if (!load.IsCancellationRequested && ReferenceEquals(_diffLoad, load))
            {
                Diff = diff;
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // Another file was selected.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_diffLoad, load))
            {
                DiffError = ErrorInfo.From(ex, "Could not load the diff");
            }
        }
        finally
        {
            if (ReferenceEquals(_diffLoad, load))
            {
                _diffLoad = null;
                IsLoadingDiff = false;
            }

            load.Dispose();
        }
    }

    private void Copy(string text, string what)
    {
        try
        {
            _section.Shell.CopyToClipboard(text);
            _section.Notifications.Show($"{what} copied", text.Length > 120 ? text[..117] + "…" : text, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _section.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _diffLoad?.Cancel();
        _diffLoad = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
