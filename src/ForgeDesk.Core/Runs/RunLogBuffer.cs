namespace ForgeDesk.Core.Runs;

/// <summary>
/// Thread-safe ring buffer of the most recent output lines. Indexes grow monotonically for
/// the whole run so a viewer can ask "everything after line N" even after trimming.
/// </summary>
internal sealed class RunLogBuffer
{
    public const int DefaultCapacity = 10_000;

    private readonly RunLogLine[] _items;
    private readonly Lock _gate = new();
    private long _nextIndex;

    public RunLogBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new RunLogLine[capacity];
    }

    public int Capacity => _items.Length;

    /// <summary>Total number of lines ever added (also the index of the next line).</summary>
    public long TotalCount
    {
        get
        {
            lock (_gate)
            {
                return _nextIndex;
            }
        }
    }

    public RunLogLine Add(DateTimeOffset at, bool isError, string text)
    {
        lock (_gate)
        {
            var line = new RunLogLine(_nextIndex, at, isError, text);
            _items[_nextIndex % _items.Length] = line;
            _nextIndex++;
            return line;
        }
    }

    public IReadOnlyList<RunLogLine> GetFrom(long fromIndex)
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _nextIndex - _items.Length);
            var start = Math.Max(oldest, fromIndex);
            if (start >= _nextIndex)
            {
                return [];
            }

            var result = new RunLogLine[_nextIndex - start];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = _items[(start + i) % _items.Length];
            }

            return result;
        }
    }
}
