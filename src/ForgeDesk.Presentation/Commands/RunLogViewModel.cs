using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Commands;

/// <summary>
/// The output of one run: a bounded, virtualized list of lines that follows a live session
/// (lines arrive on background threads and are appended in batches so fast output stays smooth),
/// a search with next / previous, and the "follow output" state the view keeps in sync with its
/// scroll position.
/// </summary>
public sealed partial class RunLogViewModel : ObservableObject, IDisposable
{
    /// <summary>Lines kept on screen; older ones stay in the log file.</summary>
    public const int MaxLines = 50_000;

    private const int TrimChunk = 5_000;

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _batchInterval;
    private readonly ConcurrentQueue<RunLogLine> _pending = new();
    private readonly List<LogLineViewModel> _matches = [];
    private IRunSession? _session;
    private long _lastIndex = -1;
    private int _flushScheduled;
    private bool _disposed;

    public RunLogViewModel(IUiDispatcher dispatcher, TimeSpan batchInterval)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _batchInterval = batchInterval;
    }

    /// <summary>Raised after lines were appended (the view scrolls to the end while following).</summary>
    public event EventHandler? LinesAppended;

    /// <summary>Raised when the view must scroll to the end (jump to latest).</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>Raised when the current search result changes: the view brings it into view.</summary>
    public event EventHandler<LogLineViewModel>? ScrollToLineRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineCountText), nameof(IsEmpty))]
    public partial ObservableCollection<LogLineViewModel> Lines { get; private set; } = [];

    /// <summary>The output is being read from the log file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Earlier lines are not shown (memory bound); the log file has them.</summary>
    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    /// <summary>Auto-scroll: true while the view is at the end. The view sets it from its scroll position.</summary>
    [ObservableProperty]
    public partial bool IsFollowing { get; set; } = true;

    /// <summary>The run is live: new lines may still arrive.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    public partial int MatchCount { get; private set; }

    /// <summary>0-based index of the current result, -1 when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    public partial int CurrentMatchIndex { get; private set; } = -1;

    public bool HasSearch => !string.IsNullOrEmpty(SearchText);

    /// <summary>"3 of 12", "No results".</summary>
    public string MatchSummary => !HasSearch ? string.Empty
        : MatchCount == 0 ? "No results"
        : $"{(CurrentMatchIndex + 1).ToString("N0", CultureInfo.CurrentCulture)} of {MatchCount.ToString("N0", CultureInfo.CurrentCulture)}";

    public string LineCountText => Format.Count(Lines.Count, "line");

    public bool IsEmpty => !IsLoading && Lines.Count == 0;

    public LogLineViewModel? CurrentMatch => CurrentMatchIndex >= 0 && CurrentMatchIndex < _matches.Count ? _matches[CurrentMatchIndex] : null;

    /// <summary>The whole output on screen, for "Copy output".</summary>
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>Shows the buffered output of a live session, then follows it.</summary>
    public void Attach(IRunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Detach();
        _session = session;
        IsLive = true;
        session.LineReceived += OnLineReceived;
        var buffered = session.GetLines(0);
        IsTruncated = buffered.Count > 0 && buffered[0].Index > 0;
        Append(buffered);
    }

    /// <summary>Stops following the session (the run finished or another run is shown).</summary>
    public void Detach()
    {
        if (_session is { } session)
        {
            session.LineReceived -= OnLineReceived;
            _session = null;
        }

        IsLive = false;
    }

    /// <summary>The run finished: takes the last lines still queued, then stops following.</summary>
    public void Complete()
    {
        FlushPendingLines();
        Detach();
    }

    /// <summary>Shows the output of a finished run, read from its log file.</summary>
    public async Task LoadAsync(Func<CancellationToken, Task<IReadOnlyList<RunLogLine>>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        Detach();
        IsLoading = true;
        try
        {
            var lines = await read(cancellationToken).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            Reset();
            IsTruncated = lines.Count > 0 && lines[0].Index > 0;
            Append(lines);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Appends every line received since the last flush (on the UI thread).</summary>
    public void FlushPendingLines()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        if (_disposed || _pending.IsEmpty)
        {
            return;
        }

        var batch = new List<RunLogLine>();
        while (_pending.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        Append(batch);
    }

    [RelayCommand]
    private void NextMatch() => MoveMatch(+1);

    [RelayCommand]
    private void PreviousMatch() => MoveMatch(-1);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void JumpToLatest()
    {
        IsFollowing = true;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        RecomputeMatches();
    }

    partial void OnLinesChanged(ObservableCollection<LogLineViewModel> value) => OnPropertyChanged(nameof(LineCountText));

    public void Dispose()
    {
        _disposed = true;
        Detach();
        _pending.Clear();
    }

    private void OnLineReceived(object? sender, RunLogLine line)
    {
        _pending.Enqueue(line);
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            _ = FlushLaterAsync();
        }
    }

    private async Task FlushLaterAsync()
    {
        if (_batchInterval == Timeout.InfiniteTimeSpan)
        {
            // Manual flushing (tests).
            return;
        }

        try
        {
            await Task.Delay(_batchInterval).ConfigureAwait(false);
            _dispatcher.Post(FlushPendingLines);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not append run output: {ex.Message}");
        }
    }

    private void Reset()
    {
        _lastIndex = -1;
        _matches.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        IsTruncated = false;
        Lines = [];
    }

    private void Append(IReadOnlyList<RunLogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var search = SearchText;
        var added = false;
        foreach (var line in lines)
        {
            if (line.Index <= _lastIndex)
            {
                // Already shown (the buffered lines and the live event can overlap).
                continue;
            }

            _lastIndex = line.Index;
            var row = new LogLineViewModel(line);
            if (search.Length > 0 && row.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                row.IsMatch = true;
                _matches.Add(row);
            }

            Lines.Add(row);
            added = true;
        }

        if (!added)
        {
            return;
        }

        if (Lines.Count > MaxLines + TrimChunk)
        {
            Trim();
        }

        MatchCount = _matches.Count;
        if (CurrentMatchIndex < 0 && _matches.Count > 0)
        {
            SetCurrentMatch(0, scroll: false);
        }

        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(IsEmpty));
        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the oldest lines in one step (one collection reset rather than thousands of removals).</summary>
    private void Trim()
    {
        var kept = Lines.Skip(Lines.Count - MaxLines).ToList();
        var current = CurrentMatch;
        var firstKept = kept[0].Index;
        _matches.RemoveAll(m => m.Index < firstKept);
        Lines = new ObservableCollection<LogLineViewModel>(kept);
        IsTruncated = true;
        var index = current is null ? -1 : _matches.IndexOf(current);
        if (index < 0 && current is not null)
        {
            current.IsCurrentMatch = false;
        }

        CurrentMatchIndex = index;
    }

    private void RecomputeMatches()
    {
        foreach (var match in _matches)
        {
            match.IsMatch = false;
            match.IsCurrentMatch = false;
        }

        _matches.Clear();
        var search = SearchText;
        if (search.Length > 0)
        {
            foreach (var line in Lines)
            {
                if (line.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    line.IsMatch = true;
                    _matches.Add(line);
                }
            }
        }

        MatchCount = _matches.Count;
        CurrentMatchIndex = -1;
        if (_matches.Count > 0)
        {
            SetCurrentMatch(0, scroll: true);
        }
    }

    private void MoveMatch(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var next = CurrentMatchIndex < 0 ? 0 : (CurrentMatchIndex + delta + _matches.Count) % _matches.Count;
        SetCurrentMatch(next, scroll: true);
    }

    private void SetCurrentMatch(int index, bool scroll)
    {
        if (CurrentMatch is { } previous)
        {
            previous.IsCurrentMatch = false;
        }

        CurrentMatchIndex = index;
        if (CurrentMatch is { } current)
        {
            current.IsCurrentMatch = true;
            if (scroll)
            {
                // Looking at a result stops the auto-scroll, otherwise new output would push it away.
                IsFollowing = false;
                ScrollToLineRequested?.Invoke(this, current);
            }
        }
    }
}
