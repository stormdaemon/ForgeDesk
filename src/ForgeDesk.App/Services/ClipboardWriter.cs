using System.Runtime.InteropServices;
using System.Windows;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Services;

/// <summary>
/// Writes text to the clipboard, retrying while another program (a clipboard manager, a
/// remote desktop session…) briefly holds it open. Must be called on an STA thread.
/// </summary>
internal static class ClipboardWriter
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    public static void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                return;
            }
            catch (ExternalException ex) when (ex.HResult == ClipboardCannotOpen)
            {
                if (attempt >= MaxAttempts)
                {
                    throw new ForgeException(ErrorKind.Unknown, "The clipboard is being used by another program.",
                        "Try again in a moment.", ex.Message, ex);
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }
}
