using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;

namespace ForgeDesk.Core.Tests.GitHub;

public class ProgressReportingStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reading_to_the_end_reports_increasing_progress_up_to_the_total()
    {
        var data = new byte[1_000_000];
        var progress = new RecordingProgress<TransferProgress>();
        await using var stream = new ProgressReportingStream(new MemoryStream(data), data.Length, progress);

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy, 4096, Ct);

        copy.Length.Should().Be(data.Length);
        progress.Reports.Select(p => p.BytesTransferred).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        progress.Reports[^1].Should().Be(new TransferProgress(data.Length, data.Length));
        progress.Reports[^1].Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Reports_are_throttled_for_small_reads()
    {
        var data = new byte[4 * 1024 * 1024];
        var progress = new RecordingProgress<TransferProgress>();
        await using var stream = new ProgressReportingStream(new MemoryStream(data), data.Length, progress);

        await stream.CopyToAsync(Stream.Null, 1024, Ct);

        progress.Reports.Count.Should().BeLessThan(80, "4096 reads of 1 KiB must not produce 4096 UI updates");
        progress.Reports[^1].BytesTransferred.Should().Be(data.Length);
    }

    [Fact]
    public void Synchronous_reads_report_too()
    {
        var progress = new RecordingProgress<TransferProgress>();
        using var stream = new ProgressReportingStream(new MemoryStream(new byte[10]), 10, progress);

        var buffer = new byte[10];
        stream.ReadExactly(buffer);

        progress.Reports.Should().ContainSingle().Which.Should().Be(new TransferProgress(10, 10));
    }

    [Fact]
    public void Seeking_back_moves_the_reported_position()
    {
        var progress = new RecordingProgress<TransferProgress>();
        using var stream = new ProgressReportingStream(new MemoryStream(new byte[100]), 100, progress);
        stream.ReadExactly(new byte[100]);

        stream.Position = 0;

        stream.Position.Should().Be(0);
        progress.Reports[^1].Should().Be(new TransferProgress(0, 100));
    }

    [Fact]
    public void The_stream_is_read_only()
    {
        using var stream = new ProgressReportingStream(new MemoryStream(new byte[4]), 4, null);

        stream.CanRead.Should().BeTrue();
        stream.CanWrite.Should().BeFalse();
        stream.CanSeek.Should().BeTrue();
        stream.Length.Should().Be(4);
        stream.Invoking(s => s.Write([1], 0, 1)).Should().Throw<NotSupportedException>();
        stream.Invoking(s => s.SetLength(1)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Disposing_closes_the_inner_stream_unless_asked_not_to()
    {
        var owned = new MemoryStream(new byte[4]);
        var borrowed = new MemoryStream(new byte[4]);

        new ProgressReportingStream(owned, 4, null).Dispose();
        new ProgressReportingStream(borrowed, 4, null, leaveOpen: true).Dispose();

        owned.CanRead.Should().BeFalse();
        borrowed.CanRead.Should().BeTrue();
    }

    [Fact]
    public void Empty_transfers_have_zero_fraction()
    {
        new TransferProgress(0, 0).Fraction.Should().Be(0);
    }
}
