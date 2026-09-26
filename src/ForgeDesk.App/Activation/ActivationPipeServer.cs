using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.App.Activation;

/// <summary>
/// Listens, in the primary instance, for activation messages sent by later instances. The pipe
/// only accepts clients running as the same user. Messages are delivered on a pool thread.
/// </summary>
internal sealed class ActivationPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // The instance being served plus the one already waiting for the next client.
    private const int MaxInstances = 2;

    private readonly string _pipeName;
    private readonly Func<ActivationMessage, Task> _onMessage;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public ActivationPipeServer(string pipeName, Func<ActivationMessage, Task> onMessage, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _onMessage = onMessage;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        NamedPipeServerStream? listener = null;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    listener ??= CreateInstance();
                    await listener.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Activation pipe unavailable; retrying");
                    listener?.Dispose();
                    listener = null;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stopping).ConfigureAwait(false);
                    continue;
                }

                // Open the next instance before serving this client, so that a launch arriving
                // meanwhile always finds a listener instead of a pipe that is being torn down.
                var connected = listener;
                listener = TryCreateInstance();
                await ServeAsync(connected, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        finally
        {
            listener?.Dispose();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream connection, CancellationToken stopping)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(ReadTimeout);
            var message = await ActivationMessage.ReadAsync(connection, timeout.Token).ConfigureAwait(false);
            if (message is null)
            {
                _logger.LogWarning("Ignored a malformed activation message");
                return;
            }

            await _onMessage(message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A client that disconnects early, times out or sends garbage must not stop the server.
            _logger.LogWarning(ex, "Could not handle an activation message");
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreateInstance() =>
        new(_pipeName, PipeDirection.In, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private NamedPipeServerStream? TryCreateInstance()
    {
        try
        {
            return CreateInstance();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not open the next activation pipe instance");
            return null;
        }
    }
}
