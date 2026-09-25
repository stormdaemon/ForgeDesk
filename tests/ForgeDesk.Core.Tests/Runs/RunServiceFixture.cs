using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Runs;

/// <summary>A RunService over a real database, with a registered project and a work folder.</summary>
public sealed class RunServiceFixture : IAsyncDisposable
{
    public const string ProjectId = "0192f0a1b2c3d4e5f60718293a4b5c6d";

    private RunServiceFixture(TestDatabase database, int historyPerProject, int bufferCapacity)
    {
        Database = database;
        Settings = Substitute.For<ISettingsService>();
        Settings.Current.Returns(AppSettings.Default with { RunHistoryPerProject = historyPerProject });
        Activity = Substitute.For<IActivityLog>();
        Service = new RunService(database.Database, database.Paths, Settings, Activity, SystemClock.Instance)
        {
            BufferCapacity = bufferCapacity,
            LogFlushInterval = TimeSpan.FromMilliseconds(50),
            OutputDrainTimeout = TimeSpan.FromSeconds(2),
        };
    }

    public TestDatabase Database { get; }

    public TempDirectory WorkDirectory { get; } = new("work");

    public ISettingsService Settings { get; }

    public IActivityLog Activity { get; }

    internal RunService Service { get; }

    public static async Task<RunServiceFixture> CreateAsync(int historyPerProject = 200, int bufferCapacity = RunLogBuffer.DefaultCapacity)
    {
        var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.Database.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO projects (id, name, path, added_at) VALUES (@id, 'Demo', @path, @at)",
                new { id = ProjectId, path = database.Paths.DataDirectory, at = DateTimeOffset.UtcNow });
        }

        return new RunServiceFixture(database, historyPerProject, bufferCapacity);
    }

    public RunRequest Request(string commandLine, string? workingDirectory = null, string label = "Test command") => new()
    {
        ProjectId = ProjectId,
        Label = label,
        CommandLine = commandLine,
        WorkingDirectory = workingDirectory ?? WorkDirectory.Path,
    };

    public async Task<RunRecord> RunToEndAsync(string commandLine)
    {
        var session = await Service.StartAsync(Request(commandLine), TestContext.Current.CancellationToken);
        return await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.CancelAllAsync();
        WorkDirectory.Dispose();
        Database.Dispose();
    }
}

/// <summary>Command lines that behave the same through cmd.exe and /bin/sh.</summary>
internal static class Shell
{
    public static string Then => OperatingSystem.IsWindows() ? " & " : "; ";

    public static string Sleep => OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";

    public static string Exit(int code) => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    public static string ToStderr(string text) => $"echo {text} 1>&2";

    public static string PrintFile(string path) => OperatingSystem.IsWindows() ? $"type \"{path}\"" : $"cat '{path}'";

    public static string EchoVariable(string name) => OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";
}
