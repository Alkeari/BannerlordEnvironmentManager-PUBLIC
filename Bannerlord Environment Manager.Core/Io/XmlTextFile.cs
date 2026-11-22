using System.Text;

namespace BannerlordEnvironmentManager.Core.Io;

// The bytes of a file BEM did not author are the author's, down to the byte order mark and whatever
// encoding they saved it in. Reading through Latin1 when the bytes are not valid UTF-8 keeps every
// byte addressable and reversible, so a file BEM cannot fully understand still round-trips exactly.
public sealed record XmlText(string Value, Encoding Encoding, bool HasByteOrderMark)
{
    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];

    // Each encoding writes its own mark: UTF-16 files carry FF FE or FE FF, never the UTF-8 one.
    public byte[] ToBytes(string value)
    {
        if (!HasByteOrderMark)
            return Encoding.GetBytes(value);

        byte[] mark = Encoding is UnicodeEncoding ? new UnicodeEncoding(XmlTextFile.IsBigEndian(Encoding), true).GetPreamble() : Utf8Mark;
        return [.. mark, .. Encoding.GetBytes(value)];
    }
}

public static class XmlTextFile
{
    // The one place BEM decides what UTF-8 means for a file it reads or writes byte for byte: no
    // identifier emitted, because the byte order mark travels with XmlText rather than the encoder,
    // and invalid bytes throw so the Latin1 fallback below can catch them rather than silently
    // replacing a byte BEM cannot round-trip.
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // No identifier: as with UTF-8, the mark travels with XmlText. Invalid pairs throw so Latin1 catches them.
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    internal static bool IsBigEndian(Encoding encoding) => ReferenceEquals(encoding, Utf16Be);

    public static XmlText Read(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // The game's own language files are UTF-16 with a mark, and read as UTF-8 each one failed at its
        // first character, so valid files were reported as broken.
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
        {
            var wide = bytes[0] == 0xFF ? Utf16Le : Utf16Be;

            try
            {
                return new XmlText(wide.GetString(bytes.AsSpan(2)), wide, true);
            }
            catch (DecoderFallbackException)
            {
                return new XmlText(Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
            }
        }

        var hasByteOrderMark = bytes is [0xEF, 0xBB, 0xBF, ..];
        var body = hasByteOrderMark ? bytes.AsSpan(3) : bytes;

        try
        {
            return new XmlText(Utf8.GetString(body), Utf8, hasByteOrderMark);
        }
        catch (DecoderFallbackException)
        {
            return new XmlText(Encoding.Latin1.GetString(body), Encoding.Latin1, hasByteOrderMark);
        }
    }
}
