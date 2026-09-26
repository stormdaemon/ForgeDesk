using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Palette;

/// <summary>
/// The command palette (Ctrl+K): queries every <see cref="IPaletteSource"/> in parallel (debounced,
/// older queries cancelled), ranks the results with fuzzy matching and shows them grouped by category,
/// with a "Recent" group when the query is empty. Keyboard: Up/Down, Enter, Esc.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject, IDisposable
{
    public const int MaxResults = 60;

    private static readonly PaletteCategory[] EmptyQueryCategoryOrder =
    [
        PaletteCategory.Project, PaletteCategory.Action, PaletteCategory.Navigation, PaletteCategory.Command,
        PaletteCategory.Branch, PaletteCategory.Task, PaletteCategory.File, PaletteCategory.Setting,
    ];

    private readonly IReadOnlyList<IPaletteSource> _sources;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private CancellationTokenSource? _search;
    private bool _opening;
    private bool _disposed;

    public CommandPaletteViewModel(
        IEnumerable<IPaletteSource> sources,
        INavigationService navigation,
        INotificationService notifications,
        ILogger<CommandPaletteViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Pause after the last keystroke before the sources are queried.</summary>
    public TimeSpan DebounceDelay { get; init; } = TimeSpan.FromMilliseconds(60);

    public PaletteRecents Recents { get; } = new();

    public IReadOnlyList<PalettePrefixHint> PrefixHints => PaletteQueryParser.Hints;

    public ObservableCollection<PaletteRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(ShowPrefixHints))]
    public partial PaletteMode Mode { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteSelectedCommand))]
    public partial PaletteResultViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    public partial bool IsSearching { get; private set; }

    /// <summary>True once results for the current query were applied (drives the "no results" message).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    public partial bool HasSearched { get; private set; }

    public string Placeholder => PaletteQueryParser.Placeholder(Mode);

    /// <summary>The prefix chips are shown until the user types something.</summary>
    public bool ShowPrefixHints => string.IsNullOrEmpty(QueryText);

    public bool ShowNoResults => HasSearched && !IsSearching && Rows.Count == 0;

    public string NoResultsText => Mode switch
    {
        PaletteMode.Files when _navigation.CurrentProjectId is null => "Open a project to search its files.",
        PaletteMode.Tasks when _navigation.CurrentProjectId is null => "Open a project to search its tasks.",
        _ => "No results. Try fewer letters or another prefix.",
    };

    /// <summary>The search in flight (for tests and for callers that must wait for results).</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>Opens the palette, optionally pre-filled ("/" for Go to file).</summary>
    [RelayCommand]
    public void Open(string? initialText = null)
    {
        if (_disposed)
        {
            return;
        }

        _opening = true;
        try
        {
            QueryText = initialText ?? string.Empty;
            IsOpen = true;
        }
        finally
        {
            _opening = false;
        }

        Search(debounce: false);
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
    }

    [RelayCommand]
    private void MoveNext() => MoveSelection(+1);

    [RelayCommand]
    private void MovePrevious() => MoveSelection(-1);

    [RelayCommand]
    private void ApplyPrefix(string? prefix)
    {
        var term = PaletteQueryParser.Parse(QueryText).Term;
        QueryText = (prefix ?? string.Empty) + term;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ExecuteSelectedAsync() => SelectedResult is { } result ? ExecuteAsync(result) : Task.CompletedTask;

    /// <summary>Closes the palette and runs the entry (click, or Enter on the selection).</summary>
    [RelayCommand]
    private async Task ExecuteAsync(PaletteResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        Recents.Remember(result.Item);
        IsOpen = false;
        try
        {
            await result.Item.Execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Palette entry '{Title}' failed", result.Title);
            _notifications.ShowError(ErrorInfo.From(ex, $"Could not run “{result.Title}”"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSearch();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            CancelSearch();
        }
        else if (!_opening)
        {
            Search(debounce: false);
        }
    }

    partial void OnQueryTextChanged(string value)
    {
        Mode = PaletteQueryParser.Parse(value).Mode;
        OnPropertyChanged(nameof(ShowPrefixHints));
        OnPropertyChanged(nameof(NoResultsText));
        if (IsOpen && !_opening)
        {
            Search(debounce: true);
        }
    }

    partial void OnSelectedResultChanged(PaletteResultViewModel? oldValue, PaletteResultViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    private bool HasSelection() => SelectedResult is not null;

    private void Search(bool debounce)
    {
        CancelSearch();
        var search = new CancellationTokenSource();
        _search = search;
        PendingSearch = SearchAsync(QueryText, debounce ? DebounceDelay : TimeSpan.Zero, search.Token);
    }

    private void CancelSearch()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
    }

    private async Task SearchAsync(string text, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }

            IsSearching = true;
            var parsed = PaletteQueryParser.Parse(text);
            var query = new PaletteQuery(parsed.Term, _navigation.CurrentProjectId) { Categories = parsed.Categories };
            var batches = await Task.WhenAll(_sources.Select(source => QuerySourceAsync(source, query, cancellationToken))).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var ranked = PaletteRanker.Rank(batches.SelectMany(batch => batch), parsed, Recents, MaxResults);
            ShowResults(ranked, parsed);
            HasSearched = true;
            IsSearching = false;
        }
        catch (OperationCanceledException)
        {
            // A newer query (or closing the palette) replaced this one.
        }
    }

    private async Task<IReadOnlyList<PaletteItem>> QuerySourceAsync(IPaletteSource source, PaletteQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetItemsAsync(query, cancellationToken).ConfigureAwait(true) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sources must not throw; one that does must not hide the others' results.
            _logger.LogWarning(ex, "Palette source {Source} failed", source.GetType().Name);
            return [];
        }
    }

    private void ShowResults(IReadOnlyList<RankedPaletteItem> ranked, ParsedPaletteQuery query)
    {
        var rows = new List<PaletteRowViewModel>();
        IEnumerable<RankedPaletteItem> rest = ranked;
        if (query.IsEmpty && query.Mode == PaletteMode.All)
        {
            var recent = ranked.Where(r => r.IsRecent).OrderBy(r => r.RecentRank).ToList();
            if (recent.Count > 0)
            {
                rows.Add(new PaletteGroupHeaderViewModel("Recent"));
                rows.AddRange(recent.Select(r => new PaletteResultViewModel(r.Item, r.TitleIndices)));
            }

            rest = ranked.Where(r => !r.IsRecent);
        }

        var groups = rest.GroupBy(r => r.Item.Category);
        if (query.IsEmpty)
        {
            groups = groups.OrderBy(g => Array.IndexOf(EmptyQueryCategoryOrder, g.Key));
        }

        foreach (var group in groups)
        {
            rows.Add(new PaletteGroupHeaderViewModel(PaletteQueryParser.CategoryTitle(group.Key)));
            rows.AddRange(group.Select(r => new PaletteResultViewModel(r.Item, r.TitleIndices)));
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        SelectedResult = Rows.OfType<PaletteResultViewModel>().FirstOrDefault();
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private void MoveSelection(int delta)
    {
        var results = Rows.OfType<PaletteResultViewModel>().ToList();
        if (results.Count == 0)
        {
            return;
        }

        var index = SelectedResult is null ? -1 : results.IndexOf(SelectedResult);
        var next = index < 0
            ? (delta > 0 ? 0 : results.Count - 1)
            : ((index + delta) % results.Count + results.Count) % results.Count;
        SelectedResult = results[next];
    }
}
