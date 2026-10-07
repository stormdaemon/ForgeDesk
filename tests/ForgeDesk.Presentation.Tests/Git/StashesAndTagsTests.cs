using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Git.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class StashesAndTagsTests : IDisposable
{
    private readonly GitHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void ServeStashes(params GitStash[] stashes) =>
        _harness.Git.GetStashesAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns(stashes);

    [Fact]
    public async Task Stash_messages_are_shown_without_git_prefixes()
    {
        ServeStashes(
            new GitStash(0, "stash@{0}", "On main: halfway through the login form", GitData.Now),
            new GitStash(1, "stash@{1}", "WIP on feature/x: 1234567 Add tests", GitData.Now.AddDays(-1)));

        var stashes = (await _harness.OpenAsync(GitView.Stashes)).Stashes;

        stashes.Stashes.Select(s => (s.Message, s.Branch)).Should().Equal(
            ("halfway through the login form", "main"),
            ("Work in progress · 1234567 Add tests", "feature/x"));
        stashes.SelectedStash.Should().BeSameAs(stashes.Stashes[0]);
        stashes.Details!.Revision.Should().Be("stash@{0}");
        stashes.Details.IsStash.Should().BeTrue();
        stashes.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Stashing_asks_for_a_message_and_whether_to_include_new_files()
    {
        _harness.Status = GitData.WithEntries(GitData.Modified("a.cs"), GitData.Untracked("b.txt"));
        var stashes = (await _harness.OpenAsync(GitView.Stashes)).Stashes;
        stashes.IsEmpty.Should().BeTrue();
        stashes.CanStash.Should().BeTrue();
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (StashDialogViewModel)call.Arg<IDialogViewModel>();
            dialog.HasUntrackedFiles.Should().BeTrue();
            dialog.Message = "  try another approach ";
            dialog.IncludeUntracked = false;
            return Task.FromResult<bool?>(true);
        });

        await stashes.StashChangesCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).StashAsync(_harness.Root, "try another approach", false, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitStash), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clean_tree_cannot_be_stashed()
    {
        var stashes = (await _harness.OpenAsync(GitView.Stashes)).Stashes;

        stashes.StashChangesCommand.CanExecute(null).Should().BeFalse();
        stashes.EmptyDescription.Should().Contain("nothing stashed");
    }

    [Fact]
    public async Task Palette_link_opens_the_stash_dialog()
    {
        _harness.Status = GitData.WithEntries(GitData.Modified("a.cs"));
        var section = await _harness.OpenAsync();
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(Task.FromResult<bool?>(false));

        await section.NavigateToAsync(GitNavigation.StashChanges());

        section.CurrentView.Should().Be(GitView.Stashes);
        await _harness.Dialogs.Received(1).ShowDialogAsync(Arg.Any<StashDialogViewModel>());
    }

    [Fact]
    public async Task Popping_with_conflicts_keeps_the_stash_and_shows_changes()
    {
        ServeStashes(new GitStash(0, "stash@{0}", "On main: wip", GitData.Now));
        var section = await _harness.OpenAsync(GitView.Stashes);
        _harness.Git.StashPopAsync(_harness.Root, 0, Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new ForgeException(ErrorKind.MergeConflict, "There is a conflict in a.cs.")));

        await section.Stashes.PopCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).Show("The stash was applied with conflicts", Arg.Is<string>(m => m.Contains("kept")), NotificationSeverity.Warning,
            Arg.Any<NotificationAction?>());
        section.CurrentView.Should().Be(GitView.Changes);
    }

    [Fact]
    public async Task Popping_restores_the_stash()
    {
        ServeStashes(new GitStash(0, "stash@{0}", "On main: wip", GitData.Now));
        var stashes = (await _harness.OpenAsync(GitView.Stashes)).Stashes;

        await stashes.PopCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).StashPopAsync(_harness.Root, 0, Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Stash restored", Arg.Any<string>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Dropping_a_stash_is_confirmed()
    {
        ServeStashes(new GitStash(0, "stash@{0}", "On main: first", GitData.Now), new GitStash(1, "stash@{1}", "On main: second", GitData.Now));
        var stashes = (await _harness.OpenAsync(GitView.Stashes)).Stashes;
        _harness.ConfirmAll(false);

        await stashes.DropCommand.ExecuteAsync(stashes.Stashes[1]);
        await _harness.Git.DidNotReceiveWithAnyArgs().StashDropAsync(default!, default, default);

        _harness.ConfirmAll(true);
        await stashes.DropCommand.ExecuteAsync(stashes.Stashes[1]);
        await _harness.Git.Received(1).StashDropAsync(_harness.Root, 1, Arg.Any<CancellationToken>());
        await _harness.Dialogs.Received().ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Drop stash"));
    }

    [Fact]
    public async Task Stash_count_change_reloads_the_list()
    {
        var section = await _harness.OpenAsync(GitView.Stashes);
        ServeStashes(new GitStash(0, "stash@{0}", "On main: new", GitData.Now));

        await _harness.SetStatusAsync(GitData.Status(stashes: 1));

        section.Stashes.Stashes.Should().ContainSingle();
    }

    [Fact]
    public async Task Tags_list_their_kind_message_and_target()
    {
        _harness.Git.GetTagsAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns(
        [
            new GitTag("v1.1.0", GitData.Sha(1), GitData.Now, "Second release\n\nMore notes", true),
            new GitTag("v1.0.0", GitData.Sha(2), GitData.Now.AddDays(-9), null, false),
        ]);

        var tags = (await _harness.OpenAsync(GitView.Tags)).Tags;

        tags.Tags.Select(t => (t.Name, t.KindText, t.Summary, t.ShortSha)).Should().Equal(
            ("v1.1.0", "Annotated", "Second release", "0000000"),
            ("v1.0.0", "Lightweight", null, "0000000"));
        tags.FilterText = "v1.0";
        tags.Tags.Select(t => t.Name).Should().Equal("v1.0.0");
        tags.SelectedTag!.Name.Should().Be("v1.0.0");
    }

    [Fact]
    public async Task Creating_a_tag_on_head_validates_the_name()
    {
        _harness.Git.GetTagsAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.0.0", GitData.Sha(1), null, null, false)]);
        var tags = (await _harness.OpenAsync(GitView.Tags)).Tags;
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (CreateTagDialogViewModel)call.Arg<IDialogViewModel>();
            dialog.TargetDescription.Should().StartWith("HEAD (main · 1111111)");
            dialog.Name = "V1.0.0";
            dialog.ConfirmCommand.Execute(null);
            dialog.ValidationMessage.Should().Contain("already exists");
            dialog.Name = "v1.1.0";
            dialog.ValidationMessage.Should().BeNull();
            return Task.FromResult<bool?>(true);
        });

        await tags.CreateCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).CreateTagAsync(_harness.Root, "v1.1.0", null, null, Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitTagCreated), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_tag_says_it_is_local_only()
    {
        _harness.Git.GetTagsAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.0.0", GitData.Sha(1), null, null, false)]);
        var tags = (await _harness.OpenAsync(GitView.Tags)).Tags;
        ConfirmOptions? asked = null;
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(true);

        await tags.DeleteCommand.ExecuteAsync(null);

        asked!.IsDestructive.Should().BeTrue();
        asked.Message.Should().Contain("this repository only");
        await _harness.Git.Received(1).DeleteTagAsync(_harness.Root, "v1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pushing_a_tag_reports_success_and_failure()
    {
        _harness.Git.GetTagsAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.0.0", GitData.Sha(1), null, null, false)]);
        var tags = (await _harness.OpenAsync(GitView.Tags)).Tags;

        await tags.PushCommand.ExecuteAsync(null);
        _harness.Notifications.Received(1).Show("Pushed tag v1.0.0", null, NotificationSeverity.Success, Arg.Any<NotificationAction?>());

        _harness.Git.PushTagAsync(default!, default!, default, default).ReturnsForAnyArgs(
            Task.FromException(new ForgeException(ErrorKind.AuthenticationRequired, "Sign in to push.")));
        await tags.PushCommand.ExecuteAsync(null);
        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "Sign in to push."), Arg.Any<NotificationAction?>());
    }

    [Theory]
    [InlineData("v1.2.0", null)]
    [InlineData("release/2026-09", null)]
    [InlineData("", "Enter a tag name, for example v1.2.0.")]
    [InlineData("v 1", "Tag names can't contain spaces.")]
    [InlineData("v1..2", "Tag names can't contain '..'.")]
    [InlineData("v1.lock", "Tag names can't end with '.' or '.lock'.")]
    [InlineData("-v1", "Tag names can't start with '-'.")]
    public void Tag_names_follow_git_rules(string name, string? error) =>
        TagNames.Validate(name).Should().Be(error);
}
