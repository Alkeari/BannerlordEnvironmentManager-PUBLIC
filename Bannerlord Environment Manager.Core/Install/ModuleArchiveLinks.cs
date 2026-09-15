using System.Text.Json;
using System.Text.Json.Serialization;

using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Install;

public enum ModuleArchiveEvidence
{
    Install,
    Backfill,
    // Read back out of BEM's own log, which names the archive it installed and the folder each module
    // landed in. A record of an install that happened, recovered from a different file, which is why
    // it outranks a name matched out of the Recycle Bin and not what BEM wrote down at the time.
    InstallLog,
    // Recovered from the name of an archive the user had already deleted, matched to a module by that
    // name alone. The weakest evidence there is, and marked so it stays tellable from the rest.
    RecycleBin
}

// Which archive put a module on disk, and everything else about that moment worth keeping. The mod id
// is read back out of the filename rather than stored beside it, because Nexus rewrote that grammar
// three times in 26 days and a stored id would keep asserting whatever the parser believed on the day
// it was written. A recorded id overrides it only when something authoritative supplied one: a nxm
// link, or Nexus answering about the archive's own hash.
public sealed record ModuleArchiveLink(
    string ModuleId,
    string ArchiveFileName,
    DateTimeOffset RecordedUtc,
    ModuleArchiveEvidence Evidence = ModuleArchiveEvidence.Install,
    long? ArchiveSizeBytes = null,
    string? ArchiveMd5 = null,
    DateTimeOffset? ArchiveModifiedUtc = null,
    int? RecordedNexusModId = null,
    int? RecordedNexusFileId = null,
    // The name Nexus itself gave the file, when the copy on disk was renamed on the way in.
    string? NexusFileName = null,
    // What Nexus called the version of the exact file that was installed. This is the only version
    // string that can be compared with the version Nexus lists today without comparing two different
    // authors' habits, which is why it is kept apart from the manifest's own version.
    string? NexusVersionAtInstall = null,
    DateTimeOffset? NexusUploadedUtc = null,
    string? ModuleVersion = null,
    // A digest of the module's files as they were left on disk. Says whether anything has changed
    // since, and nothing about what Nexus holds: Nexus publishes no hash of an extracted module.
    string? InstalledFingerprint = null)
{
    [JsonIgnore]
    public int? NexusModId => RecordedNexusModId ?? NexusArchiveName.TryGetModId(ArchiveFileName);

    [JsonIgnore]
    public int? NexusFileId => RecordedNexusFileId ?? NexusArchiveName.TryGetFileId(ArchiveFileName);

    // Whether this record could still identify the mod if the archive were deleted this second, which
    // for most users it is about to be.
    [JsonIgnore]
    public bool IdentifiesTheFile => NexusModId is not null && (NexusFileId is not null || ArchiveMd5 is not null);

    // The stored record fills the gaps in this one. Nothing already known is ever overwritten with a
    // null, because a later install that could not read a hash must not erase the hash of the copy it
    // is describing.
    public ModuleArchiveLink Fill(ModuleArchiveLink? other) =>
        other is null
            ? this
            : this with
            {
                ArchiveSizeBytes = ArchiveSizeBytes ?? other.ArchiveSizeBytes,
                ArchiveMd5 = ArchiveMd5 ?? other.ArchiveMd5,
                ArchiveModifiedUtc = ArchiveModifiedUtc ?? other.ArchiveModifiedUtc,
                RecordedNexusModId = RecordedNexusModId ?? other.RecordedNexusModId,
                RecordedNexusFileId = RecordedNexusFileId ?? other.RecordedNexusFileId,
                NexusFileName = NexusFileName ?? other.NexusFileName,
                NexusVersionAtInstall = NexusVersionAtInstall ?? other.NexusVersionAtInstall,
                NexusUploadedUtc = NexusUploadedUtc ?? other.NexusUploadedUtc,
                ModuleVersion = ModuleVersion ?? other.ModuleVersion,
                InstalledFingerprint = InstalledFingerprint ?? other.InstalledFingerprint
            };

    public ModuleArchiveLink With(ArchiveIdentity identity) =>
        this with
        {
            ArchiveSizeBytes = identity.SizeBytes ?? ArchiveSizeBytes,
            ArchiveMd5 = identity.Md5 ?? ArchiveMd5,
            ArchiveModifiedUtc = identity.ModifiedUtc ?? ArchiveModifiedUtc,
            RecordedNexusModId = identity.NexusModId ?? RecordedNexusModId,
            RecordedNexusFileId = identity.NexusFileId ?? RecordedNexusFileId,
            NexusFileName = identity.NexusFileName ?? NexusFileName,
            NexusVersionAtInstall = identity.NexusVersion ?? NexusVersionAtInstall,
            NexusUploadedUtc = identity.NexusUploadedUtc ?? NexusUploadedUtc
        };
}

// Failed is what the file would have held if the write had worked. A caller that reads Added and
// Replaced is asking what BEM now knows, and answering with links that never reached the disk would
// make a lost file read as a learned one.
public sealed record ModuleArchiveLinkRecord(int Added, int Replaced, int Unchanged, int Failed = 0)
{
    public int Total => Added + Replaced + Unchanged;
}

// A hint store and nothing more. Everything BEM does works identically with this file absent, deleted
// or full of names that parse to nothing: a module with no id here is simply one update checking
// cannot see, and it is told so rather than guessed at.
public sealed class ModuleArchiveLinkStore
{
    public const string FileName = "module-archive-links.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ModuleArchiveLinkStore(string? filePath) => FilePath = filePath;

    // The resting version's store, and the fallback for a caller that named none. It is the exact file
    // it has always been, so nothing recorded on a machine today moves or is orphaned.
    public static ModuleArchiveLinkStore Default { get; } = new(DefaultPath());

    public static ModuleArchiveLinkStore For(InstanceDataRoot? dataRoot) => new(DefaultPath(dataRoot));

    // For a caller that wants the manifest and nothing else, and for tests that must not read
    // whatever this machine happens to have installed.
    public static ModuleArchiveLinkStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    // Per version. A link is keyed on the module id and nothing else, so installing Improved Garrisons
    // 2.0 into one version replaced the record of the 1.9 sitting in another: that version's Nexus
    // column then reported it up to date against a file id it does not have, its InstalledFingerprint
    // no longer matched the folder on disk so the row read "changed since install", and Open mod page
    // pointed at the wrong file.
    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public IReadOnlyList<ModuleArchiveLink> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ModuleArchiveLink>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, ModuleArchiveLink> ByModuleId()
    {
        var links = new Dictionary<string, ModuleArchiveLink>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in Load().OrderBy(link => link.RecordedUtc))
            links[link.ModuleId] = link;

        return links;
    }

    public IReadOnlyDictionary<string, string> ArchiveFileNamesByModuleId()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in Load().OrderBy(link => link.RecordedUtc))
            names[link.ModuleId] = link.ArchiveFileName;

        return names;
    }

    // An install watched the file go in. A backfill reads back a link BEM itself wrote down at the
    // time. An install log is BEM's own account of an install it performed, recovered by reading that
    // account back. A recycle bin name is matched to a module by its name and nothing else. Weaker
    // evidence never overwrites stronger, because the weaker kind is the kind that can name the wrong
    // mod page.
    private static int Strength(ModuleArchiveEvidence evidence) => evidence switch
    {
        ModuleArchiveEvidence.Install => 3,
        ModuleArchiveEvidence.Backfill => 2,
        ModuleArchiveEvidence.InstallLog => 1,
        _ => 0
    };

    // The newest record for a module wins, because installing a different archive over a module moves
    // it to whatever page that archive came from.
    public ModuleArchiveLinkRecord Record(IEnumerable<ModuleArchiveLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var incoming = links.ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new ModuleArchiveLinkRecord(0, 0, 0);

        var existing = Load().ToDictionary(link => link.ModuleId, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var replaced = 0;
        var unchanged = 0;

        foreach (var link in incoming)
        {
            if (!existing.TryGetValue(link.ModuleId, out var already))
            {
                existing[link.ModuleId] = link;
                added++;
                continue;
            }

            if (Strength(link.Evidence) < Strength(already.Evidence))
            {
                unchanged++;
                continue;
            }

            // The same archive said again, but possibly said with more detail than last time: a hash
            // that could not be read then, or a version an authenticated lookup has since supplied.
            // Learning more about a record already held is a change, and saying "unchanged" while
            // writing new fields would be the message overstating nothing and understating something.
            if (string.Equals(already.ArchiveFileName, link.ArchiveFileName, StringComparison.OrdinalIgnoreCase))
            {
                var merged = link.Fill(already);

                if (merged with { RecordedUtc = already.RecordedUtc, Evidence = already.Evidence } == already)
                {
                    unchanged++;
                    continue;
                }

                existing[link.ModuleId] = merged;
                replaced++;
                continue;
            }

            existing[link.ModuleId] = link;
            replaced++;
        }

        if (added + replaced > 0
            && !Write([.. existing.Values.OrderBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new ModuleArchiveLinkRecord(0, 0, 0, added + replaced);

        return new ModuleArchiveLinkRecord(added, replaced, unchanged);
    }

    // Written beside the real file and moved over it, so a write that stops halfway leaves every link
    // already recorded intact rather than truncated. False means nothing on disk changed, and the
    // caller has to report that rather than a link BEM does not hold.
    private bool Write(IReadOnlyList<ModuleArchiveLink> links)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(links, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
