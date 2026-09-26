using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Workspace;

public sealed class GitSyncViewModelTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new();
    private readonly List<WorkspaceSection> _shownSections = [];
    private NotificationAction? _errorAction;
    private ErrorInfo? _error;

    public GitSyncViewModelTests()
    {
        _harness.Notifications.When(n => n.ShowError(Arg.Any<ErrorInfo>(), Arg.Any<NotificationAction?>())).Do(call =>
        {
            _error = call.ArgAt<ErrorInfo>(0);
            _errorAction = call.ArgAt<NotificationAction?>(1);
        });
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Push_reports_how_many_commits_went_where_and_journals_it()
    {
        _harness.CurrentStatus = TestData.Status("main", "origin/main", ahead: 3);
        var sync = await CreateAsync();
        _harness.Git.PushAsync(Root, Arg.Any<GitPushOptions>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _harness.CurrentStatus = TestData.Status("main", "origin/main");
                return Task.CompletedTask;
            });

        await sync.PushCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).Show("Pushed 3 commits to origin/main", Arg.Any<string?>(), NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.GitPush && e.Outcome == ActivityOutcome.Success && e.Title == "Pushed 3 commits to origin/main"
            && e.ProjectId == _harness.Project.Id && e.RefValue == "main"), Arg.Any<CancellationToken>());
        sync.Ahead.Should().Be(0, "the status is refreshed after pushing");
        sync.IsPushing.Should().BeFalse();
    }

    [Fact]
    public async Task Push_publishes_a_branch_that_has_no_upstream()
    {
        _harness.CurrentStatus = TestData.Status("feature/login", upstream: null);
        var sync = await CreateAsync();
        sync.NeedsPublish.Should().BeTrue();
        sync.PushToolTip.Should().Be("Publish feature/login to the remote");
        _harness.Git.PushAsync(Root, Arg.Any<GitPushOptions>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _harness.CurrentStatus = TestData.Status("feature/login", "origin/feature/login");
                return Task.CompletedTask;
            });

        await sync.PushCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).PushAsync(Root, Arg.Is<GitPushOptions>(o => o.SetUpstream && o.Branch == "feature/login"),
            Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Published feature/login to origin", "feature/login now tracks origin/feature/login.",
            NotificationSeverity.Success, Arg.Any<NotificationAction?>());
        sync.NeedsPublish.Should().BeFalse();
    }

    [Fact]
    public async Task Rejected_push_explains_and_offers_to_pull()
    {
        _harness.CurrentStatus = TestData.Status("main", "origin/main", ahead: 1);
        var sync = await CreateAsync();
        _harness.Git.PushAsync(Root, Arg.Any<GitPushOptions>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.NonFastForward, "origin/main has commits you don't have yet.",
                "Pull, then push again.", "! [rejected] main -> main (fetch first)")));

        await sync.PushCommand.ExecuteAsync(null);

        _error!.Kind.Should().Be(ErrorKind.NonFastForward);
        _error.Title.Should().Be("Remote has new changes");
        _error.Detail.Should().Contain("rejected");
        _errorAction!.Label.Should().Be("Pull now");
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.GitPush && e.Outcome == ActivityOutcome.Failure && e.Title == "Push failed"), Arg.Any<CancellationToken>());

        await _errorAction.Execute();

        await _harness.Git.Received(1).PullAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pull_counts_the_commits_it_brought_in()
    {
        _harness.CurrentStatus = TestData.Status("main", "origin/main", behind: 2, head: "aaaaaaa");
        var sync = await CreateAsync();
        sync.PullToolTip.Should().Be("Pull 2 commits from origin/main");
        _harness.Git.PullAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _harness.CurrentStatus = TestData.Status("main", "origin/main", head: "bbbbbbb");
            return Task.CompletedTask;
        });
        _harness.Git.CountCommitsAsync(Root, "aaaaaaa..bbbbbbb", null, Arg.Any<CancellationToken>()).Returns(2);

        await sync.PullCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).Show("Pulled 2 commits from origin/main", Arg.Any<string?>(), NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitPull && e.Outcome == ActivityOutcome.Success),
            Arg.Any<CancellationToken>());
        sync.Behind.Should().Be(0);
    }

    [Fact]
    public async Task Pull_without_new_commits_says_it_is_up_to_date()
    {
        var sync = await CreateAsync();

        await sync.PullCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).Show("Already up to date", "main matches origin/main.", NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Pull_conflict_offers_to_resolve_in_the_git_tab()
    {
        var sync = await CreateAsync();
        _harness.Git.PullAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.MergeConflict, "Pulling created conflicts in 2 files.")));

        await sync.PullCommand.ExecuteAsync(null);

        _errorAction!.Label.Should().Be("Resolve conflicts");
        await _errorAction.Execute();
        _shownSections.Should().Equal(WorkspaceSection.Git);
    }

    [Fact]
    public async Task Fetch_with_new_remote_commits_offers_to_pull()
    {
        var sync = await CreateAsync();
        _harness.Git.FetchAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _harness.CurrentStatus = TestData.Status("main", "origin/main", behind: 4);
            return Task.CompletedTask;
        });
        NotificationAction? action = null;
        _harness.Notifications.When(n => n.Show(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<NotificationSeverity>(), Arg.Any<NotificationAction?>()))
            .Do(call => action = call.ArgAt<NotificationAction?>(3));

        await sync.FetchCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).Show("4 new commits on origin/main", "Pull to bring them into main.", NotificationSeverity.Info,
            Arg.Any<NotificationAction?>());
        action!.Label.Should().Be("Pull now");
        sync.Behind.Should().Be(4);
        sync.LastFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Automatic_fetch_is_silent_when_offline()
    {
        var sync = await CreateAsync();
        _harness.Git.FetchAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.NetworkUnavailable, "Could not reach github.com.")));

        await sync.AutoFetchOnceAsync();

        sync.AutoFetchStatus.Should().Be("Offline");
        sync.AutoFetchError!.Message.Should().Be("Could not reach github.com.");
        sync.IsAutoFetching.Should().BeFalse();
        _harness.Notifications.ReceivedCalls().Should().BeEmpty();
        await _harness.Activity.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task Successful_automatic_fetch_clears_the_offline_status_and_refreshes_counts()
    {
        var sync = await CreateAsync();
        _harness.Git.FetchAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.NetworkUnavailable, "offline")),
            Task.CompletedTask);
        await sync.AutoFetchOnceAsync();
        _harness.CurrentStatus = TestData.Status("main", "origin/main", behind: 1);

        await sync.AutoFetchOnceAsync();

        sync.AutoFetchStatus.Should().BeNull();
        sync.Behind.Should().Be(1);
        _harness.Notifications.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_commands_are_disabled_outside_a_repository_and_on_a_detached_head()
    {
        _harness.CurrentStatus = null;
        var outside = await CreateAsync();

        outside.FetchCommand.CanExecute(null).Should().BeFalse();
        outside.PullCommand.CanExecute(null).Should().BeFalse();
        outside.PushCommand.CanExecute(null).Should().BeFalse();

        _harness.CurrentStatus = TestData.Status(branch: null, upstream: null);
        var detached = await CreateAsync();

        detached.FetchCommand.CanExecute(null).Should().BeTrue();
        detached.PullCommand.CanExecute(null).Should().BeFalse();
        detached.PushCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Sign_in_errors_link_to_the_github_settings()
    {
        var sync = await CreateAsync();
        _harness.Git.FetchAsync(Root, Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.AuthenticationFailed, "GitHub rejected the credentials.")));

        await sync.FetchCommand.ExecuteAsync(null);
        await _errorAction!.Execute();

        _errorAction.Label.Should().Be("Sign in");
        _harness.Navigation.Received(1).OpenSettings("GitHub");
    }

    [Theory]
    [InlineData("origin/main", "origin")]
    [InlineData("upstream/feature/x", "upstream")]
    [InlineData(null, "origin")]
    public void Remote_name_is_taken_from_the_upstream(string? upstream, string expected) =>
        GitSyncViewModel.RemoteOf(upstream).Should().Be(expected);

    private string Root => _harness.Folder.Path;

    private async Task<GitSyncViewModel> CreateAsync()
    {
        var context = _harness.CreateContext();
        await context.RefreshGitStatusAsync();
        var sync = new GitSyncViewModel(context, _harness.Services, section =>
        {
            _shownSections.Add(section);
            return Task.CompletedTask;
        });
        _harness.Notifications.ClearReceivedCalls();
        return sync;
    }
}
