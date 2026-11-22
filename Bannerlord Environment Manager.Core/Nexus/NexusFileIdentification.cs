using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record FileIdentificationOutcome(
    int Asked,
    int Identified,
    int NotOnNexus,
    int Unanswered,
    string Message,
    IReadOnlyList<ModuleArchiveLink> Learned,
    // The hashes Nexus answered 404 to on this run. Handed back rather than written here, so that the
    // caller that owns the disk decides what is remembered, exactly as it already does for Learned.
    IReadOnlyList<string> Unrecognized)
{
    public static FileIdentificationOutcome Nothing(string message) => new(0, 0, 0, 0, message, [], []);
}

// What a press would spend, split by what it would buy. Askable is the only part a request can teach
// anything: AnsweredFromCache is already on disk and Unrecognized is a 404 BEM has already collected.
public sealed record FileIdentificationCandidates(
    IReadOnlyList<ModuleArchiveLink> Askable,
    IReadOnlyList<ModuleArchiveLink> AnsweredFromCache,
    IReadOnlyList<ModuleArchiveLink> Unrecognized)
{
    public static FileIdentificationCandidates None { get; } = new([], [], []);

    // Every archive that has a hash and no Nexus version, whatever became of it here. Zero means there
    // was nothing to ask about at all, which is a different sentence from "there is nothing left worth
    // asking".
    public int Considered => Askable.Count + AnsweredFromCache.Count + Unrecognized.Count;
}

// Turns the hash BEM took of an archive into what Nexus says that archive is: the mod page, the file
// id, the file name and, most usefully, the version Nexus itself gave that exact file. That last one
// is the only version string comparable with what Nexus lists later without comparing two different
// people's numbering.
//
// It works identically for an archive downloaded by hand and one fetched through a nxm link, because
// the hash is taken from the bytes and the bytes are the same. It needs an API key; nothing else in
// update checking does.
public sealed class NexusFileIdentification(NexusClient client)
{
    // A recorded archive whose hash BEM has and whose Nexus version it does not. Anything already
    // identified is not asked about again: the request budget belongs to the user and is shared with
    // whatever other mod manager they run.
    public static IReadOnlyList<ModuleArchiveLink> Worth(IEnumerable<ModuleArchiveLink> recorded) =>
    [
        .. recorded
            .Where(link => link.ArchiveMd5 is { Length: > 0 } && link.NexusVersionAtInstall is not { Length: > 0 })
            .DistinctBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
    ];

    // The same set, split by whether a request could still learn anything from it. Two things spend a
    // request to be told what BEM already holds: a mod page whose file list is cached and already
    // carries the exact file this archive is, and a hash Nexus has already said it does not know.
    public static FileIdentificationCandidates Select(
        IEnumerable<ModuleArchiveLink> recorded,
        IReadOnlyDictionary<int, NexusModFileListing> cachedListings,
        IReadOnlySet<string> unrecognizedHashes)
    {
        ArgumentNullException.ThrowIfNull(cachedListings);
        ArgumentNullException.ThrowIfNull(unrecognizedHashes);

        var askable = new List<ModuleArchiveLink>();
        var cached = new List<ModuleArchiveLink>();
        var unrecognized = new List<ModuleArchiveLink>();

        foreach (var candidate in Worth(recorded))
        {
            if (IsAnsweredByCache(candidate, cachedListings))
                cached.Add(candidate);
            else if (unrecognizedHashes.Contains(candidate.ArchiveMd5!))
                unrecognized.Add(candidate);
            else
                askable.Add(candidate);
        }

        return new FileIdentificationCandidates(askable, cached, unrecognized);
    }

    // Whether the cached file list already says everything the hash lookup would. It does exactly when
    // BEM knows which published file this archive is and holds Nexus's own list of that page's files
    // with that file in it: the answer md5_search returns is that file's entry, and it is already here.
    //
    // Only a file id from a source that names one exact file counts. Nexus's own download filenames
    // carry no file id and are not a contract, so the pair is taken either from what BEM recorded at
    // download time or from the "nexus-{modId}-{fileId}" name BEM writes itself, and from nowhere else.
    private static bool IsAnsweredByCache(
        ModuleArchiveLink link,
        IReadOnlyDictionary<int, NexusModFileListing> cachedListings) =>
        ExactPublishedFile(link) is { } published
        && cachedListings.TryGetValue(published.ModId, out var listing)
        && listing.Files.Any(file => file.FileId == published.FileId);

    private static (int ModId, int FileId)? ExactPublishedFile(ModuleArchiveLink link) =>
        link is { RecordedNexusModId: { } recordedModId, RecordedNexusFileId: { } recordedFileId }
            ? (recordedModId, recordedFileId)
            : NexusArchiveName.TryRead(link.ArchiveFileName) is { FileId: { } nameFileId } parts
                ? (parts.ModId, nameFileId)
                : null;

    public async Task<FileIdentificationOutcome> RunAsync(
        IReadOnlyList<ModuleArchiveLink> candidates,
        NexusApiKey apiKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return FileIdentificationOutcome.Nothing(Strings.Current["Core.Nexus.FileIdentification.NothingRecorded"]);

        var learned = new List<ModuleArchiveLink>();
        var unrecognized = new List<string>();
        var asked = 0;
        var identified = 0;
        var absent = 0;
        var unanswered = 0;
        string? stopped = null;

        foreach (var candidate in candidates.Take(Math.Max(limit, 0)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            asked++;

            var result = await client.FindFileByMd5Async(apiKey, candidate.ArchiveMd5!, cancellationToken);

            if (result is { IsOk: true, Value: { } file })
            {
                learned.Add(candidate.With(new ArchiveIdentity(
                    candidate.ArchiveFileName,
                    NexusModId: file.ModId,
                    NexusFileId: file.FileId,
                    NexusFileName: file.FileName,
                    NexusVersion: file.Version,
                    NexusUploadedUtc: file.UploadedUtc)));

                identified++;
                continue;
            }

            if (result.Outcome == NexusOutcome.NotFound)
            {
                absent++;
                unrecognized.Add(candidate.ArchiveMd5!);
                continue;
            }

            unanswered++;

            // A rejected key or a spent quota will reject every remaining request too, so carrying on
            // spends the user's budget to collect the same refusal N times.
            if (result.Outcome is NexusOutcome.Unauthorized or NexusOutcome.RateLimited)
            {
                stopped = result.Message;
                break;
            }
        }

        var remaining = candidates.Count - asked;

        var message = Strings.Current.Plural("Core.Nexus.FileIdentification.Summary", asked, identified, absent, unanswered);

        if (remaining > 0)
            message += " " + Strings.Current.Plural("Core.Nexus.FileIdentification.LeftForNextTime", remaining);

        if (stopped is { Length: > 0 })
            message += " " + Strings.Current.Format("Core.Nexus.FileIdentification.StoppedEarly", stopped);

        return new FileIdentificationOutcome(asked, identified, absent, unanswered, message, learned, unrecognized);
    }
}
