using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>
/// A card of the Overview tab. Each card loads on its own (its failure shows inside the card, with
/// a retry) and reloads when its data changes while the tab is visible; while hidden it only
/// remembers that it is stale and reloads on the next activation.
/// </summary>
public abstract partial class OverviewCardViewModel : ViewModelBase, IDisposable
{
    private int _loadVersion;

    protected OverviewCardViewModel(ProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    protected ProjectContext Context { get; }

    /// <summary>Set by the Overview while it is the visible tab.</summary>
    public bool IsActive { get; internal set; }

    /// <summary>Data changed while the tab was hidden.</summary>
    public bool IsStale { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    public partial bool HasLoaded { get; private set; }

    /// <summary>Placeholder rows only for the first load; later loads keep the current content.</summary>
    public bool ShowSkeleton => IsLoading && !HasLoaded;

    protected bool IsDisposed { get; private set; }

    partial void OnHasLoadedChanged(bool value) => OnLoadStateChanged();

    /// <summary>Called when <see cref="HasLoaded"/> changes (for derived "show X" properties).</summary>
    protected virtual void OnLoadStateChanged()
    {
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsStale = false;
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var ok = await RunAsync(() => LoadCoreAsync(Context.Lifetime), errorTitle: ErrorTitle).ConfigureAwait(true);
            if (ok && version == _loadVersion)
            {
                HasLoaded = true;
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Reloads now when visible, otherwise on the next activation.</summary>
    protected void RequestReload()
    {
        if (IsDisposed)
        {
            return;
        }

        if (IsActive)
        {
            _ = LoadAsync();
        }
        else
        {
            IsStale = true;
        }
    }

    protected abstract string ErrorTitle { get; }

    protected abstract Task LoadCoreAsync(CancellationToken cancellationToken);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OnDispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Unsubscribes from services and the context.</summary>
    protected virtual void OnDispose()
    {
    }
}
