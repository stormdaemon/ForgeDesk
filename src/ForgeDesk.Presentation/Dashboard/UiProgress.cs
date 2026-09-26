using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>Reports progress on the UI thread (services report from background threads).</summary>
internal sealed class UiProgress<T> : IProgress<T>
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<T> _handler;

    public UiProgress(IUiDispatcher dispatcher, Action<T> handler)
    {
        _dispatcher = dispatcher;
        _handler = handler;
    }

    public void Report(T value) => _dispatcher.Post(() => _handler(value));
}
