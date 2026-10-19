namespace BannerlordEnvironmentManager.Core.Nexus;

// Nexus's CDN link is often an opaque, signed path with no human filename in it at all, so BEM's own
// download naming falls back to "nexus-{modId}-{fileId}.zip" - a guess, not a fact about what the file
// actually is. Most files really are zips, so the guess is usually right, but a mod published as RAR
// downloads through exactly the same link shape and lands with the same ".zip" fallback name on a file
// that is not a zip at all. BEM's own zip reader then fails to open it, and every extension-based check
// downstream - which archive reader to use, whether a dropped file is an archive worth watching at all
// - inherits the same wrong answer. Sniffing the four to eight bytes every archive format's own spec
// requires it to start with costs nothing and is never fooled by what a filename claims to be.
public static class ArchiveFormatSniffer
{
    private const int SignatureLength = 8;

    // Null means inconclusive - too short to read, unreadable, or a signature not recognized here -
    // and callers should keep whatever extension they already had rather than treat null as "not an
    // archive." This only ever narrows a guessed extension to a truth the bytes actually assert.
    public static string? DetectExtension(string filePath)
    {
        var header = new byte[SignatureLength];
        int read;

        try
        {
            using var stream = File.OpenRead(filePath);
            read = stream.Read(header, 0, header.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return DetectFromHeader(header.AsSpan(0, read));
    }

    // What every reader dispatch should switch on. A claimed extension is only ever overridden by a
    // format the bytes assert and BEM knows how to open, so an unrecognized signature or an unreadable
    // file leaves the name's own answer standing.
    public static string EffectiveExtension(string filePath)
    {
        var claimed = Path.GetExtension(filePath);
        var detected = DetectExtension(filePath);

        return detected is not null && Install.ArchiveExtensions.IsArchive(detected) ? detected : claimed;
    }

    // Split out from the file-reading half so the format table itself can be tested directly against
    // bytes, without a real archive on disk for every case.
    public static string? DetectFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 'P' && header[1] == 'K'
            && (header[2] == 3 || header[2] == 5 || header[2] == 7))
            return ".zip";

        // RAR 1.5-4.x: "Rar!\x1A\x07\x00". RAR 5.0+: "Rar!\x1A\x07\x01\x00". The shared 4-byte prefix
        // is enough to call it RAR either way; nothing downstream needs to tell the two apart.
        if (header.Length >= 4 && header[0] == 'R' && header[1] == 'a' && header[2] == 'r' && header[3] == '!')
            return ".rar";

        if (header.Length >= 4 && header[0] == '7' && header[1] == 'z' && header[2] == 0xBC && header[3] == 0xAF)
            return ".7z";

        // A mod shared outside Nexus - a GitHub release, most often - is at least as likely to be a
        // compressed tarball as any of the three formats above. Corrected to the bare compressor
        // extension rather than guessing ".tar.gz" specifically: 7-Zip opens a .gz either way and
        // shows the tar layer inside it if there is one, so nothing downstream needs the distinction.
        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
            return ".gz";

        if (header.Length >= 3 && header[0] == 'B' && header[1] == 'Z' && header[2] == 'h')
            return ".bz2";

        if (header.Length >= 6 && header[0] == 0xFD && header[1] == '7' && header[2] == 'z'
            && header[3] == 'X' && header[4] == 'Z' && header[5] == 0x00)
            return ".xz";

        if (header.Length >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
            return ".zst";

        return null;
    }
}
