using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Git.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class HistoryViewModelTests : IDisposable
{
    private readonly GitHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>A history of <paramref name="total"/> linear commits served page by page.</summary>
    private void ServeLinear(int total)
    {
        var all = GitData.Linear(total);
        _harness.Git.GetLogAsync(_harness.Root, Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<GitLogQuery>(1);
                return Task.FromResult<IReadOnlyList<GitCommit>>(all.Skip(query.Skip).Take(query.Take).ToList());
            });
        _harness.Git.GetCommitDetailsAsync(_harness.Root, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new GitCommitDetails(all.First(c => c.Sha == call.ArgAt<string>(1)), [])));
    }

    private async Task<HistoryViewModel> OpenAsync()
    {
        var section = await _harness.OpenAsync(GitView.History);
        section.History.SearchDelay = TimeSpan.Zero;
        return section.History;
    }

    [Fact]
    public async Task First_page_is_loaded_with_a_graph_and_the_first_commit_selected()
    {
        ServeLinear(3);

        var history = await OpenAsync();

        history.Commits.Select(c => c.Subject).Should().Equal("Commit 0", "Commit 1", "Commit 2");
        history.HasMore.Should().BeFalse();
        history.GraphWidth.Should().BeGreaterThan(0);
        history.Commits[1].Graph.Edges.Should().Contain(new GraphEdge(0, 0, 0, GraphEdgePart.Upper));
        history.SelectedCommit.Should().BeSameAs(history.Commits[0]);
        history.Details!.Revision.Should().Be(history.Commits[0].Sha);
        history.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Next_pages_load_on_demand()
    {
        ServeLinear(450);
        var history = await OpenAsync();
        history.Commits.Should().HaveCount(HistoryViewModel.PageSize);
        history.HasMore.Should().BeTrue();

        await history.LoadMoreCommand.ExecuteAsync(null);
        await history.LoadMoreCommand.ExecuteAsync(null);

        history.Commits.Should().HaveCount(450);
        history.HasMore.Should().BeFalse();
        await _harness.Git.Received(1).GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Skip == 400 && q.Take == 200), Arg.Any<CancellationToken>());
        history.Commits.Select(c => c.Sha).Should().OnlyHaveUniqueItems();
        history.Commits[300].Graph.Lane.Should().Be(0, "the graph continues across pages");
    }

    [Fact]
    public async Task Search_filters_by_message_or_author_and_hides_the_graph()
    {
        ServeLinear(5);
        var history = await OpenAsync();

        history.SearchText = "login";
        await Task.Yield();

        await _harness.Git.Received().GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Search == "login" && q.Skip == 0), Arg.Any<CancellationToken>());
        history.ShowGraph.Should().BeFalse();
        history.GraphWidth.Should().Be(0);
        history.HasFilters.Should().BeTrue();
    }

    [Fact]
    public async Task All_branches_toggle_is_remembered_and_reloads()
    {
        ServeLinear(2);
        var history = await OpenAsync();

        history.AllBranches = true;

        _harness.Preferences.HistoryAllBranches.Should().BeTrue();
        await _harness.Git.Received().GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.AllBranches), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_results_explain_how_to_clear_the_filters()
    {
        var history = await OpenAsync();
        history.IsEmpty.Should().BeTrue();
        history.EmptyTitle.Should().Be("No commits yet");

        history.PathFilter = "missing.txt";

        history.IsEmpty.Should().BeTrue();
        history.EmptyTitle.Should().Be("No matching commits");
        await history.ClearFiltersCommand.ExecuteAsync(null);
        history.PathFilter.Should().BeNull();
    }

    [Fact]
    public async Task Load_failure_shows_an_inline_error()
    {
        _harness.Git.GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Take == HistoryViewModel.PageSize), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitCommit>>(new ForgeException(ErrorKind.RepositoryLocked, "The repository is locked.")));

        var history = await OpenAsync();

        history.Error!.Message.Should().Be("The repository is locked.");
        history.IsEmpty.Should().BeFalse("the error panel replaces the empty state");
    }

    [Fact]
    public async Task Navigating_to_an_older_commit_loads_pages_until_it_is_found()
    {
        ServeLinear(600);
        var section = await _harness.OpenAsync(GitView.Changes);

        await section.NavigateToAsync(GitNavigation.History(GitData.Sha(510)));

        section.History.SelectedCommit!.Sha.Should().Be(GitData.Sha(510));
        section.History.Commits.Should().HaveCount(600);
    }

    [Fact]
    public async Task A_commit_missing_from_the_history_is_reported()
    {
        ServeLinear(3);
        var section = await _harness.OpenAsync(GitView.History);

        await section.NavigateToAsync(GitNavigation.History("abcdefabcdef"));

        _harness.Notifications.Received(1).Show("Commit not found", Arg.Any<string>(), NotificationSeverity.Warning, Arg.Any<NotificationAction?>());
        section.History.AllBranches.Should().BeTrue("the commit was also looked for on the other branches");
    }

    [Fact]
    public async Task Branch_history_walks_that_branch()
    {
        ServeLinear(2);
        var section = await _harness.OpenAsync(GitView.Branches);

        await section.NavigateToAsync(GitNavigation.BranchHistory("feature/x"));

        section.History.RevisionFilter.Should().Be("feature/x");
        await _harness.Git.Received().GetLogAsync(_harness.Root, Arg.Is<GitLogQuery>(q => q.Revision == "feature/x" && !q.AllBranches),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Selecting_a_commit_loads_its_files_and_the_first_diff()
    {
        ServeLinear(2);
        var sha = GitData.Sha(1);
        _harness.Git.GetCommitDetailsAsync(_harness.Root, sha, Arg.Any<CancellationToken>()).Returns(new GitCommitDetails(GitData.Commit(sha, "Commit 1"),
        [
            new GitFileChange("src/a.cs", null, GitFileState.Modified, 10, 2, false),
            new GitFileChange("img.png", null, GitFileState.Added, 0, 0, true),
        ]));
        _harness.Git.GetCommitFileDiffAsync(_harness.Root, sha, "src/a.cs", Arg.Any<CancellationToken>()).Returns(GitData.Diff("src/a.cs", "x"));
        var history = await OpenAsync();

        history.SelectedCommit = history.Commits[1];

        var details = history.Details!;
        details.Files.Select(f => f.Path).Should().Equal("src/a.cs", "img.png");
        details.StatsText.Should().Be("2 files changed · +10 −2");
        details.Files[0].AdditionsBarWidth.Should().Be(Math.Round(CommitFileViewModel.BarWidth * 10 / 12));
        details.Files[1].ChangesText.Should().Be("binary");
        details.SelectedFile.Should().BeSameAs(details.Files[0]);
        details.Diff!.Path.Should().Be("src/a.cs");
    }

    [Fact]
    public async Task Parents_link_to_their_commit()
    {
        ServeLinear(3);
        var history = await OpenAsync();

        await history.RevealCommitCommand.ExecuteAsync(history.Details!.Parents[0].Sha);

        history.SelectedCommit.Should().BeSameAs(history.Commits[1]);
    }

    [Fact]
    public async Task Commit_actions()
    {
        ServeLinear(1);
        var history = await OpenAsync();
        var row = history.Commits[0];
        _harness.Context.Project = _harness.Project with { GitHub = new GitHubRepoRef("acme", "forge-app") };

        history.CopyShaCommand.Execute(row);
        history.OpenOnGitHubCommand.Execute(row);
        _harness.Dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns("hotfix");
        await history.CreateBranchFromCommitCommand.ExecuteAsync(row);

        _harness.Shell.Received(1).CopyToClipboard(row.Sha);
        _harness.Shell.Received(1).OpenUrl($"https://github.com/acme/forge-app/commit/{row.Sha}");
        await _harness.Git.Received(1).CreateBranchAsync(_harness.Root, "hotfix", row.Sha, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creating_a_tag_on_a_commit_uses_the_dialog()
    {
        ServeLinear(1);
        var history = await OpenAsync();
        var row = history.Commits[0];
        _harness.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (CreateTagDialogViewModel)call.Arg<IDialogViewModel>();
            dialog.Name = "v1.0.0";
            dialog.Message = "First release";
            return Task.FromResult<bool?>(true);
        });

        await history.CreateTagAtCommitCommand.ExecuteAsync(row);

        await _harness.Git.Received(1).CreateTagAsync(_harness.Root, "v1.0.0", "First release", row.Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Ref_decorations_become_pills()
    {
        var pills = RefPill.Parse(["tag: v1.0", "origin/main", "HEAD -> main", "origin/HEAD", "feature"], ["origin"]);

        pills.Should().Equal(
            new RefPill("main", RefPillKind.CurrentBranch),
            new RefPill("feature", RefPillKind.Branch),
            new RefPill("origin/main", RefPillKind.RemoteBranch),
            new RefPill("v1.0", RefPillKind.Tag));
        RefPill.Parse(["HEAD"], []).Should().Equal(new RefPill("HEAD", RefPillKind.Head));
    }
}
