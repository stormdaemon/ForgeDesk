using System.Diagnostics;
using ForgeDesk.Core.Analysis.Dependencies;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Analysis;

/// <summary>
/// Builds the project health report: files, languages, dependencies, checklist, TODOs, Git
/// activity and repository hygiene, then an attention list and a score. Every stage is bounded
/// and cancellable; Git problems (not a repository, Git missing) only drop the Git sections.
/// </summary>
internal sealed class ProjectAnalyzer : IProjectAnalyzer
{
    private readonly IGitService _git;
    private readonly IProjectRegistry _registry;
    private readonly IProjectDetector _detector;
    private readonly IClock _clock;
    private readonly ILogger<ProjectAnalyzer> _logger;
    private readonly ProjectFileLister _lister;

    public ProjectAnalyzer(IGitService git, IProjectRegistry registry, IProjectDetector detector, IClock clock, ILogger<ProjectAnalyzer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(clock);
        _git = git;
        _registry = registry;
        _detector = detector;
        _clock = clock;
        _logger = logger ?? NullLogger<ProjectAnalyzer>.Instance;
        _lister = new ProjectFileLister(git, _logger);
    }

    public async Task<ProjectHealthReport> AnalyzeAsync(Project project, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Directory.Exists(project.Path))
        {
            throw new ForgeException(ErrorKind.PathNotFound, "The project folder no longer exists.",
                "Relocate the project to its new folder, or remove it from ForgeDesk.");
        }

        var stopwatch = Stopwatch.StartNew();
        var now = _clock.Now;
        var root = project.Path;

        Report(progress, "Listing files…");
        var isRepository = await IsRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
        var listing = await _lister.ListAsync(root, isRepository, cancellationToken).ConfigureAwait(false);

        Report(progress, "Counting lines of code…");
        var source = await SourceScanner.ScanAsync(root, listing.Files, message => Report(progress, message), cancellationToken).ConfigureAwait(false);

        Report(progress, "Reading dependencies…");
        var dependencies = await DependencyScanner.ScanAsync(root, listing.Files.Select(f => f.RelativePath), cancellationToken).ConfigureAwait(false);

        Report(progress, "Looking for tests…");
        var tests = await ResolveTestsAsync(project, cancellationToken).ConfigureAwait(false);
        var testFiles = listing.Files.Where(f => TestFileClassifier.IsTestFile(f.RelativePath)).ToList();
        var hasTests = tests?.HasTests == true || testFiles.Count > 0;
        var testLocation = tests?.Locations.FirstOrDefault() ?? testFiles.FirstOrDefault()?.RelativePath;
        var importantFiles = ImportantFilesChecker.Check(
            listing.Files.Select(f => f.RelativePath).ToList(),
            hasTests,
            testLocation,
            dependencies.Select(d => d.Ecosystem).ToHashSet(StringComparer.Ordinal));

        var largest = listing.Files
            .OrderByDescending(f => f.Bytes)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(AnalysisLimits.LargestFilesCount)
            .Select(f => new LargeFile(f.RelativePath, f.Bytes))
            .ToList();

        GitSnapshot? git = null;
        if (isRepository)
        {
            Report(progress, "Reading Git history…");
            git = await ReadGitAsync(root, now, cancellationToken).ConfigureAwait(false);
        }

        Report(progress, "Scoring…");
        var assessment = HealthScorer.Evaluate(new HealthSignals
        {
            ImportantFiles = importantFiles,
            HasTests = hasTests,
            TodoCount = source.TodoCount,
            Repository = git?.Repository,
            CurrentBranch = git?.Status.Branch,
            Upstream = git?.Status.Upstream,
            DefaultBranch = git?.DefaultBranch,
            HasRemote = git?.HasRemote ?? false,
            OversizedFiles = listing.FromGit
                ? largest.Where(f => f.Bytes > AnalysisLimits.OversizedFileBytes).ToList()
                : [],
        });

        var report = new ProjectHealthReport
        {
            GeneratedAt = now,
            Duration = stopwatch.Elapsed,
            FileCount = listing.Files.Count,
            TotalBytes = listing.Files.Sum(f => f.Bytes),
            TotalLines = source.TotalLines,
            ScanTruncated = listing.Truncated || source.Truncated,
            Languages = source.Languages,
            Dependencies = dependencies,
            ImportantFiles = importantFiles.Select(f => f.Check).ToList(),
            Todos = source.Todos,
            TodoCount = source.TodoCount,
            LargestFiles = largest,
            Git = git?.Activity,
            Repository = git?.Repository,
            Tests = tests,
            TestFileCount = testFiles.Count,
            Attention = assessment.Attention,
            Score = assessment.Score,
        };

        Report(progress, "Saving the report…");
        await SaveAsync(project, report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task<bool> IsRepositoryAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            return await _git.IsRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            // Git missing or broken: analyze the folder without the Git sections.
            _logger.LogInformation(ex, "Git is unavailable for {Root}; analyzing without Git", root);
            return false;
        }
    }

    private async Task<GitSnapshot?> ReadGitAsync(string root, DateTimeOffset now, CancellationToken cancellationToken)
    {
        GitStatus status;
        try
        {
            status = await _git.GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            _logger.LogInformation(ex, "Git status failed for {Root}; the report has no Git sections", root);
            return null;
        }

        var activity = status.IsUnborn
            ? GitActivityCalculator.Compute([], now)
            : await TryAsync(() => ReadActivityAsync(root, now, cancellationToken), "history").ConfigureAwait(false);
        var branches = await TryAsync(() => _git.GetBranchesAsync(root, includeRemote: false, cancellationToken), "branches").ConfigureAwait(false) ?? [];
        var merged = status.IsUnborn ? [] : await TryAsync(() => _git.GetMergedBranchesAsync(root, null, cancellationToken), "merged branches").ConfigureAwait(false) ?? [];
        var remotes = await TryAsync(() => _git.GetRemotesAsync(root, cancellationToken), "remotes").ConfigureAwait(false) ?? [];
        var defaultBranch = await TryAsync(() => _git.GetDefaultBranchAsync(root, cancellationToken), "default branch").ConfigureAwait(false);

        var local = branches.Where(b => !b.IsRemote).ToList();
        var staleBefore = now - AnalysisLimits.StaleBranchAge;
        var repository = new RepositoryState
        {
            UncommittedChanges = status.Entries.Count,
            UnpushedCommits = status.Ahead,
            BehindCommits = status.Behind,
            Stashes = status.StashCount,
            LocalBranches = local.Count,
            StaleBranches = local
                .Where(b => !b.IsCurrent && b.TipDate is { } tip && tip < staleBefore)
                .OrderBy(b => b.TipDate)
                .Select(b => b.Name)
                .ToList(),
            MergedBranches = merged
                .Where(b => !string.Equals(b, status.Branch, StringComparison.Ordinal) && !string.Equals(b, defaultBranch, StringComparison.Ordinal))
                .ToList(),
            HasUpstream = status.Upstream is not null,
        };

        return new GitSnapshot(status, activity, repository, defaultBranch, remotes.Count > 0);
    }

    private async Task<GitActivity> ReadActivityAsync(string root, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var commits = await _git.GetLogAsync(root, new GitLogQuery { Take = AnalysisLimits.MaxCommits }, cancellationToken).ConfigureAwait(false);
        var activity = GitActivityCalculator.Compute(commits, now);
        if (commits.Count < AnalysisLimits.MaxCommits)
        {
            return activity;
        }

        // Long history: the charts use the newest commits, totals are counted by git itself.
        var total = await _git.CountCommitsAsync(root, null, null, cancellationToken).ConfigureAwait(false);
        var oldestRead = commits.Min(c => c.Author.When);
        var last30 = oldestRead > now.AddDays(-30)
            ? await _git.CountCommitsAsync(root, null, now.AddDays(-30), cancellationToken).ConfigureAwait(false)
            : activity.CommitsLast30Days;
        var last90 = oldestRead > now.AddDays(-90)
            ? await _git.CountCommitsAsync(root, null, now.AddDays(-90), cancellationToken).ConfigureAwait(false)
            : activity.CommitsLast90Days;
        var first = await _git.GetLogAsync(root, new GitLogQuery { Skip = Math.Max(0, total - 1), Take = 1 }, cancellationToken).ConfigureAwait(false);

        return activity with
        {
            TotalCommits = Math.Max(total, commits.Count),
            CommitsLast30Days = last30,
            CommitsLast90Days = last90,
            FirstCommitAt = first.Count > 0 ? first[0].Author.When : activity.FirstCommitAt,
        };
    }

    private async Task<TestInfo?> ResolveTestsAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            if (await _registry.GetCachedProfileAsync(project.Id, cancellationToken).ConfigureAwait(false) is { } profile)
            {
                return profile.Tests;
            }
        }
        catch (ForgeException ex)
        {
            _logger.LogDebug(ex, "Cached profile of {ProjectId} is unavailable", project.Id);
        }

        try
        {
            return (await _detector.DetectAsync(project.Path, cancellationToken).ConfigureAwait(false)).Tests;
        }
        catch (Exception ex) when (ex is ForgeException or IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Project detection failed for {Path}; tests are inferred from file names", project.Path);
            return null;
        }
    }

    private async Task SaveAsync(Project project, ProjectHealthReport report, CancellationToken cancellationToken)
    {
        try
        {
            await _registry.SaveHealthAsync(project.Id, report, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            // The report is still worth showing; it will be cached next time.
            _logger.LogWarning(ex, "Could not cache the health report of {ProjectId}", project.Id);
        }
    }

    private async Task<T?> TryAsync<T>(Func<Task<T>> read, string what)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            _logger.LogInformation(ex, "Could not read Git {What}", what);
            return default;
        }
    }

    private void Report(IProgress<string>? progress, string message)
    {
        try
        {
            progress?.Report(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Analysis progress handler failed");
        }
    }

    private sealed record GitSnapshot(GitStatus Status, GitActivity? Activity, RepositoryState Repository, string? DefaultBranch, bool HasRemote);
}
