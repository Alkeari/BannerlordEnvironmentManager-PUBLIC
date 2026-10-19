using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// What happened the last time BEM asked Nexus what version a mod is at. "Nexus stated no version",
// "BEM could not ask" and "BEM has not asked yet" are three different statements, and a reader who
// cannot tell them apart has been misled rather than informed. The third one is the absence of a
// record, which is why nothing is ever written for it.
public enum NexusVersionLookup
{
    NotAsked,
    Listed,
    NoVersionListed,
    LookupFailed
}

// What happened the last time BEM asked Nexus for a mod's file list. Separate from the version lookup
// because it is a separate request that can fail on its own, and a list that did not come back must
// never be mistaken for a list that says nothing changed.
public enum NexusFileListLookup
{
    NotAsked,
    Listed,
    LookupFailed
}

// Name is what Nexus calls the mod page, which arrives in the same answer as the version and used to
// be thrown away. It is what lets a page BEM is proposing be recognized as the mod it is, instead of
// asking the user to judge a bare number.
//
// Files is the second question, asked separately and kept separately: a mod page's version tracks its
// main file, so it is the file list that says whether the exact file a module was installed from is
// still one of the current ones. FilesFetchedUtc is its own timestamp because refreshing the version
// must not make a file list read as newer than it is.
public sealed record NexusModVersionRecord(
    int ModId,
    string? Version,
    NexusVersionLookup Outcome,
    DateTimeOffset FetchedUtc,
    string? Name = null,
    IReadOnlyList<NexusModFile>? Files = null,
    IReadOnlyList<NexusFileReplacement>? Replacements = null,
    NexusFileListLookup FilesOutcome = NexusFileListLookup.NotAsked,
    DateTimeOffset? FilesFetchedUtc = null);

public sealed record NexusVersionLedger(IReadOnlyList<NexusModVersionRecord> Records)
{
    public static NexusVersionLedger Empty { get; } = new([]);

    public IReadOnlyDictionary<int, NexusModVersionRecord> ByModId() =>
        Records
            .GroupBy(record => record.ModId)
            .ToDictionary(group => group.Key, group => group.MaxBy(record => record.FetchedUtc)!);

    public IReadOnlyDictionary<int, NexusModFileListing> FilesByModId() => FilesIn(ByModId());

    // Only the lists BEM currently holds a good answer for. A list whose last refresh failed is left
    // out even though its contents are kept: the refresh was asked for because the feed said that mod
    // had moved, so using the old list would call a superseded file current, which is the exact
    // mistake this comparison exists to stop.
    public static IReadOnlyDictionary<int, NexusModFileListing> FilesIn(
        IReadOnlyDictionary<int, NexusModVersionRecord> known) =>
        known.Values
            .Where(record => record is { FilesOutcome: NexusFileListLookup.Listed, Files: not null })
            .ToDictionary(record => record.ModId,
                record => new NexusModFileListing(record.Files!, record.Replacements ?? []));

    // Every mod page BEM has already paid to ask about, by the name Nexus gives it. A request already
    // spent is a name already owned, so nothing here costs anything to use.
    public IReadOnlyDictionary<int, string> NamesByModId() =>
        ByModId()
            .Where(pair => pair.Value.Name is { Length: > 0 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.Name!);
}

// A version fetched once is a version paid for. Keeping it on disk is the whole difference between a
// one-time cost and a per-run cost: the feed of what changed says which of these has gone stale, and
// everything it does not name stays as it is. Nothing here holds a credential: it is public mod ids
// and the version strings their authors published.
public sealed class NexusVersionStore(string filePath)
{
    public const string FileName = "nexus-mod-versions.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; } = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    // The copy on this machine, for the callers that only read what has already been paid for. Every
    // page's file list in here was fetched once and settles which mod page a module came from without
    // spending anything.
    public static NexusVersionStore Default { get; } = new(DefaultPath());

    // The ledger sits beside the feed cache, so a caller that already knows where one lives does not
    // have to be told where the other does.
    public static NexusVersionStore Beside(NexusUpdateCache cache) =>
        new(Path.Combine(Path.GetDirectoryName(cache.FilePath) ?? string.Empty, FileName));

    // A file that will not parse throws rather than reading as a ledger with nothing in it. "Nexus has
    // never been asked" and "BEM could not read what it was told" mean opposite things, and answering
    // the first would let one corrupt file erase every version and page name in it the moment a sweep
    // wrote the ledger back.
    public NexusVersionLedger Read() =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<NexusVersionLedger>(File.ReadAllText(FilePath), Format) ?? NexusVersionLedger.Empty
            : NexusVersionLedger.Empty;

    // For the callers that only display what is held. None of them writes the ledger back, so a file
    // they cannot read costs them a gap on screen rather than the ledger itself.
    public NexusVersionLedger Load()
    {
        try
        {
            return Read();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return NexusVersionLedger.Empty;
        }
    }

    // Every record in this file was paid for with a Nexus request against the user's own quota, so the
    // save has two jobs beyond writing: it never writes over a file it could not read first, and it
    // says whether it worked. False is "nothing on disk changed", and the caller has to pass that on.
    public bool Save(NexusVersionLedger ledger)
    {
        try
        {
            if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if (!KeepUnreadableAside())
                return false;

            // Written beside the ledger and moved over it, so a write that stops halfway leaves the
            // ledger it was replacing intact instead of truncated.
            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(ledger, Format));
            File.Move(pending, FilePath, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // The ledger that will not parse is kept under a name of its own instead of being written over. It
    // is the user's paid-for data whether or not BEM can read it today, and a file still on disk can be
    // repaired by hand, while one overwritten cannot. A ledger that cannot even be looked at, because
    // something else holds it open, stops the save outright: that one is not corrupt, only unavailable.
    private bool KeepUnreadableAside()
    {
        try
        {
            Read();
            return true;
        }
        catch (JsonException)
        {
        }

        var kept = Path.Combine(
            Path.GetDirectoryName(FilePath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(FilePath)}.unreadable-{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.json");

        File.Move(FilePath, kept, overwrite: false);

        return true;
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
