using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Runs;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Runs;

public class RunServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Successful_run_is_streamed_persisted_and_journaled()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var started = new List<IRunSession>();
        var completed = new List<RunRecord>();
        fx.Service.RunStarted += (_, s) => started.Add(s);
        fx.Service.RunCompleted += (_, e) => completed.Add(e.Record);

        var session = await fx.Service.StartAsync(fx.Request("echo hello"), Ct);
        var received = new List<RunLogLine>();
        session.LineReceived += (_, l) => { lock (received) { received.Add(l); } };
        var record = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        record.Status.Should().Be(RunStatus.Succeeded);
        record.ExitCode.Should().Be(0);
        record.EndedAt.Should().NotBeNull();
        record.ErrorSummary.Should().BeNull();
        record.LineCount.Should().Be(1);
        session.Status.Should().Be(RunStatus.Succeeded);
        session.GetLines().Should().ContainSingle().Which.Text.Trim().Should().Be("hello");

        started.Should().ContainSingle().Which.Should().BeSameAs(session);
        completed.Should().ContainSingle().Which.Id.Should().Be(session.Id);
        fx.Service.ActiveRuns.Should().BeEmpty();

        var persisted = await fx.Service.GetAsync(session.Id, Ct);
        persisted!.Status.Should().Be(RunStatus.Succeeded);
        persisted.LineCount.Should().Be(1);
        persisted.LogPath.Should().Be(Path.Combine(fx.Database.Paths.RunLogsDirectory, RunServiceFixture.ProjectId, session.Id + ".log"));
        File.Exists(persisted.LogPath).Should().BeTrue();

        await fx.Activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.RunCompleted
                && e.Outcome == ActivityOutcome.Success
                && e.Title.StartsWith("Test command succeeded in ", StringComparison.Ordinal)
                && e.RefKind == "run"
                && e.RefValue == session.Id
                && e.ProjectId == RunServiceFixture.ProjectId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failing_run_reports_exit_code_stderr_and_summary()
    {
        await using var fx = await RunServiceFixture.CreateAsync();

        var record = await fx.RunToEndAsync($"echo working{Shell.Then}{Shell.ToStderr("error: boom")}{Shell.Then}{Shell.Exit(3)}");

        record.Status.Should().Be(RunStatus.Failed);
        record.ExitCode.Should().Be(3);
        record.ErrorSummary.Should().Contain("error: boom");

        var lines = await fx.Service.ReadLogAsync(record.Id, cancellationToken: Ct);
        lines.Should().Contain(l => l.IsError && l.Text.Contains("error: boom", StringComparison.Ordinal));
        lines.Should().Contain(l => !l.IsError && l.Text.Contains("working", StringComparison.Ordinal));

        await fx.Activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Outcome == ActivityOutcome.Failure && e.Title == "Test command failed (exit 3)" && e.Detail!.Contains("boom")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_run_without_recognizable_errors_summarizes_the_last_lines()
    {
        await using var fx = await RunServiceFixture.CreateAsync();

        var record = await fx.RunToEndAsync($"echo first{Shell.Then}echo last words{Shell.Then}{Shell.Exit(1)}");

        record.ErrorSummary.Should().Contain("first").And.Contain("last words");
    }

    [Fact]
    public async Task Cancel_kills_the_process_and_marks_the_run_cancelled()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var session = await fx.Service.StartAsync(fx.Request($"echo started{Shell.Then}{Shell.Sleep}"), Ct);
        await WaitUntilAsync(() => session.LineCount > 0);
        fx.Service.ActiveRuns.Should().ContainSingle().Which.Should().BeSameAs(session);
        fx.Service.FindActive(session.Id).Should().BeSameAs(session);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await fx.Service.CancelAsync(session.Id);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        session.Completion.IsCompleted.Should().BeTrue();
        var record = await session.Completion;
        record.Status.Should().Be(RunStatus.Cancelled);
        record.ErrorSummary.Should().BeNull();
        session.GetLines()[^1].Text.Should().Be("Command cancelled.");
        fx.Service.ActiveRuns.Should().BeEmpty();
        (await fx.Service.GetAsync(session.Id, Ct))!.Status.Should().Be(RunStatus.Cancelled);
        await fx.Activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Outcome == ActivityOutcome.Warning && e.Title == "Test command was cancelled"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAll_stops_every_active_run()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var first = await fx.Service.StartAsync(fx.Request(Shell.Sleep), Ct);
        var second = await fx.Service.StartAsync(fx.Request(Shell.Sleep), Ct);

        await fx.Service.CancelAllAsync();

        (await first.Completion).Status.Should().Be(RunStatus.Cancelled);
        (await second.Completion).Status.Should().Be(RunStatus.Cancelled);
        fx.Service.ActiveRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_working_directory_fails_with_an_explanation()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var missing = fx.WorkDirectory.Combine("does-not-exist");

        var session = await fx.Service.StartAsync(fx.Request("echo never", missing), Ct);
        var record = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        record.Status.Should().Be(RunStatus.Failed);
        record.ExitCode.Should().BeNull();
        record.ErrorSummary.Should().Contain("does not exist");
        var log = await fx.Service.ReadLogAsync(record.Id, cancellationToken: Ct);
        log.Should().Contain(l => l.IsError && l.Text.StartsWith("Could not start the command", StringComparison.Ordinal));
        await fx.Activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Outcome == ActivityOutcome.Failure && e.Title == "Test command could not start"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("   ", "/abs")]
    [InlineData("echo hi", "relative/path")]
    public async Task Invalid_requests_are_rejected(string commandLine, string workingDirectory)
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var request = fx.Request(commandLine) with
        {
            WorkingDirectory = workingDirectory == "/abs" ? fx.WorkDirectory.Path : workingDirectory,
        };

        var act = () => fx.Service.StartAsync(request, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Project_id_cannot_escape_the_logs_folder()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var act = () => fx.Service.StartAsync(fx.Request("echo hi") with { ProjectId = ".." }, Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Large_output_trims_the_memory_buffer_but_keeps_the_full_log()
    {
        await using var fx = await RunServiceFixture.CreateAsync(bufferCapacity: 1_000);
        var file = fx.WorkDirectory.WriteFile("big.txt", string.Concat(Enumerable.Range(0, 3_000).Select(i => $"line {i}\n")));

        var session = await fx.Service.StartAsync(fx.Request(Shell.PrintFile(file)), Ct);
        var record = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        record.LineCount.Should().Be(3_000);
        var buffered = session.GetLines();
        buffered.Should().HaveCount(1_000);
        buffered[0].Index.Should().Be(2_000);
        buffered[0].Text.Should().Be("line 2000");
        session.GetLines(2_995).Select(l => l.Text).Should().Equal("line 2995", "line 2996", "line 2997", "line 2998", "line 2999");

        var full = await fx.Service.ReadLogAsync(record.Id, cancellationToken: Ct);
        full.Should().HaveCount(3_000);
        full.Select(l => l.Index).Should().Equal(Enumerable.Range(0, 3_000).Select(i => (long)i));
        full[1234].Text.Should().Be("line 1234");

        var tail = await fx.Service.ReadLogAsync(record.Id, maxLines: 10, cancellationToken: Ct);
        tail.Should().HaveCount(10);
        tail[0].Text.Should().Be("line 2990");
    }

    [Fact]
    public async Task Log_file_is_readable_while_the_command_is_running()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var session = await fx.Service.StartAsync(fx.Request($"echo early output{Shell.Then}{Shell.Sleep}"), Ct);

        IReadOnlyList<RunLogLine> lines = [];
        await WaitUntilAsync(async () =>
        {
            lines = await fx.Service.ReadLogAsync(session.Id, cancellationToken: Ct);
            return lines.Count > 0;
        });

        lines[0].Text.Trim().Should().Be("early output");
        (await fx.Service.GetAsync(session.Id, Ct))!.Status.Should().Be(RunStatus.Running);
        await fx.Service.CancelAsync(session.Id);
    }

    [Fact]
    public async Task Progress_escape_codes_and_redraws_are_interpreted()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var file = fx.WorkDirectory.WriteFile("out.txt",
            "\u001b[32m[ 25%] compiling\u001b[0m\nDownloading 10%\rDownloading 50%\rDownloading 100%\n[ 75%] linking\ndone\n");

        var session = await fx.Service.StartAsync(fx.Request(Shell.PrintFile(file)), Ct);
        var progressEvents = 0;
        session.ProgressChanged += (_, _) => Interlocked.Increment(ref progressEvents);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        session.GetLines().Select(l => l.Text).Should().Equal("[ 25%] compiling", "Downloading 100%", "[ 75%] linking", "done");
        session.Progress.Should().BeApproximately(0.75, 0.0001);
        session.LastOutputAt.Should().BeOnOrAfter(session.StartedAt);
    }

    [Fact]
    public async Task Environment_defaults_and_overrides_reach_the_command()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var request = fx.Request($"{Shell.EchoVariable("NO_COLOR")}{Shell.Then}{Shell.EchoVariable("FORGEDESK_TEST_VALUE")}") with
        {
            Environment = new Dictionary<string, string?> { ["FORGEDESK_TEST_VALUE"] = "from-request" },
        };

        var session = await fx.Service.StartAsync(request, Ct);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        session.GetLines().Select(l => l.Text.Trim()).Should().Equal("1", "from-request");
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_break_the_run()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        fx.Service.RunStarted += (_, s) => s.LineReceived += (_, _) => throw new InvalidOperationException("subscriber bug");

        var record = await fx.RunToEndAsync($"echo a{Shell.Then}echo b");

        record.Status.Should().Be(RunStatus.Succeeded);
        record.LineCount.Should().Be(2);
    }

    [Fact]
    public async Task History_is_newest_first_and_pruned_to_the_configured_size()
    {
        await using var fx = await RunServiceFixture.CreateAsync(historyPerProject: 2);

        var first = await fx.RunToEndAsync("echo one");
        var second = await fx.RunToEndAsync("echo two");
        var third = await fx.RunToEndAsync("echo three");

        var history = await fx.Service.GetHistoryAsync(RunServiceFixture.ProjectId, cancellationToken: Ct);
        history.Select(r => r.Id).Should().Equal(third.Id, second.Id);
        File.Exists(first.LogPath).Should().BeFalse("pruned runs lose their log file too");
        File.Exists(third.LogPath).Should().BeTrue();
        (await fx.Service.GetAsync(first.Id, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteHistory_removes_rows_and_log_files()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var record = await fx.RunToEndAsync("echo bye");

        await fx.Service.DeleteHistoryAsync(RunServiceFixture.ProjectId, Ct);

        (await fx.Service.GetHistoryAsync(RunServiceFixture.ProjectId, cancellationToken: Ct)).Should().BeEmpty();
        File.Exists(record.LogPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteHistory_never_deletes_files_outside_the_logs_folder()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var outside = fx.WorkDirectory.WriteFile("precious.txt", "keep me");
        await using (var connection = await fx.Database.Database.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO runs (id, project_id, label, command_line, working_dir, category, status, started_at, log_path)
                VALUES ('evil', @project, 'x', 'x', '/', 0, 2, @at, @path)
                """,
                new { project = RunServiceFixture.ProjectId, at = DateTimeOffset.UtcNow, path = outside });
        }

        await fx.Service.DeleteHistoryAsync(RunServiceFixture.ProjectId, Ct);

        File.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public async Task Interrupted_runs_are_recovered_at_startup()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        await using (var connection = await fx.Database.Database.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO runs (id, project_id, label, command_line, working_dir, category, status, started_at, log_path)
                VALUES ('stale-running', @project, 'dev', 'npm run dev', '/', 1, 1, @at, '/x.log'),
                       ('stale-queued', @project, 'test', 'npm test', '/', 3, 0, @at, '/y.log'),
                       ('finished', @project, 'build', 'npm run build', '/', 2, 2, @at, '/z.log')
                """,
                new { project = RunServiceFixture.ProjectId, at = DateTimeOffset.UtcNow.AddMinutes(-5) });
        }

        var recovered = await fx.Service.RecoverInterruptedRunsAsync(Ct);

        recovered.Should().Be(2);
        var running = await fx.Service.GetAsync("stale-running", Ct);
        running!.Status.Should().Be(RunStatus.Interrupted);
        running.EndedAt.Should().NotBeNull();
        running.ErrorSummary.Should().Be("ForgeDesk was closed while this command was running.");
        running.Category.Should().Be(ForgeDesk.Core.Detection.CommandCategory.Dev);
        (await fx.Service.GetAsync("stale-queued", Ct))!.Status.Should().Be(RunStatus.Interrupted);
        (await fx.Service.GetAsync("finished", Ct))!.Status.Should().Be(RunStatus.Succeeded);
        (await fx.Service.RecoverInterruptedRunsAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Recovery_leaves_runs_of_this_session_alone()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var session = await fx.Service.StartAsync(fx.Request(Shell.Sleep), Ct);

        (await fx.Service.RecoverInterruptedRunsAsync(Ct)).Should().Be(0);
        (await fx.Service.GetAsync(session.Id, Ct))!.Status.Should().Be(RunStatus.Running);
        await fx.Service.CancelAsync(session.Id);
    }

    [Fact]
    public async Task Reading_the_log_of_an_unknown_run_is_not_found()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var act = () => fx.Service.ReadLogAsync("nope", cancellationToken: Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_finished_run_is_a_no_op()
    {
        await using var fx = await RunServiceFixture.CreateAsync();
        var record = await fx.RunToEndAsync("echo done");

        await fx.Service.CancelAsync(record.Id);
        await fx.Service.CancelAsync("unknown");

        (await fx.Service.GetAsync(record.Id, Ct))!.Status.Should().Be(RunStatus.Succeeded);
    }

    private static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20, Ct);
        }
    }
}
