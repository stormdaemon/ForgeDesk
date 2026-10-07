using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Git.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class ChangesViewModelTests : IDisposable
{
    private readonly GitHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<ChangesViewModel> OpenAsync(params GitStatusEntry[] entries)
    {
        _harness.Status = GitData.WithEntries(entries);
        var section = await _harness.OpenAsync(GitView.Changes);
        return section.Changes;
    }

    private static ChangeItemViewModel Item(ChangesViewModel changes, ChangeGroup group, string path) =>
        changes.Rows.OfType<ChangeItemViewModel>().Single(i => i.Group == group && i.Path == path);

    [Fact]
    public async Task Status_is_grouped_into_conflicts_staged_and_unstaged_changes()
    {
        var changes = await OpenAsync(
            GitData.Modified("src/b.cs"),
            GitData.Staged("src/a.cs"),
            GitData.PartlyStaged("README.md"),
            GitData.Untracked("new.txt"),
            GitData.Conflicted("app.json"),
            GitData.Renamed("src/old.cs", "src/name.cs"),
            GitData.Renamed("lib/moved.cs", "src/moved.cs"));

        changes.Rows.Select(r => r is ChangeGroupHeader h ? $"# {h.Title} ({h.Count})" : $"{((ChangeItemViewModel)r).StateLetter} {((ChangeItemViewModel)r).Path}")
            .Should().Equal(
                "# Merge conflicts (1)", "U app.json",
                "# Staged changes (4)", "M README.md", "M src/a.cs", "R src/moved.cs", "R src/name.cs",
                "# Changes (3)", "? new.txt", "M README.md", "M src/b.cs");
        changes.ConflictCount.Should().Be(1);
        changes.StagedCount.Should().Be(4);
        changes.UnstagedCount.Should().Be(3);
        changes.FileCount.Should().Be(7);

        var rename = Item(changes, ChangeGroup.Staged, "src/name.cs");
        rename.DisplayName.Should().Be("old.cs → name.cs");
        rename.Directory.Should().Be("src/");
        rename.StateTone.Should().Be(StatusTone.Info);
        var move = Item(changes, ChangeGroup.Staged, "src/moved.cs");
        move.DisplayName.Should().Be("lib/moved.cs → src/moved.cs");
        move.Directory.Should().BeEmpty();
        Item(changes, ChangeGroup.Unstaged, "new.txt").StateTone.Should().Be(StatusTone.Success);
        Item(changes, ChangeGroup.Conflicted, "app.json").StateTone.Should().Be(StatusTone.Danger);
    }

    [Fact]
    public async Task First_file_is_selected_and_its_diff_loaded_on_open()
    {
        _harness.Git.GetFileDiffAsync(_harness.Root, "a.cs", DiffTarget.WorkingTree, Arg.Any<CancellationToken>()).Returns(GitData.Diff("a.cs", "added"));

        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"));

        changes.SelectedItems.Select(i => i.Path).Should().Equal("a.cs");
        changes.DiffItem!.Path.Should().Be("a.cs");
        changes.Diff!.Additions.Should().Be(1);
        changes.HunkActionText.Should().Be("Stage hunk");
        changes.DiffTargetText.Should().Be("Not staged");
    }

    [Fact]
    public async Task Refreshing_the_status_keeps_rows_and_selection()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"));
        var b = Item(changes, ChangeGroup.Unstaged, "b.cs");
        changes.SetSelection([b]);

        await _harness.SetStatusAsync(GitData.WithEntries(GitData.Modified("a.cs"), GitData.Modified("b.cs"), GitData.Untracked("c.cs")));

        Item(changes, ChangeGroup.Unstaged, "b.cs").Should().BeSameAs(b);
        changes.SelectedItems.Should().Equal(b);
        changes.Rows.OfType<ChangeItemViewModel>().Select(i => i.Path).Should().Equal("a.cs", "b.cs", "c.cs");
    }

    [Fact]
    public async Task Staging_the_selected_file_moves_the_selection_to_its_staged_row()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"));
        changes.SetSelection([Item(changes, ChangeGroup.Unstaged, "a.cs")]);
        _harness.Git.When(g => g.StageAsync(_harness.Root, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()))
            .Do(_ => _harness.Status = GitData.WithEntries(GitData.Staged("a.cs"), GitData.Modified("b.cs")));

        await changes.StageCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).StageAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "a.cs" })), Arg.Any<CancellationToken>());
        var staged = changes.SelectedItems.Should().ContainSingle().Subject;
        staged.Group.Should().Be(ChangeGroup.Staged);
        staged.Path.Should().Be("a.cs");
        await _harness.Git.Received().GetFileDiffAsync(_harness.Root, "a.cs", DiffTarget.Staged, Arg.Any<CancellationToken>());
        changes.HunkActionText.Should().Be("Unstage hunk");
    }

    [Fact]
    public async Task Discarded_file_is_replaced_in_the_selection_by_the_next_one()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"), GitData.Modified("c.cs"));
        changes.SetSelection([Item(changes, ChangeGroup.Unstaged, "b.cs")]);

        await _harness.SetStatusAsync(GitData.WithEntries(GitData.Modified("a.cs"), GitData.Modified("c.cs")));

        changes.SelectedItems.Select(i => i.Path).Should().Equal("c.cs");
    }

    [Fact]
    public async Task Space_unstages_a_fully_staged_selection_and_stages_a_mixed_one()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"), GitData.Modified("b.cs"));
        var a = Item(changes, ChangeGroup.Staged, "a.cs");
        var b = Item(changes, ChangeGroup.Unstaged, "b.cs");

        changes.SetSelection([a]);
        await changes.ToggleStagingCommand.ExecuteAsync(null);
        await _harness.Git.Received(1).UnstageAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "a.cs" })), Arg.Any<CancellationToken>());

        changes.SetSelection([a, b]);
        await changes.ToggleStagingCommand.ExecuteAsync(null);
        await _harness.Git.Received(1).StageAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "b.cs" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multiple_selection_offers_to_stage_and_unstage_it()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"), GitData.Modified("b.cs"), GitData.Untracked("c.cs"));

        changes.SetSelection(changes.Rows.OfType<ChangeItemViewModel>());

        changes.HasMultipleSelection.Should().BeTrue();
        changes.DiffItem.Should().BeNull();
        changes.ShowDiffView.Should().BeFalse();
        changes.SelectionTitle.Should().Be("3 files selected");
        changes.SelectionDetail.Should().Be("1 staged · 2 not staged");
        changes.SelectionStageText.Should().Be("Stage 2 files");
        changes.SelectionUnstageText.Should().Be("Unstage 1 file");
    }

    [Fact]
    public async Task Hover_action_on_an_unselected_row_acts_on_that_row_only()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"));
        changes.SetSelection([Item(changes, ChangeGroup.Unstaged, "a.cs")]);

        await changes.StageCommand.ExecuteAsync(Item(changes, ChangeGroup.Unstaged, "b.cs"));

        await _harness.Git.Received(1).StageAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "b.cs" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Discard_asks_for_a_destructive_confirmation_first()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Untracked("new.txt"));
        changes.SetSelection(changes.Rows.OfType<ChangeItemViewModel>());
        ConfirmOptions? asked = null;
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(false);

        await changes.DiscardCommand.ExecuteAsync(null);

        asked!.IsDestructive.Should().BeTrue();
        asked.Title.Should().Be("Discard changes to 2 files?");
        asked.Message.Should().Contain("a.cs").And.Contain("new.txt").And.Contain("1 new file will be deleted");
        await _harness.Git.DidNotReceiveWithAnyArgs().DiscardAsync(default!, default!, default);

        _harness.ConfirmAll();
        await changes.DiscardCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).DiscardAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "a.cs", "new.txt" })),
            Arg.Any<CancellationToken>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitDiscard), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Discard_all_lists_what_happens_to_each_kind_of_change()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Untracked("b.txt"), GitData.Untracked("c.txt"), GitData.Staged("d.cs", GitFileState.Deleted));
        ConfirmOptions? asked = null;
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(true);

        await changes.DiscardAllCommand.ExecuteAsync(null);

        asked!.IsDestructive.Should().BeTrue();
        asked.ConfirmText.Should().Be("Discard 4 files");
        asked.Message.Should().Contain("1 modified").And.Contain("2 new (deleted)").And.Contain("1 deleted");
        await _harness.Git.Received(1).DiscardAsync(_harness.Root, Arg.Is<IReadOnlyList<string>>(p => p.Count == 4), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slow_diff_never_replaces_the_diff_of_a_newer_selection()
    {
        var slow = new TaskCompletionSource<FileDiff>();
        _harness.Git.GetFileDiffAsync(_harness.Root, "a.cs", DiffTarget.WorkingTree, Arg.Any<CancellationToken>()).Returns(slow.Task);
        _harness.Git.GetFileDiffAsync(_harness.Root, "b.cs", DiffTarget.WorkingTree, Arg.Any<CancellationToken>()).Returns(GitData.Diff("b.cs", "bee"));
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Modified("b.cs"));
        changes.IsDiffLoading.Should().BeTrue();

        changes.SetSelection([Item(changes, ChangeGroup.Unstaged, "b.cs")]);
        slow.SetResult(GitData.Diff("a.cs", "stale"));
        await Task.Yield();

        changes.Diff!.Path.Should().Be("b.cs");
        changes.IsDiffLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Hunks_are_staged_from_unstaged_diffs_and_unstaged_from_staged_ones()
    {
        var diff = GitData.Diff("a.cs", "line");
        _harness.Git.GetFileDiffAsync(_harness.Root, "a.cs", Arg.Any<DiffTarget>(), Arg.Any<CancellationToken>()).Returns(diff);
        var changes = await OpenAsync(GitData.PartlyStaged("a.cs"));

        changes.SetSelection([Item(changes, ChangeGroup.Unstaged, "a.cs")]);
        changes.HunkActionText.Should().Be("Stage hunk");
        await changes.ApplyHunkCommand.ExecuteAsync(diff.Hunks[0]);
        await _harness.Git.Received(1).ApplyHunkAsync(_harness.Root, diff, diff.Hunks[0], false, Arg.Any<CancellationToken>());

        changes.SetSelection([Item(changes, ChangeGroup.Staged, "a.cs")]);
        changes.HunkActionText.Should().Be("Unstage hunk");
        await changes.ApplyHunkCommand.ExecuteAsync(diff.Hunks[0]);
        await _harness.Git.Received(1).ApplyHunkAsync(_harness.Root, diff, diff.Hunks[0], true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hunk_failures_are_reported_as_notifications()
    {
        var diff = GitData.Diff("a.cs", "line");
        _harness.Git.GetFileDiffAsync(_harness.Root, "a.cs", Arg.Any<DiffTarget>(), Arg.Any<CancellationToken>()).Returns(diff);
        _harness.Git.ApplyHunkAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(
            Task.FromException(new ForgeException(ErrorKind.GitCommandFailed, "This change no longer matches the file.")));
        var changes = await OpenAsync(GitData.Modified("a.cs"));

        await changes.ApplyHunkCommand.ExecuteAsync(diff.Hunks[0]);

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "This change no longer matches the file."), Arg.Any<NotificationAction?>());
        changes.Error.Should().BeNull();
    }

    [Fact]
    public async Task Committing_staged_changes_clears_the_box_and_journals_the_commit()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"), GitData.Modified("b.cs"));
        changes.Summary = "Fix the login";
        changes.Description = "Details here";
        changes.CommitButtonText.Should().Be("Commit to main");

        await changes.CommitCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).CommitAsync(_harness.Root, new GitCommitOptions("Fix the login\n\nDetails here", false, false), Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Committed abcdef1 · Fix the login", "On main.", NotificationSeverity.Success, Arg.Any<NotificationAction?>());
        await _harness.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitCommit && e.RefKind == "commit"),
            Arg.Any<CancellationToken>());
        changes.Summary.Should().BeEmpty();
        changes.Description.Should().BeEmpty();
        changes.IsCommitting.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_staged_commits_all_after_an_explicit_confirmation()
    {
        var changes = await OpenAsync(GitData.Modified("a.cs"), GitData.Untracked("b.cs"));
        changes.Summary = "Everything";
        changes.CommitButtonText.Should().Be("Commit all (2)");
        _harness.ConfirmAll(false);

        await changes.CommitCommand.ExecuteAsync(null);
        await _harness.Git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        changes.Summary.Should().Be("Everything", "a declined commit keeps the message");

        _harness.ConfirmAll(true);
        await changes.CommitCommand.ExecuteAsync(null);
        await _harness.Git.Received(1).CommitAsync(_harness.Root, new GitCommitOptions("Everything", false, true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Commit_needs_a_summary_and_no_conflicts()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.CommitCommand.CanExecute(null).Should().BeFalse();
        changes.Summary = "   ";
        changes.CommitCommand.CanExecute(null).Should().BeFalse();
        changes.Summary = "Ready";
        changes.CommitCommand.CanExecute(null).Should().BeTrue();

        await _harness.SetStatusAsync(GitData.WithEntries(GitData.Staged("a.cs"), GitData.Conflicted("b.cs")));
        changes.CommitCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Long_summaries_are_flagged()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.Summary = new string('x', 72);
        changes.IsSummaryTooLong.Should().BeFalse();
        changes.Summary = new string('x', 73);
        changes.IsSummaryTooLong.Should().BeTrue();
        changes.SummaryCounterText.Should().Be("73/72");
    }

    [Fact]
    public async Task Missing_identity_is_asked_for_and_saved_before_committing()
    {
        _harness.Git.GetIdentityAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns(new GitIdentity(null, null));
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<IDialogViewModel>().Should().BeOfType<GitIdentityDialogViewModel>().Subject;
            dialog.Name = " Ada Lovelace ";
            dialog.Email = "ada@example.com";
            return Task.FromResult<bool?>(true);
        });
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.Summary = "First";

        await changes.CommitCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).SetGlobalIdentityAsync("Ada Lovelace", "ada@example.com", Arg.Any<CancellationToken>());
        await _harness.Git.Received(1).CommitAsync(_harness.Root, Arg.Any<GitCommitOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_the_identity_dialog_cancels_the_commit()
    {
        _harness.Git.GetIdentityAsync(_harness.Root, Arg.Any<CancellationToken>()).Returns(new GitIdentity("Ada", null));
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(Task.FromResult<bool?>(false));
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.Summary = "First";

        await changes.CommitCommand.ExecuteAsync(null);

        await _harness.Git.DidNotReceiveWithAnyArgs().SetGlobalIdentityAsync(default!, default!, default);
        await _harness.Git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        changes.Summary.Should().Be("First");
    }

    [Fact]
    public void Identity_dialog_validates_name_and_email()
    {
        var dialog = new GitIdentityDialogViewModel();
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;

        dialog.ConfirmCommand.Execute(null);
        dialog.ValidationMessage.Should().Be("Enter your name.");
        dialog.Name = "Ada";
        dialog.Email = "not-an-email";
        dialog.ConfirmCommand.Execute(null);
        dialog.ValidationMessage.Should().Be("Enter a valid email address.");
        closed.Should().BeNull();

        dialog.Email = "ada@example.com";
        dialog.ConfirmCommand.Execute(null);
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task Amend_prefills_the_last_commit_and_gives_the_draft_back_when_turned_off()
    {
        _harness.Status = GitData.Status(ahead: 1, entries: GitData.Staged("a.cs"));
        var last = GitData.Commit("9999999999999999999999999999999999999999", "Previous subject") with { Body = "Previous body" };
        _harness.Git.GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Take == 1), Arg.Any<CancellationToken>()).Returns([last]);
        var changes = (await _harness.OpenAsync()).Changes;
        changes.Summary = "My draft";

        changes.IsAmend = true;
        await changes.AmendToggleTask;

        _ = _harness.Dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!);
        changes.Summary.Should().Be("Previous subject");
        changes.Description.Should().Be("Previous body");
        changes.CommitButtonText.Should().Be("Amend last commit");

        changes.IsAmend = false;
        await changes.AmendToggleTask;
        changes.Summary.Should().Be("My draft");

        changes.IsAmend = true;
        await changes.AmendToggleTask;
        await changes.CommitCommand.ExecuteAsync(null);
        await _harness.Git.Received(1).CommitAsync(_harness.Root, new GitCommitOptions("Previous subject\n\nPrevious body", true, false), Arg.Any<CancellationToken>());
        changes.IsAmend.Should().BeFalse();
    }

    [Fact]
    public async Task Amending_a_pushed_commit_needs_a_confirmation()
    {
        _harness.Status = GitData.Status(upstream: "origin/main", ahead: 0, entries: GitData.Staged("a.cs"));
        var changes = (await _harness.OpenAsync()).Changes;
        ConfirmOptions? asked = null;
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(false);

        changes.IsAmend = true;
        await changes.AmendToggleTask;

        asked!.IsDestructive.Should().BeTrue();
        asked.Message.Should().Contain("origin/main");
        changes.IsAmend.Should().BeFalse();

        _harness.ConfirmAll(true);
        changes.IsAmend = true;
        await changes.AmendToggleTask;
        changes.IsAmend.Should().BeTrue();
        changes.Summary = "Reworded";
        changes.CommitAndPushCommand.CanExecute(null).Should().BeFalse("pushing an amended pushed commit needs a force push");
    }

    [Fact]
    public async Task Commit_and_push_publishes_a_branch_without_upstream()
    {
        _harness.Status = GitData.Status(branch: "feature", upstream: null, entries: GitData.Staged("a.cs"));
        var changes = (await _harness.OpenAsync()).Changes;
        changes.CommitAndPushText.Should().Be("Commit & publish");
        changes.Summary = "Ship it";

        await changes.CommitAndPushCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).PushAsync(_harness.Root, new GitPushOptions(SetUpstream: true, Branch: "feature"), null, Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Committed abcdef1 and published feature", "Ship it", NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task A_failed_push_after_commit_says_the_commit_is_safe()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.Summary = "Ship it";
        _harness.Git.PushAsync(default!, default!, default, default).ReturnsForAnyArgs(
            Task.FromException(new ForgeException(ErrorKind.NonFastForward, "Your branch is behind 'origin/main'.")));

        await changes.CommitAndPushCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Title == "Committed abcdef1, but the push failed"), Arg.Any<NotificationAction?>());
        changes.Summary.Should().BeEmpty("the commit itself succeeded");
    }

    [Fact]
    public async Task Commit_failure_is_a_notification_and_keeps_the_message()
    {
        var changes = await OpenAsync(GitData.Staged("a.cs"));
        changes.Summary = "Broken";
        _harness.Git.CommitAsync(default!, default!, default).ReturnsForAnyArgs(
            Task.FromException<GitCommit>(new ForgeException(ErrorKind.GitCommandFailed, "Git couldn't sign the commit.")));

        await changes.CommitCommand.ExecuteAsync(null);

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "Git couldn't sign the commit."), Arg.Any<NotificationAction?>());
        changes.Summary.Should().Be("Broken");
        changes.IsCommitting.Should().BeFalse();
    }

    [Fact]
    public async Task Merge_banner_offers_to_abort_after_confirmation()
    {
        _harness.Status = GitData.Status(state: GitRepositoryState.Merging, entries: GitData.Conflicted("a.cs"));
        var changes = (await _harness.OpenAsync()).Changes;
        changes.IsMerging.Should().BeTrue();
        changes.OperationMessage.Should().Contain("Resolve the conflicted files");
        changes.ShowConflictPanel.Should().BeTrue();
        _harness.ConfirmAll(true);

        await changes.AbortMergeCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).AbortMergeAsync(_harness.Root, Arg.Any<CancellationToken>());
        await _harness.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Abort merge"));
    }

    [Fact]
    public async Task Detached_head_offers_to_create_a_branch_here()
    {
        _harness.Status = GitData.Status(branch: null, upstream: null, entries: GitData.Modified("a.cs"));
        var changes = (await _harness.OpenAsync()).Changes;
        changes.IsDetached.Should().BeTrue();
        _harness.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns("rescue");

        await changes.CreateBranchHereCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).CreateBranchAsync(_harness.Root, "rescue", null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clean_tree_shows_the_last_commit()
    {
        _harness.Git.GetLogAsync(_harness.Root, Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns([GitData.Commit(GitData.Head, "Last change")]);

        var changes = await OpenAsync();

        changes.IsClean.Should().BeTrue();
        changes.ShowList.Should().BeFalse();
        changes.CleanTitle.Should().Be("No local changes");
        changes.CleanDescription.Should().Contain("1111111 · Last change");
        await changes.CleanActionCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task File_actions_use_the_shell_and_navigation()
    {
        var file = Path.Combine(_harness.Root, "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        var changes = await OpenAsync(GitData.Modified("src/a.cs"));
        var item = Item(changes, ChangeGroup.Unstaged, "src/a.cs");
        WorkspaceNavigationRequest? request = null;
        _harness.Context.NavigationRequested += (_, r) => request = r;

        changes.OpenFileCommand.Execute(item);
        changes.CopyRelativePathCommand.Execute(item);
        changes.ShowInFilesCommand.Execute(item);

        _harness.Shell.Received(1).OpenInEditor(Arg.Is<string>(p => p.EndsWith("a.cs", StringComparison.Ordinal)), null);
        _harness.Shell.Received(1).CopyToClipboard("src/a.cs");
        request.Should().Be(new WorkspaceNavigationRequest(WorkspaceSection.Files, "src/a.cs"));
    }
}
