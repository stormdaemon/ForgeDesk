using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub;

public sealed class GitHubSectionViewModelTests : IDisposable
{
    private GitHubHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Unlinked_project_explains_and_redetects_the_origin_remote()
    {
        _harness.Dispose();
        _harness = new GitHubHarness(linked: false);
        var section = await _harness.OpenGitHubAsync();
        section.IsNotLinked.Should().BeTrue();
        section.NotLinkedDescription.Should().Contain("origin").And.Contain("forge-app");
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GetRepositoryAsync(default!, default);

        await section.RedetectCommand.ExecuteAsync(null);

        await _harness.Registry.Received(1).UpdateAsync(Arg.Is<Project>(p => p.GitHub == GitHubHarness.Repo), Arg.Any<CancellationToken>());
        _harness.Context.Project.GitHub.Should().Be(GitHubHarness.Repo);
        section.IsReady.Should().BeTrue();
        section.Overview.Repository.Should().NotBeNull("the tab is visible, so it loads once linked");
    }

    [Fact]
    public async Task Redetect_without_a_github_remote_says_so()
    {
        _harness.Dispose();
        _harness = new GitHubHarness(linked: false);
        _harness.Remotes = [new GitRemote("origin", "https://gitlab.com/acme/forge-app.git", null)];
        var section = await _harness.OpenGitHubAsync();

        await section.RedetectCommand.ExecuteAsync(null);

        section.IsNotLinked.Should().BeTrue();
        _harness.Notifications.Received(1).Show("No GitHub remote found", Arg.Is<string>(m => m.Contains("origin")), NotificationSeverity.Warning,
            Arg.Any<NotificationAction?>());
        await _harness.Registry.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task Signed_out_offers_sign_in_and_loads_once_signed_in()
    {
        _harness.SignedIn = false;
        var section = await _harness.OpenGitHubAsync();
        section.IsSignedOut.Should().BeTrue();

        section.SignInCommand.Execute(null);
        _harness.Navigation.Received(1).OpenSettings("GitHub");

        _harness.SignedIn = true;
        _harness.Accounts.AccountChanged += Raise.Event<EventHandler<GitHubAccount?>>(_harness.Accounts,
            new GitHubAccount(new GitHubUser("ada", null, "", ""), ["repo"], GitHubAuthMethod.PersonalAccessToken));

        section.IsReady.Should().BeTrue();
        await _harness.GitHub.Received().GetRepositoryAsync(GitHubHarness.Repo, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_github_features_can_be_turned_back_on()
    {
        _harness.CurrentSettings = AppSettings.Default with { GitHubEnabled = false };
        var section = await _harness.OpenGitHubAsync();
        section.IsDisabled.Should().BeTrue();

        await section.EnableGitHubCommand.ExecuteAsync(null);

        _harness.CurrentSettings.GitHubEnabled.Should().BeTrue();
        section.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Rate_limit_is_shown_inline_with_its_reset_time_and_retry_reloads()
    {
        _harness.GitHub.GetRepositoryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<GitHubRepository>(new ForgeException(ErrorKind.RateLimited,
                "You've used up your GitHub API requests for now. The limit resets at 14:05.", "Wait until 14:05 and try again.")));

        var section = await _harness.OpenGitHubAsync();

        section.Overview.Error!.Kind.Should().Be(ErrorKind.RateLimited);
        section.Overview.Error.Message.Should().Contain("14:05");
        section.Overview.HasRepository.Should().BeFalse();

        _harness.GitHub.GetRepositoryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubData.Repository()));
        await section.Overview.ReloadCommand.ExecuteAsync(null);

        section.Overview.Error.Should().BeNull();
        section.Overview.HasRepository.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_drops_the_cache_before_reloading_the_visible_view()
    {
        var section = await _harness.OpenGitHubAsync();
        _harness.GitHub.ClearReceivedCalls();

        await section.RefreshCommand.ExecuteAsync(null);

        Received.InOrder(() =>
        {
            _harness.GitHub.InvalidateCache(GitHubHarness.Repo);
            _harness.GitHub.GetRepositoryAsync(GitHubHarness.Repo, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Overview_shows_ci_of_the_current_and_default_branches_counts_and_rate_limit()
    {
        _harness.Status = TestData.Status("feature/login");
        _harness.GitHub.GetPullRequestsAsync(GitHubHarness.Repo, IssueStateFilter.Open, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubPullRequest>>([GitHubData.PullRequest(1), GitHubData.PullRequest(2)]));
        _harness.GitHub.GetIssuesAsync(GitHubHarness.Repo, IssueStateFilter.Open, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubIssue>>(Enumerable.Range(1, GitHubOverviewViewModel.CountCap).Select(n => GitHubData.Issue(n)).ToList()));
        _harness.GitHub.GetCiSummaryAsync(GitHubHarness.Repo, "feature/login", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CiSummary
        {
            State = CiState.Failure,
            Branch = "feature/login",
            LatestRuns = [GitHubData.Run(10, CiState.Failure, branch: "feature/login")],
        }));

        var section = await _harness.OpenGitHubAsync();
        var overview = section.Overview;

        overview.CurrentBranchCi!.Title.Should().Be("Current branch");
        overview.CurrentBranchCi.State.Should().Be(CiState.Failure);
        overview.CurrentBranchCi.Runs.Should().ContainSingle(r => r.Id == 10);
        overview.DefaultBranchCi!.Branch.Should().Be("main");
        overview.OpenPullRequestsText.Should().Be("2");
        overview.OpenIssuesText.Should().Be("100+");
        overview.RateLimitText.Should().Contain("4,812").And.Contain("5,000");
    }

    [Fact]
    public async Task Overview_on_the_default_branch_shows_a_single_ci_card()
    {
        var section = await _harness.OpenGitHubAsync();

        section.Overview.CurrentBranchCi!.Title.Should().Be("Default branch");
        section.Overview.DefaultBranchCi.Should().BeNull();
    }

    [Fact]
    public async Task Clicking_a_ci_run_opens_it_in_actions()
    {
        _harness.GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CiSummary
        {
            State = CiState.Success,
            Branch = "main",
            LatestRuns = [GitHubData.Run(77)],
        }));
        _harness.GitHub.GetWorkflowRunsAsync(GitHubHarness.Repo, Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowRunInfo>>([GitHubData.Run(76), GitHubData.Run(77)]));
        var section = await _harness.OpenGitHubAsync();

        section.Overview.OpenRunCommand.Execute(section.Overview.CurrentBranchCi!.Runs[0]);

        section.CurrentView.Should().Be(GitHubView.Actions);
        section.Actions.SelectedRun!.Id.Should().Be(77);
    }

    [Fact]
    public async Task Deep_links_select_an_issue_or_a_pull_request()
    {
        _harness.GitHub.GetIssuesAsync(GitHubHarness.Repo, IssueStateFilter.Open, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubIssue>>([GitHubData.Issue(4), GitHubData.Issue(5)]));
        _harness.GitHub.GetPullRequestsAsync(GitHubHarness.Repo, IssueStateFilter.Open, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubPullRequest>>([GitHubData.PullRequest(1)]));
        _harness.GitHub.GetPullRequestsAsync(GitHubHarness.Repo, IssueStateFilter.All, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubPullRequest>>([GitHubData.PullRequest(1), GitHubData.PullRequest(7, state: PullRequestState.Merged)]));
        _harness.GitHub.GetPullRequestAsync(GitHubHarness.Repo, 7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubData.PullRequest(7, state: PullRequestState.Merged, checks: CiState.Success)));
        var section = await _harness.OpenGitHubAsync();

        await section.NavigateToAsync(GitHubNavigation.Issue(5));

        section.CurrentView.Should().Be(GitHubView.Issues);
        section.Issues.SelectedItem!.Number.Should().Be(5);
        section.Issues.Detail!.Number.Should().Be(5);

        await section.NavigateToAsync(GitHubNavigation.PullRequest(7));

        section.CurrentView.Should().Be(GitHubView.PullRequests);
        section.PullRequests.StateFilter.Should().Be(IssueStateFilter.All, "a merged pull request isn't in the open list");
        section.PullRequests.SelectedItem!.Number.Should().Be(7);
        section.PullRequests.Detail!.Checks.Should().Be(CiState.Success);
    }

    [Fact]
    public async Task A_checkout_reloads_the_overview_for_the_new_branch()
    {
        var section = await _harness.OpenGitHubAsync();
        await _harness.GitHub.Received(1).GetCiSummaryAsync(GitHubHarness.Repo, "main", Arg.Any<CancellationToken>());

        await _harness.SetStatusAsync(TestData.Status("feature/login"));

        await _harness.GitHub.Received(1).GetCiSummaryAsync(GitHubHarness.Repo, "feature/login", Arg.Any<CancellationToken>());
        section.Overview.CurrentBranchCi!.Branch.Should().Be("feature/login");
    }

    [Fact]
    public async Task Hidden_tab_does_not_load()
    {
        var section = await _harness.OpenGitHubAsync(activate: false);

        section.IsReady.Should().BeTrue();
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GetRepositoryAsync(default!, default);

        await section.ActivateAsync();
        section.Deactivate();
        await section.ActivateAsync();

        await _harness.GitHub.Received(1).GetRepositoryAsync(GitHubHarness.Repo, Arg.Any<CancellationToken>());
    }
}
