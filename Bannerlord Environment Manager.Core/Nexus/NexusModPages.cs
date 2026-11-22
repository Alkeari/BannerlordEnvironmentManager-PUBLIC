namespace BannerlordEnvironmentManager.Core.Nexus;

// Which modules have a Nexus page BEM can open, read off the links the rest of the Nexus tab uses so
// the Open Mod Page action and the "has a Nexus id" mark can never disagree.
public static class NexusModPages
{
    public static IReadOnlyDictionary<string, int> IdsByModuleId(IEnumerable<NexusModuleLink> links) =>
        links
            .Where(link => link.NexusModId is not null)
            .DistinctBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(link => link.ModuleId, link => link.NexusModId!.Value, StringComparer.OrdinalIgnoreCase);
}
