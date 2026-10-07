using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>An entry of the Open · Closed · All filter.</summary>
public sealed record StateFilterOption(IssueStateFilter Value, string Title)
{
    public static IReadOnlyList<StateFilterOption> All { get; } =
    [
        new(IssueStateFilter.Open, "Open"),
        new(IssueStateFilter.Closed, "Closed"),
        new(IssueStateFilter.All, "All"),
    ];

    public static StateFilterOption For(IssueStateFilter value) => All.First(o => o.Value == value);

    /// <summary>UI automation id of the filter segment ("GitHub.Filter.Open").</summary>
    public string AutomationId => $"GitHub.Filter.{Value}";
}

/// <summary>Details of the selected pull request (from the list first, then complete once loaded).</summary>
public sealed class PullRequestDetailViewModel
{
    public PullRequestDetailViewModel(GitHubPullRequest pullRequest, bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        PullRequest = pullRequest;
        IsComplete = isComplete;
        Labels = LabelViewModel.From(pullRequest.Labels);
    }

    public GitHubPullRequest PullRequest { get; }

    /// <summary>False while only the list data is known (no diff stats, mergeability, checks yet).</summary>
    public bool IsComplete { get; }

    public int Number => PullRequest.Number;

    public string NumberText => $"#{Number}";

    public string Title => PullRequest.Title;

    public string Author => PullRequest.Author;

    public string HeadBranch => PullRequest.HeadBranch;

    public string BaseBranch => PullRequest.BaseBranch;

    public PullRequestState State => PullRequest.State;

    public string StateText => GitHubText.PullRequestState(State);

    public StatusTone StateTone => GitHubText.PullRequestTone(State);

    public bool IsOpen => State is PullRequestState.Open or PullRequestState.Draft;

    public DateTimeOffset CreatedAt => PullRequest.CreatedAt;

    public DateTimeOffset UpdatedAt => PullRequest.UpdatedAt ?? PullRequest.CreatedAt;

    public int Additions => PullRequest.Additions;

    public int Deletions => PullRequest.Deletions;

    public string AdditionsText => $"+{Additions:N0}";

    public string DeletionsText => $"−{Deletions:N0}";

    public string FilesText => Format.Count(PullRequest.ChangedFiles, "file");

    public CiState Checks => PullRequest.Checks;

    public bool HasChecks => IsComplete && Checks != CiState.Unknown;

    public string MergeableText => !IsOpen ? (State == PullRequestState.Merged ? "Merged" : "Closed without merging")
        : PullRequest.Mergeable switch
        {
            true => "No conflicts with " + BaseBranch,
            false => "Has conflicts with " + BaseBranch,
            null => IsComplete ? "GitHub is still checking for conflicts" : "Checking…",
        };

    public StatusTone MergeableTone => !IsOpen ? StatusTone.Neutral
        : PullRequest.Mergeable switch
        {
            true => StatusTone.Success,
            false => StatusTone.Danger,
            null => StatusTone.Neutral,
        };

    public IReadOnlyList<LabelViewModel> Labels { get; }

    public bool HasLabels => Labels.Count > 0;

    public IReadOnlyList<string> Reviewers => PullRequest.Reviewers;

    public string ReviewersText => Reviewers.Count == 0 ? "No reviewers requested" : string.Join(", ", Reviewers);

    public string Body => PullRequest.Body;

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string HtmlUrl => PullRequest.HtmlUrl;
}

/// <summary>
/// Pull requests of the linked repository: Open · Closed · All with a search box, a virtualized list
/// and a detail pane (description, branches, diff stats, mergeability, checks, reviewers) with
/// "Check out branch", "Open on GitHub", "Copy link", and the "New pull request…" flow.
/// </summary>
public sealed partial class PullRequestsViewModel : GitHubSubViewModel
{
    /// <summary>How many pull requests are listed per filter.</summary>
    public const int MaxItems = 100;

    private List<PullRequestRowViewModel> _all = [];
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _detail;
    private int? _pendingSelection;

    internal PullRequestsViewModel(GitHubSectionContext section)
        : base(section)
    {
        SelectedFilter = StateFilterOption.For(IssueStateFilter.Open);
    }

    public override GitHubView View => GitHubView.PullRequests;

    public ObservableCollection<PullRequestRowViewModel> Items { get; } = [];

    public IReadOnlyList<StateFilterOption> FilterOptions => StateFilterOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyDescription))]
    public partial StateFilterOption SelectedFilter { get; set; }

    public IssueStateFilter StateFilter => SelectedFilter.Value;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial PullRequestRowViewModel? SelectedItem { get; set; }

    public bool IsDetailOpen => SelectedItem is not null;

    [ObservableProperty]
    public partial PullRequestDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    public partial bool IsDetailLoading { get; private set; }

    [ObservableProperty]
    public partial ErrorInfo? DetailError { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    /// <summary>The list stopped at <see cref="MaxItems"/>: older items are only on GitHub.</summary>
    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    public partial bool IsCheckingOut { get; private set; }

    [ObservableProperty]
    public partial bool IsCreating { get; private set; }

    public string EmptyTitle => StateFilter switch
    {
        IssueStateFilter.Open => "No open pull requests",
        IssueStateFilter.Closed => "No closed pull requests",
        _ => "No pull requests yet",
    };

    public string EmptyDescription => StateFilter switch
    {
        IssueStateFilter.Closed => "Merged and closed pull requests appear here.",
        _ => "Propose the changes of a branch: others review them on GitHub before they're merged.",
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
                var filter = StateFilter;
                var list = await GitHub.GetPullRequestsAsync(repo, filter, MaxItems, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                var existing = _all.ToDictionary(r => r.Number);
                _all = list.Select(pr =>
                {
                    if (existing.TryGetValue(pr.Number, out var row))
                    {
                        row.Update(pr);
                        return row;
                    }

                    return new PullRequestRowViewModel(pr);
                }).ToList();
                IsTruncated = list.Count >= MaxItems;
                ApplyFilter();
            }, errorTitle: "Could not load pull requests").ConfigureAwait(true);

            if (loaded && ReferenceEquals(_load, cts))
            {
                var previous = SelectedItem;
                ApplyPendingSelection();
                if (previous is not null && ReferenceEquals(SelectedItem, previous) && Items.Contains(previous))
                {
                    // Same selection after a reload: refresh its details too.
                    _ = LoadDetailAsync(previous);
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
    private Task RetryDetailAsync() => SelectedItem is { } row ? LoadDetailAsync(row) : Task.CompletedTask;

    [RelayCommand]
    private void OpenOnGitHub(PullRequestRowViewModel? row) => OpenUrl((row ?? SelectedItem)?.HtmlUrl);

    [RelayCommand]
    private void CopyLink(PullRequestRowViewModel? row) => Copy((row ?? SelectedItem)?.HtmlUrl, "Link");

    /// <summary>Fetches, then checks out the pull request's branch (as a local tracking branch when needed).</summary>
    [RelayCommand]
    private async Task CheckoutBranchAsync(PullRequestRowViewModel? row)
    {
        row ??= SelectedItem;
        if (row is null || IsCheckingOut)
        {
            return;
        }

        if (Context.GitStatus is null)
        {
            Notify("Not a Git repository", $"{Context.Project.Name} isn't under version control, so no branch can be checked out.", NotificationSeverity.Warning);
            return;
        }

        var head = row.HeadBranch;
        if (string.Equals(Section.CurrentBranch, head, StringComparison.Ordinal))
        {
            Notify($"Already on {head}", $"The branch of pull request {row.NumberText} is checked out.", NotificationSeverity.Info);
            return;
        }

        IsCheckingOut = true;
        try
        {
            await RunActionAsync(async () =>
            {
                var git = Section.Services.Git;
                var root = Context.Root;
                var token = Section.Lifetime;
                await git.FetchAsync(root, null, token).ConfigureAwait(true);
                var branches = await git.GetBranchesAsync(root, includeRemote: true, token).ConfigureAwait(true);
                var remoteName = $"{GitHubLinks.PreferredRemote}/{head}";
                if (branches.Any(b => !b.IsRemote && string.Equals(b.Name, head, StringComparison.Ordinal)))
                {
                    await git.CheckoutAsync(root, head, token).ConfigureAwait(true);
                }
                else if (branches.Any(b => b.IsRemote && string.Equals(b.Name, remoteName, StringComparison.Ordinal)))
                {
                    await git.CheckoutRemoteBranchAsync(root, remoteName, null, token).ConfigureAwait(true);
                }
                else
                {
                    var url = row.HtmlUrl;
                    Notify("This branch lives in a fork",
                        $"{head} isn't on {GitHubLinks.PreferredRemote}: pull request {row.NumberText} was opened from a fork of {Section.Repo?.FullName}. "
                        + $"Add the fork as a remote and fetch it, or run \"gh pr checkout {row.Number}\" in the terminal.",
                        NotificationSeverity.Warning, new NotificationAction("Open on GitHub", () =>
                        {
                            OpenUrl(url);
                            return Task.CompletedTask;
                        }));
                    return;
                }

                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
                await Section.Activity.SucceededAsync(ActivityKind.GitCheckout, $"Checked out {head}", $"Branch of pull request {row.NumberText} {row.Title}", row.HtmlUrl)
                    .ConfigureAwait(true);
                Notify($"Switched to {head}", $"You're on the branch of pull request {row.NumberText}.");
            }, $"Could not check out {head}").ConfigureAwait(true);
        }
        finally
        {
            IsCheckingOut = false;
        }
    }

    /// <summary>"New pull request…": asks for the details, offers to push the branch first, creates it and shows it.</summary>
    [RelayCommand]
    private async Task NewPullRequestAsync()
    {
        if (IsCreating || Section.Repo is not { } repo)
        {
            return;
        }

        IsCreating = true;
        try
        {
            await RunActionAsync(async () =>
            {
                var token = Section.Lifetime;
                var git = Section.Services.Git;
                var status = Context.GitStatus;
                List<string> localBranches = status is null
                    ? []
                    : (await git.GetBranchesAsync(Context.Root, includeRemote: false, token).ConfigureAwait(true))
                        .Where(b => !b.IsRemote).Select(b => b.Name).ToList();
                var remoteBranches = await GitHub.GetBranchesAsync(repo, token).ConfigureAwait(true);
                var defaultBranch = (await GitHub.GetRepositoryAsync(repo, token).ConfigureAwait(true)).DefaultBranch;

                var current = Section.CurrentBranch;
                var head = current is not null && current != defaultBranch ? current : localBranches.FirstOrDefault(b => b != defaultBranch);
                var dialog = new NewPullRequestDialogViewModel(repo.FullName, localBranches, remoteBranches, head, defaultBranch ?? remoteBranches.FirstOrDefault());
                if (await Section.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true) != true)
                {
                    return;
                }

                var headBranch = dialog.Head.Trim();
                if (PushReason(headBranch, status, remoteBranches) is { } reason
                    && await Section.Dialogs.ConfirmAsync(new ConfirmOptions
                    {
                        Title = $"Push {headBranch} first?",
                        Message = reason,
                        ConfirmText = "Push and create",
                        CancelText = "Create without pushing",
                    }).ConfigureAwait(true))
                {
                    await git.PushAsync(Context.Root, new GitPushOptions(SetUpstream: true, Branch: headBranch), null, token).ConfigureAwait(true);
                    await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
                }

                var created = await GitHub.CreatePullRequestAsync(repo, dialog.PullRequestTitle.Trim(), dialog.Body, headBranch, dialog.Base.Trim(), dialog.IsDraft, token)
                    .ConfigureAwait(true);
                await Section.Activity.SucceededAsync(ActivityKind.PullRequestCreated, $"Opened pull request #{created.Number}", created.Title, created.HtmlUrl)
                    .ConfigureAwait(true);
                var url = created.HtmlUrl;
                Notify($"Pull request #{created.Number} created", created.Title, NotificationSeverity.Success, new NotificationAction("Open on GitHub", () =>
                {
                    OpenUrl(url);
                    return Task.CompletedTask;
                }));

                GitHub.InvalidateCache(repo);
                _pendingSelection = created.Number;
                SearchText = string.Empty;
                if (StateFilter == IssueStateFilter.Closed)
                {
                    SelectedFilter = StateFilterOption.For(IssueStateFilter.Open);
                }
                else
                {
                    await LoadAsync().ConfigureAwait(true);
                }
            }, "Could not create the pull request").ConfigureAwait(true);
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
        navigation.CreateNew ? NewPullRequestAsync() : Task.CompletedTask;

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

    partial void OnSelectedItemChanged(PullRequestRowViewModel? value) => _ = LoadDetailAsync(value);

    protected override void OnReset()
    {
        _load?.Cancel();
        _detail?.Cancel();
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
        _detail?.Cancel();
        _detail?.Dispose();
    }

    private async Task LoadDetailAsync(PullRequestRowViewModel? row)
    {
        _detail?.Cancel();
        _detail?.Dispose();
        _detail = null;
        DetailError = null;
        if (row is null || IsDisposed || Section.Repo is not { } repo)
        {
            Detail = null;
            IsDetailLoading = false;
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _detail = cts;
        if (Detail?.Number != row.Number)
        {
            Detail = new PullRequestDetailViewModel(row.PullRequest, isComplete: false);
        }

        IsDetailLoading = true;
        try
        {
            var pr = await GitHub.GetPullRequestAsync(repo, row.Number, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            row.Update(pr);
            Detail = new PullRequestDetailViewModel(pr, isComplete: true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                DetailError = ErrorInfo.From(ex, $"Could not load pull request {row.NumberText}");
            }
        }
        finally
        {
            if (ReferenceEquals(_detail, cts))
            {
                IsDetailLoading = false;
            }
        }
    }

    /// <summary>Why the head branch should be pushed before opening the pull request, or null when GitHub has it.</summary>
    internal static string? PushReason(string head, GitStatus? status, IReadOnlyList<string> remoteBranches)
    {
        if (status is null)
        {
            return null;
        }

        var missing = $"{head} isn't on GitHub yet, and GitHub can only open a pull request for a branch it has. Push it now?";
        if (string.Equals(status.Branch, head, StringComparison.Ordinal))
        {
            if (status.Upstream is null)
            {
                return missing;
            }

            return status.Ahead > 0
                ? $"{Format.Count(status.Ahead, "commit")} of {head} {(status.Ahead == 1 ? "isn't" : "aren't")} on GitHub yet. Push so the pull request includes {(status.Ahead == 1 ? "it" : "them")}?"
                : null;
        }

        return remoteBranches.Contains(head, StringComparer.Ordinal) ? null : missing;
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var visible = _all.Where(r => r.Matches(search)).ToList();
        Items.SyncWith(visible);
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
            // The pull request may be closed or merged: look in every state.
            SelectedFilter = StateFilterOption.For(IssueStateFilter.All);
        }
        else
        {
            _pendingSelection = null;
            Notify($"Pull request #{number} not found", $"It isn't among the latest {MaxItems} pull requests of {Section.Repo?.FullName}.", NotificationSeverity.Warning);
        }
    }
}
