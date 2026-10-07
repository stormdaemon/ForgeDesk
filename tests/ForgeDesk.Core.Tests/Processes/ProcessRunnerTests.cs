using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Processes;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Captures_stdout_stderr_and_exit_code()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create(OperatingSystem.IsWindows() ? "echo out & echo err 1>&2 & exit /b 3" : "echo out; echo err 1>&2; exit 3", dir.Path);
        var lines = new List<OutputLine>();

        var result = await ProcessRunner.Instance.RunAsync(spec, l => { lock (lines) { lines.Add(l); } }, TestContext.Current.CancellationToken);

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Should().Contain("out");
        result.StandardError.Should().Contain("err");
        lines.Should().Contain(l => l.Stream == OutputStream.StandardError && l.Text.Contains("err"));
    }

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30", dir.Path, timeout: TimeSpan.FromMilliseconds(500));

        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);

        result.TimedOut.Should().BeTrue();
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_throws_and_kills()
    {
        using var dir = new TempDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var spec = ShellCommand.Create(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30", dir.Path);

        var act = () => ProcessRunner.Instance.RunAsync(spec, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_executable_is_reported_as_tool_not_found()
    {
        var spec = new ProcessSpec { FileName = "definitely-not-a-real-tool-xyz" };
        var act = () => ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.ToolNotFound);
    }

    [Fact]
    public async Task Missing_working_directory_is_reported_as_path_not_found()
    {
        var spec = ShellCommand.Create("echo hi", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()));
        var act = () => ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Fact]
    public async Task Command_lines_with_quotes_are_passed_verbatim_to_the_shell()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create("echo \"hello world\"", dir.Path);
        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);
        result.StandardOutput.Should().Contain("hello world");
    }

    private static string LargeInput() => string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 20_000));

    [Fact]
    public async Task Cancelling_while_writing_stdin_kills_the_process()
    {
        using var dir = new TempDirectory();
        var output = Path.Combine(dir.Path, "out.txt");
        var command = OperatingSystem.IsWindows()
            ? "ping -n 3 127.0.0.1 > nul & findstr \"^\" > out.txt"
            : "sleep 2; cat > out.txt";
        var spec = ShellCommand.Create(command, dir.Path) with { StandardInput = LargeInput() };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var act = () => ProcessRunner.Instance.RunAsync(spec, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        File.Exists(output).Should().BeFalse("the process must not survive the cancellation and consume a truncated input");
    }

    [Fact]
    public async Task A_background_grandchild_keeping_the_pipes_open_does_not_turn_success_into_a_timeout()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create(
            OperatingSystem.IsWindows() ? "start /b \"\" ping -n 20 127.0.0.1 & echo hi" : "sleep 20 & echo hi; exit 0",
            dir.Path, timeout: TimeSpan.FromSeconds(10));

        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("hi");
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task The_timeout_also_covers_writing_standard_input()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30", dir.Path, timeout: TimeSpan.FromSeconds(1))
            with { StandardInput = new string('x', 4 * 1024 * 1024) };

        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);

        result.TimedOut.Should().BeTrue();
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_while_writing_standard_input_kills_the_process()
    {
        using var dir = new TempDirectory();
        var marker = dir.Combine("survived.txt");
        var spec = ShellCommand.Create(
            OperatingSystem.IsWindows() ? "ping -n 4 127.0.0.1 > nul & echo done > survived.txt" : "sleep 3; echo done > survived.txt",
            dir.Path) with { StandardInput = new string('x', 4 * 1024 * 1024) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var act = () => ProcessRunner.Instance.RunAsync(spec, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        File.Exists(marker).Should().BeFalse("the cancelled process tree must have been killed");
    }

    [Fact]
    public async Task Nul_separated_output_beyond_the_cap_keeps_the_records_that_fit_and_ends_with_the_marker()
    {
        using var repo = TestRepository.Create(withInitialCommit: false);
        for (var i = 0; i < 300; i++)
        {
            repo.WriteFile($"file-{i:D4}.txt", "x");
        }

        repo.Git("add", "-A");
        var spec = new ProcessSpec
        {
            FileName = "git",
            Arguments = ["ls-files", "-z"],
            WorkingDirectory = repo.Path,
            MaxCapturedChars = 2000,
        };

        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().StartWith("file-0000.txt\0file-0001.txt\0");
        result.StandardOutput.Should().EndWith("\0… [output truncated]\n");
        result.StandardOutput.Length.Should().BeLessThanOrEqualTo(2000 + 32);
    }

    [Fact]
    public async Task Truncation_marker_ends_with_a_line_feed_on_every_platform()
    {
        using var dir = new TempDirectory();
        var spec = ShellCommand.Create(
            OperatingSystem.IsWindows() ? "for /l %i in (1,1,200) do @echo line %i" : "for i in $(seq 1 200); do echo line $i; done",
            dir.Path) with { MaxCapturedChars = 100 };

        var result = await ProcessRunner.Instance.RunAsync(spec, TestContext.Current.CancellationToken);

        result.StandardOutput.Should().StartWith("line 1");
        result.StandardOutput.Should().EndWith("\n… [output truncated]\n");
        result.StandardOutput.Should().NotContain("\r");
    }
}
