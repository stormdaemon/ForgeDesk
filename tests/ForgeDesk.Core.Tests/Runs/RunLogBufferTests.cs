using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class RunLogBufferTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assigns_monotonic_indexes()
    {
        var buffer = new RunLogBuffer(10);
        buffer.Add(At, false, "a").Index.Should().Be(0);
        buffer.Add(At, true, "b").Index.Should().Be(1);
        buffer.TotalCount.Should().Be(2);
        buffer.GetFrom(0).Select(l => l.Text).Should().Equal("a", "b");
        buffer.GetFrom(0)[1].IsError.Should().BeTrue();
    }

    [Fact]
    public void Keeps_only_the_most_recent_lines()
    {
        var buffer = new RunLogBuffer(100);
        for (var i = 0; i < 250; i++)
        {
            buffer.Add(At, false, $"line {i}");
        }

        var lines = buffer.GetFrom(0);
        lines.Should().HaveCount(100);
        lines[0].Index.Should().Be(150);
        lines[0].Text.Should().Be("line 150");
        lines[^1].Index.Should().Be(249);
        buffer.TotalCount.Should().Be(250);
    }

    [Fact]
    public void GetFrom_returns_what_remains_after_an_index()
    {
        var buffer = new RunLogBuffer(100);
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(At, false, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        buffer.GetFrom(7).Select(l => l.Index).Should().Equal(7, 8, 9);
        buffer.GetFrom(10).Should().BeEmpty();
        buffer.GetFrom(1000).Should().BeEmpty();
    }

    [Fact]
    public void Is_safe_under_concurrent_writers()
    {
        var buffer = new RunLogBuffer(50_000);
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1000; i++)
            {
                buffer.Add(At, false, "x");
            }
        });

        var lines = buffer.GetFrom(0);
        lines.Should().HaveCount(8000);
        lines.Select(l => l.Index).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }
}
