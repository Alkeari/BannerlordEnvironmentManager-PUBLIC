using System.Text;

namespace BannerlordEnvironmentManager.Core.Io;

// The bytes of a file BEM did not author are the author's, down to the byte order mark and whatever
// encoding they saved it in. Reading through Latin1 when the bytes are not valid UTF-8 keeps every
// byte addressable and reversible, so a file BEM cannot fully understand still round-trips exactly.
public sealed record XmlText(string Value, Encoding Encoding, bool HasByteOrderMark)
{
    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    public byte[] ToBytes(string value) =>
        HasByteOrderMark ? [.. ByteOrderMark, .. Encoding.GetBytes(value)] : Encoding.GetBytes(value);
}

public static class XmlTextFile
{
    // The one place BEM decides what UTF-8 means for a file it reads or writes byte for byte: no
    // identifier emitted, because the byte order mark travels with XmlText rather than the encoder,
    // and invalid bytes throw so the Latin1 fallback below can catch them rather than silently
    // replacing a byte BEM cannot round-trip.
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static XmlText Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
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
