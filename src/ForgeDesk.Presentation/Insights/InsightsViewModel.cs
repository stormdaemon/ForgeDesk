using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Overview;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Insights;

/// <summary>
/// The Insights tab: an actionable health report of the project (score, what needs attention,
/// codebase, dependencies, hygiene, TODOs, large files, git activity, tests). The cached report
/// shows immediately; "Analyze" rescans with progress and can be cancelled.
/// </summary>
public sealed partial class InsightsViewModel : ViewModelBase, IWorkspaceSectionViewModel, IRefreshable, IDisposable
{
    internal const int MaxTodos = 500;

    private readonly IProjectAnalyzer _analyzer;
    private readonly IProjectRegistry _registry;
    private readonly IBackgroundOperations _operations;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<InsightsDependencyRowViewModel> _allDependencyRows = [];
    private CancellationTokenSource? _analysis;
    private bool _activatedOnce;
    private bool _disposed;

    public InsightsViewModel(
        ProjectContext context,
        IProjectAnalyzer analyzer,
        IProjectRegistry registry,
        IBackgroundOperations operations,
        INotificationService notifications,
        IShellIntegration shell,
        IUiDispatcher dispatcher)
    {
        _shell = shell;
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        _analyzer = analyzer;
        _registry = registry;
        _operations = operations;
        _notifications = notifications;
        _dispatcher = dispatcher;
        Context.PropertyChanged += OnContextPropertyChanged;
        Context.GitStatusChanged += OnGitStatusChanged;
        UpdateFolderState();
    }

    public WorkspaceSection Section => WorkspaceSection.Insights;

    public ProjectContext Context { get; }

    // ----- State ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(ShowReport))]
    [NotifyPropertyChangedFor(nameof(ShowNotAnalyzed))]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowFirstAnalysis))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial ProjectHealthReport? Report { get; private set; }

    public bool HasReport => Report is not null;

    /// <summary>Reading the cached report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowNotAnalyzed))]
    public partial bool IsLoadingCache { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFirstAnalysis))]
    [NotifyPropertyChangedFor(nameof(ShowNotAnalyzed))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(AnalyzeText))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAnalysisCommand))]
    public partial bool IsAnalyzing { get; private set; }

    /// <summary>What the analyzer is doing ("Scanning files…").</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReport))]
    [NotifyPropertyChangedFor(nameof(ShowNotAnalyzed))]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowFirstAnalysis))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsFolderMissing { get; private set; }

    public bool ShowLoading => IsLoadingCache && Report is null && !IsFolderMissing;

    /// <summary>First analysis of a project without a cached report: full-page progress.</summary>
    public bool ShowFirstAnalysis => IsAnalyzing && Report is null && !IsFolderMissing;

    public bool ShowError => HasError && Report is null && !IsAnalyzing && !IsFolderMissing;

    public bool ShowNotAnalyzed => Report is null && !IsLoadingCache && !IsAnalyzing && !HasError && !IsFolderMissing;

    public bool ShowReport => Report is not null && !IsFolderMissing;

    public string AnalyzeText => IsAnalyzing ? "Analyzing…" : "Analyze";

    // ----- Header -------------------------------------------------------------------------

    [ObservableProperty]
    public partial int Score { get; private set; }

    /// <summary>0..1 for the gauge.</summary>
    [ObservableProperty]
    public partial double ScoreFraction { get; private set; }

    [ObservableProperty]
    public partial StatusTone ScoreTone { get; private set; }

    [ObservableProperty]
    public partial string ScoreLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? GeneratedAt { get; private set; }

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    public static StatusTone ToneForScore(int score) => score switch
    {
        >= 80 => StatusTone.Success,
        >= 50 => StatusTone.Warning,
        _ => StatusTone.Danger,
    };

    public static string LabelForScore(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 80 => "Healthy",
        >= 60 => "Fair",
        >= 40 => "Needs work",
        _ => "At risk",
    };

    // ----- Sections -------------------------------------------------------------------------

    public ObservableCollection<AttentionItemViewModel> Attention { get; } = [];

    [ObservableProperty]
    public partial bool HasAttention { get; private set; }

    public ObservableCollection<InsightsLanguageViewModel> Languages { get; } = [];

    [ObservableProperty]
    public partial string FilesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SizeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LinesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ScanTruncated { get; private set; }

    /// <summary>Manifest headers and dependencies matching <see cref="DependencySearch"/>.</summary>
    public ObservableCollection<InsightsDependencyRowViewModel> DependencyRows { get; } = [];

    [ObservableProperty]
    public partial int DependencyCount { get; private set; }

    [ObservableProperty]
    public partial int DevDependencyCount { get; private set; }

    [ObservableProperty]
    public partial string DependencySearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasDependencies { get; private set; }

    [ObservableProperty]
    public partial bool HasDependencyMatches { get; private set; }

    public string DependencySummary => DevDependencyCount > 0
        ? $"{Format.Count(DependencyCount, "package")} · {DevDependencyCount} for development"
        : Format.Count(DependencyCount, "package");

    public ObservableCollection<InsightsHygieneItemViewModel> Hygiene { get; } = [];

    [ObservableProperty]
    public partial string HygieneSummary { get; private set; } = string.Empty;

    public ObservableCollection<InsightsTodoTagViewModel> TodoTags { get; } = [];

    public ObservableCollection<InsightsTodoViewModel> Todos { get; } = [];

    [ObservableProperty]
    public partial int TodoCount { get; private set; }

    /// <summary>More markers exist than are listed.</summary>
    [ObservableProperty]
    public partial string? TodoOverflowText { get; private set; }

    public ObservableCollection<InsightsLargeFileViewModel> LargestFiles { get; } = [];

    [ObservableProperty]
    public partial bool HasLargestFiles { get; private set; }

    [ObservableProperty]
    public partial bool HasGitActivity { get; private set; }

    [ObservableProperty]
    public partial int Commits30 { get; private set; }

    [ObservableProperty]
    public partial int Commits90 { get; private set; }

    [ObservableProperty]
    public partial int TotalCommits { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? FirstCommitAt { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastCommitAt { get; private set; }

    public ObservableCollection<InsightsWeekViewModel> Weeks { get; } = [];

    public ObservableCollection<InsightsContributorViewModel> Contributors { get; } = [];

    [ObservableProperty]
    public partial bool HasRepository { get; private set; }

    public ObservableCollection<InsightsRepositoryFactViewModel> RepositoryFacts { get; } = [];

    public ObservableCollection<InsightsBranchViewModel> CleanupBranches { get; } = [];

    [ObservableProperty]
    public partial bool HasTests { get; private set; }

    [ObservableProperty]
    public partial int TestFileCount { get; private set; }

    public ObservableCollection<string> TestFrameworks { get; } = [];

    public ObservableCollection<string> TestLocations { get; } = [];

    // ----- Lifecycle --------------------------------------------------------------------------

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        UpdateFolderState();
        if (_activatedOnce)
        {
            return;
        }

        _activatedOnce = true;
        await LoadCachedAsync().ConfigureAwait(true);
        if (Report is null && !IsFolderMissing && !HasError)
        {
            await AnalyzeAsync().ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
    }

    /// <summary>F5 re-analyzes (the cached report stays visible meanwhile).</summary>
    [RelayCommand]
    private Task RefreshAsync() => IsAnalyzing ? Task.CompletedTask : AnalyzeAsync();

    private async Task LoadCachedAsync()
    {
        IsLoadingCache = true;
        try
        {
            await RunAsync(async () =>
            {
                var cached = await _registry.GetCachedHealthAsync(Context.ProjectId, Context.Lifetime).ConfigureAwait(true);
                if (cached is not null && Report is null)
                {
                    Apply(cached);
                }
            }, errorTitle: "Could not read the last analysis").ConfigureAwait(true);
        }
        finally
        {
            IsLoadingCache = false;
        }
    }

    private bool CanAnalyze() => !IsAnalyzing && !IsFolderMissing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        UpdateFolderState();
        if (_disposed || IsAnalyzing || IsFolderMissing)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(Context.Lifetime);
        _analysis = cts;
        IsAnalyzing = true;
        ProgressText = "Starting analysis…";
        var hadReport = Report is not null;
        using var operation = _operations.Begin($"Analyzing {Context.Project.Name}", Context.ProjectId);
        var progress = new DispatchedProgress(_dispatcher, text =>
        {
            if (ReferenceEquals(_analysis, cts))
            {
                ProgressText = text;
                operation.Message = text;
            }
        });
        try
        {
            await RunAsync(async () =>
            {
                var report = await _analyzer.AnalyzeAsync(Context.Project, progress, cts.Token).ConfigureAwait(true);
                cts.Token.ThrowIfCancellationRequested();
                Apply(report);
            }, errorTitle: "The analysis failed", errorMode: hadReport ? ErrorMode.Toast : ErrorMode.Inline, notifications: _notifications)
                .ConfigureAwait(true);
        }
        finally
        {
            progress.Complete();
            if (ReferenceEquals(_analysis, cts))
            {
                _analysis = null;
            }

            cts.Dispose();
            IsAnalyzing = false;
            ProgressText = null;
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowNotAnalyzed));
        }
    }

    /// <summary>"Check again" of the missing-folder state (after the folder was restored or located).</summary>
    [RelayCommand]
    private async Task RecheckFolderAsync()
    {
        UpdateFolderState();
        if (IsFolderMissing)
        {
            return;
        }

        if (Report is null && !IsLoadingCache)
        {
            await LoadCachedAsync().ConfigureAwait(true);
        }

        if (Report is null)
        {
            await AnalyzeAsync().ConfigureAwait(true);
        }
    }

    private bool CanCancelAnalysis() => IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis()
    {
        try
        {
            _analysis?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        ProgressText = "Cancelling…";
    }

    // ----- Navigation ----------------------------------------------------------------------------

    [RelayCommand]
    private void OpenAttention(AttentionItemViewModel? item)
    {
        if (item?.Section is { } section)
        {
            Context.RequestNavigation(section);
        }
    }

    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Context.RequestNavigation(WorkspaceSection.Files, path);
        }
    }

    [RelayCommand]
    private void OpenTodo(InsightsTodoViewModel? todo) => OpenFile(todo?.Path);

    [RelayCommand]
    private void OpenLargeFile(InsightsLargeFileViewModel? file) => OpenFile(file?.Path);

    [RelayCommand]
    private void OpenHygieneItem(InsightsHygieneItemViewModel? item)
    {
        if (item is { CanOpen: true })
        {
            OpenFile(item.Path);
        }
    }

    [RelayCommand]
    private void OpenGit() => Context.RequestNavigation(WorkspaceSection.Git);

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            _shell.CopyToClipboard(text);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(Core.Common.ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    [RelayCommand]
    private void ClearDependencySearch() => DependencySearch = string.Empty;

    partial void OnDependencySearchChanged(string value) => FilterDependencies();

    // ----- Report -----------------------------------------------------------------------------

    internal void Apply(ProjectHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Error = null;
        Score = Math.Clamp(report.Score, 0, 100);
        ScoreFraction = Score / 100d;
        ScoreTone = ToneForScore(Score);
        ScoreLabel = LabelForScore(Score);
        GeneratedAt = report.GeneratedAt;
        SummaryText = report.Attention.Count == 0
            ? "Nothing needs your attention."
            : $"{Format.Count(report.Attention.Count, "finding")} to look at · analyzed in {Format.Duration(report.Duration)}";

        Replace(Attention, AttentionItemViewModel.FromAll(report.Attention, WorkspaceSection.Insights));
        HasAttention = Attention.Count > 0;

        ApplyCodebase(report);
        ApplyDependencies(report);
        ApplyHygiene(report);
        ApplyTodos(report);
        ApplyLargestFiles(report);
        ApplyGit(report);
        ApplyTests(report);

        Report = report;
    }

    private void ApplyCodebase(ProjectHealthReport report)
    {
        var culture = CultureInfo.CurrentCulture;
        FilesText = report.FileCount.ToString("N0", culture);
        SizeText = Format.Bytes(report.TotalBytes);
        LinesText = report.TotalLines.ToString("N0", culture);
        ScanTruncated = report.ScanTruncated;
        var totalLines = report.Languages.Sum(l => l.Lines);
        var totalBytes = report.Languages.Sum(l => l.Bytes);
        Replace(Languages, report.Languages
            .OrderByDescending(l => l.Lines).ThenByDescending(l => l.Bytes)
            .Select(l => new InsightsLanguageViewModel(l.Language, l.Files, l.Lines, l.Bytes, l.Color,
                totalLines > 0 ? (double)l.Lines / totalLines : totalBytes > 0 ? (double)l.Bytes / totalBytes : 0))
            .ToList());
    }

    private void ApplyDependencies(ProjectHealthReport report)
    {
        _allDependencyRows.Clear();
        foreach (var group in report.Dependencies
            .GroupBy(d => (d.Ecosystem, d.Manifest))
            .OrderBy(g => g.Key.Ecosystem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Manifest, StringComparer.OrdinalIgnoreCase))
        {
            _allDependencyRows.Add(new InsightsDependencyGroupViewModel(group.Key.Ecosystem, group.Key.Manifest, group.Count(), group.Count(d => d.IsDevelopment)));
            foreach (var dependency in group.OrderBy(d => d.IsDevelopment).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                _allDependencyRows.Add(new InsightsDependencyViewModel(dependency));
            }
        }

        DependencyCount = report.Dependencies.Count;
        DevDependencyCount = report.Dependencies.Count(d => d.IsDevelopment);
        HasDependencies = DependencyCount > 0;
        OnPropertyChanged(nameof(DependencySummary));
        FilterDependencies();
    }

    private void FilterDependencies()
    {
        var search = DependencySearch.Trim();
        var rows = new List<InsightsDependencyRowViewModel>();
        InsightsDependencyGroupViewModel? pendingHeader = null;
        foreach (var row in _allDependencyRows)
        {
            if (row is InsightsDependencyGroupViewModel header)
            {
                pendingHeader = header;
                continue;
            }

            var dependency = (InsightsDependencyViewModel)row;
            if (search.Length > 0
                && !dependency.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !dependency.Manifest.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pendingHeader is not null)
            {
                rows.Add(pendingHeader);
                pendingHeader = null;
            }

            rows.Add(dependency);
        }

        DependencyRows.SyncWith(rows);
        HasDependencyMatches = rows.Count > 0;
    }

    private void ApplyHygiene(ProjectHealthReport report)
    {
        Replace(Hygiene, report.ImportantFiles
            .OrderBy(f => f.Present)
            .Select(f => new InsightsHygieneItemViewModel(f.Label, f.Present, f.Path, f.Why))
            .ToList());
        var present = report.ImportantFiles.Count(f => f.Present);
        HygieneSummary = $"{present} of {report.ImportantFiles.Count} in place";
    }

    private void ApplyTodos(ProjectHealthReport report)
    {
        TodoCount = Math.Max(report.TodoCount, report.Todos.Count);
        Replace(TodoTags, report.Todos
            .GroupBy(t => t.Tag.ToUpperInvariant())
            .Select(g => new InsightsTodoTagViewModel(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList());
        Replace(Todos, report.Todos.Take(MaxTodos).Select(t => new InsightsTodoViewModel(t.RelativePath, t.Line, t.Tag, t.Text)).ToList());
        TodoOverflowText = TodoCount > Todos.Count ? $"Showing {Todos.Count:N0} of {TodoCount:N0} markers" : null;
    }

    private void ApplyLargestFiles(ProjectHealthReport report)
    {
        var max = report.LargestFiles.Count == 0 ? 0 : report.LargestFiles.Max(f => f.Bytes);
        Replace(LargestFiles, report.LargestFiles
            .OrderByDescending(f => f.Bytes)
            .Select(f => new InsightsLargeFileViewModel(f.RelativePath, f.Bytes, max > 0 ? (double)f.Bytes / max : 0))
            .ToList());
        HasLargestFiles = LargestFiles.Count > 0;
    }

    private void ApplyGit(ProjectHealthReport report)
    {
        var git = report.Git;
        HasGitActivity = git is not null;
        Commits30 = git?.CommitsLast30Days ?? 0;
        Commits90 = git?.CommitsLast90Days ?? 0;
        TotalCommits = git?.TotalCommits ?? 0;
        FirstCommitAt = git?.FirstCommitAt;
        LastCommitAt = git?.LastCommitAt;
        Replace(Weeks, BuildWeeks(git?.WeeklyCommits ?? [], report.GeneratedAt));
        var top = git?.TopContributors.Count > 0 ? git.TopContributors.Max(c => c.Commits) : 0;
        Replace(Contributors, (git?.TopContributors ?? [])
            .Select(c => new InsightsContributorViewModel(c.Name, c.Commits, top > 0 ? (double)c.Commits / top : 0))
            .ToList());

        var repository = report.Repository;
        HasRepository = repository is not null;
        var facts = new List<InsightsRepositoryFactViewModel>();
        if (repository is not null)
        {
            facts.Add(new("Uncommitted changes", repository.UncommittedChanges, repository.UncommittedChanges > 0 ? StatusTone.Warning : StatusTone.Success, "DocumentEdit20"));
            facts.Add(new("Unpushed commits", repository.UnpushedCommits, repository.UnpushedCommits > 0 ? StatusTone.Info : StatusTone.Success, "ArrowUp20"));
            facts.Add(new("Behind the remote", repository.BehindCommits, repository.BehindCommits > 0 ? StatusTone.Warning : StatusTone.Success, "ArrowDown20"));
            facts.Add(new("Stashes", repository.Stashes, repository.Stashes > 0 ? StatusTone.Neutral : StatusTone.Success, "Archive20"));
        }

        Replace(RepositoryFacts, facts);
        Replace(CleanupBranches, (repository?.MergedBranches ?? []).Select(b => new InsightsBranchViewModel(b, IsMerged: true))
            .Concat((repository?.StaleBranches ?? []).Except(repository?.MergedBranches ?? [], StringComparer.Ordinal)
                .Select(b => new InsightsBranchViewModel(b, IsMerged: false)))
            .ToList());
    }

    /// <summary>The 12 rolling weeks (oldest first) labelled by their first day.</summary>
    internal static IReadOnlyList<InsightsWeekViewModel> BuildWeeks(IReadOnlyList<int> weekly, DateTimeOffset generatedAt)
    {
        if (weekly.Count == 0)
        {
            return [];
        }

        var max = Math.Max(1, weekly.Max());
        var end = DateOnly.FromDateTime(generatedAt.ToLocalTime().DateTime);
        return weekly
            .Select((count, index) => new InsightsWeekViewModel(
                end.AddDays(-7 * (weekly.Count - index) + 1),
                count,
                (double)count / max,
                index == weekly.Count - 1))
            .ToList();
    }

    private void ApplyTests(ProjectHealthReport report)
    {
        var tests = report.Tests;
        HasTests = tests is { HasTests: true } || report.TestFileCount > 0;
        TestFileCount = report.TestFileCount;
        Replace(TestFrameworks, tests?.Frameworks ?? []);
        Replace(TestLocations, tests?.Locations ?? []);
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    // ----- Context ------------------------------------------------------------------------------

    private void UpdateFolderState() => IsFolderMissing = !Context.FolderExists;

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectContext.Project))
        {
            UpdateFolderState();
        }
    }

    private void OnGitStatusChanged(object? sender, EventArgs e) => UpdateFolderState();

    partial void OnIsFolderMissingChanged(bool value) => OnPropertyChanged(nameof(ShowNotAnalyzed));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Error))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowError)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowNotAnalyzed)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Context.PropertyChanged -= OnContextPropertyChanged;
        Context.GitStatusChanged -= OnGitStatusChanged;
        try
        {
            _analysis?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Reports analyzer progress on the UI thread, and nothing once the analysis ended.</summary>
    private sealed class DispatchedProgress(IUiDispatcher dispatcher, Action<string> report) : IProgress<string>
    {
        private volatile bool _completed;

        public void Report(string value)
        {
            if (_completed || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            dispatcher.Post(() =>
            {
                if (!_completed)
                {
                    report(value);
                }
            });
        }

        public void Complete() => _completed = true;
    }
}
