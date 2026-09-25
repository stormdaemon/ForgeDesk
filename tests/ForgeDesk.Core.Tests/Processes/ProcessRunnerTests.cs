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
}
