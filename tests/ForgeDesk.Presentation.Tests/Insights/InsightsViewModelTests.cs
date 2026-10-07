using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Insights;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Presentation.Tests.Insights;

public sealed class InsightsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly TestFolder _folder = new();
    private readonly IProjectAnalyzer _analyzer = Substitute.For<IProjectAnalyzer>();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly BackgroundOperations _operations = new(ImmediateDispatcher.Instance);
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly List<WorkspaceNavigationRequest> _navigations = [];
    private readonly ProjectContext _context;
    private readonly List<InsightsViewModel> _created = [];

    public InsightsViewModelTests()
    {
        var project = TestData.Project("forge-app", _folder.Path);
        _context = new ProjectContext(project, Substitute.For<IGitService>(), Substitute.For<IProjectDetector>(), _registry, ImmediateDispatcher.Instance);
        _context.NavigationRequested += (_, request) => _navigations.Add(request);
        _registry.GetCachedHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProjectHealthReport?>(null));
    }

    public void Dispose()
    {
        foreach (var insights in _created)
        {
            insights.Dispose();
        }

        _context.Dispose();
        _folder.Dispose();
    }

    private InsightsViewModel Create()
    {
        var insights = new InsightsViewModel(_context, _analyzer, _registry, _operations, _notifications, _shell, ImmediateDispatcher.Instance);
        _created.Add(insights);
        return insights;
    }

    // ----- Cached report, then analysis ------------------------------------------------

    [Fact]
    public async Task The_cached_report_shows_immediately_without_a_new_analysis()
    {
        _registry.GetCachedHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProjectHealthReport?>(Report(score: 86)));
        var insights = Create();

        await insights.ActivateAsync();

        insights.ShowReport.Should().BeTrue();
        insights.Score.Should().Be(86);
        insights.ScoreTone.Should().Be(StatusTone.Success);
        insights.GeneratedAt.Should().Be(Generated);
        await _analyzer.DidNotReceive().AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_cached_report_the_first_activation_analyzes_and_reports_progress()
    {
        var progressSeen = new List<string?>();
        var insights = Create();
        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.ArgAt<IProgress<string>?>(1)!.Report("Scanning files…");
            progressSeen.Add(insights.ProgressText);
            progressSeen.Add(insights.ShowFirstAnalysis ? "first" : "not first");
            progressSeen.Add(_operations.Operations.Single().Title);
            progressSeen.Add(_operations.Operations.Single().Message);
            return Task.FromResult(Report(score: 42));
        });

        await insights.ActivateAsync();

        progressSeen.Should().Equal("Scanning files…", "first", "Analyzing forge-app", "Scanning files…");
        insights.ShowReport.Should().BeTrue();
        insights.IsAnalyzing.Should().BeFalse();
        insights.ProgressText.Should().BeNull();
        insights.ScoreTone.Should().Be(StatusTone.Danger);
        insights.ScoreLabel.Should().Be("Needs work");
        _operations.Operations.Should().BeEmpty("the status bar entry ends with the analysis");
    }

    [Fact]
    public async Task Analyze_refreshes_the_report_while_the_old_one_stays_visible()
    {
        _registry.GetCachedHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProjectHealthReport?>(Report(score: 60)));
        var insights = Create();
        await insights.ActivateAsync();
        var gate = new TaskCompletionSource<ProjectHealthReport>();
        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(gate.Task);

        var analysis = insights.AnalyzeCommand.ExecuteAsync(null);

        insights.IsAnalyzing.Should().BeTrue();
        insights.ShowReport.Should().BeTrue();
        insights.ShowFirstAnalysis.Should().BeFalse();
        insights.AnalyzeCommand.CanExecute(null).Should().BeFalse();

        gate.SetResult(Report(score: 95));
        await analysis;

        insights.Score.Should().Be(95);
        insights.ScoreLabel.Should().Be("Excellent");
    }

    [Fact]
    public async Task Cancelling_the_first_analysis_leaves_a_not_analyzed_state_without_error()
    {
        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellationAsync(call.ArgAt<CancellationToken>(2)));
        var insights = Create();

        var activation = insights.ActivateAsync();
        insights.ShowFirstAnalysis.Should().BeTrue();
        insights.CancelAnalysisCommand.Execute(null);
        await activation;

        insights.IsAnalyzing.Should().BeFalse();
        insights.HasError.Should().BeFalse();
        insights.ShowNotAnalyzed.Should().BeTrue();
        _notifications.DidNotReceiveWithAnyArgs().ShowError(default!);
    }

    [Fact]
    public async Task A_failed_first_analysis_shows_the_error_with_a_retry()
    {
        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.PermissionDenied, "Access to the folder is denied."));
        var insights = Create();

        await insights.ActivateAsync();

        insights.ShowError.Should().BeTrue();
        insights.Error!.Message.Should().Be("Access to the folder is denied.");

        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Report()));
        await insights.AnalyzeCommand.ExecuteAsync(null);

        insights.ShowError.Should().BeFalse();
        insights.ShowReport.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_reanalysis_keeps_the_report_and_notifies()
    {
        _registry.GetCachedHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProjectHealthReport?>(Report(score: 70)));
        var insights = Create();
        await insights.ActivateAsync();
        _analyzer.AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.Unknown, "Boom."));

        await insights.RefreshCommand.ExecuteAsync(null);

        insights.Score.Should().Be(70);
        insights.HasError.Should().BeFalse();
        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "Boom."), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task A_missing_folder_is_not_analyzed()
    {
        _folder.Dispose();
        var insights = Create();

        await insights.ActivateAsync();

        insights.IsFolderMissing.Should().BeTrue();
        insights.ShowNotAnalyzed.Should().BeFalse();
        insights.AnalyzeCommand.CanExecute(null).Should().BeFalse();
        await _analyzer.DidNotReceive().AnalyzeAsync(Arg.Any<Project>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
    }

    // ----- Score --------------------------------------------------------------------------

    [Theory]
    [InlineData(100, StatusTone.Success, "Excellent")]
    [InlineData(80, StatusTone.Success, "Healthy")]
    [InlineData(79, StatusTone.Warning, "Fair")]
    [InlineData(50, StatusTone.Warning, "Needs work")]
    [InlineData(49, StatusTone.Danger, "Needs work")]
    [InlineData(10, StatusTone.Danger, "At risk")]
    public void Score_maps_to_a_tone_and_a_label(int score, StatusTone tone, string label)
    {
        InsightsViewModel.ToneForScore(score).Should().Be(tone);
        InsightsViewModel.LabelForScore(score).Should().Be(label);
    }

    // ----- Sections and navigation ---------------------------------------------------------

    [Fact]
    public async Task Attention_items_open_the_related_tab()
    {
        var insights = await LoadAsync(Report() with
        {
            Attention =
            [
                new AttentionReason(AttentionLevel.Info, "Add a LICENSE file.", "Files"),
                new AttentionReason(AttentionLevel.Warning, "2 commits behind.", "Git"),
                new AttentionReason(AttentionLevel.Info, "No tests found.", "Insights"),
            ],
        });

        insights.Attention.Select(a => a.Message).Should().Equal("2 commits behind.", "Add a LICENSE file.", "No tests found.");
        insights.Attention[2].CanOpen.Should().BeFalse();
        insights.OpenAttentionCommand.Execute(insights.Attention[0]);

        _navigations.Should().ContainSingle().Which.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Git));
    }

    [Fact]
    public async Task Todos_hygiene_and_large_files_open_the_file_in_Files()
    {
        var insights = await LoadAsync(Report() with
        {
            Todos = [new TodoItem("src/app.ts", 42, "FIXME", "handle null"), new TodoItem("src/b.ts", 3, "TODO", "rename")],
            TodoCount = 2,
            ImportantFiles =
            [
                new ImportantFileCheck("README", true, "README.md", "Explains the project."),
                new ImportantFileCheck("LICENSE", false, null, "Lets others use the code."),
            ],
            LargestFiles = [new LargeFile("assets/video.mp4", 40_000_000), new LargeFile("assets/logo.png", 10_000_000)],
        });

        insights.TodoTags.Select(t => (t.Tag, t.Count)).Should().BeEquivalentTo([("FIXME", 1), ("TODO", 1)]);
        insights.Todos[0].Location.Should().Be("src/app.ts:42");
        insights.Hygiene[0].Label.Should().Be("LICENSE", "missing files come first");
        insights.Hygiene[0].CanOpen.Should().BeFalse();
        insights.HygieneSummary.Should().Be("1 of 2 in place");
        insights.LargestFiles[1].Ratio.Should().Be(0.25);

        insights.OpenTodoCommand.Execute(insights.Todos[0]);
        insights.OpenHygieneItemCommand.Execute(insights.Hygiene[1]);
        insights.OpenHygieneItemCommand.Execute(insights.Hygiene[0]);
        insights.OpenLargeFileCommand.Execute(insights.LargestFiles[0]);

        _navigations.Should().Equal(
            new WorkspaceNavigationRequest(WorkspaceSection.Files, "src/app.ts"),
            new WorkspaceNavigationRequest(WorkspaceSection.Files, "README.md"),
            new WorkspaceNavigationRequest(WorkspaceSection.Files, "assets/video.mp4"));
    }

    [Fact]
    public async Task Codebase_lists_languages_with_their_share_of_lines()
    {
        var insights = await LoadAsync(Report() with
        {
            FileCount = 1234,
            TotalLines = 400,
            ScanTruncated = true,
            Languages = [new LanguageStat("C#", 10, 100, 1000, "#178600"), new LanguageStat("TypeScript", 20, 300, 3000, "#3178C6")],
        });

        insights.Languages.Select(l => l.Name).Should().Equal("TypeScript", "C#");
        insights.Languages[0].Share.Should().Be(0.75);
        insights.Languages[0].ShareText.Should().Contain("75");
        insights.ScanTruncated.Should().BeTrue();
        insights.FilesText.Should().Be(1234.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task Dependencies_are_grouped_by_manifest_and_searchable()
    {
        var insights = await LoadAsync(Report() with
        {
            Dependencies =
            [
                new DependencyInfo("npm", "vitest", "1.0.0", true, "package.json"),
                new DependencyInfo("npm", "react", "18.2.0", false, "package.json"),
                new DependencyInfo("NuGet", "Dapper", "2.1.0", false, "src/App/App.csproj"),
            ],
        });

        insights.DependencySummary.Should().Be("3 packages · 1 for development");
        Rows(insights).Should().Equal("[npm package.json 2]", "react", "vitest", "[NuGet src/App/App.csproj 1]", "Dapper");

        insights.DependencySearch = "REA";
        Rows(insights).Should().Equal("[npm package.json 2]", "react");

        insights.DependencySearch = "zzz";
        insights.HasDependencyMatches.Should().BeFalse();

        insights.ClearDependencySearchCommand.Execute(null);
        Rows(insights).Should().HaveCount(5);
    }

    [Fact]
    public async Task Git_activity_builds_a_12_week_chart_and_repository_facts()
    {
        var insights = await LoadAsync(Report() with
        {
            Git = new GitActivity
            {
                CommitsLast30Days = 12,
                CommitsLast90Days = 30,
                TotalCommits = 500,
                WeeklyCommits = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 20],
                TopContributors = [new ContributorStat("Ada Lovelace", 40), new ContributorStat("Linus", 10)],
            },
            Repository = new RepositoryState
            {
                UncommittedChanges = 3,
                UnpushedCommits = 0,
                StaleBranches = ["old-feature", "done"],
                MergedBranches = ["done"],
            },
        });

        insights.Weeks.Should().HaveCount(12);
        insights.Weeks[^1].Ratio.Should().Be(1);
        insights.Weeks[^1].IsCurrent.Should().BeTrue();
        insights.Weeks[10].Ratio.Should().Be(0.5);
        insights.Weeks[0].Commits.Should().Be(0);
        insights.Contributors[1].Ratio.Should().Be(0.25);
        insights.Contributors[0].Initials.Should().Be("AL");
        insights.RepositoryFacts.Should().Contain(f => f.Label == "Uncommitted changes" && f.Value == 3 && f.Tone == StatusTone.Warning);
        insights.CleanupBranches.Select(b => (b.Name, b.IsMerged)).Should().Equal(("done", true), ("old-feature", false));

        insights.OpenGitCommand.Execute(null);
        _navigations.Should().ContainSingle(n => n.Section == WorkspaceSection.Git && n.Argument == null);
    }

    [Fact]
    public void Week_bars_are_labelled_by_their_first_day()
    {
        var weeks = InsightsViewModel.BuildWeeks([1, 2], new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero).ToLocalTime());

        weeks[1].WeekStart.Should().Be(new DateOnly(2026, 10, 1));
        weeks[0].WeekStart.Should().Be(new DateOnly(2026, 9, 24));
        weeks[0].Ratio.Should().Be(0.5);
    }

    [Fact]
    public async Task Tests_card_reports_frameworks_or_their_absence()
    {
        var withTests = await LoadAsync(Report() with { Tests = new TestInfo(true, ["xUnit"], ["tests"]), TestFileCount = 14 });
        withTests.HasTests.Should().BeTrue();
        withTests.TestFrameworks.Should().Equal("xUnit");
        withTests.TestFileCount.Should().Be(14);

        withTests.Apply(Report() with { Tests = new TestInfo(false, [], []), TestFileCount = 0 });
        withTests.HasTests.Should().BeFalse();
        withTests.TestFrameworks.Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_puts_text_on_the_clipboard()
    {
        var insights = await LoadAsync(Report());

        insights.CopyTextCommand.Execute("src/app.ts:42");

        _shell.Received(1).CopyToClipboard("src/app.ts:42");
    }

    [Fact]
    public void The_registration_adds_the_tab()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddInsightsPresentation();

        services.Select(d => d.ImplementationInstance)
            .Should().Contain(new WorkspaceSectionRegistration(WorkspaceSection.Insights, typeof(InsightsViewModel)));
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private async Task<InsightsViewModel> LoadAsync(ProjectHealthReport report)
    {
        _registry.GetCachedHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProjectHealthReport?>(report));
        var insights = Create();
        await insights.ActivateAsync();
        insights.ShowReport.Should().BeTrue();
        return insights;
    }

    private static ProjectHealthReport Report(int score = 75) => new()
    {
        GeneratedAt = Generated,
        Duration = TimeSpan.FromSeconds(2),
        Score = score,
    };

    private static IReadOnlyList<string> Rows(InsightsViewModel insights) => insights.DependencyRows
        .Select(r => r is InsightsDependencyGroupViewModel g ? $"[{g.Ecosystem} {g.Manifest} {g.Count}]" : ((InsightsDependencyViewModel)r).Name)
        .ToList();

    private static async Task<ProjectHealthReport> WaitForCancellationAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException("Unreachable.");
    }
}
