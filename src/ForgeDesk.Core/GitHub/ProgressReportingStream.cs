namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Read-only wrapper that reports how far a consumer (the HTTP stack uploading a file) has read.
/// Reports are throttled to about every 0.5 % (at least 64 KiB) so a large upload doesn't flood the
/// UI thread; the final position is always reported. Seeking (a request replayed by the HTTP
/// stack) moves the reported position accordingly.
/// </summary>
internal sealed class ProgressReportingStream : Stream
{
    private const long MinimumReportInterval = 64 * 1024;

    private readonly Stream _inner;
    private readonly long _totalBytes;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly long _reportInterval;
    private readonly bool _leaveOpen;
    private long _position;
    private long _lastReported = -1;

    public ProgressReportingStream(Stream inner, long totalBytes, IProgress<TransferProgress>? progress, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _totalBytes = Math.Max(0, totalBytes);
        _progress = progress;
        _leaveOpen = leaveOpen;
        _reportInterval = Math.Max(MinimumReportInterval, _totalBytes / 200);
        _position = inner.CanSeek ? inner.Position : 0;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Advance(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Advance(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = _inner.Seek(offset, origin);
        Report(force: true);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("The stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Advance(int read)
    {
        _position += read;
        Report(force: read == 0 || _position >= _totalBytes);
    }

    private void Report(bool force)
    {
        if (_progress is null || _position == _lastReported)
        {
            return;
        }

        if (!force && _position - _lastReported < _reportInterval && _lastReported >= 0)
        {
            return;
        }

        _lastReported = _position;
        _progress.Report(new TransferProgress(_position, _totalBytes));
    }
}
