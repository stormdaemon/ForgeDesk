using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>
/// The details pane of a task. Every edit is saved automatically: choices (status, priority,
/// labels, due date, links) at once, typed text (title, description) after a short pause or when
/// the pane closes.
/// </summary>
public sealed partial class TaskDetailViewModel : ViewModelBase, IDisposable
{
    /// <summary>Events shown in the timeline (newest first).</summary>
    public const int MaxHistory = 100;

    private readonly TasksSectionViewModel _owner;
    private CancellationTokenSource? _textSave;
    private bool _applying;
    private bool _titleDirty;
    private bool _descriptionDirty;
    private bool _disposed;

    internal TaskDetailViewModel(TasksSectionViewModel owner, TaskCardViewModel card)
    {
        _owner = owner;
        Card = card;
        Apply(card.Item, force: true);
    }

    public TaskCardViewModel Card { get; }

    public string Id => Card.Id;

    public string Key => Card.Key;

    public IReadOnlyList<TaskStatusOption> StatusOptions => TaskStatusOption.All;

    public IReadOnlyList<TaskPriorityOption> PriorityOptions => TaskPriorityOption.All;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription), nameof(PreviewMarkdown))]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>The description to render as Markdown, only while the preview is shown (no rendering while typing).</summary>
    public string? PreviewMarkdown => IsPreview ? Description : null;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Shows the description rendered as Markdown instead of the editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewMarkdown))]
    public partial bool IsPreview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial WorkItemStatus Status { get; set; }

    public bool IsDone => Status == WorkItemStatus.Done;

    [ObservableProperty]
    public partial WorkItemPriority Priority { get; set; }

    /// <summary>Due day (calendar date picked in the user's time zone), null for none.</summary>
    [ObservableProperty]
    public partial DateTime? DueDate { get; set; }

    public ObservableCollection<string> Labels { get; } = [];

    [ObservableProperty]
    public partial string NewLabelText { get; set; } = string.Empty;

    public ObservableCollection<TaskLinkViewModel> Links { get; } = [];

    [ObservableProperty]
    public partial bool HasLinks { get; private set; }

    public ObservableCollection<TaskEventViewModel> History { get; } = [];

    [ObservableProperty]
    public partial bool IsHistoryLoading { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset CreatedAt { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedText => $"Created {Format.Timestamp(CreatedAt)}";

    public string UpdatedText => $"Updated {Format.RelativeTime(UpdatedAt, _owner.Now)}";

    /// <summary>"Saving…" while an edit is written, "Saved" after, empty before any edit.</summary>
    [ObservableProperty]
    public partial string SaveState { get; private set; } = string.Empty;

    /// <summary>The checked-out branch can be linked (repository, branch not linked yet).</summary>
    public bool CanLinkCurrentBranch => _owner.CurrentBranch is { } branch
        && !Links.Any(l => l.Kind == WorkItemLinkKind.Branch && string.Equals(l.Value, branch, StringComparison.Ordinal));

    public string LinkCurrentBranchText => _owner.CurrentBranch is { } branch ? $"Link current branch ({branch})" : "Link current branch";

    public bool IsGitRepository => _owner.Context.IsGitRepository;

    public bool HasGitHub => _owner.Context.Project.GitHub is not null;

    /// <summary>Applies the latest version of the task, keeping text the user is still typing.</summary>
    internal void Apply(WorkItem item, bool force = false)
    {
        _applying = true;
        try
        {
            if (force || !_titleDirty)
            {
                Title = item.Title;
            }

            if (force || !_descriptionDirty)
            {
                Description = item.Description;
            }

            Status = item.Status;
            Priority = item.Priority;
            DueDate = item.DueAt?.ToLocalTime().Date;
            Labels.SyncValues(item.Labels);
            var gitHub = _owner.Context.Project.GitHub;
            var links = item.Links.Select(l => Links.FirstOrDefault(x => x.Link == l && x.GitHub == gitHub) ?? new TaskLinkViewModel(l, gitHub)).ToList();
            Links.SyncWith(links);
            HasLinks = Links.Count > 0;
            CreatedAt = item.CreatedAt;
            UpdatedAt = item.UpdatedAt;
            OnPropertyChanged(nameof(CreatedText));
            OnPropertyChanged(nameof(UpdatedText));
            RefreshBranchState();
        }
        finally
        {
            _applying = false;
        }
    }

    internal void RefreshBranchState()
    {
        OnPropertyChanged(nameof(CanLinkCurrentBranch));
        OnPropertyChanged(nameof(LinkCurrentBranchText));
        OnPropertyChanged(nameof(IsGitRepository));
        OnPropertyChanged(nameof(HasGitHub));
        LinkCurrentBranchCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadHistoryAsync()
    {
        IsHistoryLoading = true;
        try
        {
            var events = await _owner.Services.WorkItems.GetHistoryAsync(Id, _owner.Context.Lifetime).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            History.Clear();
            foreach (var e in events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(MaxHistory))
            {
                History.Add(new TaskEventViewModel(e));
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            // The timeline is secondary: keep the pane usable.
            System.Diagnostics.Trace.TraceWarning($"Could not load the history of {Key}: {ex.Message}");
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    /// <summary>Saves text edits waiting for their pause (closing the pane, switching task).</summary>
    public async Task FlushAsync()
    {
        _textSave?.Cancel();
        if (!_titleDirty && !_descriptionDirty)
        {
            return;
        }

        var title = _titleDirty ? Title.Trim() : null;
        if (title is { Length: 0 })
        {
            TitleError = "Give the task a title.";
            title = null;
            _titleDirty = false;
        }

        var description = _descriptionDirty ? Description : null;
        _titleDirty = false;
        _descriptionDirty = false;
        if (title is null && description is null)
        {
            return;
        }

        await SaveAsync(new WorkItemPatch { Title = title, Description = description }).ConfigureAwait(true);
    }

    // ----- Field edits ------------------------------------------------------------------

    partial void OnTitleChanged(string value)
    {
        if (_applying)
        {
            return;
        }

        TitleError = string.IsNullOrWhiteSpace(value) ? "Give the task a title." : null;
        _titleDirty = TitleError is null;
        ScheduleTextSave();
    }

    partial void OnDescriptionChanged(string value)
    {
        if (_applying)
        {
            return;
        }

        _descriptionDirty = true;
        ScheduleTextSave();
    }

    partial void OnStatusChanged(WorkItemStatus value)
    {
        if (!_applying && !_disposed && value != Card.Item.Status)
        {
            _ = SaveAsync(new WorkItemPatch { Status = value });
        }
    }

    partial void OnPriorityChanged(WorkItemPriority value)
    {
        if (!_applying && !_disposed && value != Card.Item.Priority)
        {
            _ = SaveAsync(new WorkItemPatch { Priority = value });
        }
    }

    partial void OnDueDateChanged(DateTime? value)
    {
        if (_applying || _disposed || value?.Date == Card.Item.DueAt?.ToLocalTime().Date)
        {
            return;
        }

        _ = SaveAsync(value is { } date
            ? new WorkItemPatch { DueAt = new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(date.Date)) }
            : new WorkItemPatch { ClearDueDate = true });
    }

    [RelayCommand]
    private Task AddLabelAsync()
    {
        var added = NewLabelText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !Labels.Contains(l, StringComparer.OrdinalIgnoreCase))
            .ToList();
        NewLabelText = string.Empty;
        return added.Count == 0 ? Task.CompletedTask : SaveAsync(new WorkItemPatch { Labels = [.. Labels, .. added] });
    }

    [RelayCommand]
    private Task RemoveLabelAsync(string? label) =>
        label is null ? Task.CompletedTask : SaveAsync(new WorkItemPatch { Labels = Labels.Where(l => !string.Equals(l, label, StringComparison.Ordinal)).ToList() });

    [RelayCommand]
    private void ClearDueDate() => DueDate = null;

    [RelayCommand]
    private void TogglePreview() => IsPreview = !IsPreview;

    // ----- Links ------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanLinkCurrentBranch))]
    private Task LinkCurrentBranchAsync() =>
        _owner.CurrentBranch is { } branch ? AddLinkAsync(WorkItemLinkKind.Branch, branch, null) : Task.CompletedTask;

    [RelayCommand]
    private async Task LinkCommitAsync()
    {
        if (!_owner.Context.IsGitRepository)
        {
            _owner.Services.Notifications.Show("Not a Git repository", "Commits can be linked once the project is under version control.", NotificationSeverity.Warning);
            return;
        }

        IReadOnlyList<GitCommit> commits;
        try
        {
            commits = await _owner.Services.Git.GetLogAsync(_owner.Context.Root, new GitLogQuery { Take = PickCommitDialogViewModel.CommitCount },
                _owner.Context.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not read the commits"));
            return;
        }
        catch (Exception)
        {
            return;
        }

        if (commits.Count == 0)
        {
            _owner.Services.Notifications.Show("No commits yet", "Commit your work first, then link the commit to the task.", NotificationSeverity.Info);
            return;
        }

        var dialog = new PickCommitDialogViewModel(commits, Key);
        if (await ShowDialogAsync(dialog).ConfigureAwait(true) == true && dialog.SelectedCommit is { } choice)
        {
            var label = choice.Subject.Length > 200 ? choice.Subject[..199] + "…" : choice.Subject;
            await AddLinkAsync(WorkItemLinkKind.Commit, choice.Sha, label).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task LinkFileAsync()
    {
        if (_owner.FileIndex is not { } index)
        {
            _owner.Services.Notifications.Show("File search isn't available", "Restart ForgeDesk to link files.", NotificationSeverity.Warning);
            return;
        }

        var dialog = new PickFileDialogViewModel(index, _owner.Context.Root, Key);
        _ = dialog.LoadAsync(_owner.Context.Lifetime);
        if (await ShowDialogAsync(dialog).ConfigureAwait(true) == true && dialog.SelectedFile is { } file)
        {
            await AddLinkAsync(WorkItemLinkKind.File, file.RelativePath, null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task LinkIssueAsync() => LinkNumberAsync(WorkItemLinkKind.Issue, "Link an issue", "Issue number");

    [RelayCommand]
    private Task LinkPullRequestAsync() => LinkNumberAsync(WorkItemLinkKind.PullRequest, "Link a pull request", "Pull request number");

    [RelayCommand]
    private async Task LinkUrlAsync()
    {
        var url = await PromptAsync(new PromptOptions
        {
            Title = "Link a web page",
            Message = "A design, a document, a ticket in another tracker…",
            Placeholder = "https://",
            ConfirmText = "Link",
            Validate = ValidateUrl,
        }).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(url))
        {
            await AddLinkAsync(WorkItemLinkKind.Url, url.Trim(), null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenLink(TaskLinkViewModel? link)
    {
        if (link is null)
        {
            return;
        }

        try
        {
            switch (link.Kind)
            {
                case WorkItemLinkKind.Branch:
                    _owner.Context.RequestNavigation(WorkspaceSection.Git, GitNavigation.Branches(link.Value));
                    break;
                case WorkItemLinkKind.Commit:
                    _owner.Context.RequestNavigation(WorkspaceSection.Git, GitNavigation.History(link.Value));
                    break;
                case WorkItemLinkKind.File:
                    _owner.Context.RequestNavigation(WorkspaceSection.Files, link.Value);
                    break;
                default:
                    if (link.Url is { } url)
                    {
                        _owner.Services.Shell.OpenUrl(url);
                    }
                    else
                    {
                        _owner.Services.Notifications.Show("No GitHub repository", link.ToolTip, NotificationSeverity.Info);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the link"));
        }
    }

    [RelayCommand]
    private void CopyLink(TaskLinkViewModel? link)
    {
        if (link is null)
        {
            return;
        }

        try
        {
            _owner.Services.Shell.CopyToClipboard(link.Url ?? link.Value);
            _owner.Services.Notifications.Show("Copied", link.Url ?? link.Value, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    [RelayCommand]
    private Task RemoveLinkAsync(TaskLinkViewModel? link) => link is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var item = await _owner.Services.WorkItems.RemoveLinkAsync(Id, link.Id, _owner.Context.Lifetime).ConfigureAwait(true);
        _owner.ApplyItem(item);
        _ = LoadHistoryAsync();
    }, errorTitle: "Could not remove the link", errorMode: ErrorMode.Toast, notifications: _owner.Services.Notifications);

    // ----- Task actions -----------------------------------------------------------------

    [RelayCommand]
    private Task StartWorkingAsync() => _owner.StartWorkingAsync(Card);

    [RelayCommand]
    private Task CompleteAsync() => _owner.CompleteAsync(Card);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(Card);

    [RelayCommand]
    private Task CloseAsync() => _owner.CloseDetailAsync();

    [RelayCommand]
    private void CopyKey()
    {
        try
        {
            _owner.Services.Shell.CopyToClipboard($"{Key} {Title}");
            _owner.Services.Notifications.Show("Copied", $"{Key} {Title}", NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textSave?.Cancel();
        _textSave?.Dispose();
        _textSave = null;
    }

    // ----- Helpers ----------------------------------------------------------------------

    internal static string? ValidateNumber(string? text)
    {
        var value = text?.Trim().TrimStart('#') ?? string.Empty;
        return value.Length is > 0 and <= 9 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0
            ? null
            : "Enter a number, such as 42 or #42.";
    }

    internal static string? ValidateUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? null
            : "Enter a full address starting with https://.";

    private async Task LinkNumberAsync(WorkItemLinkKind kind, string title, string placeholder)
    {
        var gitHub = _owner.Context.Project.GitHub;
        var number = await PromptAsync(new PromptOptions
        {
            Title = title,
            Message = gitHub is null
                ? "The project has no GitHub remote: the number is recorded but can't be opened."
                : $"A number of {gitHub.FullName} on GitHub.",
            Placeholder = placeholder,
            ConfirmText = "Link",
            Validate = ValidateNumber,
        }).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(number))
        {
            await AddLinkAsync(kind, number.Trim().TrimStart('#'), null).ConfigureAwait(true);
        }
    }

    private Task AddLinkAsync(WorkItemLinkKind kind, string value, string? label) => RunAsync(async () =>
    {
        var item = await _owner.Services.WorkItems.AddLinkAsync(Id, kind, value, label, _owner.Context.Lifetime).ConfigureAwait(true);
        _owner.ApplyItem(item);
        _ = LoadHistoryAsync();
    }, errorTitle: "Could not add the link", errorMode: ErrorMode.Toast, notifications: _owner.Services.Notifications);

    private async Task SaveAsync(WorkItemPatch patch)
    {
        SaveState = "Saving…";
        var saved = await RunAsync(async () =>
        {
            var item = await _owner.Services.WorkItems.UpdateAsync(Id, patch, _owner.Context.Lifetime).ConfigureAwait(true);
            _owner.ApplyItem(item);
        }, errorTitle: $"Could not save {Key}", errorMode: ErrorMode.Toast, notifications: _owner.Services.Notifications).ConfigureAwait(true);

        if (_disposed)
        {
            return;
        }

        SaveState = saved ? "Saved" : string.Empty;
        if (!saved)
        {
            // Show what is really stored again.
            Apply(Card.Item, force: patch.Title is not null || patch.Description is not null);
        }

        _ = LoadHistoryAsync();
    }

    private void ScheduleTextSave()
    {
        _textSave?.Cancel();
        _textSave?.Dispose();
        _textSave = null;
        var delay = _owner.TextSaveDelay;
        if (delay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _textSave = cts;
        _ = SaveTextLaterAsync(delay, cts.Token);
    }

    private async Task SaveTextLaterAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                await FlushAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> PromptAsync(PromptOptions options)
    {
        try
        {
            return await _owner.Services.Dialogs.PromptAsync(options).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the dialog"));
            return null;
        }
    }

    private async Task<bool?> ShowDialogAsync(IDialogViewModel dialog)
    {
        try
        {
            return await _owner.Services.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the dialog"));
            return false;
        }
    }
}

internal static class TaskCollectionExtensions
{
    /// <summary>Makes a list of strings equal to <paramref name="values"/> with minimal changes.</summary>
    public static void SyncValues(this ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!values.Contains(target[i], StringComparer.Ordinal))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < values.Count; i++)
        {
            var existing = target.IndexOf(values[i]);
            if (existing == i)
            {
                continue;
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, values[i]);
            }
        }
    }
}
