using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>A visible long-running operation (clone, fetch, analysis…) shown in the status bar.</summary>
public sealed partial class BackgroundOperation : ObservableObject, IDisposable
{
    private readonly Action<BackgroundOperation> _onDispose;

    internal BackgroundOperation(string title, string? projectId, Action<BackgroundOperation> onDispose)
    {
        Title = title;
        ProjectId = projectId;
        _onDispose = onDispose;
        StartedAt = DateTimeOffset.Now;
    }

    public string Title { get; }
    public string? ProjectId { get; }
    public DateTimeOffset StartedAt { get; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>0..1, or null for indeterminate.</summary>
    [ObservableProperty]
    public partial double? Progress { get; set; }

    public void Dispose() => _onDispose(this);
}

public interface IBackgroundOperations
{
    ReadOnlyObservableCollection<BackgroundOperation> Operations { get; }

    /// <summary>Starts tracking an operation; dispose the handle when it ends.</summary>
    BackgroundOperation Begin(string title, string? projectId = null);
}

public sealed class BackgroundOperations : IBackgroundOperations
{
    private readonly ObservableCollection<BackgroundOperation> _operations = [];
    private readonly IUiDispatcher _dispatcher;

    public BackgroundOperations(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Operations = new ReadOnlyObservableCollection<BackgroundOperation>(_operations);
    }

    public ReadOnlyObservableCollection<BackgroundOperation> Operations { get; }

    public BackgroundOperation Begin(string title, string? projectId = null)
    {
        var op = new BackgroundOperation(title, projectId, o => _dispatcher.Post(() => _operations.Remove(o)));
        _dispatcher.Post(() => _operations.Add(op));
        return op;
    }
}
