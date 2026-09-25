using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace ForgeDesk.App.Activation;

/// <summary>
/// What a second ForgeDesk instance forwards to the running one: its arguments and working
/// directory (so "ForgeDesk ." resolves against the caller's folder). Framed on the pipe as a
/// 4-byte little-endian length followed by UTF-8 JSON.
/// </summary>
internal sealed record ActivationMessage(IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public const int MaxPayloadBytes = 64 * 1024;

    public static async Task WriteAsync(Stream stream, ActivationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(message.Arguments.ToArray(), message.WorkingDirectory));
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("The activation message is too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message; returns null when the data is truncated or malformed.</summary>
    public static async Task<ActivationMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxPayloadBytes)
        {
            return null;
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var data = JsonSerializer.Deserialize<Payload>(payload);
            return data?.Arguments is null
                ? null
                : new ActivationMessage(data.Arguments.Where(a => a is not null).ToArray(), data.WorkingDirectory ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private sealed record Payload(string[]? Arguments, string? WorkingDirectory);
}
