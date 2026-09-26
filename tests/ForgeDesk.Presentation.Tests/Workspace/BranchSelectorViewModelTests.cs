using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Workspace;

public sealed class BranchSelectorViewModelTests : IDisposable
{
    private readonly WorkspaceHarness _harness = new();

    public BranchSelectorViewModelTests()
    {
        _harness.Git.GetBranchesAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(
        [
            TestData.LocalBranch("main", current: true, upstream: "origin/main", tip: TestData.Now.AddHours(-1)),
            TestData.LocalBranch("feature/login", tip: TestData.Now.AddDays(-2)),
            TestData.LocalBranch("dev", upstream: "origin/dev", tip: TestData.Now.AddHours(-5)),
            TestData.RemoteBranch("origin/HEAD"),
            TestData.RemoteBranch("origin/main"),
            TestData.RemoteBranch("origin/dev"),
            TestData.RemoteBranch("origin/feature/login"),
            TestData.RemoteBranch("origin/release/2.0", tip: TestData.Now.AddDays(-1)),
            TestData.RemoteBranch("upstream/experiment", tip: TestData.Now.AddDays(-3)),
        ]);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Opening_lists_local_branches_current_first_and_only_remote_branches_worth_checking_out()
    {
        var selector = await CreateAsync();

        selector.IsOpen = true;
        await selector.LoadAsync();

        selector.LocalBranches.Select(b => b.Name).Should().Equal("main", "dev", "feature/login");
        selector.RemoteBranches.Select(b => b.Name).Should().Equal("origin/release/2.0", "upstream/experiment");
        selector.RemoteBranches[0].LocalName.Should().Be("release/2.0");
        selector.HasNoMatches.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_narrows_both_lists()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();

        selector.FilterText = "REL";

        selector.LocalBranches.Should().BeEmpty();
        selector.RemoteBranches.Select(b => b.Name).Should().Equal("origin/release/2.0");

        selector.FilterText = "zzz";
        selector.HasNoMatches.Should().BeTrue();
    }

    [Fact]
    public async Task Switching_to_a_local_branch_checks_it_out_and_journals_it()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();

        await selector.CheckoutCommand.ExecuteAsync(selector.LocalBranches.Single(b => b.Name == "dev"));

        await _harness.Git.Received(1).CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>());
        await _harness.Git.DidNotReceiveWithAnyArgs().StashAsync(default!, default, default, default);
        _harness.Notifications.Received(1).Show("Switched to dev", null, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitCheckout && e.Title == "Switched to dev"),
            Arg.Any<CancellationToken>());
        selector.IsOpen.Should().BeFalse();
        selector.IsSwitching.Should().BeFalse();
    }

    [Fact]
    public async Task Dirty_working_tree_offers_to_stash_then_switches()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();
        _harness.Git.CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.DirtyWorkingTree, "Your local changes would be overwritten.")),
            Task.CompletedTask);
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);

        await selector.CheckoutCommand.ExecuteAsync(selector.LocalBranches.Single(b => b.Name == "dev"));

        await _harness.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.ConfirmText == "Stash and switch" && !o.IsDestructive));
        Received.InOrder(() =>
        {
            _harness.Git.CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>());
            _harness.Git.StashAsync(Root, Arg.Is<string?>(m => m!.Contains("dev")), true, Arg.Any<CancellationToken>());
            _harness.Git.CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>());
        });
        _harness.Notifications.Received(1).Show("Switched to dev", Arg.Is<string?>(m => m!.Contains("stashed")), NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitStash), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_to_stash_leaves_everything_as_it_was()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();
        _harness.Git.CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.DirtyWorkingTree, "Your local changes would be overwritten.")));
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);

        await selector.CheckoutCommand.ExecuteAsync(selector.LocalBranches.Single(b => b.Name == "dev"));

        await _harness.Git.DidNotReceiveWithAnyArgs().StashAsync(default!, default, default, default);
        await _harness.Git.Received(1).CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>());
        _harness.Notifications.ReceivedCalls().Should().BeEmpty();
        selector.IsSwitching.Should().BeFalse();
    }

    [Fact]
    public async Task Failure_after_stashing_says_the_changes_are_safe()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();
        _harness.Git.CheckoutAsync(Root, "dev", Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.DirtyWorkingTree, "Your local changes would be overwritten.")),
            Task.FromException(new ForgeException(ErrorKind.RepositoryLocked, "Another git process is running.")));
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);

        await selector.CheckoutCommand.ExecuteAsync(selector.LocalBranches.Single(b => b.Name == "dev"));

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e =>
            e.Kind == ErrorKind.RepositoryLocked && e.Hint!.Contains("stash")), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Remote_branch_is_checked_out_as_a_tracking_branch()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();

        await selector.CheckoutCommand.ExecuteAsync(selector.RemoteBranches.Single(b => b.Name == "origin/release/2.0"));

        await _harness.Git.Received(1).CheckoutRemoteBranchAsync(Root, "origin/release/2.0", null, Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Switched to release/2.0", null, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Choosing_the_current_branch_just_closes_the_list()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();
        selector.IsOpen = true;

        await selector.CheckoutCommand.ExecuteAsync(selector.LocalBranches.Single(b => b.IsCurrent));

        selector.IsOpen.Should().BeFalse();
        await _harness.Git.DidNotReceiveWithAnyArgs().CheckoutAsync(default!, default!, default);
    }

    [Fact]
    public async Task Create_branch_validates_the_name_and_switches_to_it()
    {
        var selector = await CreateAsync();
        await selector.LoadAsync();
        PromptOptions? prompt = null;
        _harness.Dialogs.PromptAsync(Arg.Do<PromptOptions>(o => prompt = o)).Returns(" feature/search ");

        await selector.CreateBranchCommand.ExecuteAsync(null);

        prompt!.Validate!("dev").Should().Be("A branch named 'dev' already exists.");
        prompt.Validate("my branch").Should().NotBeNull();
        prompt.Validate("feature/search").Should().BeNull();
        await _harness.Git.Received(1).CreateBranchAsync(Root, "feature/search", null, true, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.GitBranchCreated && e.RefValue == "feature/search"), Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Created and switched to feature/search", Arg.Any<string?>(), NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Cancelled_create_branch_prompt_does_nothing()
    {
        var selector = await CreateAsync();
        _harness.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns((string?)null);

        await selector.CreateBranchCommand.ExecuteAsync(null);

        await _harness.Git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Current_branch_display_covers_detached_and_unborn_heads()
    {
        _harness.CurrentStatus = TestData.Status(branch: null, upstream: null, head: "abcdef1234567890");
        (await CreateAsync()).CurrentBranchDisplay.Should().Be("abcdef1 (detached)");

        _harness.CurrentStatus = new GitStatus { IsUnborn = true };
        (await CreateAsync()).CurrentBranchDisplay.Should().Be("No commits yet");

        _harness.CurrentStatus = null;
        var outside = await CreateAsync();
        outside.CurrentBranchDisplay.Should().BeNull();
        outside.CreateBranchCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Listing_failure_is_shown_inside_the_dropdown()
    {
        _harness.Git.GetBranchesAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitBranch>>(new ForgeException(ErrorKind.RepositoryLocked, "Repository is busy.")));
        var selector = await CreateAsync();

        await selector.LoadAsync();

        selector.LoadError!.Message.Should().Be("Repository is busy.");
        selector.HasNoMatches.Should().BeFalse();
    }

    private string Root => _harness.Folder.Path;

    private async Task<BranchSelectorViewModel> CreateAsync()
    {
        var context = _harness.CreateContext();
        await context.RefreshGitStatusAsync();
        _harness.Notifications.ClearReceivedCalls();
        return new BranchSelectorViewModel(context, _harness.Services);
    }
}
