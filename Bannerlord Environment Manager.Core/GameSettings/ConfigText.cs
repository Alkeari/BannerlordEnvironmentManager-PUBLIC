using System.Text;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// The bytes of a settings file are the game's, not BEM's. XmlTextFile already reads a file that way,
// byte order mark preserved and UTF-8 with a Latin1 fallback so an unexpected byte still round-trips,
// and nothing about that is XML-specific, so the three settings files read through it rather than
// through a second copy of the same logic. engine_config.txt carries no byte order mark and CRLF
// endings, BannerlordConfig.txt carries one and LF endings, and neither ends the way the other does.
public sealed record ConfigText(string Value, Encoding Encoding, bool HasByteOrderMark)
{
    public static ConfigText Read(string path)
    {
        var text = XmlTextFile.Read(path);

        return new ConfigText(text.Value, text.Encoding, text.HasByteOrderMark);
    }

    // A file BEM is about to create rather than one it read. There are no bytes of the game's to
    // preserve, so the encoding is BEM's own UTF-8 and the byte order mark is whichever one the game
    // puts on a file of that kind: see GameSettingsFiles.FormatOf.
    public static ConfigText Create(bool hasByteOrderMark) =>
        new(string.Empty, XmlTextFile.Utf8, hasByteOrderMark);

    public byte[] ToBytes(string value) => new XmlText(value, Encoding, HasByteOrderMark).ToBytes(value);
}

// One settings file, read so that every byte BEM did not deliberately change survives the trip.
// Values are the keys the file declares; Set rewrites a value where it already sits and reports how
// many occurrences actually changed, so a document nobody edited renders byte for byte as it came in.
public interface IGameSettingsDocument
{
    IReadOnlyDictionary<string, string> Values { get; }

    int Set(string key, string value);

    string Text { get; }

    byte[] ToBytes();
}
