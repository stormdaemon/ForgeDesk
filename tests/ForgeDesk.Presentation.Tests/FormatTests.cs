using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tests;

public class FormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(600, "10 min ago")]
    [InlineData(3 * 3600, "3 hours ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(3 * 86400, "3 days ago")]
    public void RelativeTime_reads_naturally(int secondsAgo, string expected) =>
        Format.RelativeTime(Now.AddSeconds(-secondsAgo), Now).Should().Be(expected);

    [Fact]
    public void RelativeTime_handles_null() => Format.RelativeTime(null).Should().Be("never");

    [Fact]
    public void Duration_formats_minutes_and_seconds() =>
        Format.Duration(TimeSpan.FromSeconds(125)).Should().Be("2m 05s");
}
