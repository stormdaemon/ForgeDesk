using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Files;

/// <summary>A row of the search results: a file header or one matching line.</summary>
public abstract class SearchResultRow : ObservableObject
{
    public abstract string RelativePath { get; }
}

/// <summary>Group header: the file and its match count.</summary>
public sealed partial class SearchFileRow : SearchResultRow
{
    public SearchFileRow(string relativePath)
    {
        Path = relativePath;
    }

    private string Path { get; }

    public override string RelativePath => Path;

    public string Name => FileLocation.NameOf(Path);

    public string Directory => FileLocation.ParentOf(Path);

    public bool HasDirectory => Directory.Length > 0;

    public string Icon => FileIcons.For(Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountText))]
    public partial int MatchCount { get; internal set; }

    public string MatchCountText => MatchCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>A matching line, split around the match so the view can highlight it.</summary>
public sealed class SearchMatchRow : SearchResultRow
{
    /// <summary>Characters of context kept before the match on long lines.</summary>
    internal const int ContextBefore = 48;

    /// <summary>Longest text shown after the match.</summary>
    internal const int MaxAfter = 240;

    public SearchMatchRow(SearchFileRow file, ContentMatch match)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(match);
        File = file;
        LineNumber = match.LineNumber;
        Column = match.Column;
        (Before, Match, After) = Split(match.LineText ?? string.Empty, match.Column, match.Length);
    }

    public SearchFileRow File { get; }

    public override string RelativePath => File.RelativePath;

    public int LineNumber { get; }

    /// <summary>1-based column of the match.</summary>
    public int Column { get; }

    public string Before { get; }

    public string Match { get; }

    public string After { get; }

    public string LineNumberText => LineNumber.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ToolTip => $"{File.RelativePath}:{LineNumber}:{Column}";

    /// <summary>
    /// Splits a line into the text before the match (leading whitespace trimmed, long prefixes
    /// shortened with an ellipsis), the match, and the rest (capped).
    /// </summary>
    internal static (string Before, string Match, string After) Split(string line, int column, int length)
    {
        var start = Math.Clamp(column - 1, 0, line.Length);
        var count = Math.Clamp(length, 0, line.Length - start);
        var before = line[..start].TrimStart();
        if (before.Length > ContextBefore)
        {
            before = "…" + before[^ContextBefore..];
        }

        var after = line[(start + count)..].TrimEnd();
        if (after.Length > MaxAfter)
        {
            after = after[..MaxAfter] + "…";
        }

        return (before, line.Substring(start, count), after);
    }
}

/// <summary>
/// "Search in files" (Ctrl+Shift+F): query with match case / whole word / regex toggles and a
/// "files to include" glob, results streamed as they are found and grouped by file, a summary
/// (matches, files, engine, duration, truncation) and cancellation.
/// </summary>
public sealed partial class FileSearchViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(80);

    private readonly ProjectContext _context;
    private readonly IContentSearchService _search;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<FileLocation, Task> _open;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SearchFileRow> _fileRows = new(PathUtil.Comparer);
    private List<ContentMatch> _pending = [];
    private CancellationTokenSource? _running;
    private int _version;
    private bool _flushScheduled;

    public FileSearchViewModel(ProjectContext context, IContentSearchService search, IUiDispatcher dispatcher, Func<FileLocation, Task> open)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _search = search;
        _dispatcher = dispatcher;
        _open = open;
    }

    /// <summary>Raised when the view should focus the query box (Ctrl+Shift+F, "Search in folder").</summary>
    public event EventHandler? FocusRequested;

    /// <summary>File headers and matching lines, in discovery order (one virtualized list).</summary>
    public ObservableCollection<SearchResultRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool IsRegex { get; set; }

    /// <summary>"Files to include": comma-separated globs ("*.cs, src/**").</summary>
    [ObservableProperty]
    public partial string IncludeGlob { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults), nameof(ShowIntro))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults), nameof(ShowIntro))]
    public partial bool HasSearched { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults), nameof(HasResults))]
    public partial int MatchCount { get; private set; }

    [ObservableProperty]
    public partial int FileCount { get; private set; }

    /// <summary>"42 matches in 7 files · git grep · 120 ms".</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    public partial string? TruncatedMessage { get; private set; }

    /// <summary>The query the results belong to (shown in the "no results" message).</summary>
    [ObservableProperty]
    public partial string? SearchedQuery { get; private set; }

    public bool HasResults => MatchCount > 0;

    public bool ShowNoResults => HasSearched && !IsSearching && MatchCount == 0 && Error is null;

    public bool ShowIntro => !HasSearched && !IsSearching && Error is null;

    /// <summary>The search in flight (tests wait on it).</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>Opens the panel scoped to a folder ("" for the whole project).</summary>
    public void ScopeTo(string? folder)
    {
        IncludeGlob = string.IsNullOrEmpty(folder) ? string.Empty : folder.TrimEnd('/') + "/**";
        RequestFocus();
    }

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync()
    {
        PendingSearch = RunSearchAsync();
        return PendingSearch;
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(IsSearching))]
    private void Cancel()
    {
        _running?.Cancel();
    }

    /// <summary>Clears the query and the results.</summary>
    [RelayCommand]
    private void Clear()
    {
        CancelRunning();
        _version++;
        Query = string.Empty;
        ResetResults();
        HasSearched = false;
        IsSearching = false;
        Error = null;
        OnPropertyChanged(nameof(ShowIntro));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    [RelayCommand]
    private Task OpenResultAsync(SearchResultRow? row) => row switch
    {
        SearchMatchRow match => _open(new FileLocation(match.RelativePath, match.LineNumber)),
        SearchFileRow file => _open(new FileLocation(file.RelativePath)),
        _ => Task.CompletedTask,
    };

    public void Dispose()
    {
        _version++;
        CancelRunning();
    }

    private async Task RunSearchAsync()
    {
        var pattern = Query;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        CancelRunning();
        var version = ++_version;
        CancellationTokenSource cts;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _running = cts;
        ResetResults();
        Error = null;
        SearchedQuery = pattern;
        IsSearching = true;
        HasSearched = true;
        Summary = "Searching…";
        var stopwatch = Stopwatch.StartNew();
        var query = new ContentSearchQuery
        {
            Pattern = pattern,
            IsRegex = IsRegex,
            MatchCase = MatchCase,
            WholeWord = WholeWord,
            PathFilter = string.IsNullOrWhiteSpace(IncludeGlob) ? null : IncludeGlob.Trim(),
        };

        try
        {
            var summary = await _search.SearchAsync(_context.Root, query, match => Enqueue(match, version), cts.Token).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            Flush(version);
            ApplySummary(summary);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            if (version == _version)
            {
                Flush(version);
                Summary = $"{MatchesText()} · cancelled after {Format.Duration(stopwatch.Elapsed)}";
            }
        }
        catch (Exception ex)
        {
            if (version == _version)
            {
                Flush(version);
                Summary = null;
                Error = ErrorInfo.From(ex, ex is ForgeException { Kind: ErrorKind.InvalidInput } ? "Invalid search" : "Search failed");
            }
        }
        finally
        {
            if (version == _version)
            {
                IsSearching = false;
                OnPropertyChanged(nameof(ShowNoResults));
                OnPropertyChanged(nameof(ShowIntro));
            }

            if (ReferenceEquals(_running, cts))
            {
                _running = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Called by the search engine (any thread): buffers matches and schedules a batched flush.</summary>
    private void Enqueue(ContentMatch match, int version)
    {
        bool schedule;
        lock (_gate)
        {
            if (version != _version)
            {
                return;
            }

            _pending.Add(match);
            schedule = !_flushScheduled;
            _flushScheduled = true;
        }

        if (schedule)
        {
            _ = FlushLaterAsync(version);
        }
    }

    private async Task FlushLaterAsync(int version)
    {
        try
        {
            await Task.Delay(FlushInterval).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => Flush(version)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Flush(int version)
    {
        List<ContentMatch> batch;
        lock (_gate)
        {
            if (version != _version)
            {
                return;
            }

            batch = _pending;
            _pending = [];
            _flushScheduled = false;
        }

        if (batch.Count == 0)
        {
            return;
        }

        foreach (var match in batch)
        {
            var path = match.RelativePath.Replace('\\', '/');
            if (!_fileRows.TryGetValue(path, out var file))
            {
                file = new SearchFileRow(path);
                _fileRows[path] = file;
                Rows.Add(file);
                FileCount++;
            }

            file.MatchCount++;

            // Keep a file's matches together even when the engine interleaves files.
            var insertAt = Rows.IndexOf(file) + file.MatchCount;
            if (insertAt >= Rows.Count)
            {
                Rows.Add(new SearchMatchRow(file, match));
            }
            else
            {
                Rows.Insert(insertAt, new SearchMatchRow(file, match));
            }
        }

        MatchCount += batch.Count;
        if (IsSearching)
        {
            Summary = $"{MatchesText()} so far…";
        }
    }

    private void ApplySummary(ContentSearchSummary summary)
    {
        IsTruncated = summary.IsTruncated;
        TruncatedMessage = summary.IsTruncated
            ? $"Showing the first {Format.Count(MatchCount, "match", "matches")}. Narrow the search or the files to include to see the rest."
            : null;
        Summary = $"{MatchesText()} · {summary.Engine} · {Format.Duration(summary.Duration)}";
    }

    private string MatchesText() =>
        $"{Format.Count(MatchCount, "match", "matches")} in {Format.Count(FileCount, "file")}";

    private void ResetResults()
    {
        lock (_gate)
        {
            _pending = [];
            _flushScheduled = false;
        }

        _fileRows.Clear();
        Rows.Clear();
        MatchCount = 0;
        FileCount = 0;
        IsTruncated = false;
        TruncatedMessage = null;
        Summary = null;
    }

    private void CancelRunning()
    {
        var running = _running;
        _running = null;
        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName == nameof(Error))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowNoResults)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowIntro)));
        }
    }
}
