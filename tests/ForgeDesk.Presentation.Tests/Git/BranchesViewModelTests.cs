using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Git.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class BranchesViewModelTests : IDisposable
{
    private readonly GitHarness _harness = new();

    public BranchesViewModelTests()
    {
        _harness.Git.GetBranchesAsync(_harness.Root, true, Arg.Any<CancellationToken>()).Returns(
        [
            GitData.Local("feature/old", upstream: "origin/feature/old", gone: true),
            GitData.Local("main", current: true, upstream: "origin/main", ahead: 2, behind: 1),
            GitData.Local("feature/login", upstream: null),
            GitData.Remote("origin/main"),
            GitData.Remote("origin/feature/remote-only"),
            GitData.Remote("upstream/main"),
        ]);
        _harness.Git.GetMergedBranchesAsync(_harness.Root, null, Arg.Any<CancellationToken>()).Returns(["feature/old"]);
    }

    public void Dispose() => _harness.Dispose();

    private async Task<BranchesViewModel> OpenAsync() => (await _harness.OpenAsync(GitView.Branches)).Branches;

    private static BranchRowViewModel Row(BranchesViewModel branches, string name) =>
        branches.Rows.OfType<BranchRowViewModel>().Single(r => r.Name == name);

    [Fact]
    public async Task Local_branches_come_first_then_remote_branches_by_remote()
    {
        var branches = await OpenAsync();

        branches.Rows.Select(r => r is BranchGroupHeader h ? $"# {h.Title}" : ((BranchRowViewModel)r).Name).Should().Equal(
            "# Local branches", "main", "feature/login", "feature/old",
            "# origin", "origin/feature/remote-only", "origin/main",
            "# upstream", "upstream/main");
        branches.SelectedBranch!.Name.Should().Be("main");
        branches.CurrentBranch.Should().Be("main");

        var main = Row(branches, "main");
        main.IsCurrent.Should().BeTrue();
        main.UpstreamText.Should().Be("Tracks origin/main · 2 to push, 1 to pull");
        Row(branches, "feature/old").IsGone.Should().BeTrue();
        Row(branches, "feature/old").IsMerged.Should().BeTrue();
        Row(branches, "feature/login").NeedsPublish.Should().BeTrue();
        Row(branches, "origin/feature/remote-only").DisplayName.Should().Be("feature/remote-only");
    }

    [Fact]
    public async Task A_repository_without_commits_has_no_branches_yet()
    {
        _harness.Git.GetBranchesAsync(_harness.Root, true, Arg.Any<CancellationToken>()).Returns([]);
        _harness.Status = GitData.Status(unborn: true);

        var branches = await OpenAsync();

        branches.HasNoBranches.Should().BeTrue();
        branches.NoBranchesDescription.Should().Contain("main");
        branches.HasNoMatches.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_narrows_the_list()
    {
        var branches = await OpenAsync();

        branches.FilterText = "login";

        branches.Rows.OfType<BranchRowViewModel>().Select(r => r.Name).Should().Equal("feature/login");
        branches.SelectedBranch!.Name.Should().Be("feature/login");
        branches.FilterText = "nothing-like-this";
        branches.HasNoMatches.Should().BeTrue();
    }

    [Fact]
    public async Task Switching_with_local_changes_offers_to_stash_them_first()
    {
        var branches = await OpenAsync();
        _harness.Git.CheckoutAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.DirtyWorkingTree, "Your local changes would be overwritten.")),
            Task.CompletedTask);
        _harness.ConfirmAll(true);

        await branches.SwitchCommand.ExecuteAsync(Row(branches, "feature/login"));

        Received.InOrder(() =>
        {
            _harness.Git.CheckoutAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>());
            _harness.Git.StashAsync(_harness.Root, Arg.Is<string>(m => m.Contains("before switching to feature/login")), true, Arg.Any<CancellationToken>());
            _harness.Git.CheckoutAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>());
        });
        _harness.Notifications.Received(1).Show("Switched to feature/login", Arg.Is<string>(m => m.Contains("stashed")), NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitCheckout), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_stash_leaves_everything_as_it_was()
    {
        var branches = await OpenAsync();
        _harness.Git.CheckoutAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.DirtyWorkingTree, "Your local changes would be overwritten.")));
        _harness.ConfirmAll(false);

        await branches.SwitchCommand.ExecuteAsync(Row(branches, "feature/login"));

        await _harness.Git.DidNotReceiveWithAnyArgs().StashAsync(default!, default, default, default);
        _harness.Notifications.DidNotReceiveWithAnyArgs().ShowError(default!, default);
    }

    [Fact]
    public async Task Remote_branches_are_checked_out_as_local_tracking_branches()
    {
        var branches = await OpenAsync();

        await branches.SwitchCommand.ExecuteAsync(Row(branches, "origin/feature/remote-only"));

        await _harness.Git.Received(1).CheckoutRemoteBranchAsync(_harness.Root, "origin/feature/remote-only", null, Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Switched to feature/remote-only", null, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Deleting_an_unmerged_branch_asks_again_before_forcing()
    {
        var branches = await OpenAsync();
        _harness.Git.DeleteBranchAsync(_harness.Root, "feature/login", false, Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.GitCommandFailed, "The branch 'feature/login' has commits that aren't merged into another branch.")));
        var asked = new List<ConfirmOptions>();
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(asked.Add)).Returns(true);

        await branches.DeleteCommand.ExecuteAsync(Row(branches, "feature/login"));

        asked.Select(o => o.ConfirmText).Should().Equal("Delete branch", "Force delete");
        asked.Should().OnlyContain(o => o.IsDestructive);
        await _harness.Git.Received(1).DeleteBranchAsync(_harness.Root, "feature/login", true, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitBranchDeleted && e.Detail!.Contains("Force")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Current_branch_cannot_be_deleted_or_merged_into_itself()
    {
        var branches = await OpenAsync();
        var main = Row(branches, "main");

        branches.DeleteCommand.CanExecute(main).Should().BeFalse();
        branches.MergeCommand.CanExecute(main).Should().BeFalse();
        branches.SwitchCommand.CanExecute(main).Should().BeFalse();
        branches.RenameCommand.CanExecute(Row(branches, "origin/main")).Should().BeFalse();
        branches.PublishCommand.CanExecute(Row(branches, "feature/login")).Should().BeTrue();
        branches.PublishCommand.CanExecute(main).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_conflicts_lead_to_changes_with_a_prepared_message()
    {
        var section = await _harness.OpenAsync(GitView.Branches);
        _harness.Git.MergeAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.MergeConflict, "There is a conflict in a.cs.")));
        _harness.Git.When(g => g.MergeAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>()))
            .Do(_ => _harness.Status = GitData.Status(state: GitRepositoryState.Merging, entries: GitData.Conflicted("a.cs")));
        _harness.ConfirmAll(true);

        await section.Branches.MergeCommand.ExecuteAsync(Row(section.Branches, "feature/login"));

        section.CurrentView.Should().Be(GitView.Changes);
        section.Changes.IsMerging.Should().BeTrue();
        section.Changes.Summary.Should().Be("Merge branch 'feature/login' into main");
        _harness.Notifications.Received(1).Show("The merge has conflicts", Arg.Any<string>(), NotificationSeverity.Warning, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Successful_merge_is_journaled()
    {
        var branches = await OpenAsync();
        _harness.ConfirmAll(true);

        await branches.MergeCommand.ExecuteAsync(Row(branches, "feature/login"));

        await _harness.Git.Received(1).MergeAsync(_harness.Root, "feature/login", Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitMerge && e.Title == "Merged feature/login into main"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publishing_pushes_with_upstream()
    {
        var branches = await OpenAsync();

        await branches.PublishCommand.ExecuteAsync(Row(branches, "feature/login"));

        await _harness.Git.Received(1).PushAsync(_harness.Root, new GitPushOptions(SetUpstream: true, Branch: "feature/login"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renaming_validates_the_new_name()
    {
        var branches = await OpenAsync();
        PromptOptions? asked = null;
        _harness.Dialogs.PromptAsync(Arg.Do<PromptOptions>(o => asked = o)).Returns("feature/sign-in");

        await branches.RenameCommand.ExecuteAsync(Row(branches, "feature/login"));

        asked!.InitialValue.Should().Be("feature/login");
        asked.Validate!("feature/login").Should().Be("Enter a different name.");
        asked.Validate("main").Should().Contain("already exists");
        asked.Validate("has space").Should().NotBeNull();
        await _harness.Git.Received(1).RenameBranchAsync(_harness.Root, "feature/login", "feature/sign-in", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Bulk_deletion_never_offers_the_current_or_default_branch()
    {
        BranchesViewModel.MergedBranchesToDelete(["develop", "feature/a", "main", "master", "feature/b", "feature/a"], current: "develop", defaultBranch: "trunk")
            .Should().Equal("feature/a", "feature/b");
    }

    [Fact]
    public async Task Delete_merged_branches_confirms_the_list_then_deletes_each()
    {
        _harness.Git.GetMergedBranchesAsync(_harness.Root, null, Arg.Any<CancellationToken>()).Returns(["feature/old", "fix/typo", "main"]);
        var branches = await OpenAsync();
        ConfirmOptions? asked = null;
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(true);

        await branches.DeleteMergedCommand.ExecuteAsync(null);

        asked!.IsDestructive.Should().BeTrue();
        asked.Message.Should().Contain("feature/old").And.Contain("fix/typo").And.NotContain("• main");
        await _harness.Git.Received(1).DeleteBranchAsync(_harness.Root, "feature/old", false, Arg.Any<CancellationToken>());
        await _harness.Git.Received(1).DeleteBranchAsync(_harness.Root, "fix/typo", false, Arg.Any<CancellationToken>());
        await _harness.Git.DidNotReceive().DeleteBranchAsync(_harness.Root, "main", Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Deleted 2 merged branches", Arg.Any<string>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Nothing_to_bulk_delete_is_said_plainly()
    {
        _harness.Git.GetMergedBranchesAsync(_harness.Root, null, Arg.Any<CancellationToken>()).Returns(["main"]);
        var branches = await OpenAsync();

        await branches.DeleteMergedCommand.ExecuteAsync(null);

        _ = _harness.Dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!);
        _harness.Notifications.Received(1).Show("No merged branches to delete", Arg.Any<string>(), NotificationSeverity.Info, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task New_branch_from_the_selected_branch_starts_there()
    {
        var branches = await OpenAsync();
        _harness.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns("feature/next");

        await branches.NewBranchCommand.ExecuteAsync(Row(branches, "feature/login"));

        await _harness.Git.Received(1).CreateBranchAsync(_harness.Root, "feature/next", "feature/login", true, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitBranchCreated), Arg.Any<CancellationToken>());
    }
}
