using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// The History view: a paged, searchable commit list with a lane graph and ref pills, and the
/// details of the selected commit (message, files, read-only diffs).
/// </summary>
public sealed partial class HistoryViewModel : GitSubViewModel
{
    public const int PageSize = 200;

    /// <summary>Pages loaded at most to find a commit someone navigated to.</summary>
    public const int MaxNavigationPages = 10;

    /// <summary>Lanes drawn at most; wider graphs are clipped.</summary>
    public const int MaxDrawnLanes = 12;

    public const double LaneWidth = 12;

    private CommitGraphBuilder _graph = new();
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _searchDelay;
    private IReadOnlyCollection<string> _remotes = [];
    private int _generation;
    private bool _suspendReload;
    private bool _needsReload;
    private string? _selectAfterLoad;
    private Task _loadTask = Task.CompletedTask;

    public HistoryViewModel(GitSectionContext section)
        : base(section)
    {
        AllBranches = section.Preferences.HistoryAllBranches;
    }

    public override GitView View => GitView.History;

    public ObservableCollection<CommitRowViewModel> Commits { get; } = [];

    [ObservableProperty]
    public partial CommitRowViewModel? SelectedCommit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsHint))]
    public partial CommitDetailsViewModel? Details { get; private set; }

    /// <summary>Commits are listed but none is selected.</summary>
    public bool ShowDetailsHint => HasResults && Details is null;

    /// <summary>Message or author text; the list reloads a moment after typing stops.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph), nameof(HasFilters), nameof(EmptyTitle), nameof(EmptyDescription), nameof(EmptyActionText))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllBranchesToolTip))]
    public partial bool AllBranches { get; set; }

    /// <summary>Only commits touching this repository-relative path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph), nameof(HasFilters), nameof(HasPathFilter), nameof(EmptyTitle), nameof(EmptyDescription), nameof(EmptyActionText))]
    public partial string? PathFilter { get; set; }

    /// <summary>Walk this branch instead of HEAD (from Branches → "View history").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters), nameof(HasRevisionFilter), nameof(EmptyTitle), nameof(EmptyDescription), nameof(EmptyActionText), nameof(AllBranchesToolTip))]
    public partial string? RevisionFilter { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>Width of the graph column, from the widest row loaded.</summary>
    [ObservableProperty]
    public partial double GraphWidth { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowDetailsHint))]
    public partial bool HasResults { get; private set; }

    /// <summary>Loaded, no error, and no commit to show.</summary>
    public bool IsEmpty => HasLoaded && !IsLoading && !HasError && !HasResults;

    public bool HasPathFilter => PathFilter is not null;

    public bool HasRevisionFilter => RevisionFilter is not null;

    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchText) || HasPathFilter || HasRevisionFilter;

    /// <summary>The lane graph only makes sense for an unfiltered walk.</summary>
    public bool ShowGraph => string.IsNullOrWhiteSpace(SearchText) && PathFilter is null;

    public bool CanOpenOnGitHub => Context.Project.GitHub is not null;

    public string AllBranchesToolTip => HasRevisionFilter
        ? $"Showing the history of {RevisionFilter}; clear the branch filter to walk all branches"
        : AllBranches ? "Showing commits of every branch and tag" : "Show commits of every branch and tag, not only the current branch";

    public string EmptyTitle => HasFilters ? "No matching commits" : "No commits yet";

    /// <summary>"Clear filters" when filters explain the empty list, nothing otherwise.</summary>
    public string? EmptyActionText => HasFilters ? "Clear filters" : null;

    public string EmptyDescription => HasFilters
        ? "Nothing in this history matches the search or the filters. Clear them to see every commit."
        : "Commits appear here once you make the first one from Changes.";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsLoading) or nameof(HasError))
        {
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    partial void OnSelectedCommitChanged(CommitRowViewModel? value)
    {
        Details?.Dispose();
        if (value is null)
        {
            Details = null;
            return;
        }

        var details = new CommitDetailsViewModel(Section, value.Sha, value.Commit, value.Refs);
        Details = details;
        _ = details.LoadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        if (_suspendReload)
        {
            return;
        }

        var delay = new CancellationTokenSource();
        _searchDelay = delay;
        _ = ReloadAfterTypingAsync(delay);
    }

    partial void OnAllBranchesChanged(bool value)
    {
        Section.Preferences.HistoryAllBranches = value;
        ReloadForFilter();
    }

    partial void OnPathFilterChanged(string? value) => ReloadForFilter();

    partial void OnRevisionFilterChanged(string? value) => ReloadForFilter();

    /// <summary>Delay between the last keystroke and the search.</summary>
    internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public override Task LoadAsync() => _loadTask = LoadCoreAsync();

    /// <summary>Filters changed while the view was hidden without a link to apply: reload now.</summary>
    protected override void OnReactivated()
    {
        if (_needsReload)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadCoreAsync()
    {
        _needsReload = false;
        _load?.Cancel();
        var load = CancellationTokenSource.CreateLinkedTokenSource(Section.Lifetime);
        _load = load;
        var generation = ++_generation;
        var select = _selectAfterLoad ?? SelectedCommit?.Sha;
        _selectAfterLoad = null;
        IsLoading = Commits.Count == 0;
        try
        {
            await RunAsync(async () =>
            {
                _remotes = await LoadRemotesAsync(load.Token).ConfigureAwait(true);
                var page = await Git.GetLogAsync(Root, Query(0), load.Token).ConfigureAwait(true);
                if (generation != _generation)
                {
                    return;
                }

                _graph = new CommitGraphBuilder();
                var previous = Commits.ToDictionary(c => c.Sha, StringComparer.Ordinal);
                var rows = BuildRows(page).Select(row => previous.TryGetValue(row.Sha, out var old) && old.SameAs(row) ? old : row).ToList();
                Commits.SyncWith(rows);
                HasMore = page.Count >= PageSize;
                HasResults = Commits.Count > 0;
                UpdateGraphWidth();
                SelectedCommit = (select is null ? null : Find(select)) ?? Commits.FirstOrDefault();
            }, errorTitle: "Could not load the history").ConfigureAwait(true);
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                if (HasError)
                {
                    Commits.Clear();
                    HasResults = false;
                    HasMore = false;
                    SelectedCommit = null;
                }
            }

            if (ReferenceEquals(_load, load))
            {
                _load = null;
            }

            load.Dispose();
        }
    }

    /// <summary>Loads the next page (the view calls it when scrolling near the end).</summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore || IsLoading || IsDisposed)
        {
            return;
        }

        var generation = _generation;
        IsLoadingMore = true;
        try
        {
            var page = await Git.GetLogAsync(Root, Query(Commits.Count), Section.Lifetime).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            foreach (var row in BuildRows(page))
            {
                Commits.Add(row);
            }

            HasMore = page.Count >= PageSize;
            HasResults = Commits.Count > 0;
            UpdateGraphWidth();
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // A reload replaced this page, or the project was closed.
        }
        catch (Exception ex)
        {
            HasMore = false;
            ShowError(ex, "Could not load more commits");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    internal override void PrepareNavigation(GitNavigation navigation)
    {
        // A plain "History" link keeps the current filters; a commit, file or branch link sets its own
        // (a commit link shows the whole history so the commit can be found).
        if (navigation.Sha is null && navigation.Path is null && navigation.Name is null)
        {
            return;
        }

        _suspendReload = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                _searchDelay?.Cancel();
                SearchText = string.Empty;
                _needsReload = true;
            }

            if (!string.Equals(PathFilter, navigation.Path, StringComparison.Ordinal))
            {
                PathFilter = navigation.Path;
                _needsReload = true;
            }

            if (!string.Equals(RevisionFilter, navigation.Name, StringComparison.Ordinal))
            {
                RevisionFilter = navigation.Name;
                _needsReload = true;
            }
        }
        finally
        {
            _suspendReload = false;
        }
    }

    internal override async Task NavigateAsync(GitNavigation navigation)
    {
        if (_needsReload || !HasLoaded)
        {
            await LoadAsync().ConfigureAwait(true);
        }
        else if (_loadTask is { IsCompleted: false } running)
        {
            await running.ConfigureAwait(true);
        }

        if (navigation.Sha is { } sha)
        {
            await RevealCommitAsync(sha).ConfigureAwait(true);
        }
    }

    /// <summary>Selects a commit, loading more pages (then every branch) until it is found.</summary>
    [RelayCommand]
    public async Task RevealCommitAsync(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return;
        }

        if (await FindLoadingAsync(sha).ConfigureAwait(true) is { } row)
        {
            SelectedCommit = row;
            return;
        }

        if (!AllBranches && !HasRevisionFilter && !HasPathFilter && string.IsNullOrWhiteSpace(SearchText))
        {
            _suspendReload = true;
            try
            {
                AllBranches = true;
            }
            finally
            {
                _suspendReload = false;
            }

            await LoadAsync().ConfigureAwait(true);
            if (await FindLoadingAsync(sha).ConfigureAwait(true) is { } other)
            {
                SelectedCommit = other;
                return;
            }
        }

        Notify("Commit not found", $"{sha[..Math.Min(7, sha.Length)]} isn't in the {Format.Count(Commits.Count, "commit")} loaded from this history.",
            NotificationSeverity.Warning);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ClearPathFilter() => PathFilter = null;

    [RelayCommand]
    private void ClearRevisionFilter() => RevisionFilter = null;

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _suspendReload = true;
        try
        {
            _searchDelay?.Cancel();
            SearchText = string.Empty;
            PathFilter = null;
            RevisionFilter = null;
        }
        finally
        {
            _suspendReload = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopySha(CommitRowViewModel? row)
    {
        if ((row ?? SelectedCommit) is { } target)
        {
            Copy(target.Sha, "Commit SHA");
        }
    }

    [RelayCommand]
    private void CopyMessage(CommitRowViewModel? row)
    {
        if ((row ?? SelectedCommit) is { } target)
        {
            var commit = target.Commit;
            Copy(commit.Body.Length == 0 ? commit.Subject : $"{commit.Subject}\n\n{commit.Body}", "Commit message");
        }
    }

    [RelayCommand]
    private Task CreateBranchFromCommitAsync(CommitRowViewModel? row) =>
        (row ?? SelectedCommit) is { } target
            ? GitFlows.CreateBranchAsync(Section, target.Sha, $"{target.ShortSha} · {target.Subject}")
            : Task.CompletedTask;

    [RelayCommand]
    private Task CreateTagAtCommitAsync(CommitRowViewModel? row) =>
        (row ?? SelectedCommit) is { } target
            ? GitFlows.CreateTagAsync(Section, target.Sha, $"{target.ShortSha} · {target.Subject}")
            : Task.CompletedTask;

    [RelayCommand]
    private void OpenOnGitHub(CommitRowViewModel? row)
    {
        if ((row ?? SelectedCommit) is not { } target || GitFlows.GitHubCommitUrl(Context, target.Sha) is not { } url)
        {
            return;
        }

        try
        {
            Section.Shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            ShowError(ex, "Could not open the browser");
        }
    }

    private GitLogQuery Query(int skip) => new()
    {
        Skip = skip,
        Take = PageSize,
        Revision = RevisionFilter,
        AllBranches = AllBranches && RevisionFilter is null,
        PathFilter = PathFilter,
        Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
    };

    private List<CommitRowViewModel> BuildRows(IReadOnlyList<GitCommit> page)
    {
        var graph = ShowGraph;
        return page.Select(commit => new CommitRowViewModel(commit, graph ? _graph.Add(commit) : CommitGraphRow.Empty, RefPill.Parse(commit.Refs, _remotes)))
            .ToList();
    }

    private void UpdateGraphWidth() =>
        GraphWidth = ShowGraph && _graph.MaxLaneCount > 0 ? (Math.Min(_graph.MaxLaneCount, MaxDrawnLanes) * LaneWidth) + 8 : 0;

    private CommitRowViewModel? Find(string sha) =>
        Commits.FirstOrDefault(c => c.Sha.StartsWith(sha.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<CommitRowViewModel?> FindLoadingAsync(string sha)
    {
        for (var page = 0; ; page++)
        {
            if (Find(sha) is { } row)
            {
                return row;
            }

            if (!HasMore || page >= MaxNavigationPages)
            {
                return null;
            }

            var before = Commits.Count;
            await LoadMoreAsync().ConfigureAwait(true);
            if (Commits.Count == before)
            {
                return null;
            }
        }
    }

    private async Task<IReadOnlyCollection<string>> LoadRemotesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var remotes = await Git.GetRemotesAsync(Root, cancellationToken).ConfigureAwait(true);
            return remotes.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // Only used to tell remote branches from local ones in the ref pills.
            return _remotes;
        }
    }

    private void ReloadForFilter()
    {
        if (_suspendReload)
        {
            return;
        }

        if (IsActive && HasLoaded)
        {
            _ = LoadAsync();
        }
        else
        {
            _needsReload = true;
            Invalidate();
        }
    }

    private async Task ReloadAfterTypingAsync(CancellationTokenSource delay)
    {
        try
        {
            if (SearchDelay > TimeSpan.Zero)
            {
                await Task.Delay(SearchDelay, delay.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!delay.IsCancellationRequested && !IsDisposed)
        {
            await Section.Services.Dispatcher.InvokeAsync(() => _ = LoadAsync()).ConfigureAwait(true);
        }
    }

    protected override void OnDisposed()
    {
        _load?.Cancel();
        _searchDelay?.Cancel();
        Details?.Dispose();
    }
}
