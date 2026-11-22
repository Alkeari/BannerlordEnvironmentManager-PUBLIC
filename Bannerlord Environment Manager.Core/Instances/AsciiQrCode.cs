namespace BannerlordEnvironmentManager.Core.Instances;

// DepotDownloader draws Steam's sign-in QR code as text and never prints the link behind it
// (Steam3Session.DisplayQrCode: QRCoder's AsciiQRCode.GetLineByLineGraphic(1, drawQuietZones: true),
// which writes two block characters per dark module and two spaces per light one). Text is the wrong
// shape for a thing a camera has to read: in a proportional font it is not square, and at a readable
// font size it is enormous.
//
// So the text is read back into the module grid it came from, and the app paints that grid as a real
// QR code. Nothing is decoded and no link is recovered: this is the same picture, drawn properly.
public static class AsciiQrCode
{
    private const int CharactersPerModule = 2;
    private const int SmallestQrSize = 21;

    // A square grid where true is a dark module, or null when the text is not one of these blocks.
    public static bool[,]? Parse(string asciiBlock)
    {
        if (string.IsNullOrWhiteSpace(asciiBlock))
            return null;

        var lines = asciiBlock
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.Trim('\0', ' ').Length > 0 || line.Length >= SmallestQrSize * CharactersPerModule)
            .ToList();

        if (lines.Count < SmallestQrSize)
            return null;

        var width = lines[0].Length;

        if (width % CharactersPerModule != 0 || lines.Any(line => line.Length != width))
            return null;

        var size = width / CharactersPerModule;

        // A QR code is square, and the quiet zone is part of what was drawn, so the row count has to
        // match the column count exactly. Anything else is some other block of text.
        if (size != lines.Count)
            return null;

        var matrix = new bool[size, size];

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var first = lines[row][column * CharactersPerModule];
                var second = lines[row][(column * CharactersPerModule) + 1];

                if (first != second)
                    return null;

                matrix[row, column] = !char.IsWhiteSpace(first);
            }
        }

        return matrix;
    }
}
