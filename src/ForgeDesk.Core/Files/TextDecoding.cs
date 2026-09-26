using System.Text;
using System.Text.Unicode;

namespace ForgeDesk.Core.Files;

/// <summary>A decoded text payload and the name of the encoding that was used.</summary>
internal readonly record struct DecodedText(string Text, string EncodingName);

/// <summary>
/// Encoding detection for source files: byte-order marks first (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE),
/// then strict UTF-8, then Windows-1252 — the usual encoding of legacy files on Windows, which
/// can decode any byte sequence.
/// </summary>
internal static class TextDecoding
{
    public const int BinarySniffLength = 8 * 1024;

    private static readonly Lazy<Encoding> Windows1252Encoding = new(CreateWindows1252);

    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);

    public static Encoding Windows1252 => Windows1252Encoding.Value;

    /// <summary>Encoding announced by a byte-order mark, if any.</summary>
    public static (Encoding Encoding, int BomLength, string Name, int UnitSize)? DetectBom(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE starts like UTF-16 LE: test it first.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return (Encoding.UTF32, 4, "UTF-32 LE", 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (Utf32BigEndian, 4, "UTF-32 BE", 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8, 3, "UTF-8 with BOM", 1);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode, 2, "UTF-16 LE", 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2, "UTF-16 BE", 2);
        }

        return null;
    }

    /// <summary>Text files never contain NUL bytes (UTF-16/32 files are recognized by their BOM beforehand).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample) =>
        sample[..Math.Min(sample.Length, BinarySniffLength)].IndexOf((byte)0) >= 0;

    /// <summary>Decodes bytes that carry no BOM: strict UTF-8, else Windows-1252.</summary>
    public static DecodedText DecodeWithoutBom(ReadOnlySpan<byte> bytes) =>
        Utf8.IsValid(bytes)
            ? new DecodedText(Encoding.UTF8.GetString(bytes), "UTF-8")
            : new DecodedText(Windows1252.GetString(bytes), Windows1252.WebName == "windows-1252" ? "Windows-1252" : "Latin-1");

    /// <summary>Decodes a whole file's bytes (BOM-aware).</summary>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        if (DetectBom(bytes) is { } bom)
        {
            return new DecodedText(bom.Encoding.GetString(bytes[bom.BomLength..]), bom.Name);
        }

        return DecodeWithoutBom(bytes);
    }

    private static Encoding CreateWindows1252()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
