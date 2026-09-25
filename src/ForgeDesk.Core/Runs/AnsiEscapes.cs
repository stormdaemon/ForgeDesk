using System.Text;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Removes terminal control sequences (CSI colors and cursor moves, OSC titles and hyperlinks,
/// charset switches…) and stray control characters so command output reads as plain text.
/// </summary>
internal static class AnsiEscapes
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';
    private const char Backspace = '\b';
    private const char C1Csi = '\u009b';
    private const char C1Osc = '\u009d';
    private const char C1StringTerminator = '\u009c';

    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var i = 0;
        while (i < text.Length && !IsControl(text[i]))
        {
            i++;
        }

        if (i == text.Length)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, i);
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case Escape:
                    i = SkipEscapeSequence(text, i + 1);
                    break;
                case C1Csi:
                    i = SkipControlSequence(text, i + 1);
                    break;
                case C1Osc:
                    i = SkipStringSequence(text, i + 1);
                    break;
                case Backspace:
                    // Spinners draw "⠋\b⠙\b⠹": the character after a backspace replaces the previous one.
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                    }

                    i++;
                    break;
                case '\t':
                    builder.Append(c);
                    i++;
                    break;
                default:
                    if (!IsControl(c))
                    {
                        builder.Append(c);
                    }

                    i++;
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsControl(char c) =>
        (c < ' ' && c != '\t') || c == '\u007f' || (c >= '\u0080' && c <= '\u009f');

    /// <summary>Skips what follows an ESC character and returns the index after the sequence.</summary>
    private static int SkipEscapeSequence(string text, int i)
    {
        if (i >= text.Length)
        {
            return i;
        }

        var c = text[i];
        switch (c)
        {
            case '[':
                return SkipControlSequence(text, i + 1);
            case ']':
            case 'P':
            case 'X':
            case '^':
            case '_':
                // OSC, DCS, SOS, PM and APC carry a string terminated by BEL or ST.
                return SkipStringSequence(text, i + 1);
        }

        // nF sequences: intermediate bytes (0x20–0x2F) then one final byte, e.g. "ESC ( B".
        while (i < text.Length && text[i] >= ' ' && text[i] <= '/')
        {
            i++;
        }

        if (i < text.Length && text[i] >= '0' && text[i] <= '~')
        {
            i++;
        }

        return i;
    }

    /// <summary>CSI: parameter bytes (0x30–0x3F), intermediate bytes (0x20–0x2F), one final byte (0x40–0x7E).</summary>
    private static int SkipControlSequence(string text, int i)
    {
        while (i < text.Length && text[i] >= '0' && text[i] <= '?')
        {
            i++;
        }

        while (i < text.Length && text[i] >= ' ' && text[i] <= '/')
        {
            i++;
        }

        if (i < text.Length && text[i] >= '@' && text[i] <= '~')
        {
            i++;
        }

        return i;
    }

    private static int SkipStringSequence(string text, int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (c == Bell || c == C1StringTerminator)
            {
                return i + 1;
            }

            if (c == Escape && i + 1 < text.Length && text[i + 1] == '\\')
            {
                return i + 2;
            }

            i++;
        }

        return i;
    }
}
