using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub;

public sealed class PullRequestsViewModelTests : IDisposable
{
    private readonly GitHubHarness _harness = new();

    public PullRequestsViewModelTests()
    {
        Returns(IssueStateFilter.Open, GitHubData.PullRequest(1, "Add login"), GitHubData.PullRequest(2, "Fix crash", head: "fix/crash"),
            GitHubData.PullRequest(3, "Draft docs", PullRequestState.Draft, head: "docs"));
        Returns(IssueStateFilter.Closed, GitHubData.PullRequest(9, "Old work", PullRequestState.Merged));
        _harness.GitHub.GetPullRequestAsync(GitHubHarness.Repo, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(GitHubData.PullRequest(call.ArgAt<int>(1), checks: CiState.Failure) with
            {
                Additions = 120,
                Deletions = 7,
                ChangedFiles = 4,
                Mergeable = true,
                Reviewers = ["linus"],
                Body = "## Why\nBecause.",
            }));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Open_pull_requests_load_and_filters_reload_or_search()
    {
        var pulls = await OpenAsync();

        pulls.Items.Select(i => i.Number).Should().Equal(1, 2, 3);
        pulls.Items[2].StateText.Should().Be("Draft");
        pulls.Items[0].Labels.Should().ContainSingle(l => l.Name == "enhancement" && l.Color == "#A2EEEF" && l.Background == "#33A2EEEF");

        pulls.SearchText = "crash";
        pulls.Items.Select(i => i.Number).Should().Equal(2);
        pulls.SearchText = "#3";
        pulls.Items.Select(i => i.Number).Should().Equal(3);
        pulls.SearchText = "nothing like this";
        pulls.HasNoMatches.Should().BeTrue();
        pulls.ClearSearchCommand.Execute(null);
        pulls.Items.Should().HaveCount(3);

        pulls.SelectedFilter = StateFilterOption.For(IssueStateFilter.Closed);

        pulls.Items.Select(i => i.Number).Should().Equal(9);
        pulls.Items[0].StateText.Should().Be("Merged");
    }

    [Fact]
    public async Task Empty_list_offers_to_create_a_pull_request()
    {
        Returns(IssueStateFilter.Open);

        var pulls = await OpenAsync();

        pulls.IsEmpty.Should().BeTrue();
        pulls.EmptyTitle.Should().Be("No open pull requests");
        pulls.NewPullRequestCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task Selecting_loads_the_details_and_the_checks_of_the_row()
    {
        var pulls = await OpenAsync();
        pulls.Items[0].HasChecks.Should().BeFalse("the list endpoint has no checks");

        pulls.SelectedItem = pulls.Items[0];

        pulls.Detail!.IsComplete.Should().BeTrue();
        pulls.Detail.AdditionsText.Should().Be("+120");
        pulls.Detail.FilesText.Should().Be("4 files");
        pulls.Detail.MergeableText.Should().Be("No conflicts with main");
        pulls.Detail.ReviewersText.Should().Be("linus");
        pulls.Detail.HasBody.Should().BeTrue();
        pulls.Items[0].Checks.Should().Be(CiState.Failure);
        pulls.Items[0].HasChecks.Should().BeTrue();

        pulls.CloseDetailCommand.Execute(null);
        pulls.Detail.Should().BeNull();
    }

    [Fact]
    public async Task Detail_failure_is_shown_in_the_pane_with_a_retry()
    {
        _harness.GitHub.GetPullRequestAsync(GitHubHarness.Repo, 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubPullRequest>(new Core.Common.ForgeException(Core.Common.ErrorKind.NetworkUnavailable, "GitHub is unreachable.")));
        var pulls = await OpenAsync();

        pulls.SelectedItem = pulls.Items[0];

        pulls.DetailError!.Kind.Should().Be(Core.Common.ErrorKind.NetworkUnavailable);
        pulls.Detail!.IsComplete.Should().BeFalse("the list data stays visible");
        _harness.GitHub.GetPullRequestAsync(GitHubHarness.Repo, 1, Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubData.PullRequest(1)));

        await pulls.RetryDetailCommand.ExecuteAsync(null);

        pulls.DetailError.Should().BeNull();
        pulls.Detail!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Check_out_fetches_then_tracks_the_remote_branch()
    {
        _harness.Branches = [TestData.LocalBranch("main", current: true), TestData.RemoteBranch("origin/feature/login")];
        var pulls = await OpenAsync();

        await pulls.CheckoutBranchCommand.ExecuteAsync(pulls.Items[0]);

        Received.InOrder(() =>
        {
            _harness.Git.FetchAsync(_harness.Root, null, Arg.Any<CancellationToken>());
            _harness.Git.CheckoutRemoteBranchAsync(_harness.Root, "origin/feature/login", null, Arg.Any<CancellationToken>());
        });
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitCheckout), Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Switched to feature/login", Arg.Any<string>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Check_out_uses_an_existing_local_branch()
    {
        _harness.Branches = [TestData.LocalBranch("main", current: true), TestData.LocalBranch("feature/login"), TestData.RemoteBranch("origin/feature/login")];
        var pulls = await OpenAsync();

        await pulls.CheckoutBranchCommand.ExecuteAsync(pulls.Items[0]);

        await _harness.Git.Received(1).CheckoutAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>());
        await _harness.Git.DidNotReceiveWithAnyArgs().CheckoutRemoteBranchAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Check_out_of_a_fork_explains_instead_of_failing()
    {
        _harness.Branches = [TestData.LocalBranch("main", current: true)];
        var pulls = await OpenAsync();

        await pulls.CheckoutBranchCommand.ExecuteAsync(pulls.Items[1]);

        await _harness.Git.DidNotReceiveWithAnyArgs().CheckoutRemoteBranchAsync(default!, default!, default, default);
        await _harness.Git.DidNotReceiveWithAnyArgs().CheckoutAsync(default!, default!, default);
        _harness.Notifications.Received(1).Show("This branch lives in a fork", Arg.Is<string>(m => m.Contains("gh pr checkout 2")), NotificationSeverity.Warning,
            Arg.Is<NotificationAction?>(a => a != null && a.Label == "Open on GitHub"));
    }

    [Fact]
    public async Task New_pull_request_pushes_an_unpublished_branch_first_then_shows_the_result()
    {
        _harness.Status = TestData.Status("feature/new", upstream: null);
        _harness.Branches = [TestData.LocalBranch("main"), TestData.LocalBranch("feature/new", current: true)];
        _harness.GitHub.GetBranchesAsync(GitHubHarness.Repo, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["main", "develop"]));
        NewPullRequestDialogViewModel? shown = null;
        _harness.AnswerDialog<NewPullRequestDialogViewModel>(dialog =>
        {
            shown = dialog;
            dialog.Body = "Adds things";
            dialog.IsDraft = true;
        });
        _harness.GitHub.CreatePullRequestAsync(GitHubHarness.Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubData.PullRequest(12, "New", PullRequestState.Draft, head: "feature/new")));
        var pulls = await OpenAsync();
        Returns(IssueStateFilter.Open, GitHubData.PullRequest(12, "New", PullRequestState.Draft, head: "feature/new"), GitHubData.PullRequest(1));

        await pulls.NewPullRequestCommand.ExecuteAsync(null);

        shown!.Head.Should().Be("feature/new");
        shown.Base.Should().Be("main");
        shown.PullRequestTitle.Should().Be("New");
        shown.BaseBranches.Should().Equal("main", "develop");
        await _harness.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.Title == "Push feature/new first?"));
        Received.InOrder(() =>
        {
            _harness.Git.PushAsync(_harness.Root, Arg.Is<GitPushOptions>(o => o.SetUpstream && o.Branch == "feature/new"), null, Arg.Any<CancellationToken>());
            _harness.GitHub.CreatePullRequestAsync(GitHubHarness.Repo, "New", "Adds things", "feature/new", "main", true, Arg.Any<CancellationToken>());
        });
        _harness.GitHub.Received().InvalidateCache(GitHubHarness.Repo);
        pulls.SelectedItem!.Number.Should().Be(12);
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.PullRequestCreated), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_dialog_creates_nothing()
    {
        _harness.AnswerDialog<NewPullRequestDialogViewModel>(_ => { }, result: false);
        var pulls = await OpenAsync();

        await pulls.NewPullRequestCommand.ExecuteAsync(null);

        await _harness.GitHub.DidNotReceiveWithAnyArgs().CreatePullRequestAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public void Push_is_suggested_for_branches_github_does_not_have()
    {
        PullRequestsViewModel.PushReason("feature/x", TestData.Status("feature/x", upstream: null), ["main"]).Should().Contain("isn't on GitHub yet");
        PullRequestsViewModel.PushReason("feature/x", TestData.Status("feature/x", ahead: 2), ["main", "feature/x"]).Should().Contain("2 commits");
        PullRequestsViewModel.PushReason("feature/x", TestData.Status("feature/x"), ["main", "feature/x"]).Should().BeNull();
        PullRequestsViewModel.PushReason("other", TestData.Status("main"), ["main"]).Should().Contain("other isn't on GitHub");
        PullRequestsViewModel.PushReason("other", null, []).Should().BeNull();
    }

    [Fact]
    public void Pull_request_form_validates_title_and_branches()
    {
        var dialog = new NewPullRequestDialogViewModel("acme/forge-app", ["main", "feature/user-login"], ["main"], "feature/user-login", "main");
        dialog.PullRequestTitle.Should().Be("User login");
        dialog.Summary.Should().Be("Merge feature/user-login into main");

        dialog.Base = "feature/user-login";
        dialog.ConfirmCommand.Execute(null);
        dialog.ValidationMessage.Should().Contain("merged into itself");

        dialog.Base = "main";
        dialog.PullRequestTitle = " ";
        dialog.ConfirmCommand.Execute(null);
        dialog.ValidationMessage.Should().Be("Give the pull request a title.");
    }

    private void Returns(IssueStateFilter state, params GitHubPullRequest[] pulls) =>
        _harness.GitHub.GetPullRequestsAsync(GitHubHarness.Repo, state, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubPullRequest>>(pulls));

    private async Task<PullRequestsViewModel> OpenAsync()
    {
        var section = await _harness.OpenGitHubAsync();
        await section.SelectViewAsync(GitHubView.PullRequests);
        return section.PullRequests;
    }
}
