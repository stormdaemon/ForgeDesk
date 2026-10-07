using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>An entry of the issue thread: the description (first) or a comment.</summary>
public sealed class IssueCommentViewModel
{
    public IssueCommentViewModel(GitHubComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        Id = comment.Id;
        Author = comment.Author;
        Body = comment.Body;
        CreatedAt = comment.CreatedAt;
        HtmlUrl = comment.HtmlUrl;
    }

    private IssueCommentViewModel(GitHubIssue issue)
    {
        Author = issue.Author;
        Body = issue.Body;
        CreatedAt = issue.CreatedAt;
        HtmlUrl = issue.HtmlUrl;
        IsDescription = true;
    }

    /// <summary>The issue's own description, shown as the first entry of the thread.</summary>
    public static IssueCommentViewModel Description(GitHubIssue issue) => new(issue);

    public long Id { get; }

    public bool IsDescription { get; }

    /// <summary>"opened this issue" or "commented".</summary>
    public string Action => IsDescription ? "opened this issue" : "commented";

    public string Author { get; }

    public string Body { get; }

    public DateTimeOffset CreatedAt { get; }

    public string HtmlUrl { get; }
}

/// <summary>The selected issue: its description, the comment thread and the reply box.</summary>
public sealed partial class IssueDetailViewModel : ObservableObject
{
    private IssueCommentViewModel? _description;

    public IssueDetailViewModel(GitHubIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        Issue = issue;
        Comments.CollectionChanged += (_, _) => RebuildThread();
        RebuildThread();
    }

    /// <summary>The description followed by the comments, in one virtualized thread.</summary>
    public ObservableCollection<IssueCommentViewModel> Thread { get; } = [];

    /// <summary>"3 comments".</summary>
    public string CommentsText => Format.Count(Comments.Count, "comment");

    partial void OnIssueChanged(GitHubIssue value)
    {
        if (_description is not null && (_description.Body != value.Body || _description.Author != value.Author))
        {
            _description = null;
        }

        RebuildThread();
    }

    private void RebuildThread()
    {
        if (Thread is null || Comments is null)
        {
            // Called while the generated property setter runs in the constructor.
            return;
        }

        _description ??= IssueCommentViewModel.Description(Issue);
        Thread.SyncWith([_description, .. Comments]);
        OnPropertyChanged(nameof(CommentsText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(NumberText), nameof(Title), nameof(Author), nameof(IsOpen), nameof(StateText), nameof(StateTone),
        nameof(ToggleStateText), nameof(Labels), nameof(HasLabels), nameof(Body), nameof(HasBody), nameof(CreatedAt), nameof(HtmlUrl), nameof(AssigneesText))]
    public partial GitHubIssue Issue { get; internal set; }

    public int Number => Issue.Number;

    public string NumberText => $"#{Number}";

    public string Title => Issue.Title;

    public string Author => Issue.Author;

    public bool IsOpen => Issue.IsOpen;

    public string StateText => IsOpen ? "Open" : "Closed";

    public StatusTone StateTone => IsOpen ? StatusTone.Success : StatusTone.Info;

    /// <summary>"Close issue" or "Reopen issue".</summary>
    public string ToggleStateText => IsOpen ? "Close issue" : "Reopen issue";

    public IReadOnlyList<LabelViewModel> Labels => LabelViewModel.From(Issue.Labels);

    public bool HasLabels => Issue.Labels.Count > 0;

    public string Body => Issue.Body;

    public bool HasBody => !string.IsNullOrWhiteSpace(Issue.Body);

    public DateTimeOffset CreatedAt => Issue.CreatedAt;

    public string HtmlUrl => Issue.HtmlUrl;

    public string AssigneesText => Issue.Assignees.Count == 0 ? "Nobody assigned" : string.Join(", ", Issue.Assignees);

    public ObservableCollection<IssueCommentViewModel> Comments { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingComments { get; internal set; }

    [ObservableProperty]
    public partial ErrorInfo? CommentsError { get; internal set; }

    /// <summary>The reply being written (Ctrl+Enter posts it).</summary>
    [ObservableProperty]
    public partial string NewComment { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPostingComment { get; internal set; }

    [ObservableProperty]
    public partial bool IsChangingState { get; internal set; }

    [ObservableProperty]
    public partial bool IsCreatingTask { get; internal set; }
}

/// <summary>
/// Issues of the linked repository: Open · Closed · All with a search box, a virtualized list and a
/// detail pane (description, comment thread, reply box, close/reopen, "Create task from issue"), and
/// the "New issue…" flow.
/// </summary>
public sealed partial class IssuesViewModel : GitHubSubViewModel
{
    /// <summary>How many issues are listed per filter.</summary>
    public const int MaxItems = 100;

    private const int MaxTaskDescriptionBody = 4000;

    private List<IssueRowViewModel> _all = [];
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _comments;
    private int? _pendingSelection;

    internal IssuesViewModel(GitHubSectionContext section)
        : base(section)
    {
        SelectedFilter = StateFilterOption.For(IssueStateFilter.Open);
    }

    public override GitHubView View => GitHubView.Issues;

    public ObservableCollection<IssueRowViewModel> Items { get; } = [];

    public IReadOnlyList<StateFilterOption> FilterOptions => StateFilterOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyDescription))]
    public partial StateFilterOption SelectedFilter { get; set; }

    public IssueStateFilter StateFilter => SelectedFilter.Value;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial IssueRowViewModel? SelectedItem { get; set; }

    public bool IsDetailOpen => SelectedItem is not null;

    [ObservableProperty]
    public partial IssueDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    public partial bool IsCreating { get; private set; }

    public string EmptyTitle => StateFilter switch
    {
        IssueStateFilter.Open => "No open issues",
        IssueStateFilter.Closed => "No closed issues",
        _ => "No issues yet",
    };

    public string EmptyDescription => StateFilter switch
    {
        IssueStateFilter.Open => "Nothing is waiting: every reported bug and idea has been handled. Open an issue to track the next one.",
        IssueStateFilter.Closed => "Issues that were resolved or dismissed appear here.",
        _ => "Issues track bugs, ideas and questions about the project. Open the first one.",
    };

    public override async Task LoadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _load = cts;
        var token = cts.Token;
        IsLoading = _all.Count == 0;
        try
        {
            var loaded = await RunAsync(async () =>
            {
                var repo = Section.RequireRepo();
                var list = await GitHub.GetIssuesAsync(repo, StateFilter, MaxItems, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                var existing = _all.ToDictionary(r => r.Number);
                _all = list.Select(issue =>
                {
                    if (existing.TryGetValue(issue.Number, out var row))
                    {
                        row.Update(issue);
                        return row;
                    }

                    return new IssueRowViewModel(issue);
                }).ToList();
                IsTruncated = list.Count >= MaxItems;
                ApplyFilter();
            }, errorTitle: "Could not load issues").ConfigureAwait(true);

            if (loaded && ReferenceEquals(_load, cts))
            {
                var previous = SelectedItem;
                ApplyPendingSelection();
                if (previous is not null && ReferenceEquals(SelectedItem, previous) && Items.Contains(previous))
                {
                    if (Detail is { } detail && detail.Number == previous.Number)
                    {
                        detail.Issue = previous.Issue;
                    }

                    _ = LoadCommentsAsync(previous.Number);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                IsLoading = false;
                UpdateEmptyStates();
            }
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void CloseDetail() => SelectedItem = null;

    [RelayCommand]
    private Task RetryCommentsAsync() => Detail is { } detail ? LoadCommentsAsync(detail.Number) : Task.CompletedTask;

    [RelayCommand]
    private void OpenOnGitHub(IssueRowViewModel? row) => OpenUrl(row?.HtmlUrl ?? Detail?.HtmlUrl);

    [RelayCommand]
    private void CopyLink(IssueRowViewModel? row) => Copy(row?.HtmlUrl ?? Detail?.HtmlUrl, "Link");

    /// <summary>Posts the reply box as a comment (Ctrl+Enter).</summary>
    [RelayCommand]
    private async Task AddCommentAsync()
    {
        if (Detail is not { } detail || detail.IsPostingComment || Section.Repo is not { } repo)
        {
            return;
        }

        var body = detail.NewComment.Trim();
        if (body.Length == 0)
        {
            Notify("Write a comment first", "The reply box is empty.", NotificationSeverity.Info);
            return;
        }

        detail.IsPostingComment = true;
        try
        {
            await RunActionAsync(async () =>
            {
                var comment = await GitHub.AddIssueCommentAsync(repo, detail.Number, body, Section.Lifetime).ConfigureAwait(true);
                detail.Comments.Add(new IssueCommentViewModel(comment));
                detail.NewComment = string.Empty;
                var updated = detail.Issue with { Comments = detail.Issue.Comments + 1 };
                detail.Issue = updated;
                _all.FirstOrDefault(r => r.Number == detail.Number)?.Update(updated);
                await Section.Activity.SucceededAsync(ActivityKind.IssueCommented, $"Commented on issue #{detail.Number}", detail.Title, comment.HtmlUrl)
                    .ConfigureAwait(true);
                Notify("Comment added", $"On issue #{detail.Number} {detail.Title}");
            }, "Could not add the comment").ConfigureAwait(true);
        }
        finally
        {
            detail.IsPostingComment = false;
        }
    }

    /// <summary>Closes (after confirmation) or reopens the selected issue.</summary>
    [RelayCommand]
    private async Task ToggleStateAsync()
    {
        if (Detail is not { } detail || detail.IsChangingState || Section.Repo is not { } repo)
        {
            return;
        }

        var open = !detail.IsOpen;
        if (!open && !await Section.Dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = $"Close issue #{detail.Number}?",
            Message = $"\"{detail.Title}\" will be marked as closed on GitHub. You can reopen it at any time.",
            ConfirmText = "Close issue",
            IsDestructive = true,
        }).ConfigureAwait(true))
        {
            return;
        }

        detail.IsChangingState = true;
        try
        {
            await RunActionAsync(async () =>
            {
                var updated = await GitHub.SetIssueOpenAsync(repo, detail.Number, open, Section.Lifetime).ConfigureAwait(true);
                if (ReferenceEquals(Detail, detail))
                {
                    detail.Issue = updated;
                }

                _all.FirstOrDefault(r => r.Number == updated.Number)?.Update(updated);
                GitHub.InvalidateCache(repo);
                var title = open ? $"Reopened issue #{updated.Number}" : $"Closed issue #{updated.Number}";
                await Section.Activity.SucceededAsync(ActivityKind.IssueStateChanged, title, updated.Title, updated.HtmlUrl).ConfigureAwait(true);
                Notify(title, updated.Title);
            }, open ? "Could not reopen the issue" : "Could not close the issue").ConfigureAwait(true);
        }
        finally
        {
            detail.IsChangingState = false;
        }
    }

    /// <summary>Adds a task to the project's board, linked to the issue, and offers to open it.</summary>
    [RelayCommand]
    private async Task CreateTaskAsync(IssueRowViewModel? row)
    {
        var issue = row?.Issue ?? Detail?.Issue;
        if (issue is null)
        {
            return;
        }

        var detail = Detail is { } d && d.Number == issue.Number ? d : null;
        if (detail is { IsCreatingTask: true })
        {
            return;
        }

        if (detail is not null)
        {
            detail.IsCreatingTask = true;
        }

        try
        {
            await RunActionAsync(async () =>
            {
                var workItems = Section.Services.WorkItems;
                var item = await workItems.CreateAsync(Context.ProjectId, new WorkItemDraft(issue.Title, TaskDescription(issue)), Section.Lifetime)
                    .ConfigureAwait(true);
                try
                {
                    await workItems.AddLinkAsync(item.Id, WorkItemLinkKind.Issue, issue.HtmlUrl, $"#{issue.Number}", Section.Lifetime).ConfigureAwait(true);
                }
                catch (Exception ex) when (!ex.IsCancellation())
                {
                    // The task exists and its description carries the link: report, don't fail.
                    Section.Notifications.ShowError(ErrorInfo.From(ex, "Could not link the task to the issue"));
                }

                var id = item.Id;
                Notify($"Task {item.Key} created", issue.Title, NotificationSeverity.Success, new NotificationAction("Open task", () =>
                {
                    Context.RequestNavigation(WorkspaceSection.Tasks, id);
                    return Task.CompletedTask;
                }));
            }, "Could not create the task").ConfigureAwait(true);
        }
        finally
        {
            if (detail is not null)
            {
                detail.IsCreatingTask = false;
            }
        }
    }

    /// <summary>"New issue…": asks for a title and a description, creates the issue and shows it.</summary>
    [RelayCommand]
    private async Task NewIssueAsync()
    {
        if (IsCreating || Section.Repo is not { } repo)
        {
            return;
        }

        var dialog = new NewIssueDialogViewModel(repo.FullName);
        if (await Section.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true) != true)
        {
            return;
        }

        IsCreating = true;
        try
        {
            await RunActionAsync(async () =>
            {
                var issue = await GitHub.CreateIssueAsync(repo, dialog.IssueTitle.Trim(), dialog.Body, Section.Lifetime).ConfigureAwait(true);
                await Section.Activity.SucceededAsync(ActivityKind.IssueCreated, $"Opened issue #{issue.Number}", issue.Title, issue.HtmlUrl).ConfigureAwait(true);
                var url = issue.HtmlUrl;
                Notify($"Issue #{issue.Number} created", issue.Title, NotificationSeverity.Success, new NotificationAction("Open on GitHub", () =>
                {
                    OpenUrl(url);
                    return Task.CompletedTask;
                }));

                GitHub.InvalidateCache(repo);
                _pendingSelection = issue.Number;
                SearchText = string.Empty;
                if (StateFilter == IssueStateFilter.Closed)
                {
                    SelectedFilter = StateFilterOption.For(IssueStateFilter.Open);
                }
                else
                {
                    await LoadAsync().ConfigureAwait(true);
                }
            }, "Could not create the issue").ConfigureAwait(true);
        }
        finally
        {
            IsCreating = false;
        }
    }

    internal override void PrepareNavigation(GitHubNavigation navigation)
    {
        if (navigation.Number is { } number)
        {
            _pendingSelection = number;
            SearchText = string.Empty;
            if (HasLoaded)
            {
                ApplyPendingSelection();
            }
        }
    }

    internal override Task NavigateAsync(GitHubNavigation navigation) =>
        navigation.CreateNew ? NewIssueAsync() : Task.CompletedTask;

    /// <summary>The task's description: where it comes from, then the issue's own description.</summary>
    internal static string TaskDescription(GitHubIssue issue)
    {
        var header = $"From GitHub issue [#{issue.Number}]({issue.HtmlUrl}) opened by {issue.Author}.";
        var body = issue.Body.Trim();
        if (body.Length == 0)
        {
            return header;
        }

        if (body.Length > MaxTaskDescriptionBody)
        {
            body = body[..MaxTaskDescriptionBody].TrimEnd() + "…";
        }

        return header + "\n\n" + body;
    }

    partial void OnSelectedFilterChanged(StateFilterOption value)
    {
        if (HasLoaded && !IsDisposed)
        {
            _all = [];
            Items.Clear();
            _ = LoadAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(IssueRowViewModel? value)
    {
        _comments?.Cancel();
        if (value is null)
        {
            Detail = null;
            return;
        }

        if (Detail?.Number != value.Number)
        {
            Detail = new IssueDetailViewModel(value.Issue);
        }

        _ = LoadCommentsAsync(value.Number);
    }

    protected override void OnReset()
    {
        _load?.Cancel();
        _comments?.Cancel();
        _all = [];
        Items.Clear();
        SelectedItem = null;
        IsEmpty = false;
        HasNoMatches = false;
    }

    protected override void OnDisposed()
    {
        _load?.Cancel();
        _load?.Dispose();
        _comments?.Cancel();
        _comments?.Dispose();
    }

    private async Task LoadCommentsAsync(int number)
    {
        _comments?.Cancel();
        _comments?.Dispose();
        _comments = null;
        if (Detail is not { } detail || detail.Number != number || Section.Repo is not { } repo || IsDisposed)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _comments = cts;
        detail.CommentsError = null;
        detail.IsLoadingComments = detail.Comments.Count == 0;
        try
        {
            var comments = await GitHub.GetIssueCommentsAsync(repo, number, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            detail.Comments.Clear();
            foreach (var comment in comments.OrderBy(c => c.CreatedAt))
            {
                detail.Comments.Add(new IssueCommentViewModel(comment));
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                detail.CommentsError = ErrorInfo.From(ex, "Could not load the comments");
            }
        }
        finally
        {
            if (ReferenceEquals(_comments, cts))
            {
                detail.IsLoadingComments = false;
            }
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Items.SyncWith(_all.Where(r => r.Matches(search)).ToList());
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        var ready = HasLoaded && !IsLoading && Error is null;
        IsEmpty = ready && _all.Count == 0;
        HasNoMatches = ready && _all.Count > 0 && Items.Count == 0;
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is not { } number)
        {
            return;
        }

        var row = _all.FirstOrDefault(r => r.Number == number);
        if (row is not null)
        {
            _pendingSelection = null;
            if (!Items.Contains(row))
            {
                SearchText = string.Empty;
            }

            SelectedItem = row;
        }
        else if (StateFilter != IssueStateFilter.All)
        {
            SelectedFilter = StateFilterOption.For(IssueStateFilter.All);
        }
        else
        {
            _pendingSelection = null;
            Notify($"Issue #{number} not found", $"It isn't among the latest {MaxItems} issues of {Section.Repo?.FullName}.", NotificationSeverity.Warning);
        }
    }
}
