using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Commands.Support;
using NSubstitute;
using static ForgeDesk.Presentation.Tests.Commands.Support.CommandsHarness;

namespace ForgeDesk.Presentation.Tests.Commands;

public sealed class CommandsSectionViewModelTests : IDisposable
{
    private readonly CommandsHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ----- Commands list ---------------------------------------------------------------

    [Fact]
    public async Task Commands_are_grouped_by_category_in_the_developer_order_with_custom_commands_merged()
    {
        _h.Custom.Add(Command("Start API", CommandCategory.Run, "dotnet run --project api", custom: true));
        var section = await _h.CreateAsync(
            Command("clean", CommandCategory.Clean),
            Command("test", CommandCategory.Test),
            Command("dev", CommandCategory.Dev),
            Command("build", CommandCategory.Build),
            Command("lint", CommandCategory.Lint));

        section.Entries.Select(e => e is CommandGroupHeader h ? $"[{h.Title}]" : ((CommandRowViewModel)e).Name)
            .Should().Equal("[Dev]", "dev", "[Build]", "build", "[Test]", "test", "[Lint]", "lint", "[Run]", "Start API", "[Clean]", "clean");
        section.CommandCount.Should().Be(6);
        section.ShowNoCommands.Should().BeFalse();
        section.Entries.OfType<CommandGroupHeader>().Single(h => h.Category == CommandCategory.Run).Count.Should().Be(1);
        section.Entries.OfType<CommandRowViewModel>().Single(r => r.Name == "Start API").Source.Should().Be("custom");
    }

    [Fact]
    public async Task No_commands_shows_the_empty_state()
    {
        var section = await _h.CreateAsync();

        section.ShowNoCommands.Should().BeTrue();
        section.Entries.Should().BeEmpty();
        section.NoCommandsDescription.Should().Contain("package.json").And.Contain("Makefile");
    }

    [Fact]
    public async Task Search_filters_by_name_command_line_and_source_and_hides_empty_groups()
    {
        var section = await _h.CreateAsync(
            Command("build", CommandCategory.Build, "cargo build", source: "Cargo.toml"),
            Command("test", CommandCategory.Test, "cargo test", source: "Cargo.toml"),
            Command("dev", CommandCategory.Dev, "npm run dev"));

        section.SearchText = "cargo";
        section.Entries.OfType<CommandRowViewModel>().Select(r => r.Name).Should().Equal("build", "test");
        section.Entries.OfType<CommandGroupHeader>().Select(h => h.Category).Should().Equal(CommandCategory.Build, CommandCategory.Test);

        section.SearchText = "nothing-matches";
        section.ShowNoMatches.Should().BeTrue();

        section.ClearSearchCommand.Execute(null);
        section.VisibleCommandCount.Should().Be(3);
    }

    [Fact]
    public async Task Navigating_to_a_category_filters_the_list_to_it()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build), Command("test", CommandCategory.Test));

        await section.NavigateToAsync(CommandCategory.Test);

        section.CategoryFilter.Should().Be(CommandCategory.Test);
        section.CategoryFilterText.Should().Be("Category: Test");
        section.Entries.OfType<CommandRowViewModel>().Select(r => r.Name).Should().Equal("test");
        section.SelectedCommand!.Name.Should().Be("test");

        await section.NavigateToAsync("build");
        section.CategoryFilter.Should().Be(CommandCategory.Build);

        section.ClearCategoryFilterCommand.Execute(null);
        section.Entries.OfType<CommandRowViewModel>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Profile_changes_rebuild_the_list_keeping_rows()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        var row = section.Entries.OfType<CommandRowViewModel>().Single();

        _h.Workspace.Profile = new ProjectProfile { Commands = [Command("build", CommandCategory.Build), Command("test", CommandCategory.Test)] };
        await _h.Context.RefreshProfileAsync();

        section.Entries.OfType<CommandRowViewModel>().Select(r => r.Name).Should().Equal("build", "test");
        section.Entries.OfType<CommandRowViewModel>().First().Should().BeSameAs(row);
    }

    // ----- Custom commands -------------------------------------------------------------

    [Fact]
    public async Task Add_command_saves_the_form_and_selects_the_new_command()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        Directory.CreateDirectory(Path.Combine(_h.Root, "api"));
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (CommandEditorDialogViewModel)call[0];
            dialog.Name = " Start API ";
            dialog.CommandLine = "dotnet run";
            dialog.SelectedCategory = CommandCategories.Options.Single(o => o.Category == CommandCategory.Run);
            dialog.WorkingDirectory = "./api/";
            return dialog.Validate() ? true : (bool?)false;
        });
        var added = Command("Start API", CommandCategory.Run, "dotnet run", workingDirectory: "api", custom: true);
        _h.CustomCommands.AddAsync(_h.ProjectId, "Start API", "dotnet run", CommandCategory.Run, "api", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _h.Custom.Add(added);
                return added;
            });

        await section.AddCommandCommand.ExecuteAsync(null);

        await _h.CustomCommands.Received(1).AddAsync(_h.ProjectId, "Start API", "dotnet run", CommandCategory.Run, "api", Arg.Any<CancellationToken>());
        section.SelectedCommand!.Name.Should().Be("Start API");
        section.SelectedCommand.HasWorkingDirectory.Should().BeTrue();
        _h.Workspace.Notifications.Received(1).Show("Added Start API", "dotnet run", NotificationSeverity.Success, null);
    }

    [Fact]
    public async Task Cancelled_editor_adds_nothing()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(false);

        await section.AddCommandCommand.ExecuteAsync(null);

        await _h.CustomCommands.DidNotReceiveWithAnyArgs().AddAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Editing_a_custom_command_updates_it()
    {
        var custom = Command("serve", CommandCategory.Run, "npx serve", custom: true);
        _h.Custom.Add(custom);
        var section = await _h.CreateAsync();
        var row = section.Entries.OfType<CommandRowViewModel>().Single();
        _h.Workspace.Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            var dialog = (CommandEditorDialogViewModel)call[0];
            dialog.IsEditing.Should().BeTrue();
            dialog.Name.Should().Be("serve");
            dialog.CommandLine = "npx serve -p 5000";
            return true;
        });

        await section.EditCommandCommand.ExecuteAsync(row);

        await _h.CustomCommands.Received(1).UpdateAsync(_h.ProjectId,
            Arg.Is<DetectedCommand>(c => c.Id == custom.Id && c.CommandLine == "npx serve -p 5000" && c.Category == CommandCategory.Run),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_custom_command_asks_for_a_destructive_confirmation()
    {
        var custom = Command("serve", CommandCategory.Run, "npx serve", custom: true);
        _h.Custom.Add(custom);
        var section = await _h.CreateAsync();
        var row = section.Entries.OfType<CommandRowViewModel>().Single();

        _h.Workspace.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);
        await section.DeleteCommandCommand.ExecuteAsync(row);
        await _h.CustomCommands.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default);

        _h.Workspace.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);
        _h.CustomCommands.When(s => s.DeleteAsync(_h.ProjectId, custom.Id, Arg.Any<CancellationToken>())).Do(_ => _h.Custom.Clear());
        await section.DeleteCommandCommand.ExecuteAsync(row);

        await _h.Workspace.Dialogs.Received().ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Delete command"));
        await _h.CustomCommands.Received(1).DeleteAsync(_h.ProjectId, custom.Id, Arg.Any<CancellationToken>());
        section.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Detected_commands_cannot_be_edited_or_deleted()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        var row = section.Entries.OfType<CommandRowViewModel>().Single();

        await section.EditCommandCommand.ExecuteAsync(row);
        await section.DeleteCommandCommand.ExecuteAsync(row);

        await _h.Workspace.Dialogs.DidNotReceiveWithAnyArgs().ShowDialogAsync(default!);
        await _h.Workspace.Dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!);
    }

    [Fact]
    public async Task Redetect_refreshes_the_profile()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        _h.Workspace.Profile = new ProjectProfile { Commands = [Command("build", CommandCategory.Build), Command("dev", CommandCategory.Dev)] };

        await section.RedetectCommand.ExecuteAsync(null);

        section.CommandCount.Should().Be(2);
        section.IsDetecting.Should().BeFalse();
    }

    // ----- Running ---------------------------------------------------------------------

    [Fact]
    public async Task Run_starts_the_command_in_its_working_directory_and_selects_the_run()
    {
        Directory.CreateDirectory(Path.Combine(_h.Root, "web"));
        var command = Command("dev", CommandCategory.Dev, workingDirectory: "web");
        var section = await _h.CreateAsync(command);
        var row = section.Entries.OfType<CommandRowViewModel>().Single();
        var session = _h.Session("run-1", command);
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).Returns(session);

        await section.RunOrStopCommand.ExecuteAsync(row);

        await _h.Runs.Received(1).StartAsync(Arg.Is<RunRequest>(r =>
            r.ProjectId == _h.ProjectId && r.CommandId == command.Id && r.Label == "dev" && r.Category == CommandCategory.Dev
            && r.WorkingDirectory == Path.Combine(_h.Root, "web")), Arg.Any<CancellationToken>());
        section.SelectedRun!.Id.Should().Be("run-1");
        section.Detail!.Run.IsRunning.Should().BeTrue();
        row.IsRunning.Should().BeTrue();
        row.RunButtonText.Should().Be("Stop");
        section.Runs.OfType<RunGroupHeader>().First().Title.Should().Be("Running");
    }

    [Fact]
    public async Task Run_on_a_running_command_stops_it()
    {
        var command = Command("dev", CommandCategory.Dev);
        var section = await _h.CreateAsync(command);
        var row = section.Entries.OfType<CommandRowViewModel>().Single();
        var session = _h.Session("run-1", command);
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).Returns(session);
        await section.RunOrStopCommand.ExecuteAsync(row);

        await section.RunOrStopCommand.ExecuteAsync(row);

        await _h.Runs.Received(1).CancelAsync("run-1");
        await _h.Runs.Received(1).StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_failure_is_reported_as_a_notification()
    {
        var section = await _h.CreateAsync(Command("dev", CommandCategory.Dev));
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>())
            .Returns<IRunSession>(_ => throw new Core.Common.ForgeException(Core.Common.ErrorKind.ToolNotFound, "npm is not installed."));

        await section.RunOrStopCommand.ExecuteAsync(section.Entries.OfType<CommandRowViewModel>().Single());

        _h.Workspace.Notifications.Received(1).ShowError(Arg.Is<Core.Common.ErrorInfo>(e => e.Message == "npm is not installed."), null);
        section.Error.Should().BeNull();
    }

    [Fact]
    public async Task Runs_started_elsewhere_appear_live_and_completion_updates_the_command()
    {
        var command = Command("test", CommandCategory.Test);
        var section = await _h.CreateAsync(command);
        var row = section.Entries.OfType<CommandRowViewModel>().Single();
        var session = _h.Session("run-9", command);

        _h.Runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_h.Runs, session);

        section.RunningCount.Should().Be(1);
        section.SelectedRun!.Id.Should().Be("run-9");
        row.LastResultTone.Should().Be(StatusTone.Running);

        _h.Runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_h.Runs,
            new RunCompletedEventArgs(_h.Record("run-9", RunStatus.Failed, command, exitCode: 1, errorSummary: "1 test failed")));

        section.RunningCount.Should().Be(0);
        row.IsRunning.Should().BeFalse();
        row.LastStatus.Should().Be(RunStatus.Failed);
        row.LastResultTone.Should().Be(StatusTone.Danger);
        section.SelectedRun.Status.Should().Be(RunStatus.Failed);
        section.SelectedRun.ExitCodeText.Should().Be("exit 1");
        section.SelectedRun.HasErrorSummary.Should().BeTrue();
        section.Runs.OfType<RunGroupHeader>().Select(h => h.Title).Should().Equal("History");
    }

    [Fact]
    public async Task A_run_started_while_the_tab_was_hidden_is_shown_when_the_tab_opens()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("old", RunStatus.Succeeded, build));
        var section = await _h.CreateAsync(build);
        section.SelectedRun!.Id.Should().Be("old");
        section.Deactivate();

        _h.Runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_h.Runs, _h.Session("from-header", build));
        section.SelectedRun.Id.Should().Be("old", "a hidden tab doesn't change its selection");

        await section.ActivateAsync();

        section.SelectedRun.Id.Should().Be("from-header");
    }

    [Fact]
    public async Task Runs_of_other_projects_are_ignored()
    {
        var section = await _h.CreateAsync(Command("test", CommandCategory.Test));
        var foreign = Substitute.For<IRunSession>();
        foreign.Id.Returns("other");
        foreign.Request.Returns(new RunRequest { ProjectId = "another-project", Label = "x", CommandLine = "x", WorkingDirectory = "." });

        _h.Runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_h.Runs, foreign);

        section.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task History_lists_active_runs_first_then_finished_ones_newest_first_and_sets_last_results()
    {
        var build = Command("build", CommandCategory.Build);
        var test = Command("test", CommandCategory.Test);
        _h.History.Add(_h.Record("old", RunStatus.Succeeded, build, DateTimeOffset.Now.AddHours(-2)));
        _h.History.Add(_h.Record("new", RunStatus.Failed, build, DateTimeOffset.Now.AddHours(-1)));
        _h.Workspace.ActiveRuns = [_h.Session("live", test)];

        var section = await _h.CreateAsync(build, test);

        section.Runs.Select(e => e is RunGroupHeader h ? $"[{h.Title}]" : ((RunItemViewModel)e).Id)
            .Should().Equal("[Running]", "live", "[History]", "new", "old");
        var buildRow = section.Entries.OfType<CommandRowViewModel>().Single(r => r.Name == "build");
        buildRow.LastStatus.Should().Be(RunStatus.Failed);
        section.Entries.OfType<CommandRowViewModel>().Single(r => r.Name == "test").IsRunning.Should().BeTrue();
        section.SelectedRun!.Id.Should().Be("live", "the most recent run is shown when the tab opens");
    }

    [Fact]
    public async Task A_history_load_failure_shows_the_error_panel_and_retry_recovers()
    {
        _h.Runs.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RunRecord>>(_ => throw new IOException("database locked"));
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        section.Error.Should().NotBeNull();

        _h.Runs.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        await section.RetryCommand.ExecuteAsync(null);

        section.Error.Should().BeNull();
        section.HasLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_the_history_confirms_and_keeps_running_runs()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("done", RunStatus.Succeeded, build));
        _h.Workspace.ActiveRuns = [_h.Session("live", build)];
        var section = await _h.CreateAsync(build);
        _h.Workspace.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);

        await section.ClearHistoryCommand.ExecuteAsync(null);

        await _h.Runs.Received(1).DeleteHistoryAsync(_h.ProjectId, Arg.Any<CancellationToken>());
        section.Runs.OfType<RunItemViewModel>().Select(r => r.Id).Should().Equal("live");
    }

    // ----- Run detail ------------------------------------------------------------------

    [Fact]
    public async Task A_live_run_shows_buffered_output_then_appends_new_lines_in_batches()
    {
        var command = Command("dev", CommandCategory.Dev);
        var session = _h.Session("live", command, lines: [Line(0, "starting"), Line(1, "listening on 3000")]);
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);
        var log = section.Detail!.Log;
        log.Lines.Select(l => l.Text).Should().Equal("starting", "listening on 3000");
        log.IsLive.Should().BeTrue();

        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(1, "listening on 3000"));
        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(2, "GET / 200"));
        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(3, "error: boom", isError: true));
        log.Lines.Should().HaveCount(2, "lines are appended in batches, not one by one");

        log.FlushPendingLines();

        log.Lines.Select(l => l.Text).Should().Equal("starting", "listening on 3000", "GET / 200", "error: boom");
        log.Lines[3].IsStdErr.Should().BeTrue();
        log.Lines[3].IsErrorLine.Should().BeTrue();
        log.LineCountText.Should().Be("4 lines");
    }

    [Fact]
    public async Task A_finished_run_reads_its_log_file()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("done", RunStatus.Succeeded, build));
        _h.Runs.ReadLogAsync("done", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Line(0, "compiling"), Line(1, "done in 2s")]);

        var section = await _h.CreateAsync(build);

        section.Detail!.Log.Lines.Select(l => l.Text).Should().Equal("compiling", "done in 2s");
        section.Detail.Log.IsLive.Should().BeFalse();
        section.Detail.Run.DurationText.Should().Be("42.0 s");
    }

    [Fact]
    public async Task Completion_of_the_shown_run_stops_following_and_keeps_the_output()
    {
        var command = Command("dev", CommandCategory.Dev);
        var session = _h.Session("live", command, lines: [Line(0, "a")]);
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);
        var detail = section.Detail!;
        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(1, "b"));

        _h.Runs.RunCompleted += Raise.Event<EventHandler<RunCompletedEventArgs>>(_h.Runs,
            new RunCompletedEventArgs(_h.Record("live", RunStatus.Succeeded, command, exitCode: 0)));

        section.Detail.Should().BeSameAs(detail);
        detail.Log.Lines.Select(l => l.Text).Should().Equal("a", "b");
        detail.Log.IsLive.Should().BeFalse();
        detail.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_without_output_for_the_stalled_threshold_shows_the_warning()
    {
        _h.Workspace.CurrentSettings = AppSettings.Default with { StalledRunWarningMinutes = 5 };
        var command = Command("deploy", CommandCategory.Deploy);
        var session = _h.Session("live", command);
        session.LastOutputAt.Returns(DateTimeOffset.Now.AddMinutes(-7));
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);

        section.TickAll();

        section.Detail!.IsStalled.Should().BeTrue();
        section.Detail.StalledMessage.Should().Be("No output for 7 minutes — the command may be waiting for input or stuck.");

        session.LastOutputAt.Returns(DateTimeOffset.Now);
        section.TickAll();
        section.Detail.IsStalled.Should().BeFalse();
    }

    [Fact]
    public async Task Stalled_warning_is_off_when_disabled_in_settings()
    {
        _h.Workspace.CurrentSettings = AppSettings.Default with { StalledRunWarningMinutes = 0 };
        var command = Command("deploy", CommandCategory.Deploy);
        var session = _h.Session("live", command);
        session.LastOutputAt.Returns(DateTimeOffset.Now.AddHours(-1));
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);

        section.TickAll();

        section.Detail!.IsStalled.Should().BeFalse();
    }

    [Fact]
    public async Task Running_durations_tick()
    {
        var command = Command("dev", CommandCategory.Dev);
        var session = _h.Session("live", command, startedAt: DateTimeOffset.Now.AddMinutes(-3));
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);

        section.TickAll();

        section.SelectedRun!.DurationText.Should().StartWith("3m ");
    }

    [Fact]
    public async Task Progress_reported_by_the_session_makes_the_bar_determinate()
    {
        var command = Command("install", CommandCategory.Install);
        var session = _h.Session("live", command);
        _h.Workspace.ActiveRuns = [session];
        var section = await _h.CreateAsync(command);
        section.SelectedRun!.HasProgress.Should().BeFalse();

        session.Progress.Returns(0.42);
        session.ProgressChanged += Raise.Event<EventHandler>(session, EventArgs.Empty);

        section.SelectedRun.HasProgress.Should().BeTrue();
        section.SelectedRun.ProgressPercent.Should().BeApproximately(42, 0.001);
    }

    [Fact]
    public async Task Run_again_starts_the_same_request()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("done", RunStatus.Failed, build));
        var section = await _h.CreateAsync(build);
        var session = _h.Session("again", build);
        _h.Runs.StartAsync(Arg.Any<RunRequest>(), Arg.Any<CancellationToken>()).Returns(session);

        await section.Detail!.RunAgainCommand.ExecuteAsync(null);

        await _h.Runs.Received(1).StartAsync(Arg.Is<RunRequest>(r => r.CommandId == build.Id && r.CommandLine == build.CommandLine), Arg.Any<CancellationToken>());
        section.SelectedRun!.Id.Should().Be("again");
    }

    [Fact]
    public async Task Copy_output_and_command_line_use_the_clipboard()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("done", RunStatus.Succeeded, build));
        _h.Runs.ReadLogAsync("done", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Line(0, "a"), Line(1, "b")]);
        var section = await _h.CreateAsync(build);

        section.Detail!.CopyOutputCommand.Execute(null);
        section.Detail.CopyCommandLineCommand.Execute(null);

        _h.Workspace.Shell.Received(1).CopyToClipboard("a" + Environment.NewLine + "b");
        _h.Workspace.Shell.Received(1).CopyToClipboard("npm run build");
    }

    [Fact]
    public async Task Navigating_to_a_run_id_selects_it_loading_it_from_history_when_needed()
    {
        var build = Command("build", CommandCategory.Build);
        _h.History.Add(_h.Record("recent", RunStatus.Succeeded, build));
        var section = await _h.CreateAsync(build);
        var older = _h.Record("older", RunStatus.Failed, build, DateTimeOffset.Now.AddDays(-30));
        _h.Runs.GetAsync("older", Arg.Any<CancellationToken>()).Returns(older);

        await section.NavigateToAsync("older");

        section.SelectedRun!.Id.Should().Be("older");
        section.Runs.OfType<RunItemViewModel>().Select(r => r.Id).Should().Contain("older");

        await section.NavigateToAsync("recent");
        section.SelectedRun!.Id.Should().Be("recent");
    }

    [Fact]
    public async Task Navigating_to_an_unknown_run_notifies()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        _h.Runs.GetAsync("gone", Arg.Any<CancellationToken>()).Returns((RunRecord?)null);

        await section.NavigateToAsync("gone");

        _h.Workspace.Notifications.Received(1).ShowError(Arg.Is<Core.Common.ErrorInfo>(e => e.Message.Contains("no longer in the history")), null);
    }

    [Fact]
    public async Task Disposing_unsubscribes_from_the_run_service()
    {
        var section = await _h.CreateAsync(Command("build", CommandCategory.Build));
        section.Dispose();

        _h.Runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_h.Runs, _h.Session("late"));

        section.RunCount.Should().Be(0);
    }
}
