namespace ForgeDesk.Core.Runs;

/// <summary>A decoded, escape-free piece of output.</summary>
/// <param name="Text">The display text.</param>
/// <param name="IsTransient">
/// True for a "\r" progress redraw that the next write overwrites: it feeds progress detection
/// but is not a log line of its own.
/// </param>
internal readonly record struct OutputSegment(string Text, bool IsTransient);

/// <summary>
/// Splits a raw byte stream into display lines the way a terminal would show them.
/// <list type="bullet">
/// <item>"\n" and "\r\n" end a line (even when "\r" and "\n" arrive in different reads).</item>
/// <item>A lone "\r" redraws the current line: those intermediate states are reported as transient
/// segments, and only the final state becomes a line — a progress bar redrawn 500 times yields one
/// log line instead of 500.</item>
/// <item>Very long lines are wrapped so a single runaway line cannot exhaust memory.</item>
/// </list>
/// Not thread-safe: use one instance per stream.
/// </summary>
internal sealed class OutputLineSplitter
{
    public const int DefaultMaxLineBytes = 64 * 1024;

    private readonly int _maxLineBytes;
    private byte[] _buffer = new byte[512];
    private int _length;
    private bool _pendingCarriageReturn;
    private string? _transient;

    public OutputLineSplitter(int maxLineBytes = DefaultMaxLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 16);
        _maxLineBytes = maxLineBytes;
    }

    public void Feed(ReadOnlySpan<byte> data, List<OutputSegment> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var b in data)
        {
            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                if (b == (byte)'\n')
                {
                    EmitLine(output);
                    continue;
                }

                CaptureTransient(output);
            }

            switch (b)
            {
                case (byte)'\n':
                    EmitLine(output);
                    break;
                case (byte)'\r':
                    _pendingCarriageReturn = true;
                    break;
                default:
                    Append(b, output);
                    break;
            }
        }
    }

    /// <summary>Flushes whatever is left when the stream ends.</summary>
    public void Complete(List<OutputSegment> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            CaptureTransient(output);
        }

        if (_length > 0 || _transient is not null)
        {
            EmitLine(output);
        }
    }

    private void Append(byte b, List<OutputSegment> output)
    {
        if (_length == _maxLineBytes)
        {
            WrapLongLine(output);
        }

        if (_length == _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, _maxLineBytes));
        }

        _buffer[_length++] = b;
    }

    private void EmitLine(List<OutputSegment> output)
    {
        var text = DecodeBuffer();
        _length = 0;

        // "Downloading 100%\r" followed by an empty line: the redrawn state is the real content.
        if (string.IsNullOrWhiteSpace(text) && _transient is not null)
        {
            text = _transient;
        }

        _transient = null;
        output.Add(new OutputSegment(text, IsTransient: false));
    }

    private void CaptureTransient(List<OutputSegment> output)
    {
        if (_length == 0)
        {
            return;
        }

        var text = DecodeBuffer();
        _length = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _transient = text;
        output.Add(new OutputSegment(text, IsTransient: true));
    }

    private void WrapLongLine(List<OutputSegment> output)
    {
        // Cut before a UTF-8 continuation byte so no character is split in two.
        var cut = _length;
        while (cut > _length - 4 && cut > 1 && (_buffer[cut - 1] & 0xC0) == 0x80)
        {
            cut--;
        }

        if (cut > 1 && _buffer[cut - 1] >= 0xC0)
        {
            cut--;
        }

        if (cut <= 0)
        {
            cut = _length;
        }

        var text = Clean(OutputDecoder.Decode(_buffer.AsSpan(0, cut)));
        var remaining = _length - cut;
        Array.Copy(_buffer, cut, _buffer, 0, remaining);
        _length = remaining;
        _transient = null;
        output.Add(new OutputSegment(text, IsTransient: false));
    }

    private string DecodeBuffer() => _length == 0 ? string.Empty : Clean(OutputDecoder.Decode(_buffer.AsSpan(0, _length)));

    private static string Clean(string text)
    {
        var stripped = AnsiEscapes.Strip(text);
        return stripped.Length > 0 && stripped[0] == '﻿' ? stripped[1..] : stripped;
    }
}
