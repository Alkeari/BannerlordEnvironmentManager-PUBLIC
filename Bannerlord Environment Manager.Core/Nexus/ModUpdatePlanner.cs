namespace BannerlordEnvironmentManager.Core.Nexus;

// One file an autonomous update should fetch: the module, its Nexus page, and the current file on
// that page that has superseded what is installed.
public sealed record ModUpdateTarget(
    string ModuleId,
    string ModuleName,
    int NexusModId,
    int FileId,
    string? FileName,
    string? Version)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ModuleName) ? ModuleId : ModuleName;
}

// Works out which newer file to fetch for each module, from the same evidence the update check used.
// It only ever plans a fetch for a module Nexus has moved out of its current files, and it names the
// file at the end of Nexus's own replacement chain as the target. An optional or support file updated
// later than the main file must not win the fetch, which is what following the chain avoids.
public static class ModUpdatePlanner
{
    public static IReadOnlyList<ModUpdateTarget> Plan(
        IEnumerable<NexusModuleLink> links,
        IReadOnlyDictionary<int, NexusModFileListing> filesByModId)
    {
        var planned = new List<ModUpdateTarget>();

        foreach (var link in links)
        {
            if (link.NexusModId is not { } modId)
                continue;

            if (!filesByModId.TryGetValue(modId, out var listing))
                continue;

            var finding = NexusFileComparison.Find(link, listing);

            if (finding.Standing is not InstalledFileStanding.Superseded)
                continue;

            if (Target(finding.Installed?.FileId, listing) is not { } target)
                continue;

            planned.Add(new ModUpdateTarget(
                link.ModuleId, link.ModuleName, modId, target.FileId, target.FileName, target.Version));
        }

        return planned;
    }

    // Nexus records a replacement one hop at a time, so the file that replaced the installed one is
    // found by walking the chain, and the file that ends it is the one to fetch. Where no chain names a
    // target, the newest current file stands in because an out-of-date verdict already said the
    // installed file is no longer current; only a page with no identifiable current file refuses to plan.
    private static NexusModFile? Target(int? installedFileId, NexusModFileListing listing)
    {
        if (installedFileId is { } start)
        {
            var seen = new HashSet<int> { start };
            var current = start;
            NexusFileReplacement? lastHop = null;

            while (listing.Replacements.FirstOrDefault(hop => hop.OldFileId == current) is { } next
                   && seen.Add(next.NewFileId))
            {
                lastHop = next;
                current = next.NewFileId;
            }

            if (lastHop is not null && listing.Files.FirstOrDefault(file => file.FileId == current) is { } chained)
                return chained;
        }

        return NewestCurrent(listing);
    }

    private static NexusModFile? NewestCurrent(NexusModFileListing listing) =>
        listing.Files
            .Where(file => file.Standing is NexusFileStanding.Current)
            .OrderByDescending(file => file.UploadedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
}
