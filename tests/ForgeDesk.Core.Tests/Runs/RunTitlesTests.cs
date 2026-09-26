using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class RunTitlesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static RunRecord Record(RunStatus status, TimeSpan? duration = null, int? exitCode = null) => new()
    {
        Id = "r1",
        ProjectId = "p1",
        Label = "npm run build",
        CommandLine = "npm run build",
        WorkingDirectory = "/src",
        LogPath = "/logs/r1.log",
        Status = status,
        StartedAt = Start,
        EndedAt = duration is { } d ? Start + d : null,
        ExitCode = exitCode,
    };

    [Theory]
    [InlineData(0.4, "0.4 s")]
    [InlineData(12.34, "12.3 s")]
    [InlineData(59.96, "59.9 s")]
    [InlineData(60, "1 min")]
    [InlineData(125, "2 min 5 s")]
    [InlineData(3600, "1 h")]
    [InlineData(3780, "1 h 3 min")]
    public void Formats_durations(double seconds, string expected)
    {
        RunTitles.FormatDuration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void Titles_describe_the_outcome()
    {
        RunTitles.Completed(Record(RunStatus.Succeeded, TimeSpan.FromSeconds(12.3), 0)).Should().Be("npm run build succeeded in 12.3 s");
        RunTitles.Completed(Record(RunStatus.Failed, TimeSpan.FromSeconds(1), 1)).Should().Be("npm run build failed (exit 1)");
        RunTitles.Completed(Record(RunStatus.Failed, TimeSpan.FromSeconds(1))).Should().Be("npm run build could not start");
        RunTitles.Completed(Record(RunStatus.Cancelled, TimeSpan.FromSeconds(1))).Should().Be("npm run build was cancelled");
        RunTitles.Completed(Record(RunStatus.Interrupted)).Should().Be("npm run build was interrupted");
    }
}
