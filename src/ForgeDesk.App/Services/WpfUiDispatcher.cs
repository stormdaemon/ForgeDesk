using System.Windows.Threading;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Services;

/// <summary><see cref="IUiDispatcher"/> backed by the WPF dispatcher of the UI thread.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.HasShutdownStarted
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : _dispatcher.InvokeAsync(action).Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }

        return _dispatcher.HasShutdownStarted
            ? Task.FromCanceled<T>(new CancellationToken(canceled: true))
            : _dispatcher.InvokeAsync(func).Task;
    }
}
