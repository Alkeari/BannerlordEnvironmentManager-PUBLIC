namespace BannerlordEnvironmentManager.Core.Nexus;

// Two lists compared by Nexus mod page: what the account tracks on Nexus and what is installed here.
// ToTrack is every installed page Nexus does not yet track; NotInstalled is every tracked page with no
// module installed, which is Vortex's tracked list read as a wish list. Nothing is ever untracked: a
// mod the owner stopped using may still be one they want to hear about.
public sealed record NexusTrackedSyncPlan(IReadOnlyList<int> ToTrack, IReadOnlyList<int> NotInstalled, int InBoth)
{
    public bool IsInSync => ToTrack.Count == 0;
}

public static class NexusTrackedSync
{
    public static NexusTrackedSyncPlan Plan(IEnumerable<int> tracked, IEnumerable<NexusModuleLink> installed, IEnumerable<int>? declined = null)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(installed);

        var onNexus = new HashSet<int>(tracked);
        var local = new HashSet<int>(installed.Where(link => link.NexusModId is not null).Select(link => link.NexusModId!.Value));
        var skip = new HashSet<int>(declined ?? []);

        return new NexusTrackedSyncPlan(
            [.. local.Where(id => !onNexus.Contains(id) && !skip.Contains(id)).Order()],
            [.. onNexus.Where(id => !local.Contains(id)).Order()],
            local.Count(onNexus.Contains));
    }

    // Tracks each page in turn and stops at the first refusal, since a rate limit or a rejected key
    // applies to every request after it. Returns the pages that are now tracked and the failure, if any.
    public static async Task<(IReadOnlyList<int> Tracked, NexusResult<string>? Failure)> ApplyAsync(
        NexusClient client,
        NexusApiKey apiKey,
        IEnumerable<int> pages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var done = new List<int>();

        foreach (var page in pages)
        {
            var result = await client.TrackModAsync(apiKey, page, cancellationToken);

            if (!result.IsOk)
                return (done, result);

            done.Add(page);
        }

        return (done, null);
    }
}
