using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace ForgeDesk.App.Activation;

/// <summary>Sends this instance's arguments to the primary instance, retrying while it starts up.</summary>
internal static class ActivationPipeClient
{
    public static async Task<bool> TrySendAsync(
        string pipeName,
        ActivationMessage message,
        TimeSpan timeout,
        Action<NamedPipeClientStream>? onConnected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(message);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var remaining = timeout - clock.Elapsed;
            try
            {
                await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync((int)Math.Clamp(remaining.TotalMilliseconds, 1, 1000), cancellationToken).ConfigureAwait(false);
                onConnected?.Invoke(client);
                await ActivationMessage.WriteAsync(client, message, cancellationToken).ConfigureAwait(false);
                if (OperatingSystem.IsWindows())
                {
                    client.WaitForPipeDrain();
                }

                return true;
            }
            catch (TimeoutException)
            {
                // The primary instance is starting or busy with another client: try again.
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // The pipe belongs to another user: never talk to it.
                return false;
            }
        }

        return false;
    }
}
