using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tests.Git.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class GitSectionViewModelTests : IDisposable
{
    private readonly GitHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Folder_without_repository_offers_to_initialize_one()
    {
        _harness.Status = null;
        var section = await _harness.OpenAsync();
        section.State.Should().Be(GitSectionState.NotRepository);
        section.NotRepositoryDescription.Should().Contain("forge-app");
        _harness.Git.When(g => g.InitAsync(_harness.Root, Arg.Any<CancellationToken>())).Do(_ => _harness.Status = GitData.Status(unborn: true));

        await section.InitializeRepositoryCommand.ExecuteAsync(null);

        section.IsReady.Should().BeTrue();
        section.Changes.IsUnborn.Should().BeTrue();
        section.Changes.CleanTitle.Should().Be("No commits yet");
        _harness.Notifications.Received(1).Show("Repository initialized", Arg.Any<string>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Missing_git_links_to_the_installer_and_checks_again()
    {
        _harness.StatusError = new ForgeException(ErrorKind.GitNotFound, "Git is not installed.");
        var section = await _harness.OpenAsync();
        section.State.Should().Be(GitSectionState.GitMissing);

        section.InstallGitCommand.Execute(null);
        _harness.Shell.Received(1).OpenUrl(GitSectionViewModel.GitDownloadUrl);

        _harness.StatusError = null;
        await section.CheckAgainCommand.ExecuteAsync(null);
        section.State.Should().Be(GitSectionState.Ready);
    }

    [Fact]
    public async Task Unreadable_status_is_shown_as_an_error()
    {
        _harness.StatusError = new ForgeException(ErrorKind.RepositoryLocked, "Another git process seems to be running.");

        var section = await _harness.OpenAsync();

        section.State.Should().Be(GitSectionState.Unavailable);
        section.RepositoryError!.Message.Should().Be("Another git process seems to be running.");
    }

    [Fact]
    public async Task The_last_view_is_remembered()
    {
        var section = await _harness.OpenAsync();
        await section.SelectViewAsync(GitView.Branches);

        _harness.Preferences.LastView.Should().Be(GitView.Branches);
        using var next = new GitSectionViewModel(_harness.Context, _harness.Services, _harness.Preferences);
        next.CurrentView.Should().Be(GitView.Branches);
        next.SelectedTab!.View.Should().Be(GitView.Branches);
    }

    [Fact]
    public async Task Selecting_a_tab_shows_its_view_once_and_keeps_it_open()
    {
        var section = await _harness.OpenAsync();

        section.SelectedTab = section.Tabs[(int)GitView.History];
        await Task.Yield();

        section.CurrentView.Should().Be(GitView.History);
        section.OpenedTabs.Select(t => t.View).Should().Equal(GitView.Changes, GitView.History);
        section.Tabs.Single(t => t.IsCurrent).View.Should().Be(GitView.History);
        await _harness.Git.Received(1).GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Take == HistoryViewModel.PageSize), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hidden_views_only_reload_when_shown_again()
    {
        var section = await _harness.OpenAsync(GitView.History);
        await section.SelectViewAsync(GitView.Changes);
        _harness.Git.ClearReceivedCalls();

        await _harness.Context.NotifyRepositoryChangedAsync();
        await _harness.Git.DidNotReceive().GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Take == HistoryViewModel.PageSize), Arg.Any<CancellationToken>());
        await _harness.Git.DidNotReceive().GetBranchesAsync(_harness.Root, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await section.SelectViewAsync(GitView.History);
        await _harness.Git.Received(1).GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Take == HistoryViewModel.PageSize), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inactive_section_defers_rebuilding_the_changes()
    {
        var section = await _harness.OpenAsync();
        section.Deactivate();

        await _harness.SetStatusAsync(GitData.WithEntries(GitData.Modified("late.cs")));
        section.Changes.Rows.Should().BeEmpty();
        section.Tabs[(int)GitView.Changes].BadgeText.Should().Be("1", "badges are cheap and always current");

        await section.ActivateAsync();
        section.Changes.Rows.OfType<ChangeItemViewModel>().Select(i => i.Path).Should().Equal("late.cs");
    }

    [Fact]
    public async Task Badges_count_changes_conflicts_and_stashes()
    {
        _harness.Status = GitData.Status(stashes: 2, entries: [GitData.Modified("a.cs"), GitData.Conflicted("b.cs")]);

        var section = await _harness.OpenAsync();

        var changes = section.Tabs[(int)GitView.Changes];
        changes.BadgeText.Should().Be("2");
        changes.BadgeTone.Should().Be(StatusTone.Danger);
        section.Tabs[(int)GitView.Stashes].BadgeText.Should().Be("2");
    }

    [Fact]
    public async Task A_sha_argument_opens_the_commit_in_history()
    {
        var commits = GitData.Linear(3);
        _harness.Git.GetLogAsync(_harness.Root, Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns(commits);
        var section = await _harness.OpenAsync();

        await section.NavigateToAsync(commits[2].Sha);

        section.CurrentView.Should().Be(GitView.History);
        section.History.SelectedCommit!.Sha.Should().Be(commits[2].Sha);
    }

    [Fact]
    public async Task A_changed_path_is_selected_in_changes_and_another_path_shows_its_history()
    {
        _harness.Status = GitData.WithEntries(GitData.Modified("a.cs"), GitData.Modified("src/b.cs"));
        var section = await _harness.OpenAsync(GitView.Tags);

        await section.NavigateToAsync("src/b.cs");
        section.CurrentView.Should().Be(GitView.Changes);
        section.Changes.SelectedItems.Select(i => i.Path).Should().Equal("src/b.cs");

        await section.NavigateToAsync("docs/guide.md");
        section.CurrentView.Should().Be(GitView.History);
        section.History.PathFilter.Should().Be("docs/guide.md");
        await _harness.Git.Received().GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.PathFilter == "docs/guide.md"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Commit_link_focuses_the_summary_box()
    {
        var section = await _harness.OpenAsync(GitView.History);
        var focused = 0;
        section.Changes.FocusSummaryRequested += (_, _) => focused++;

        await section.NavigateToAsync(GitNavigation.Commit());

        section.CurrentView.Should().Be(GitView.Changes);
        focused.Should().Be(1);
        section.Changes.IsSummaryFocusPending.Should().BeTrue("a view created later still focuses the box");
        section.Changes.AcknowledgeSummaryFocus();
        section.Changes.IsSummaryFocusPending.Should().BeFalse();
    }

    [Fact]
    public async Task Git_that_cannot_be_used_explains_why()
    {
        _harness.StatusError = new ForgeException(ErrorKind.GitNotFound, "Git 2.10 is too old for ForgeDesk.", "Update Git for Windows.");

        var section = await _harness.OpenAsync();

        section.GitMissingDescription.Should().Be("Git 2.10 is too old for ForgeDesk. Update Git for Windows.");
    }

    [Fact]
    public async Task Refresh_rereads_status_and_the_current_view()
    {
        var section = await _harness.OpenAsync(GitView.Tags);
        _harness.Git.ClearReceivedCalls();

        await section.RefreshCommand.ExecuteAsync(null);

        await _harness.Git.Received(1).GetStatusAsync(_harness.Root, Arg.Any<CancellationToken>());
        await _harness.Git.Received(1).GetTagsAsync(_harness.Root, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disposed_section_ignores_context_events()
    {
        var section = await _harness.OpenAsync();
        section.Dispose();

        await _harness.SetStatusAsync(GitData.WithEntries(GitData.Modified("a.cs")));

        section.Changes.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Navigation_arguments_are_read_as_shas_paths_or_links()
    {
        GitNavigation.From("a1b2c3d").Should().Be(GitNavigation.History("a1b2c3d"));
        GitNavigation.From("src\\app.cs").Should().Be(GitNavigation.Changes("src/app.cs"));
        GitNavigation.From(GitView.Tags).Should().Be(new GitNavigation(GitView.Tags));
        GitNavigation.From(42).Should().BeNull();
        GitNavigation.LooksLikeSha("abc12").Should().BeFalse();
        GitNavigation.LooksLikeSha(new string('f', 40)).Should().BeTrue();
    }

    [Fact]
    public void Section_is_registered_for_the_git_tab()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddGitPresentation();

        services.Select(d => d.ImplementationInstance).OfType<WorkspaceSectionRegistration>()
            .Should().ContainSingle(r => r.Section == WorkspaceSection.Git && r.ViewModelType == typeof(GitSectionViewModel));
        services.Should().Contain(d => d.ServiceType == typeof(IPaletteSource) && d.ImplementationType == typeof(GitPaletteSource));
    }
}
