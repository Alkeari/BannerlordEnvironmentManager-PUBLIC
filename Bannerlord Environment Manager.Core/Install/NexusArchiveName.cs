using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Install;

// Nexus rewrote its download filenames three times in 26 days and states plainly that the filename is
// not a supported contract, so everything here is a hint. Two grammars are read: the legacy
// "{name}-{modId}-{version}-{unixEpoch}.{ext}" and the current
// "{name} {modId} {version} {timestamp} {slug}.{ext}". Each is anchored on the token that cannot be
// mistaken for part of a mod's name, because naming the wrong mod page is worse than offering none.
// What a Nexus download filename says, once the grammar it was written in has been recognized. The
// mod name is empty for the grammar BEM writes itself, which carries an id and nothing else.
// FileId is only ever set for the grammar BEM writes itself, which is the one grammar here that is a
// contract. No Nexus filename is obliged to carry a file id, so a null one means "the name did not
// say" and never "this mod has no file id".
//
// Version is what Nexus called that file, which is the same string an authenticated hash lookup comes
// back with and a different thing from the version the module's own SubModule.xml declares. The two
// routes have never been observed to disagree on any real archive that carries both, which
// is why the name is worth reading where no lookup was ever paid for. That is stated as an invariant
// rather than as a count on purpose: an earlier count here went stale against the ledger it described
// and read as a fact long after it stopped being reproducible. It stays a hint either way, and a
// lookup wins wherever there is one.
public readonly record struct NexusArchiveNameParts(int ModId, string ModName, int? FileId = null, string? Version = null);

public static partial class NexusArchiveName
{
    public const string GameDomain = "mountandblade2bannerlord";

    private const int TimestampMinimumDigits = 10;

    private const int ModIdMaximumDigits = 7;

    private const int CurrentGrammarTokensBeforeTimestamp = 3;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z$")]
    private static partial Regex UploadTimestampPattern { get; }

    [GeneratedRegex(@"^[A-Za-z0-9]+$")]
    private static partial Regex FileSlugPattern { get; }

    // The one grammar here that is a contract, because BEM writes it: NxmDownload names a file it
    // could not get a name for "nexus-{modId}-{fileId}". It is read first for that reason.
    [GeneratedRegex(@"^nexus-(?<mod>\d+)-(?<file>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnDownloadPattern { get; }

    public static int? TryGetModId(string? fileNameOrPath) => TryRead(fileNameOrPath)?.ModId;

    // The mod id out of a name BEM wrote itself, and null for every other grammar. BEM only writes
    // this name for a file it has just downloaded through a nxm link, so an id read out of it is a
    // record of a download that happened rather than a reading of somebody else's naming, and it is
    // ranked accordingly. Kept as its own method because the caller that needs the distinction should
    // not have to know that a file id implies it.
    public static int? TryGetOwnDownloadModId(string? fileNameOrPath) =>
        string.IsNullOrWhiteSpace(fileNameOrPath)
            ? null
            : TryReadOwnDownload(Path.GetFileNameWithoutExtension(fileNameOrPath))?.ModId;

    public static int? TryGetFileId(string? fileNameOrPath) => TryRead(fileNameOrPath)?.FileId;

    // The version Nexus gave the file, for a copy nobody ever paid a hash lookup for. Null means the
    // name did not carry one, never that the file has no version.
    public static string? TryGetVersion(string? fileNameOrPath) =>
        TryRead(fileNameOrPath) is { Version: { Length: > 0 } version } ? version : null;

    // The mod's own name as the download was called, which is the only thing in a filename that says
    // which mod it is when nothing else recorded that.
    public static string? TryGetModName(string? fileNameOrPath) =>
        TryRead(fileNameOrPath) is { ModName.Length: > 0 } parts ? parts.ModName : null;

    public static NexusArchiveNameParts? TryRead(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
            return null;

        var name = Path.GetFileNameWithoutExtension(fileNameOrPath);

        return TryReadOwnDownload(name) ?? TryReadCurrentGrammar(name) ?? TryReadLegacyGrammar(name);
    }

    // A module's own manifest is the one place a mod author states where the mod lives, so it outranks a
    // filename that any download, rename or re-zip can have changed.
    public static int? TryGetModIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length >= 3
            && segments[0].Equals(GameDomain, StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)
            ? PlausibleModId(segments[2])
            : null;
    }

    public static string PageUrl(int modId) =>
        $"https://www.nexusmods.com/{GameDomain}/mods/{modId}";

    // "{name} {modId} {version} {yyyy-MM-ddTHH-mmZ} {slug}". The name may contain spaces, so the
    // timestamp is the anchor and everything is counted back from it. The 2 to 8 July 2026 variant
    // dropped the timestamp, and without that anchor a mod id cannot be told from a version, so a name
    // that does not carry one yields nothing.
    private static NexusArchiveNameParts? TryReadCurrentGrammar(string name)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var timestamp = Array.FindIndex(tokens, token => UploadTimestampPattern.IsMatch(token));

        if (timestamp < CurrentGrammarTokensBeforeTimestamp
            || timestamp != tokens.Length - 2
            || !FileSlugPattern.IsMatch(tokens[^1]))
            return null;

        return PlausibleModId(tokens[timestamp - 2]) is { } modId
            ? new NexusArchiveNameParts(modId, string.Join(' ', tokens[..(timestamp - 2)]),
                Version: tokens[timestamp - 1])
            : null;
    }

    private static NexusArchiveNameParts? TryReadOwnDownload(string name)
    {
        var match = OwnDownloadPattern.Match(name);

        return match.Success && PlausibleModId(match.Groups["mod"].Value) is { } modId
            ? new NexusArchiveNameParts(modId, string.Empty,
                int.TryParse(match.Groups["file"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId) && fileId > 0 ? fileId : null)
            : null;
    }

    private static NexusArchiveNameParts? TryReadLegacyGrammar(string name)
    {
        var parts = name.Split('-');

        if (parts.Length < 3 || !IsDownloadTimestamp(parts[^1]))
            return null;

        // A mod name may itself contain hyphens, so the first all-digit segment after it is the mod
        // id. Reading past that segment when it does not look like an id would start picking version
        // numbers out of the name, so an unconvincing first candidate ends the search.
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit))
                continue;

            // This grammar writes a version's dots as hyphens, so 1.0.5 arrives as "1-0-5" between the
            // mod id and the upload stamp. Putting the dots back is what makes it the same shape as
            // every other version string BEM handles.
            var version = i < parts.Length - 2 ? string.Join('.', parts[(i + 1)..^1]) : null;

            return PlausibleModId(parts[i]) is { } modId
                ? new NexusArchiveNameParts(modId, string.Join('-', parts[..i]), Version: version)
                : null;
        }

        return null;
    }

    private static int? PlausibleModId(string candidate) =>
        candidate.Length is > 0 and <= ModIdMaximumDigits
        && candidate.All(char.IsAsciiDigit)
        && int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modId)
        && modId > 0
            ? modId
            : null;

    private static bool IsDownloadTimestamp(string part) =>
        part.Length >= TimestampMinimumDigits && part.All(char.IsAsciiDigit);
}
