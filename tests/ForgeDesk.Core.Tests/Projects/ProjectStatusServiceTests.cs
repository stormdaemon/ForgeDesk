using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Tests.Detection;
using ForgeDesk.Core.Tests.Infrastructure;
using ForgeDesk.Core.WorkItems;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Projects;

public sealed class ProjectStatusServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IWorkItemService _workItems = Substitute.For<IWorkItemService>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly TempDirectory _folder = new("status");
    private readonly Project _project;
    private readonly ProjectStatusService _service;

    public ProjectStatusServiceTests()
    {
        _project = new Project { Id = "p1", Name = "App", Path = _folder.Path, GitHub = new GitHubRepoRef("acme", "app") };
        _service = new ProjectStatusService(_registry, _git, _gitHub, _workItems, _runs, new FixedClock(Now));

        _registry.GetCachedSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ProjectSnapshot?)null);
        _registry.GetCachedProfileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProjectProfile
        {
            PrimaryLanguage = "TypeScript",
            Technologies =
            [
                new Technology("npm", TechnologyKind.PackageManager, "package.json"),
                new Technology("TypeScript", TechnologyKind.Language, "tsconfig.json"),
                new Technology("Vite", TechnologyKind.BuildTool, "package.json"),
                new Technology("React", TechnologyKind.Framework, "package.json"),
                new Technology("Node.js", TechnologyKind.Runtime, "package.json"),
            ],
        });
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new GitStatus
        {
            Branch = "main",
            HeadSha = "abc",
            Upstream = "origin/main",
            Ahead = 1,
            StashCount = 2,
            Entries =
            [
                new GitStatusEntry { Path = "a.ts", IndexState = GitFileState.Modified },
                new GitStatusEntry { Path = "b.ts", WorkTreeState = GitFileState.Modified },
                new GitStatusEntry { Path = "new.ts", WorkTreeState = GitFileState.Untracked },
            ],
        });
        _git.GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            new GitCommit
            {
                Sha = "abcdef1234",
                Subject = "Fix the build",
                Author = new GitSignature("Ada", "ada@example.com", Now.AddDays(-2)),
                Committer = new GitSignature("Ada", "ada@example.com", Now.AddDays(-1)),
            },
        ]);
        _gitHub.IsSignedIn.Returns(true);
        _gitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new CiSummary { State = CiState.Success, Branch = "main" });
        _workItems.CountOpenAsync("p1", Arg.Any<CancellationToken>()).Returns(4);
        IRunSession[] sessions = [Session("p1", RunStatus.Running), Session("p1", RunStatus.Succeeded), Session("other", RunStatus.Running)];
        _runs.ActiveRuns.Returns(sessions);
        _runs.GetHistoryAsync("p1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Run("Test", RunStatus.Succeeded, Now.AddHours(-1))]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Refresh_combines_git_profile_ci_tasks_and_runs()
    {
        ProjectSnapshot? raised = null;
        _service.SnapshotUpdated += (_, s) => raised = s;

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.Should().Match<ProjectSnapshot>(s =>
            s.ProjectId == "p1" && s.CapturedAt == Now && s.FolderExists && s.IsGitRepository && s.Branch == "main"
            && s.Upstream == "origin/main" && s.Ahead == 1 && s.Behind == 0 && s.StashCount == 2
            && s.ChangedFiles == 3 && s.StagedFiles == 1 && s.UntrackedFiles == 1 && s.ConflictedFiles == 0
            && s.LastCommitSubject == "Fix the build" && s.LastCommitAt == Now.AddDays(-1)
            && s.PrimaryLanguage == "TypeScript" && s.OpenWorkItems == 4 && s.RunningCommands == 1 && s.Problem == null);
        snapshot.Technologies.Should().Equal("React", "Node.js", "Vite", "npm");
        snapshot.Ci!.State.Should().Be(CiState.Success);
        snapshot.LastRun.Should().Be(new RunOutcomeSummary("Test", true, Now.AddHours(-1).AddMinutes(1)));
        snapshot.Attention.Select(a => a.Message).Should().Equal("1 commit not pushed.", "3 uncommitted changes.");
        raised.Should().BeSameAs(snapshot);
        await _registry.Received(1).SaveSnapshotAsync(snapshot, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).GetCiSummaryAsync(new GitHubRepoRef("acme", "app"), "main", Arg.Any<CancellationToken>());
        await _git.Received(1).GetLogAsync(_folder.Path, Arg.Is<GitLogQuery>(q => q.Take == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_folder_is_critical_and_git_is_not_queried()
    {
        var moved = _project with { Path = _folder.Combine("moved-away") };

        var snapshot = await _service.RefreshAsync(moved, cancellationToken: Ct);

        snapshot.FolderExists.Should().BeFalse();
        snapshot.IsGitRepository.Should().BeFalse();
        snapshot.AttentionLevel.Should().Be(AttentionLevel.Critical);
        snapshot.Attention.Single().Section.Should().Be("Overview");
        snapshot.OpenWorkItems.Should().Be(4, "tasks live in ForgeDesk, not in the folder");
        await _git.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default);
    }

    [Fact]
    public async Task Folder_that_is_not_a_repository_is_fine()
    {
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.NotARepository, "Not a git repository."));

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.IsGitRepository.Should().BeFalse();
        snapshot.Problem.Should().BeNull();
        snapshot.Attention.Should().BeEmpty();
        await _git.DidNotReceiveWithAnyArgs().GetLogAsync(default!, default!, default);
    }

    [Fact]
    public async Task Missing_git_is_reported_as_a_problem()
    {
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.GitNotFound, "Git is not installed."));

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.Problem.Should().Be("Git is not installed.");
        snapshot.IsGitRepository.Should().BeFalse();
    }

    [Fact]
    public async Task Failing_ci_needs_critical_attention()
    {
        _gitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new CiSummary { State = CiState.Failure, Branch = "main" });

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.Attention[0].Should().Be(new AttentionReason(AttentionLevel.Critical, "CI is failing on main.", "GitHub"));
    }

    [Fact]
    public async Task Ci_errors_never_fail_the_snapshot()
    {
        _gitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.RateLimited, "GitHub rate limit reached."));

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.Ci!.State.Should().Be(CiState.Unknown);
        snapshot.Ci.Message.Should().Be("GitHub rate limit reached.");
        snapshot.Branch.Should().Be("main");
    }

    [Fact]
    public async Task Ci_is_not_queried_when_signed_out_unlinked_or_local_only()
    {
        _gitHub.IsSignedIn.Returns(false);
        var signedOut = await _service.RefreshAsync(_project, cancellationToken: Ct);
        _gitHub.IsSignedIn.Returns(true);
        var unlinked = await _service.RefreshAsync(_project with { GitHub = null }, cancellationToken: Ct);
        var localOnly = await _service.RefreshAsync(_project, includeRemote: false, Ct);

        signedOut.Ci.Should().BeNull();
        unlinked.Ci.Should().BeNull();
        localOnly.Ci.Should().BeNull();
        await _gitHub.DidNotReceiveWithAnyArgs().GetCiSummaryAsync(default!, default, default);
    }

    [Fact]
    public async Task Local_refresh_keeps_the_last_known_ci_of_the_same_branch()
    {
        var ci = new CiSummary { State = CiState.Failure, Branch = "main" };
        _registry.GetCachedSnapshotAsync("p1", Arg.Any<CancellationToken>()).Returns(new ProjectSnapshot { ProjectId = "p1", Branch = "main", Ci = ci });

        var sameBranch = await _service.RefreshAsync(_project, includeRemote: false, Ct);
        _registry.GetCachedSnapshotAsync("p1", Arg.Any<CancellationToken>()).Returns(new ProjectSnapshot { ProjectId = "p1", Branch = "develop", Ci = ci });
        var otherBranch = await _service.RefreshAsync(_project, includeRemote: false, Ct);

        sameBranch.Ci.Should().BeSameAs(ci);
        otherBranch.Ci.Should().BeNull();
    }

    [Fact]
    public async Task Last_run_is_the_latest_definite_outcome()
    {
        _runs.GetHistoryAsync("p1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            Run("Dev server", RunStatus.Running, Now),
            Run("Lint", RunStatus.Cancelled, Now.AddMinutes(-5)),
            Run("Build", RunStatus.Failed, Now.AddMinutes(-10)),
            Run("Test", RunStatus.Succeeded, Now.AddMinutes(-20)),
        ]);

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.LastRun.Should().Be(new RunOutcomeSummary("Build", false, Now.AddMinutes(-9)));
        snapshot.Attention.Should().Contain(new AttentionReason(AttentionLevel.Warning, "The last run of 'Build' failed.", "Commands"));
    }

    [Fact]
    public async Task Failing_sources_fall_back_to_neutral_values()
    {
        _workItems.CountOpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.StorageFailure, "db"));
        _runs.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        _registry.GetCachedProfileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.StorageFailure, "db"));
        _git.GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.GitCommandFailed, "log"));
        _registry.SaveSnapshotAsync(Arg.Any<ProjectSnapshot>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.StorageFailure, "db"));

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.OpenWorkItems.Should().Be(0);
        snapshot.LastRun.Should().BeNull();
        snapshot.PrimaryLanguage.Should().BeNull();
        snapshot.Technologies.Should().BeEmpty();
        snapshot.LastCommitAt.Should().BeNull();
        snapshot.Branch.Should().Be("main");
    }

    [Fact]
    public async Task Unpublished_branch_is_flagged_when_the_repository_has_remotes()
    {
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new GitStatus { Branch = "feature/login", HeadSha = "abc" });
        _git.GetRemotesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([new GitRemote("origin", "https://github.com/acme/app.git", null)]);

        var snapshot = await _service.RefreshAsync(_project, cancellationToken: Ct);

        snapshot.Attention.Should().ContainSingle().Which.Message.Should().Be("Branch 'feature/login' is not published to a remote yet.");
    }

    [Fact]
    public async Task Concurrent_refreshes_of_a_project_share_one_computation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var raised = 0;
        _service.SnapshotUpdated += (_, _) => Interlocked.Increment(ref raised);

        var first = _service.RefreshAsync(_project, cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = _service.RefreshAsync(_project, includeRemote: false, Ct);
        var third = _service.RefreshAsync(_project, cancellationToken: Ct);
        release.SetResult(new GitStatus { Branch = "main", HeadSha = "abc" });
        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        results[1].Should().BeSameAs(results[0]);
        results[2].Should().BeSameAs(results[0]);
        await _git.Received(1).GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        raised.Should().Be(1);

        await _service.RefreshAsync(_project, cancellationToken: Ct);
        await _git.Received(2).GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remote_request_waits_for_a_running_local_refresh_then_runs_its_own()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                return release.Task;
            }

            return Task.FromResult(new GitStatus { Branch = "main", HeadSha = "def" });
        });

        var local = _service.RefreshAsync(_project, includeRemote: false, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var remote = _service.RefreshAsync(_project, includeRemote: true, Ct);
        release.SetResult(new GitStatus { Branch = "main", HeadSha = "abc" });

        var localSnapshot = await local.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var remoteSnapshot = await remote.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        localSnapshot.Ci.Should().BeNull();
        remoteSnapshot.Ci!.State.Should().Be(CiState.Success);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_others()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        using var impatient = new CancellationTokenSource();

        var first = _service.RefreshAsync(_project, cancellationToken: impatient.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = _service.RefreshAsync(_project, cancellationToken: Ct);
        await impatient.CancelAsync();
        release.SetResult(new GitStatus { Branch = "main", HeadSha = "abc" });

        var firstOutcome = () => first;
        await firstOutcome.Should().ThrowAsync<OperationCanceledException>();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Branch.Should().Be("main");
    }

    [Fact]
    public async Task Work_is_cancelled_when_every_caller_gives_up()
    {
        var observed = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            var pending = new TaskCompletionSource<GitStatus>();
            token.Register(() => pending.TrySetCanceled(token));
            observed.TrySetResult(token);
            return pending.Task;
        });
        using var cts = new CancellationTokenSource();

        var refresh = _service.RefreshAsync(_project, cancellationToken: cts.Token);
        var workToken = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await cts.CancelAsync();

        var outcome = () => refresh;
        await outcome.Should().ThrowAsync<OperationCanceledException>();
        workToken.IsCancellationRequested.Should().BeTrue();
        await _registry.DidNotReceiveWithAnyArgs().SaveSnapshotAsync(default!, default);
    }

    [Fact]
    public async Task GetCached_reads_the_registry_cache()
    {
        var cached = new ProjectSnapshot { ProjectId = "p1", Branch = "main" };
        _registry.GetCachedSnapshotAsync("p1", Arg.Any<CancellationToken>()).Returns(cached);

        (await _service.GetCachedAsync("p1", Ct)).Should().BeSameAs(cached);
    }

    private static IRunSession Session(string projectId, RunStatus status)
    {
        var session = Substitute.For<IRunSession>();
        session.Request.Returns(new RunRequest { ProjectId = projectId, Label = "Dev", CommandLine = "npm run dev", WorkingDirectory = "." });
        session.Status.Returns(status);
        return session;
    }

    private static RunRecord Run(string label, RunStatus status, DateTimeOffset startedAt) => new()
    {
        Id = Ids.New(),
        ProjectId = "p1",
        Label = label,
        CommandLine = label,
        WorkingDirectory = ".",
        Status = status,
        StartedAt = startedAt,
        EndedAt = status == RunStatus.Running ? null : startedAt.AddMinutes(1),
        LogPath = "log.txt",
    };
}
