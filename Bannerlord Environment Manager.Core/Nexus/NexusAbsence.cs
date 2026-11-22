using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Why a module has no Nexus id when the archive it was installed from is on this machine: Nexus was
// asked about that archive's hash and answered 404, so no published file matches it (a Patreon release,
// a repack, a file from somewhere else). It explains the gap and is never a verdict: the hash memory
// expires, and nothing here reaches not-on-nexus.json.
public static class NexusAbsence
{
    public static IReadOnlySet<string> ModulesWithUnpublishedArchive(
        IEnumerable<NexusModuleLink> links,
        IReadOnlyDictionary<string, ModuleArchiveLink> recorded,
        IReadOnlySet<string> unrecognizedHashes)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(unrecognizedHashes);

        return links
            .Where(link => link.NexusModId is null
                           && recorded.TryGetValue(link.ModuleId, out var install)
                           && install.ArchiveMd5 is { Length: > 0 } md5
                           && unrecognizedHashes.Contains(md5.ToLowerInvariant()))
            .Select(link => link.ModuleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
