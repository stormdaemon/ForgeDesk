using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Analysis;

public sealed class ProjectAnalyzerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly AdjustableClock _clock = new(Now);
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectDetector _detector = Substitute.For<IProjectDetector>();
    private readonly List<IDisposable> _cleanup = [];

    public ProjectAnalyzerTests()
    {
        _detector.DetectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProjectProfile());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var item in _cleanup)
        {
            item.Dispose();
        }
    }

    [Fact]
    public async Task Analyzes_a_repository_end_to_end()
    {
        using var repo = CreateFixtureRepository();
        var progress = new List<string>();

        var report = await Analyzer(new CliGitService()).AnalyzeAsync(Project(repo.Path), new SynchronousProgress(progress), Ct);

        report.GeneratedAt.Should().Be(Now);
        report.ScanTruncated.Should().BeFalse();
        report.FileCount.Should().Be(12);
        report.TotalBytes.Should().BeGreaterThan(0);
        report.LargestFiles.Should().HaveCount(10).And.BeInDescendingOrder(f => f.Bytes);
        report.LargestFiles[0].RelativePath.Should().Be("assets/logo.svg");
        report.LargestFiles.Should().NotContain(f => f.RelativePath.StartsWith("node_modules/", StringComparison.Ordinal) || f.RelativePath.StartsWith("dist/", StringComparison.Ordinal));

        report.Languages.Select(l => (l.Language, l.Files, l.Lines)).Should().Equal(("TypeScript", 2, 7), ("Python", 1, 2));
        report.TotalLines.Should().Be(9);
        report.Todos.Should().Equal(
            new TodoItem("src/app.ts", 2, "TODO", "cache the result"),
            new TodoItem("src/app.ts", 4, "FIXME", "crashes on empty input"),
            new TodoItem("tools/report.py", 1, "HACK", "hard-coded path"));
        report.TodoCount.Should().Be(3);

        report.Dependencies.Select(d => (d.Name, d.Version, d.IsDevelopment)).Should().Equal(("express", "^4.19.2", false), ("vitest", "^1.6.0", true));
        report.ImportantFiles.Select(f => (f.Label, f.Present)).Should().Equal(
            ("README", true), ("LICENSE", true), (".gitignore", true), ("CI workflow", true), ("Tests", true),
            ("CONTRIBUTING", false), ("CHANGELOG", false), ("SECURITY", false), (".editorconfig", false), ("Lockfile (npm)", true));
        report.TestFileCount.Should().Be(1);

        var git = report.Git!;
        git.TotalCommits.Should().Be(4);
        git.CommitsLast30Days.Should().Be(2);
        git.CommitsLast90Days.Should().Be(3);
        git.WeeklyCommits.Should().HaveCount(12);
        git.WeeklyCommits.Sum().Should().Be(3);
        git.WeeklyCommits[^1].Should().Be(1);
        git.FirstCommitAt.Should().Be(Now.AddDays(-200));
        git.LastCommitAt.Should().Be(Now.AddDays(-2));
        git.TopContributors.Should().Equal(new ContributorStat("Ana Lima", 3), new ContributorStat("Bob", 1));

        var state = report.Repository!;
        state.UncommittedChanges.Should().Be(2);
        state.UnpushedCommits.Should().Be(0);
        state.HasUpstream.Should().BeFalse();
        state.Stashes.Should().Be(1);
        state.LocalBranches.Should().Be(3);
        state.StaleBranches.Should().Equal("old-feature", "experiment");
        state.MergedBranches.Should().Equal("old-feature");

        report.Attention.Should().Contain(a => a.Section == "Git" && a.Message.StartsWith("This repository has no remote", StringComparison.Ordinal));
        report.Attention.Should().Contain(a => a.Section == "Git" && a.Message.Contains("old-feature, experiment", StringComparison.Ordinal));
        report.Score.Should().BeInRange(1, 99);

        progress.Should().StartWith("Listing files…").And.Contain("Reading Git history…").And.EndWith("Saving the report…");
        await _registry.Received(1).SaveHealthAsync("p1", report, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_plain_folder_is_walked_and_has_no_git_sections()
    {
        var dir = Temp();
        dir.WriteFile("main.go", "package main\n\n// TODO: flags\nfunc main() {}\n");
        dir.WriteFile("main_test.go", "package main\n");
        dir.WriteFile("node_modules/pkg/index.js", "module.exports = 1;\n");
        dir.WriteFile("bin/tool", "binary");
        dir.WriteFile(".github/workflows/ci.yml", "on: push");

        var report = await Analyzer(new CliGitService()).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.FileCount.Should().Be(3);
        report.Git.Should().BeNull();
        report.Repository.Should().BeNull();
        report.Languages.Should().ContainSingle().Which.Should().Be(new LanguageStat("Go", 2, 5, 57, "#00ADD8"));
        report.Todos.Should().ContainSingle().Which.Text.Should().Be("flags");
        report.TestFileCount.Should().Be(1);
        report.Attention.Should().NotContain(a => a.Section == "Git" || a.Message.Contains(".gitignore", StringComparison.Ordinal));
        report.Attention.Should().Contain(a => a.Message.StartsWith("Add a README", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_git_produces_a_report_without_git_sections()
    {
        var dir = Temp();
        dir.WriteFile("README.md", "# hi");
        var git = Substitute.For<IGitService>();
        git.IsRepositoryAsync(dir.Path, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.GitNotFound, "Git is not installed."));

        var report = await Analyzer(git).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.FileCount.Should().Be(1);
        report.Git.Should().BeNull();
        report.Repository.Should().BeNull();
        await git.DidNotReceiveWithAnyArgs().ListFilesAsync(default!, default);
        await git.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default);
    }

    [Fact]
    public async Task A_git_status_failure_drops_only_the_git_sections()
    {
        var dir = Temp();
        dir.WriteFile("a.py", "print(1)\n");
        var git = Substitute.For<IGitService>();
        git.IsRepositoryAsync(dir.Path, Arg.Any<CancellationToken>()).Returns(true);
        git.ListFilesAsync(dir.Path, Arg.Any<CancellationToken>()).Returns(["a.py"]);
        git.GetStatusAsync(dir.Path, Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.RepositoryLocked, "The repository is locked."));

        var report = await Analyzer(git).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.Languages.Should().ContainSingle();
        report.Git.Should().BeNull();
        report.Repository.Should().BeNull();
    }

    [Fact]
    public async Task Long_histories_use_git_counts_for_totals()
    {
        var dir = Temp();
        var git = Substitute.For<IGitService>();
        git.IsRepositoryAsync(dir.Path, Arg.Any<CancellationToken>()).Returns(true);
        git.ListFilesAsync(dir.Path, Arg.Any<CancellationToken>()).Returns([]);
        git.GetStatusAsync(dir.Path, Arg.Any<CancellationToken>()).Returns(new GitStatus { Branch = "main", HeadSha = "abc", Upstream = "origin/main" });
        var recent = Enumerable.Range(0, AnalysisLimits.MaxCommits)
            .Select(i => new GitCommit { Sha = $"{i}", Subject = "s", Author = new GitSignature("Dev", "dev@x", Now.AddMinutes(-i)) })
            .ToList();
        git.GetLogAsync(dir.Path, Arg.Is<GitLogQuery>(q => q.Skip == 0), Arg.Any<CancellationToken>()).Returns(recent);
        git.GetLogAsync(dir.Path, Arg.Is<GitLogQuery>(q => q.Skip == 24_999 && q.Take == 1), Arg.Any<CancellationToken>())
            .Returns([new GitCommit { Sha = "first", Subject = "init", Author = new GitSignature("Dev", "dev@x", Now.AddYears(-6)) }]);
        git.CountCommitsAsync(dir.Path, null, null, Arg.Any<CancellationToken>()).Returns(25_000);
        git.CountCommitsAsync(dir.Path, null, Arg.Is<DateTimeOffset?>(d => d == Now.AddDays(-30)), Arg.Any<CancellationToken>()).Returns(12_000);
        git.CountCommitsAsync(dir.Path, null, Arg.Is<DateTimeOffset?>(d => d == Now.AddDays(-90)), Arg.Any<CancellationToken>()).Returns(20_000);
        git.GetRemotesAsync(dir.Path, Arg.Any<CancellationToken>()).Returns([new GitRemote("origin", "https://github.com/o/r.git", null)]);

        var report = await Analyzer(git).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.Git!.TotalCommits.Should().Be(25_000);
        report.Git.CommitsLast30Days.Should().Be(12_000);
        report.Git.CommitsLast90Days.Should().Be(20_000);
        report.Git.FirstCommitAt.Should().Be(Now.AddYears(-6));
        report.Git.WeeklyCommits[^1].Should().Be(AnalysisLimits.MaxCommits);
    }

    [Fact]
    public async Task Tests_come_from_the_cached_profile_when_available()
    {
        var dir = Temp();
        var tests = new TestInfo(true, ["xUnit"], ["tests/"]);
        _registry.GetCachedProfileAsync("p1", Arg.Any<CancellationToken>()).Returns(new ProjectProfile { Tests = tests });

        var report = await Analyzer(new CliGitService()).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.Tests.Should().Be(tests);
        report.ImportantFiles.Single(f => f.Label == "Tests").Should().Match<ImportantFileCheck>(c => c.Present && c.Path == "tests/");
        await _detector.DidNotReceiveWithAnyArgs().DetectAsync(default!, default);
    }

    [Fact]
    public async Task Tests_are_detected_when_no_profile_is_cached()
    {
        var dir = Temp();
        var tests = new TestInfo(false, [], []);
        _detector.DetectAsync(dir.Path, Arg.Any<CancellationToken>()).Returns(new ProjectProfile { Tests = tests });

        var report = await Analyzer(new CliGitService()).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.Tests.Should().Be(tests);
        report.Attention.Should().Contain(a => a.Message.StartsWith("No tests found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_to_cache_the_report_does_not_lose_it()
    {
        var dir = Temp();
        _registry.SaveHealthAsync(Arg.Any<string>(), Arg.Any<ProjectHealthReport>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.StorageFailure, "Disk full."));

        var report = await Analyzer(new CliGitService()).AnalyzeAsync(Project(dir.Path), cancellationToken: Ct);

        report.Should().NotBeNull();
    }

    [Fact]
    public async Task A_missing_folder_is_reported()
    {
        var act = () => Analyzer(new CliGitService()).AnalyzeAsync(Project(Path.Combine(Path.GetTempPath(), "forgedesk-missing-" + Guid.NewGuid())), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Fact]
    public async Task Cancellation_stops_the_analysis()
    {
        var dir = Temp();
        dir.WriteFile("a.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Analyzer(new CliGitService()).AnalyzeAsync(Project(dir.Path), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _registry.DidNotReceiveWithAnyArgs().SaveHealthAsync(default!, default!, default);
    }

    private ProjectAnalyzer Analyzer(IGitService git) => new(git, _registry, _detector, _clock);

    private static Project Project(string path) => new() { Id = "p1", Name = "fixture", Path = path };

    private TempDirectory Temp()
    {
        var dir = new TempDirectory("analysis");
        _cleanup.Add(dir);
        return dir;
    }

    /// <summary>
    /// A small web project: dated history by three identities of two people, a merged stale
    /// branch, an unmerged stale branch, a stash, and uncommitted work.
    /// </summary>
    private static TestRepository CreateFixtureRepository()
    {
        var repo = TestRepository.Create(withInitialCommit: false);
        repo.CommitDated(Now.AddDays(-200), "Initial commit", "Ana Lima", "ana@home.dev",
            ("README.md", "# Fixture\n"),
            ("LICENSE", "MIT License\n"),
            (".gitignore", "dist/\n*.log\n"),
            ("package.json", """{ "dependencies": { "express": "^4.19.2" }, "devDependencies": { "vitest": "^1.6.0" } }"""),
            ("package-lock.json", "{}"),
            (".github/workflows/ci.yml", "on: push\n"),
            ("assets/logo.svg", "<svg>" + new string(' ', 4000) + "</svg>"));
        repo.Git("branch", "old-feature");

        repo.Git("checkout", "-b", "experiment");
        repo.CommitDated(Now.AddDays(-120), "Try something", "Bob", "bob@example.com", ("experiment.txt", "x"));
        repo.Git("checkout", "main");

        repo.CommitDated(Now.AddDays(-60), "feat: app", "ana lima", "ANA@HOME.DEV",
            ("src/app.ts", "export function run(input: string) {\n  // TODO: cache the result\n  const value = input.trim();\n  // FIXME: crashes on empty input\n  return value[0];\n}\n"));
        repo.CommitDated(Now.AddDays(-10), "test: app", "Bob", "bob@example.com",
            ("tests/app.test.ts", "import { run } from '../src/app';\n"),
            ("tools/report.py", "# HACK: hard-coded path\nprint('/tmp/report')\n"),
            ("node_modules/left-pad/index.js", "module.exports = 1;\n"));
        repo.CommitDated(Now.AddDays(-2), "docs: readme", "Ana Lima", "ana@work.com", ("docs/guide.md", "# Guide\n"));

        repo.WriteFile("README.md", "# Fixture (stashed edit)\n");
        repo.Git("stash", "push", "-m", "wip");
        repo.WriteFile("README.md", "# Fixture (edited)\n");
        repo.WriteFile("notes.txt", "untracked");
        repo.WriteFile("dist/bundle.js", "ignored build output");
        return repo;
    }

    private sealed class SynchronousProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
