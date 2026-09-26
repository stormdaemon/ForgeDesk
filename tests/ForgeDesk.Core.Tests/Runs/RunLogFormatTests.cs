using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Tests.Runs;

public class RunLogFormatTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 9, 25, 10, 15, 30, 123, TimeSpan.FromHours(2)).AddTicks(4567);

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var line = new RunLogLine(42, At, true, "error: something   with  spaces ");

        var formatted = RunLogFormat.Format(line);
        RunLogFormat.TryParse(formatted, out var parsed).Should().BeTrue();

        formatted.Should().StartWith("42 2026-09-25T08:15:30.1234567Z E ");
        parsed.Index.Should().Be(42);
        parsed.At.Should().Be(At);
        parsed.IsError.Should().BeTrue();
        parsed.Text.Should().Be(line.Text);
    }

    [Fact]
    public void Empty_text_round_trips()
    {
        RunLogFormat.TryParse(RunLogFormat.Format(new RunLogLine(0, At, false, "")), out var parsed).Should().BeTrue();
        parsed.Text.Should().BeEmpty();
        parsed.IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("12 not-a-date O text")]
    [InlineData("12 2026-09-25T08:15:30Z X text")]
    public void Malformed_lines_are_kept_as_text(string raw)
    {
        RunLogFormat.TryParse(raw, out _).Should().BeFalse();

        var previous = new RunLogLine(5, At, true, "previous");
        var parsed = RunLogFormat.Parse(raw, previous);
        parsed.Index.Should().Be(6);
        parsed.Text.Should().Be(raw);
        parsed.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Writer_and_tail_reader_round_trip()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("sub", "run.log");
        using (var writer = RunLogWriter.Open(path, NullLogger.Instance))
        {
            for (var i = 0; i < 500; i++)
            {
                writer.Append(new RunLogLine(i, At, i % 7 == 0, $"line {i} ✓"));
            }
        }

        var all = await RunLogFormat.ReadTailAsync(path, 20_000, TestContext.Current.CancellationToken);
        all.Should().HaveCount(500);
        all[0].Text.Should().Be("line 0 ✓");
        all[0].IsError.Should().BeTrue();
        all[499].Index.Should().Be(499);

        var tail = await RunLogFormat.ReadTailAsync(path, 3, TestContext.Current.CancellationToken);
        tail.Select(l => l.Index).Should().Equal(497, 498, 499);
    }

    [Fact]
    public async Task Tail_reader_crosses_chunk_boundaries()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("big.log");
        using (var writer = RunLogWriter.Open(path, NullLogger.Instance))
        {
            for (var i = 0; i < 20_000; i++)
            {
                writer.Append(new RunLogLine(i, At, false, new string('x', i % 50)));
            }
        }

        var tail = await RunLogFormat.ReadTailAsync(path, 5_000, TestContext.Current.CancellationToken);
        tail.Should().HaveCount(5_000);
        tail[0].Index.Should().Be(15_000);
        tail[^1].Index.Should().Be(19_999);
    }

    [Fact]
    public async Task Missing_file_reads_as_empty()
    {
        using var dir = new TempDirectory();
        var lines = await RunLogFormat.ReadTailAsync(dir.Combine("missing.log"), 100, TestContext.Current.CancellationToken);
        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Writer_flushes_periodically_while_open()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("live.log");
        using var writer = RunLogWriter.Open(path, NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        writer.Append(new RunLogLine(0, At, false, "hello"));

        IReadOnlyList<RunLogLine> lines = [];
        for (var attempt = 0; attempt < 100 && lines.Count == 0; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            lines = await RunLogFormat.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);
        }

        lines.Should().ContainSingle().Which.Text.Should().Be("hello");
    }

    [Fact]
    public async Task Writer_stops_at_its_size_limit_with_a_notice()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("capped.log");
        using (var writer = RunLogWriter.Open(path, NullLogger.Instance, maxBytes: 2_000))
        {
            for (var i = 0; i < 1_000; i++)
            {
                writer.Append(new RunLogLine(i, At, false, "some output line"));
            }
        }

        new FileInfo(path).Length.Should().BeLessThan(3_000);
        var lines = await RunLogFormat.ReadTailAsync(path, 10_000, TestContext.Current.CancellationToken);
        lines[^1].Text.Should().Contain("size limit");
    }

    [Fact]
    public void Writer_on_an_unusable_path_is_disabled_instead_of_throwing()
    {
        using var dir = new TempDirectory();
        var blocker = dir.WriteFile("file.txt", "not a folder");
        using var writer = RunLogWriter.Open(Path.Combine(blocker, "run.log"), NullLogger.Instance);

        var act = () =>
        {
            writer.Append(new RunLogLine(0, At, false, "ignored"));
            writer.Flush();
        };
        act.Should().NotThrow();
    }
}
