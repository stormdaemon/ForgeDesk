using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ForgeDesk.Presentation.Tests.Commands.Support.CommandsHarness;

namespace ForgeDesk.Presentation.Tests.Commands;

public sealed class RunNotificationsAndPaletteTests : IDisposable
{
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private readonly WorkspaceHarness _harness = new(WorkspaceSection.Commands, WorkspaceSection.Git);
    private AppSettings _current = AppSettings.Default;

    public RunNotificationsAndPaletteTests()
    {
        _settings.Current.Returns(_ => _current);
        _registry.GetAsync("p1", Arg.Any<CancellationToken>()).Returns(TestData.Project("forge-app", "/tmp/forge-app", id: "p1"));
    }

    public void Dispose() => _harness.Dispose();

    private RunNotificationsCoordinator Coordinator() =>
        new(_runs, _settings, _notifications, _registry, _actions, ImmediateDispatcher.Instance, NullLogger<RunNotificationsCoordinator>.Instance);

    private static RunRecord Record(RunStatus status, int? exitCode = null, string? errorSummary = null) => new()
    {
        Id = "run-1",
        ProjectId = "p1",
        Label = "test",
        CommandLine = "npm test",
        WorkingDirectory = "/tmp/forge-app",
        Status = status,
        StartedAt = TestData.Now,
        EndedAt = TestData.Now.AddSeconds(75),
        ExitCode = exitCode,
        LogPath = "/tmp/run-1.log",
        ErrorSummary = errorSummary,
    };

    private async Task CompleteAsync(RunNotificationsCoordinator coordinator, RunRecord record)
    {
        _runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_runs, new RunCompletedEventArgs(record));
        await coordinator.LastNotification;
    }

    [Fact]
    public async Task A_success_sends_only_a_windows_notification()
    {
        using var coordinator = Coordinator();

        await CompleteAsync(coordinator, Record(RunStatus.Succeeded, 0));

        _notifications.Received(1).ShowSystemNotification("test succeeded", "forge-app · finished in 1m 15s", "p1");
        _notifications.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task A_failure_also_shows_an_in_app_notification_whose_action_opens_the_run()
    {
        using var coordinator = Coordinator();
        NotificationAction? action = null;
        _notifications.Show(Arg.Any<string>(), Arg.Any<string?>(), NotificationSeverity.Error, Arg.Do<NotificationAction?>(a => action = a));

        await CompleteAsync(coordinator, Record(RunStatus.Failed, 1, "FAIL src/app.test.ts\nExpected 2\nReceived 3\nmore"));

        _notifications.Received(1).ShowSystemNotification("test failed", "forge-app · exit code 1 after 1m 15s", "p1");
        _notifications.Received(1).Show("test failed", Arg.Is<string>(m => m.Contains("Received 3") && !m.Contains("more")), NotificationSeverity.Error,
            Arg.Any<NotificationAction?>());
        action!.Label.Should().Be("View log");

        await action.Execute();
        await _actions.Received(1).OpenAsync("p1", WorkspaceSection.Commands, "run-1");
    }

    [Fact]
    public async Task Cancelled_runs_and_disabled_notifications_stay_silent()
    {
        using var coordinator = Coordinator();
        await CompleteAsync(coordinator, Record(RunStatus.Cancelled));

        _current = AppSettings.Default with { NotifyWhenRunCompletes = false };
        await CompleteAsync(coordinator, Record(RunStatus.Failed, 2));

        _notifications.DidNotReceiveWithAnyArgs().ShowSystemNotification(default!, default!, default);
        _notifications.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task A_disposed_coordinator_no_longer_listens()
    {
        var coordinator = Coordinator();
        coordinator.Dispose();

        _runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_runs, new RunCompletedEventArgs(Record(RunStatus.Failed, 1)));
        await coordinator.LastNotification;

        _notifications.DidNotReceiveWithAnyArgs().ShowSystemNotification(default!, default!, default);
    }

    [Fact]
    public async Task An_unknown_project_still_notifies()
    {
        using var coordinator = Coordinator();
        _registry.GetAsync("p1", Arg.Any<CancellationToken>()).Returns((Project?)null);

        await CompleteAsync(coordinator, Record(RunStatus.Succeeded, 0));

        _notifications.Received(1).ShowSystemNotification("test succeeded", Arg.Is<string>(m => m.StartsWith("ForgeDesk", StringComparison.Ordinal)), "p1");
    }

    // ----- Palette -------------------------------------------------------------------------

    [Fact]
    public async Task Palette_lists_the_commands_of_the_project_on_screen_and_runs_them()
    {
        var customStore = Substitute.For<ICustomCommandStore>();
        var build = Command("build", CommandCategory.Build);
        var custom = Command("Seed", CommandCategory.Other, "node seed.js", custom: true);
        customStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([custom]);
        _harness.Profile = new ProjectProfile { Commands = [build, Command("dev", CommandCategory.Dev)] };
        var workspace = await _harness.OpenWorkspaceAsync();
        var navigation = Substitute.For<INavigationService>();
        navigation.CurrentPage.Returns(workspace);
        var session = Substitute.For<IRunSession>();
        session.Id.Returns("run-42");
        _harness.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).Returns(session);
        var source = new CommandsPaletteSource(navigation, _harness.Runs, _harness.Notifications, NullLogger<CommandsPaletteSource>.Instance, customStore);

        var items = await source.GetItemsAsync(new PaletteQuery(string.Empty, workspace.ProjectId), TestContext.Current.CancellationToken);

        items.Select(i => i.Title).Should().BeEquivalentTo("Run: build", "Run: dev", "Run: Seed");
        items.Should().OnlyContain(i => i.Category == PaletteCategory.Command);
        var buildItem = items.Single(i => i.Title == "Run: build");
        buildItem.Keywords.Should().Contain("Build").And.Contain("package.json");
        buildItem.Subtitle.Should().Be("forge-app · npm run build");

        await buildItem.Execute();

        await _harness.Runs.Received(1).StartAsync(Arg.Is<RunRequest>(r => r.CommandId == build.Id && r.ProjectId == workspace.ProjectId), Arg.Any<CancellationToken>());
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Commands);
        _harness.Sections.Single(WorkspaceSection.Commands).NavigatedTo.Should().Equal("run-42");
    }

    [Fact]
    public async Task Palette_returns_nothing_outside_a_project_or_in_other_modes()
    {
        var customStore = Substitute.For<ICustomCommandStore>();
        var navigation = Substitute.For<INavigationService>();
        var source = new CommandsPaletteSource(navigation, _harness.Runs, _harness.Notifications, NullLogger<CommandsPaletteSource>.Instance, customStore);

        (await source.GetItemsAsync(new PaletteQuery("build", null), TestContext.Current.CancellationToken)).Should().BeEmpty();

        var workspace = await _harness.OpenWorkspaceAsync();
        navigation.CurrentPage.Returns(workspace);
        var tasksOnly = new PaletteQuery("build", null) { Categories = new HashSet<PaletteCategory> { PaletteCategory.Task } };
        (await source.GetItemsAsync(tasksOnly, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
