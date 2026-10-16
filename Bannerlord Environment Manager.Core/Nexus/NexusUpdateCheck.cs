using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusUpdateStatus
{
    Disabled,
    NoApiKey,
    NothingCheckable,
    // Nexus answered and named no new file for any installed module. That is not "up to date": a mod
    // that did not change in the window asked about may have been out of date before it started, and
    // this used to be called UpToDate while establishing nothing of the kind.
    NoNewFiles,
    UpdatesFound,
    Unreachable,
    RateLimited,
    Unauthorized,
    ServiceError,
    Canceled
}

public sealed record NexusModuleUpdate(
    string ModuleId,
    string ModuleName,
    int NexusModId,
    string ModPageUrl,
    DateTimeOffset LatestFileUpdate,
    string? NexusVersion = null);

public sealed record NexusUpdateRequest(
    IReadOnlyList<NexusModuleLink> Modules,
    NexusApiKey? ApiKey,
    bool Enabled,
    NexusUpdatePeriod Period,
    TimeSpan CacheLifetime,
    DateTimeOffset Now,
    bool ForceRefresh = false,
    // A runaway guard, not a budget. It was 250, which is fewer than the mod ids a large install can
    // hold, so a first run stopped short and reported modules as unknown for a reason that had nothing
    // to do with the modules. Saving requests is not worth an unfinished answer, so this is now set
    // well above any real load order and the live X-RL headers are what actually decide when to stop.
    int VersionLookupLimit = 5000,
    // Requests BEM will not spend. The quota belongs to the user and is shared with whatever else they
    // have signed in to Nexus, so an update check stops with visible headroom rather than leaving their
    // downloads to fail on an exhausted limit.
    int RateLimitReserve = 20);

public sealed record NexusUpdateReport(
    NexusUpdateStatus Status,
    string Message,
    IReadOnlyList<NexusModuleUpdate> Updates,
    int CheckableModuleCount,
    int UncheckableModuleCount,
    DateTimeOffset? DataFetchedUtc,
    bool FromCache,
    NexusRateLimit RateLimit,
    // One verdict per installed community module, including the ones nothing could be said about.
    // Updates lists only what the Nexus feed named; this lists everything, so a module that fell out
    // of the feed is visible as "cannot tell" instead of being silently absent.
    IReadOnlyList<ModuleUpdateVerdict>? Verdicts = null,
    // How much of the load order BEM has actually asked Nexus about, and why it stopped where it did.
    // Null only where no version ledger was consulted at all.
    NexusVersionSweepResult? Sweep = null,
    // Modules Nexus was not asked about, counted separately because they are outside the checkable and
    // uncheckable split. They are not gaps: they are modules Nexus is the wrong place to ask, and each
    // one carries a verdict of its own, so they are inside the tally like everything else.
    int WorkshopModuleCount = 0,
    int NotOnNexusModuleCount = 0,
    int BuiltLocallyModuleCount = 0)
{
    public ModuleUpdateTally Tally => ModuleUpdateVerdicts.Tally(Verdicts ?? []);

    public int ExcludedModuleCount => WorkshopModuleCount + NotOnNexusModuleCount + BuiltLocallyModuleCount;

    // Where the up-to-date count came by its answer for these, since it did not come from Nexus. It
    // used to end "so they were not checked", which read as a shortfall in a tally they were missing
    // from; they are counted now, and this says on whose authority.
    public string? DescribeExcluded()
    {
        var parts = new List<string>();

        if (WorkshopModuleCount > 0)
            parts.Add(Strings.Current.Plural("Core.Nexus.UpdateCheck.Excluded.Workshop", WorkshopModuleCount));

        if (NotOnNexusModuleCount > 0)
            parts.Add(Strings.Current.Plural("Core.Nexus.UpdateCheck.Excluded.NotOnNexus", NotOnNexusModuleCount));

        if (BuiltLocallyModuleCount > 0)
            parts.Add(Strings.Current.Plural("Core.Nexus.UpdateCheck.Excluded.BuiltLocally", BuiltLocallyModuleCount));

        return parts.Count == 0
            ? null
            : Strings.Current.Format("Core.Nexus.UpdateCheck.Excluded.Summary", string.Join(Strings.Current["Core.Nexus.UpdateCheck.Excluded.Joiner"], parts));
    }
}

// Information only. Nothing here downloads and nothing here installs: a manager that silently
// updates a working setup is exactly how a working setup stops working.
//
// Two questions are asked, not one. The feed answers "what changed lately" in a single request for the
// whole game, and the sweep answers "what version is this at" for every module BEM knows a mod id for.
// Only the second one can put a number beside the number on disk, which is why the first one alone
// left almost every module at "cannot tell". The sweep is expensive once and nearly free afterwards,
// because every answer is written down and only a mod the feed says has moved is ever asked again.
public sealed class NexusUpdateCheck
{
    private readonly NexusClient client;
    private readonly NexusUpdateCache cache;
    private readonly NexusVersionStore versions;
    private readonly NexusVersionSweep sweep;

    public NexusUpdateCheck(NexusClient client, NexusUpdateCache cache, NexusVersionStore? versions = null)
    {
        this.client = client;
        this.cache = cache;
        this.versions = versions ?? NexusVersionStore.Beside(cache);
        sweep = new NexusVersionSweep(client, this.versions);
    }

    // What is already on disk, read without asking Nexus anything. Opening a page must never turn into
    // a request the user did not ask for, and an answer that is already paid for should not be thrown
    // away. Null means nothing has ever been fetched, which is not the same statement as "nothing was
    // found", so the caller has to say which.
    public static NexusUpdateReport? FromCache(
        NexusUpdateCache cache,
        NexusUpdateRequest request,
        NexusVersionStore? versions = null)
    {
        if (!request.Enabled || cache.Load() is not { } cached || cached.Period != request.Period)
            return null;

        var checkable = request.Modules.Where(module => module.IsCheckable).ToList();
        var known = (versions ?? NexusVersionStore.Beside(cache)).Load().ByModId();

        // Nothing on this path asks Nexus anything, and nothing on it reads a module's files either.
        // It runs on every automatic refresh, including the one the Modules folder watcher starts the
        // moment a folder appears, and the reading comparison holds each file in a module open for the
        // length of its hash - which is a mod build's deploy into that folder refused for as long as
        // it takes. The verdict falls back to what BEM last recorded, exactly as it does for a module
        // no fingerprint was ever taken of; the check the user presses reads the folders for real.
        return Describe(cached, checkable, Uncheckable(request.Modules, checkable.Count), fromCache: true,
            NexusRateLimit.Unknown, request.Modules, NexusVersionSweepResult.FromLedger(checkable, known),
            contentsSinceInstall: WithoutReadingFolders);
    }

    private static ModuleContentComparison WithoutReadingFolders(NexusModuleLink link) =>
        InstalledModuleContents.CompareWithoutReading(link.FolderPath ?? string.Empty, link.InstalledFingerprint);

    // Modules Nexus could have been asked about and was not, which is not the same set as "everything
    // that is not checkable": a Workshop subscription and a mod the user never published are outside
    // this feature rather than gaps in it.
    private static int Uncheckable(IReadOnlyList<NexusModuleLink> all, int checkable) =>
        all.Count(module => !module.IsExcluded) - checkable;

    private static int Workshop(IReadOnlyList<NexusModuleLink> all) =>
        all.Count(module => module.Exclusion is NexusCheckExclusion.SteamWorkshop);

    private static int NotOnNexus(IReadOnlyList<NexusModuleLink> all) =>
        all.Count(module => module.Exclusion is NexusCheckExclusion.OwnerSaysNotOnNexus);

    private static int BuiltLocally(IReadOnlyList<NexusModuleLink> all) =>
        all.Count(module => module.Exclusion is NexusCheckExclusion.BuiltLocally);

    public async Task<NexusUpdateReport> RunAsync(NexusUpdateRequest request, CancellationToken cancellationToken)
    {
        var checkable = request.Modules.Where(module => module.IsCheckable).ToList();
        var uncheckable = Uncheckable(request.Modules, checkable.Count);

        if (!request.Enabled)
            return Nothing(NexusUpdateStatus.Disabled,
                Strings.Current["Core.Nexus.UpdateCheck.Disabled"], 0, uncheckable + checkable.Count, request);

        if (request.ApiKey is not { } apiKey)
            return Nothing(NexusUpdateStatus.NoApiKey,
                Strings.Current["Core.Nexus.UpdateCheck.NoApiKey"],
                checkable.Count, uncheckable, request);

        if (checkable.Count == 0)
            return Nothing(NexusUpdateStatus.NothingCheckable,
                uncheckable == 0
                    ? Strings.Current["Core.Nexus.UpdateCheck.NothingCheckable.NoneAtAll"]
                    : Strings.Current.Plural("Core.Nexus.UpdateCheck.NothingCheckable.NoneWithId", uncheckable),
                0, uncheckable, request);

        var cached = cache.Load();

        if (!request.ForceRefresh
            && cached is not null
            && cached.Period == request.Period
            && cached.IsFreshAt(request.Now, request.CacheLifetime))
            return Describe(cached, checkable, uncheckable, fromCache: true, NexusRateLimit.Unknown, request.Modules,
                NexusVersionSweepResult.FromLedger(checkable, versions.Load().ByModId()));

        var result = await client.GetUpdatedModsAsync(apiKey, request.Period, cancellationToken);

        if (result is { IsOk: true, Value: { } mods })
        {
            var changed = mods.DistinctBy(mod => mod.ModId).ToDictionary(mod => mod.ModId);

            var swept = await sweep.RunAsync(apiKey, checkable, changed, request.VersionLookupLimit,
                request.RateLimitReserve, result.RateLimit, request.Now, cancellationToken);

            var feed = new NexusCachedFeed(request.Now, request.Period, mods, FeedVersions(changed.Keys, swept.Known));
            cache.Save(feed);

            return Describe(feed, checkable, uncheckable, fromCache: false, swept.RateLimit, request.Modules, swept);
        }

        var status = result.Outcome switch
        {
            NexusOutcome.Unauthorized => NexusUpdateStatus.Unauthorized,
            NexusOutcome.RateLimited => NexusUpdateStatus.RateLimited,
            NexusOutcome.Canceled => NexusUpdateStatus.Canceled,
            NexusOutcome.Unreachable => NexusUpdateStatus.Unreachable,
            _ => NexusUpdateStatus.ServiceError
        };

        // Stale beats nothing, but only when it is dated and only when the failure is still the
        // headline. Nobody is told "no updates" on the strength of a request that never landed.
        var fallback = status is NexusUpdateStatus.Unreachable or NexusUpdateStatus.RateLimited or NexusUpdateStatus.ServiceError
            ? cached
            : null;

        // The feed is what failed, not the ledger. Every version BEM has already paid for still counts,
        // so a network blip does not throw the load order back to knowing nothing.
        var ledger = NexusVersionSweepResult.FromLedger(checkable, versions.Load().ByModId());

        return new NexusUpdateReport(
            status,
            fallback is null
                ? result.Message
                : Strings.Current.Format(
                    "Core.Nexus.UpdateCheck.ShowingLastFetched",
                    result.Message,
                    $"{fallback.FetchedUtc:yyyy-MM-dd HH:mm}"),
            fallback is null ? [] : Matches(fallback, checkable, VersionMap(fallback, ledger.Known)),
            checkable.Count,
            uncheckable,
            fallback?.FetchedUtc,
            fallback is not null,
            result.RateLimit,
            fallback is null ? Unknown(request.Modules) : Verdicts(fallback, request.Modules, ledger.Known),
            ledger,
            Workshop(request.Modules),
            NotOnNexus(request.Modules));
    }

    // The headline is the four-state tally rather than the feed's own sentence. "Nexus recorded no new
    // files this week" is true and reads as "you are up to date", which for a module BEM holds no
    // Nexus version for is a claim nothing here can support.
    private static NexusUpdateReport Describe(
        NexusCachedFeed feed,
        IReadOnlyList<NexusModuleLink> checkable,
        int uncheckable,
        bool fromCache,
        NexusRateLimit rateLimit,
        IReadOnlyList<NexusModuleLink> all,
        NexusVersionSweepResult sweep,
        Func<NexusModuleLink, ModuleContentComparison>? contentsSinceInstall = null)
    {
        var versions = VersionMap(feed, sweep.Known);
        var updates = Matches(feed, checkable, versions);
        var verdicts = Verdicts(feed, all, sweep.Known, contentsSinceInstall);

        var message = ModuleUpdateVerdicts.Tally(verdicts).Describe();

        if (updates.Count > 0)
            message += " " + Strings.Current.Format("Core.Nexus.UpdateCheck.NewFilesRecorded", NexusEndpoints.Describe(feed.Period), updates.Count);

        if (uncheckable > 0)
            message += " " + Strings.Current.Format("Core.Nexus.UpdateCheck.NoModIdFor", uncheckable);

        // Said once, where a reader would otherwise wonder why the unknown count is not zero.
        if (sweep.Describe() is { } coverage)
            message += " " + coverage;

        var report = new NexusUpdateReport(
            updates.Count == 0 ? NexusUpdateStatus.NoNewFiles : NexusUpdateStatus.UpdatesFound,
            message,
            updates,
            checkable.Count,
            uncheckable,
            feed.FetchedUtc,
            fromCache,
            rateLimit,
            verdicts,
            sweep,
            Workshop(all),
            NotOnNexus(all),
            BuiltLocally(all));

        return report.DescribeExcluded() is { } excluded
            ? report with { Message = report.Message + " " + excluded }
            : report;
    }

    private static IReadOnlyList<ModuleUpdateVerdict> Verdicts(
        NexusCachedFeed feed,
        IReadOnlyList<NexusModuleLink> all,
        IReadOnlyDictionary<int, NexusModVersionRecord> known,
        Func<NexusModuleLink, ModuleContentComparison>? contentsSinceInstall = null) =>
        ModuleUpdateVerdicts.From(
            all,
            feed.Mods.DistinctBy(mod => mod.ModId).ToDictionary(mod => mod.ModId),
            VersionMap(feed, known),
            feed.Period,
            answered: true,
            LookupMap(all, known),
            NexusVersionLedger.FilesIn(known),
            contentsSinceInstall);

    // Nexus was not asked, or did not answer. Every module comes back "cannot tell", which is the
    // whole point: a check that did not happen must never read as a check that found nothing.
    private static IReadOnlyList<ModuleUpdateVerdict> Unknown(IReadOnlyList<NexusModuleLink> all) =>
        ModuleUpdateVerdicts.From(all, new Dictionary<int, NexusUpdatedMod>(), new Dictionary<int, string?>(),
            NexusUpdatePeriod.Week, answered: false);

    // The ledger is where a version lives now. The feed cache still carries the versions belonging to
    // the mods it named, so a cache file written by an older build keeps working, and it wins where the
    // two disagree because it was fetched in the same breath as the feed.
    private static IReadOnlyDictionary<int, string?> VersionMap(
        NexusCachedFeed feed,
        IReadOnlyDictionary<int, NexusModVersionRecord> known)
    {
        var map = new Dictionary<int, string?>();

        foreach (var record in known.Values)
            map[record.ModId] = record.Version;

        foreach (var version in (feed.Versions ?? []).DistinctBy(version => version.ModId))
        {
            if (version.Version is { Length: > 0 })
                map[version.ModId] = version.Version;
        }

        return map;
    }

    // A module absent from this map has not been asked about at all, which the verdict has to say in
    // different words from a module Nexus was asked about and had no version for.
    private static IReadOnlyDictionary<int, NexusVersionLookup> LookupMap(
        IReadOnlyList<NexusModuleLink> all,
        IReadOnlyDictionary<int, NexusModVersionRecord> known)
    {
        var map = new Dictionary<int, NexusVersionLookup>();

        foreach (var link in all)
        {
            if (link.NexusModId is { } modId)
                map[modId] = known.TryGetValue(modId, out var record) ? record.Outcome : NexusVersionLookup.NotAsked;
        }

        return map;
    }

    private static IReadOnlyList<NexusModVersion> FeedVersions(
        IEnumerable<int> feedModIds,
        IReadOnlyDictionary<int, NexusModVersionRecord> known) =>
    [
        .. feedModIds
            .Where(known.ContainsKey)
            .Select(modId => new NexusModVersion(modId, known[modId].Version))
    ];

    private static IReadOnlyList<NexusModuleUpdate> Matches(
        NexusCachedFeed feed,
        IReadOnlyList<NexusModuleLink> checkable,
        IReadOnlyDictionary<int, string?> versions)
    {
        var changed = feed.Mods.DistinctBy(mod => mod.ModId).ToDictionary(mod => mod.ModId);

        return
        [
            .. checkable
                .Where(module => module.NexusModId is { } id && changed.ContainsKey(id))
                .Select(module => new NexusModuleUpdate(
                    module.ModuleId,
                    module.ModuleName,
                    module.NexusModId!.Value,
                    module.ModPageUrl!,
                    changed[module.NexusModId.Value].LatestFileUpdate,
                    versions.GetValueOrDefault(module.NexusModId.Value)))
                .OrderByDescending(update => update.LatestFileUpdate)
        ];
    }

    private static NexusUpdateReport Nothing(
        NexusUpdateStatus status,
        string message,
        int checkable,
        int uncheckable,
        NexusUpdateRequest request) =>
        new(status, message, [], checkable, uncheckable, null, false, NexusRateLimit.Unknown, Unknown(request.Modules),
            null, Workshop(request.Modules), NotOnNexus(request.Modules));
}
