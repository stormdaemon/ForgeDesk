using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Decodes one line of process output. Most tools write UTF-8, but on Windows cmd.exe built-ins
/// and localized console programs write in the OEM code page (850, 437…): a line that is not
/// valid UTF-8 is decoded with that code page instead of turning into replacement characters.
/// </summary>
internal static class OutputDecoder
{
    private static readonly Lazy<Encoding> LegacyEncoding = new(CreateLegacyEncoding);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : LegacyEncoding.Value.GetString(bytes);
    }

    private static Encoding CreateLegacyEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Encoding.Latin1;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
