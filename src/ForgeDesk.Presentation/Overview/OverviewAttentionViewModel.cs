using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>A reason the project needs attention, with the tab that fixes it.</summary>
public sealed record AttentionItemViewModel(AttentionLevel Level, string Message, WorkspaceSection? Section)
{
    public StatusTone Tone => ToneOf(Level);

    public bool CanOpen => Section is not null;

    public string ActionText => Section is { } section ? $"Open {WorkspaceSectionInfo.For(section).Title}" : string.Empty;

    public string LevelText => Level switch
    {
        AttentionLevel.Critical => "Critical",
        AttentionLevel.Warning => "Warning",
        _ => "Info",
    };

    public static StatusTone ToneOf(AttentionLevel level) => level switch
    {
        AttentionLevel.Critical => StatusTone.Danger,
        AttentionLevel.Warning => StatusTone.Warning,
        AttentionLevel.Info => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    /// <summary>
    /// Maps a reason to an item. Its section name ("Git", "Commands"…) becomes the tab to open;
    /// <paramref name="current"/> (the tab showing the list) and unknown names open nothing.
    /// </summary>
    public static AttentionItemViewModel From(AttentionReason reason, WorkspaceSection current)
    {
        ArgumentNullException.ThrowIfNull(reason);
        WorkspaceSection? section = Enum.TryParse<WorkspaceSection>(reason.Section, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed) && parsed != current
            ? parsed
            : null;
        return new AttentionItemViewModel(reason.Level, reason.Message, section);
    }

    /// <summary>Most severe first, keeping the original order within a level.</summary>
    public static IReadOnlyList<AttentionItemViewModel> FromAll(IEnumerable<AttentionReason> reasons, WorkspaceSection current) =>
        reasons.Select((r, i) => (Reason: r, Index: i))
            .OrderByDescending(x => x.Reason.Level)
            .ThenBy(x => x.Index)
            .Select(x => From(x.Reason, current))
            .ToList();
}

/// <summary>"Needs attention" card: the reasons computed by the project status service, live.</summary>
public sealed partial class OverviewAttentionViewModel : OverviewCardViewModel
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly IProjectStatusService _status;
    private readonly IUiDispatcher _dispatcher;
    private readonly Debouncer _recompute = new(TimeSpan.FromSeconds(2));
    private readonly TimeProvider _time;
    private DateTimeOffset? _capturedAt;

    public OverviewAttentionViewModel(ProjectContext context, IProjectStatusService status, IUiDispatcher dispatcher, TimeProvider? time = null)
        : base(context)
    {
        _status = status;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _status.SnapshotUpdated += OnSnapshotUpdated;
        Context.GitStatusChanged += OnGitStatusChanged;
    }

    public ObservableCollection<AttentionItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasItems { get; private set; }

    /// <summary>A snapshot exists and lists nothing.</summary>
    [ObservableProperty]
    public partial bool IsHealthy { get; private set; }

    [ObservableProperty]
    public partial StatusTone Tone { get; private set; }

    protected override string ErrorTitle => "Could not check the project";

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _status.GetCachedAsync(Context.ProjectId, cancellationToken).ConfigureAwait(true);
        if (snapshot is not null)
        {
            Apply(snapshot);
        }

        if (snapshot is null || _time.GetUtcNow() - snapshot.CapturedAt > StaleAfter)
        {
            // Recompute in the background; SnapshotUpdated brings the result.
            _ = RecomputeAsync();
        }
    }

    /// <summary>Recomputes the snapshot without calling GitHub (CI comes from the last remote refresh).</summary>
    public async Task RecomputeAsync(bool includeRemote = false)
    {
        try
        {
            await _status.RefreshAsync(Context.Project, includeRemote, Context.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            System.Diagnostics.Trace.TraceWarning($"Could not refresh the status of {Context.Root}: {ex.Message}");
        }
        catch (Exception)
        {
            // The project was closed.
        }
    }

    [RelayCommand]
    private void Open(AttentionItemViewModel? item)
    {
        if (item?.Section is { } section)
        {
            Context.RequestNavigation(section);
        }
    }

    internal void Apply(ProjectSnapshot snapshot)
    {
        if (_capturedAt is { } previous && snapshot.CapturedAt < previous)
        {
            return;
        }

        _capturedAt = snapshot.CapturedAt;
        var items = AttentionItemViewModel.FromAll(snapshot.Attention, WorkspaceSection.Overview);
        if (!items.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        HasItems = Items.Count > 0;
        IsHealthy = Items.Count == 0;
        Tone = AttentionItemViewModel.ToneOf(snapshot.AttentionLevel);
    }

    private void OnSnapshotUpdated(object? sender, ProjectSnapshot snapshot)
    {
        if (string.Equals(snapshot.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            _dispatcher.Post(() =>
            {
                if (!IsDisposed)
                {
                    Apply(snapshot);
                }
            });
        }
    }

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        // Uncommitted/unpushed reasons follow the working tree while the tab is visible.
        if (IsActive && HasLoaded && !IsDisposed)
        {
            _recompute.Trigger(() => RecomputeAsync());
        }
    }

    protected override void OnDispose()
    {
        _status.SnapshotUpdated -= OnSnapshotUpdated;
        Context.GitStatusChanged -= OnGitStatusChanged;
        _recompute.Dispose();
    }
}
