using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Why a sweep stopped, because "asked about everything" and "ran out of budget partway" are opposite
// statements and an incomplete sweep must never read as a complete one.
public enum NexusSweepStop
{
    // Every module that needed asking about was asked about.
    Complete,
    // No sweep was run at all: this is what a cached answer read back off disk reports.
    NotRun,
    RequestCeiling,
    // Stopped voluntarily, with requests still left, so the rest of the quota stays available to
    // whatever else the user does with Nexus today.
    QuotaHeadroom,
    // Nexus refused. The quota was already gone when the sweep reached it.
    RateLimited,
    Canceled
}

public sealed record NexusVersionSweepResult(
    int Checkable,
    int Covered,
    int Asked,
    NexusSweepStop Stop,
    NexusRateLimit RateLimit,
    IReadOnlyDictionary<int, NexusModVersionRecord> Known,
    bool Saved = true)
{
    public int Outstanding => Checkable - Covered;

    public bool IsComplete => Outstanding == 0;

    // What BEM already holds, with nothing asked. A page that reads the last answer off disk still has
    // to be able to say how much of the load order that answer covers.
    public static NexusVersionSweepResult FromLedger(
        IReadOnlyList<NexusModuleLink> checkable,
        IReadOnlyDictionary<int, NexusModVersionRecord> known)
    {
        var modIds = ModIds(checkable);

        return new NexusVersionSweepResult(
            modIds.Count, Covers(modIds, known), 0, NexusSweepStop.NotRun, NexusRateLimit.Unknown, known);
    }

    // One sentence, and only when there is a gap to name. A sweep that covered everything says nothing,
    // because a line that appears every time is a line nobody reads.
    public string? Describe()
    {
        // A sweep whose answers never reached the disk is said even when the sweep covered everything,
        // because the cost lands on the next run rather than this one: the same questions get asked
        // again, against the same quota.
        var unsaved = Saved
            ? null
            : Strings.Current["Core.Nexus.VersionSweep.Unsaved"];

        if (Checkable == 0 || Outstanding == 0)
            return unsaved;

        var why = Stop switch
        {
            NexusSweepStop.RateLimited or NexusSweepStop.QuotaHeadroom =>
                Strings.Current["Core.Nexus.VersionSweep.Why.QuotaLow"],
            NexusSweepStop.RequestCeiling =>
                Strings.Current["Core.Nexus.VersionSweep.Why.RequestCeiling"],
            NexusSweepStop.Canceled =>
                Strings.Current["Core.Nexus.VersionSweep.Why.Canceled"],
            NexusSweepStop.NotRun =>
                Strings.Current["Core.Nexus.VersionSweep.Why.NotRun"],
            _ =>
                Strings.Current["Core.Nexus.VersionSweep.Why.Default"]
        };

        var spent = Asked > 0 ? " " + Strings.Current.Plural("Core.Nexus.VersionSweep.RequestsSpent", Asked) : string.Empty;

        return Strings.Current.Plural("Core.Nexus.VersionSweep.Coverage", Checkable, Covered, Outstanding)
               + spent + " " + why
               + (unsaved is null ? string.Empty : " " + unsaved);
    }

    internal static List<int> ModIds(IEnumerable<NexusModuleLink> modules) =>
    [
        .. modules
            .Where(module => module.NexusModId is not null)
            .Select(module => module.NexusModId!.Value)
            .Distinct()
    ];

    // A failed lookup is not coverage. BEM asked and got nothing back, which leaves the module exactly
    // as unknown as it was before the request was spent.
    internal static int Covers(IEnumerable<int> modIds, IReadOnlyDictionary<int, NexusModVersionRecord> known) =>
        modIds.Count(id => known.TryGetValue(id, out var record)
                           && record.Outcome is NexusVersionLookup.Listed or NexusVersionLookup.NoVersionListed);
}

public sealed record NexusModNameSweepResult(
    IReadOnlyDictionary<int, string> Names,
    int Wanted,
    int Asked,
    int Learned,
    NexusSweepStop Stop,
    NexusRateLimit RateLimit,
    bool Saved = true)
{
    public int Unnamed => Wanted - Learned;

    // Said only when there is something to say. A run that named every page it set out to name adds
    // nothing to a screen that already lists them.
    public string? Describe()
    {
        var unsaved = Saved
            ? string.Empty
            : " " + Strings.Current["Core.Nexus.NameSweep.Unsaved"];

        if (Wanted == 0)
            return Strings.Current["Core.Nexus.NameSweep.AlreadyAsked"];

        if (Unnamed <= 0)
            return Strings.Current.Plural("Core.Nexus.NameSweep.Asked", Learned) + unsaved;

        var why = Stop switch
        {
            NexusSweepStop.RateLimited or NexusSweepStop.QuotaHeadroom =>
                Strings.Current["Core.Nexus.NameSweep.Why.QuotaOut"],
            NexusSweepStop.RequestCeiling =>
                Strings.Current["Core.Nexus.NameSweep.Why.RequestCeiling"],
            NexusSweepStop.Canceled =>
                Strings.Current["Core.Nexus.NameSweep.Why.Canceled"],
            _ =>
                Strings.Current["Core.Nexus.NameSweep.Why.Default"]
        };

        return Strings.Current.Plural("Core.Nexus.NameSweep.AskedOfWanted", Wanted, Learned) + " " + why + unsaved;
    }
}

// Asks Nexus what version every module BEM knows a mod id for is at, which is the only way the verdict
// rules ever get something to compare against. The feed of what changed narrows the whole game to the
// handful that moved, and that is the right question to ask second, not first: a module that has been
// out of date for a year does not appear in this week's feed at all.
//
// The cost is paid once. Every answer is written down, and a later sweep re-asks only about a mod the
// feed says has had a new file since BEM last looked, or one the last request never came back for.
public sealed class NexusVersionSweep(NexusClient client, NexusVersionStore store)
{
    public async Task<NexusVersionSweepResult> RunAsync(
        NexusApiKey apiKey,
        IReadOnlyList<NexusModuleLink> checkable,
        IReadOnlyDictionary<int, NexusUpdatedMod> changedByModId,
        int ceiling,
        int reserve,
        NexusRateLimit startingLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        DateTimeOffset? feedCoversSince = null)
    {
        var ledger = store.Load();
        var known = ledger.ByModId().ToDictionary(pair => pair.Key, pair => pair.Value);
        var modIds = NexusVersionSweepResult.ModIds(checkable);
        var stale = StaleModIds(checkable, known, feedCoversSince);

        NexusVersionSweepResult Result(int asked, NexusSweepStop stop, NexusRateLimit limit, bool saved = true) =>
            new(modIds.Count, NexusVersionSweepResult.Covers(modIds, known), asked, stop, limit, known, saved);

        if (ceiling <= 0)
            return Result(0, NexusSweepStop.RequestCeiling, startingLimit);

        // The file list is asked for about every mod, not only the ones BEM can name a file for. It used
        // to be withheld where nothing recorded which file a module came from, on the reasoning that
        // there would be nothing to match it against, and that reasoning was wrong: the current files on
        // a page carry versions, and a module declaring one of them is current whether or not BEM knows
        // which file it arrived in. Withholding it left the four BUTR core libraries permanently unknown
        // on a page BEM had never read the files of. It costs one request per mod once, and NeedsFiles
        // keeps it from being spent twice.
        var wanted = modIds
            .Where(id => NeedsAsking(id, known, changedByModId, stale) || NeedsFiles(id, known, changedByModId, stale))
            // The mods the feed says moved go first. A sweep the quota cuts short should have spent
            // what it had on the ones most likely to have gone out of date.
            .OrderByDescending(id => changedByModId.TryGetValue(id, out var changed)
                ? changed.LatestFileUpdate
                : DateTimeOffset.MinValue)
            .ToList();

        var rateLimit = startingLimit;
        var asked = 0;
        var fetched = 0;
        var stop = NexusSweepStop.Complete;

        NexusSweepStop? Blocked() =>
            cancellationToken.IsCancellationRequested ? NexusSweepStop.Canceled
            : asked >= ceiling ? NexusSweepStop.RequestCeiling
            : !HasHeadroom(rateLimit, reserve) ? NexusSweepStop.QuotaHeadroom
            : null;

        foreach (var modId in wanted)
        {
            if (NeedsAsking(modId, known, changedByModId, stale))
            {
                if (Blocked() is { } before)
                {
                    stop = before;
                    break;
                }

                var detail = await client.GetModAsync(apiKey, modId, cancellationToken);
                asked++;

                if (detail.RateLimit.IsKnown)
                    rateLimit = detail.RateLimit;

                if (Halted(detail.Outcome) is { } halted)
                {
                    stop = halted;
                    break;
                }

                var version = WithVersion(modId, detail, now, known.GetValueOrDefault(modId));

                // A version and the page name that came with it were paid for once and must not be lost
                // to a request that only failed this time. Anything Nexus refuses outright ends the
                // sweep above, so what reaches here is the 500 or the malformed body, and writing its
                // empty answer over a good one would spend a request to destroy the answer. The older
                // record is kept whole, and NeedsAsking still re-asks about it on the next run.
                if (version.Outcome != NexusVersionLookup.LookupFailed || !known.ContainsKey(modId))
                {
                    known[modId] = version;
                    fetched++;
                }
            }

            if (!NeedsFiles(modId, known, changedByModId, stale))
                continue;

            if (Blocked() is { } beforeFiles)
            {
                stop = beforeFiles;
                break;
            }

            var files = await client.GetModFilesAsync(apiKey, modId, cancellationToken);
            asked++;

            if (files.RateLimit.IsKnown)
                rateLimit = files.RateLimit;

            if (Halted(files.Outcome) is { } stoppedByFiles)
            {
                stop = stoppedByFiles;
                break;
            }

            known[modId] = WithFiles(modId, files, now, known.GetValueOrDefault(modId));
            fetched++;
        }

        // The category names are one request for the whole game, asked only once a mod page names a
        // category BEM has no name for, or a month after the names were read, so every category id
        // read above can be shown by the name Nexus files it under.
        if (stop == NexusSweepStop.Complete && NeedsCategories(ledger, known.Values, now) && Blocked() is null)
        {
            var categories = await client.GetCategoriesAsync(apiKey, cancellationToken);
            asked++;

            if (categories.RateLimit.IsKnown)
                rateLimit = categories.RateLimit;

            if (categories is { IsOk: true, Value: { Count: > 0 } read })
            {
                ledger = ledger with
                {
                    Categories = read.GroupBy(category => category.Id)
                        .ToDictionary(group => group.Key, group => group.First().Name),
                    CategoriesFetchedUtc = now
                };
                fetched++;
            }
        }

        // Everything paid for is kept, however the sweep ended. Throwing away a partial sweep would
        // make the next run start over and spend the quota twice for the same answers.
        var saved = fetched == 0
            || store.Save(ledger with { Records = [.. known.Values.OrderBy(record => record.ModId)] });

        return Result(asked, stop, rateLimit, saved);
    }

    // Asks Nexus what each of these mod pages is called. It is a separate entry point from the version
    // sweep because the question is asked about pages BEM is about to propose rather than about modules
    // already installed, but the answer lands in the same ledger: one Nexus request carries the name and
    // the version together, so neither is ever paid for twice.
    //
    // A name is what turns "is this module mod 791 or mod 4277" into a question a person can answer.
    // It ticks nothing and records nothing against any module: it only makes the proposal readable.
    public async Task<NexusModNameSweepResult> LearnNamesAsync(
        NexusApiKey apiKey,
        IReadOnlyList<int> modIds,
        int ceiling,
        int reserve,
        NexusRateLimit startingLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var ledger = store.Load();
        var known = ledger.ByModId().ToDictionary(pair => pair.Key, pair => pair.Value);

        var wanted = modIds
            .Distinct()
            .Where(id => !known.TryGetValue(id, out var record) || record.Name is not { Length: > 0 })
            .OrderBy(id => id)
            .ToList();

        NexusModNameSweepResult Result(
            int asked, int learned, NexusSweepStop stop, NexusRateLimit limit, bool saved = true) =>
            new(Names(known), wanted.Count, asked, learned, stop, limit, saved);

        if (wanted.Count == 0)
            return Result(0, 0, NexusSweepStop.Complete, startingLimit);

        if (ceiling <= 0)
            return Result(0, 0, NexusSweepStop.RequestCeiling, startingLimit);

        var rateLimit = startingLimit;
        var asked = 0;
        var learned = 0;
        var stop = NexusSweepStop.Complete;

        foreach (var modId in wanted)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stop = NexusSweepStop.Canceled;
                break;
            }

            if (asked >= ceiling)
            {
                stop = NexusSweepStop.RequestCeiling;
                break;
            }

            if (!HasHeadroom(rateLimit, reserve))
            {
                stop = NexusSweepStop.QuotaHeadroom;
                break;
            }

            var detail = await client.GetModAsync(apiKey, modId, cancellationToken);
            asked++;

            if (detail.RateLimit.IsKnown)
                rateLimit = detail.RateLimit;

            if (detail.Outcome == NexusOutcome.Canceled)
            {
                stop = NexusSweepStop.Canceled;
                break;
            }

            if (detail.Outcome == NexusOutcome.RateLimited)
            {
                stop = NexusSweepStop.RateLimited;
                break;
            }

            var record = Record(modId, detail, now);

            // A page BEM already holds a version for must not lose it to a request that only failed
            // this time, so an answer that came back with nothing is not written over one that did.
            if (record.Outcome == NexusVersionLookup.LookupFailed && known.ContainsKey(modId))
                continue;

            known[modId] = record;

            if (record.Name is { Length: > 0 })
                learned++;
        }

        var saved = learned == 0
            || store.Save(ledger with { Records = [.. known.Values.OrderBy(record => record.ModId)] });

        return Result(asked, learned, stop, rateLimit, saved);
    }

    private static IReadOnlyDictionary<int, string> Names(IReadOnlyDictionary<int, NexusModVersionRecord> known) =>
        known.Where(pair => pair.Value.Name is { Length: > 0 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.Name!);

    private static NexusModVersionRecord Record(int modId, NexusResult<NexusModDetail> detail, DateTimeOffset now) =>
        detail is { IsOk: true, Value: { } mod }
            ? mod.Version is { Length: > 0 } version
                ? new NexusModVersionRecord(modId, version, NexusVersionLookup.Listed, now, mod.Name, CategoryId: mod.CategoryId, CategoryAsked: true)
                : new NexusModVersionRecord(modId, null, NexusVersionLookup.NoVersionListed, now, mod.Name, CategoryId: mod.CategoryId, CategoryAsked: true)
            : new NexusModVersionRecord(modId, null, NexusVersionLookup.LookupFailed, now);

    // The version and the file list are two requests, paid for separately, and neither answer may
    // erase the other. A version fetched now leaves whatever file list is already held exactly as it
    // was, timestamp included.
    private static NexusModVersionRecord WithVersion(
        int modId,
        NexusResult<NexusModDetail> detail,
        DateTimeOffset now,
        NexusModVersionRecord? held) =>
        Record(modId, detail, now) with
        {
            Files = held?.Files,
            Replacements = held?.Replacements,
            FilesOutcome = held?.FilesOutcome ?? NexusFileListLookup.NotAsked,
            FilesFetchedUtc = held?.FilesFetchedUtc
        };

    // A list that did not come back leaves the last one on disk and is marked as failed, so nothing
    // paid for is thrown away and nothing stale is presented as current.
    private static NexusModVersionRecord WithFiles(
        int modId,
        NexusResult<NexusModFileListing> result,
        DateTimeOffset now,
        NexusModVersionRecord? held)
    {
        var record = held ?? new NexusModVersionRecord(modId, null, NexusVersionLookup.NotAsked, now);

        return result is { IsOk: true, Value: { } listing }
            ? record with
            {
                Files = listing.Files,
                Replacements = listing.Replacements,
                FilesOutcome = NexusFileListLookup.Listed,
                FilesFetchedUtc = now
            }
            : record with { FilesOutcome = NexusFileListLookup.LookupFailed };
    }

    // Nexus refusing, and the user stopping the run, are the two answers that end a sweep rather than
    // being written down about one mod.
    private static NexusSweepStop? Halted(NexusOutcome outcome) => outcome switch
    {
        NexusOutcome.Canceled => NexusSweepStop.Canceled,
        NexusOutcome.RateLimited => NexusSweepStop.RateLimited,
        _ => null
    };

    // The feed vouches for a written-down answer only from the moment it starts covering: a mod whose
    // answer is older than that may have moved in the gap and the feed cannot say. And a module installed
    // after its answer was written down is a file BEM never saw listed, so the answer may predate it.
    // Both were left alone before, which kept Marry Anyone and Frontline Mod "unknown" against versions
    // fetched two and a half months earlier while their installed copies were newer than either.
    public static HashSet<int> StaleModIds(
        IReadOnlyList<NexusModuleLink> checkable,
        IReadOnlyDictionary<int, NexusModVersionRecord> known,
        DateTimeOffset? feedCoversSince)
    {
        var stale = new HashSet<int>();

        foreach (var module in checkable)
        {
            if (module.NexusModId is not { } modId || !known.TryGetValue(modId, out var record))
                continue;

            var oldest = record.FilesFetchedUtc is { } files && files < record.FetchedUtc ? files : record.FetchedUtc;

            if ((feedCoversSince is { } since && oldest < since)
                || (module.InstalledUtc is { } installed && installed > oldest))
                stale.Add(modId);
        }

        return stale;
    }

    // Steady state is meant to be nearly free. A version already written down is re-asked only when
    // the feed says a new file landed after BEM wrote it, when the answer is stale (above), or when the
    // last attempt never came back.
    private static bool NeedsAsking(
        int modId,
        IReadOnlyDictionary<int, NexusModVersionRecord> known,
        IReadOnlyDictionary<int, NexusUpdatedMod> changedByModId,
        IReadOnlySet<int> stale)
    {
        if (!known.TryGetValue(modId, out var record) || record.Outcome == NexusVersionLookup.LookupFailed)
            return true;

        if (stale.Contains(modId))
            return true;

        // A record kept before BEM stored the category is asked about once more, so every mod page ends
        // up with the Nexus category it is filed under. A record read since is never re-asked for that.
        if (!record.CategoryAsked)
            return true;

        return changedByModId.TryGetValue(modId, out var changed) && record.FetchedUtc < changed.LatestFileUpdate;
    }

    private static bool NeedsCategories(
        NexusVersionLedger ledger,
        IEnumerable<NexusModVersionRecord> records,
        DateTimeOffset now)
    {
        var ids = records.Select(record => record.CategoryId).OfType<int>().ToHashSet();

        if (ids.Count == 0)
            return false;

        return ledger.Categories is not { Count: > 0 } names
            || ids.Any(id => !names.ContainsKey(id))
            || ledger.CategoriesFetchedUtc is not { } fetched
            || now - fetched > TimeSpan.FromDays(30);
    }

    // The same bargain for the file list, kept against its own timestamp: a list is re-asked for only
    // when the feed says a file landed after that list was read, or when the last attempt failed.
    private static bool NeedsFiles(
        int modId,
        IReadOnlyDictionary<int, NexusModVersionRecord> known,
        IReadOnlyDictionary<int, NexusUpdatedMod> changedByModId,
        IReadOnlySet<int> stale)
    {
        if (!known.TryGetValue(modId, out var record)
            || record.FilesOutcome != NexusFileListLookup.Listed
            || record.FilesFetchedUtc is not { } fetched)
            return true;

        if (stale.Contains(modId))
            return true;

        return changedByModId.TryGetValue(modId, out var changed) && fetched < changed.LatestFileUpdate;
    }

    // The budget belongs to the user and is shared with whatever else they have signed in to Nexus.
    // Nothing here is hard-coded: the live figures come off the headers of the response before this
    // one, and the reserve is what BEM refuses to spend so nothing else of theirs starts failing.
    private static bool HasHeadroom(NexusRateLimit limit, int reserve) =>
        (limit.HourlyRemaining is not { } hourly || hourly > reserve)
        && (limit.DailyRemaining is not { } daily || daily > reserve);
}
