namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Coalesces bursts of calls into one execution after a quiet period.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;

    public Debouncer(TimeSpan delay) => _delay = delay;

    public void Trigger(Func<Task> action)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }

        _ = RunAsync(action, cts.Token);
    }

    private async Task RunAsync(Func<Task> action, CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token).ConfigureAwait(false);
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Debounced action failed: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
