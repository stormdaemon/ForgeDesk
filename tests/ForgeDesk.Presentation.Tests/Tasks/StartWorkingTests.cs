using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Tasks.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Tasks;

public sealed class StartWorkingTests : IDisposable
{
    private const string Branch = "task/12-fix-login-redirect";
    private readonly TasksHarness _h = new();

    public StartWorkingTests()
    {
        _h.WorkItems.AddLinkAsync("w12", WorkItemLinkKind.Branch, Arg.Any<string>(), null, Arg.Any<CancellationToken>())
            .Returns(call => _h.Replace(_h.Item(12, "Fix login redirect!", links: [new WorkItemLink("l", WorkItemLinkKind.Branch, (string)call[2], null, DateTimeOffset.Now)])));
        _h.WorkItems.MoveAsync("w12", WorkItemStatus.InProgress, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _h.Replace(_h.Items.Single(i => i.Id == "w12") with { Status = WorkItemStatus.InProgress }));
    }

    public void Dispose() => _h.Dispose();

    private async Task<(TasksSectionViewModel Section, TaskCardViewModel Card)> CreateAsync(GitStatus? status = null)
    {
        _h.Workspace.CurrentStatus = status ?? TestData.Status();
        _h.Items.Add(_h.Item(12, "Fix login redirect!"));
        _h.Items.Add(_h.Item(3, "Already in progress", WorkItemStatus.InProgress));
        var section = await _h.CreateAsync();
        return (section, section.Columns[(int)WorkItemStatus.Todo].Cards.Single());
    }

    [Fact]
    public void Branch_names_are_task_number_and_a_slug_of_the_title()
    {
        var item = new WorkItem { Id = "x", ProjectId = "p", Number = 12, Title = "Fix login redirect!" };
        TaskBoardMath.BranchName(item).Should().Be(Branch);
        TaskBoardMath.BranchName(item with { Title = "Écran d'accueil: ça marche" }).Should().Be("task/12-ecran-d-accueil-ca-marche");
        TaskBoardMath.BranchName(item with { Title = "!!!" }).Should().Be("task/12");
        TaskBoardMath.Slug(new string('a', 30) + " " + new string('b', 30)).Should().Be(new string('a', 30));
        TaskBoardMath.Slug("Upgrade to .NET 10 / C# 14").Should().Be("upgrade-to-net-10-c-14");
        ForgeDesk.Presentation.Workspace.BranchNames.Validate(TaskBoardMath.BranchName(item with { Title = "a..b @{x} ~^:?*[" })).Should().BeNull();
    }

    [Fact]
    public async Task On_a_clean_tree_it_creates_and_checks_out_the_branch_links_it_and_moves_the_task()
    {
        var (section, card) = await CreateAsync();

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        await _h.Git.Received(1).CreateBranchAsync(_h.Context.Root, Branch, null, true, Arg.Any<CancellationToken>());
        await _h.Git.DidNotReceiveWithAnyArgs().StashAsync(default!, default, default, default);
        await _h.WorkItems.Received(1).AddLinkAsync("w12", WorkItemLinkKind.Branch, Branch, null, Arg.Any<CancellationToken>());
        await _h.WorkItems.Received(1).MoveAsync("w12", WorkItemStatus.InProgress, null, "w3", Arg.Any<CancellationToken>());
        await _h.Workspace.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.GitBranchCreated && e.RefValue == "w12" && e.Title.Contains("#12")), Arg.Any<CancellationToken>());
        _h.Workspace.Notifications.Received(1).Show("Working on #12", Arg.Is<string>(m => m.Contains(Branch)), NotificationSeverity.Success, null);
        section.Columns[(int)WorkItemStatus.InProgress].Cards.Select(c => c.Id).Should().Contain("w12");
        card.IsWorking.Should().BeFalse();
    }

    [Fact]
    public async Task An_existing_branch_is_checked_out_instead_of_created()
    {
        _h.Branches.Add(TestData.LocalBranch(Branch));
        var (section, card) = await CreateAsync();

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        await _h.Git.Received(1).CheckoutAsync(_h.Context.Root, Branch, Arg.Any<CancellationToken>());
        await _h.Git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default!, default!, default, default, default);
        await _h.Workspace.Activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.GitCheckout), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Already_on_the_branch_it_only_links_and_moves()
    {
        _h.Branches.Add(TestData.LocalBranch(Branch, current: true));
        var (section, card) = await CreateAsync(TestData.Status(branch: Branch, entries: TestData.Modified("a.txt")));

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        await _h.Workspace.Dialogs.DidNotReceiveWithAnyArgs().ShowDialogAsync(default!);
        await _h.Git.DidNotReceiveWithAnyArgs().CheckoutAsync(default!, default!, default);
        await _h.WorkItems.Received(1).MoveAsync("w12", WorkItemStatus.InProgress, null, "w3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_uncommitted_changes_it_can_stash_then_switch()
    {
        var (section, card) = await CreateAsync(TestData.Status(entries: TestData.Modified("src/a.cs")));
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (DirtyTreeDialogViewModel)call[0];
            dialog.Branch.Should().Be(Branch);
            dialog.ChangedFiles.Should().Be(1);
            dialog.StashAndSwitchCommand.Execute(null);
            return true;
        });

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        Received.InOrder(() =>
        {
            _h.Git.StashAsync(_h.Context.Root, "ForgeDesk: before starting #12", true, Arg.Any<CancellationToken>());
            _h.Git.CreateBranchAsync(_h.Context.Root, Branch, null, true, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task With_uncommitted_changes_it_can_create_the_branch_without_switching()
    {
        var (section, card) = await CreateAsync(TestData.Status(entries: TestData.Modified("src/a.cs")));
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            ((DirtyTreeDialogViewModel)call[0]).CreateWithoutSwitchingCommand.Execute(null);
            return true;
        });

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        await _h.Git.Received(1).CreateBranchAsync(_h.Context.Root, Branch, null, false, Arg.Any<CancellationToken>());
        await _h.Git.DidNotReceiveWithAnyArgs().StashAsync(default!, default, default, default);
        await _h.WorkItems.Received(1).AddLinkAsync("w12", WorkItemLinkKind.Branch, Branch, null, Arg.Any<CancellationToken>());
        _h.Workspace.Notifications.Received(1).Show("Working on #12", Arg.Is<string>(m => m.Contains("still on main")), NotificationSeverity.Success, null);
    }

    [Fact]
    public async Task Cancelling_the_uncommitted_changes_dialog_does_nothing()
    {
        var (section, card) = await CreateAsync(TestData.Status(entries: TestData.Modified("src/a.cs")));
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(false);

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        await _h.Git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default!, default!, default, default, default);
        await _h.WorkItems.DidNotReceiveWithAnyArgs().AddLinkAsync(default!, default, default!, default, default);
        await _h.WorkItems.DidNotReceiveWithAnyArgs().MoveAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task Outside_a_repository_it_explains_why()
    {
        _h.Items.Add(_h.Item(12, "Fix login redirect!"));
        _h.Workspace.CurrentStatus = null;
        var section = await _h.CreateAsync();

        await section.StartWorkingOnCommand.ExecuteAsync(section.Columns[1].Cards.Single());

        _h.Workspace.Notifications.Received(1).Show("Not a Git repository", Arg.Any<string?>(), NotificationSeverity.Warning, null);
        await _h.Git.DidNotReceiveWithAnyArgs().GetBranchesAsync(default!, default, default);
    }

    [Fact]
    public async Task A_git_failure_is_reported_and_the_task_is_not_moved()
    {
        var (section, card) = await CreateAsync();
        _h.Git.CreateBranchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromException(new Core.Common.ForgeException(Core.Common.ErrorKind.GitCommandFailed, "Could not create the branch.")));

        await section.StartWorkingOnCommand.ExecuteAsync(card);

        _h.Workspace.Notifications.Received(1).ShowError(Arg.Is<Core.Common.ErrorInfo>(e => e.Title == "Could not start working on #12"), null);
        await _h.WorkItems.DidNotReceiveWithAnyArgs().MoveAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task Complete_moves_the_task_to_the_top_of_done()
    {
        _h.Items.AddRange([_h.Item(1, "Ship it", WorkItemStatus.Review), _h.Item(2, "Shipped", WorkItemStatus.Done, completed: DateTimeOffset.Now)]);
        var section = await _h.CreateAsync();
        _h.WorkItems.MoveAsync("w1", WorkItemStatus.Done, null, "w2", Arg.Any<CancellationToken>())
            .Returns(_ => _h.Replace(_h.Item(1, "Ship it", WorkItemStatus.Done, sortOrder: 1, completed: DateTimeOffset.Now)));

        await section.CompleteCardCommand.ExecuteAsync(section.Columns[(int)WorkItemStatus.Review].Cards.Single());

        await _h.WorkItems.Received(1).MoveAsync("w1", WorkItemStatus.Done, null, "w2", Arg.Any<CancellationToken>());
        section.Columns[(int)WorkItemStatus.Done].Cards.Select(c => c.Title).Should().Equal("Ship it", "Shipped");
    }
}
