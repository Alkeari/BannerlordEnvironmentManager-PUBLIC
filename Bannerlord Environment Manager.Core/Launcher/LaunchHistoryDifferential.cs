namespace BannerlordEnvironmentManager.Core.Launcher;

// Modules that were enabled in at least MinimumCrashes launches that crashed and in no launch that ended
// cleanly, grouped when they share exactly the same crashing launches. A with-and-without count, not a
// cause: it names suspects only from the history BEM already keeps.
public sealed record CrashSuspectSet(
    IReadOnlyList<string> ModuleIds,
    int CrashingLaunchesWithThem,
    int CrashingLaunches,
    int CleanLaunches);

// Gap G4 in the findings catalog: the four BetterFemaleBody mods were in 11 of 11 crashing launches and in
// no clean one, which this count over launches.json finds with no static detector at all.
public static class LaunchHistoryDifferential
{
    // Below this, one or two unlucky crashes would name whatever happened to be new that day.
    public const int MinimumCrashes = 3;

    public static IReadOnlyList<CrashSuspectSet> Find(IReadOnlyList<LaunchRecord> history, IEnumerable<string> enabledNow)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(enabledNow);

        var crashes = history.Where(r => r.Outcome == LaunchOutcome.Crashed).ToList();
        var clean = history.Where(r => r.Outcome == LaunchOutcome.ExitedCleanly).ToList();

        // With no clean launch to compare against, every module would qualify, which says nothing.
        if (crashes.Count < MinimumCrashes || clean.Count == 0)
            return [];

        static HashSet<string> Enabled(LaunchRecord record) =>
            record.Order.Entries.Where(e => e.IsEnabled).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var crashSets = crashes.Select(Enabled).ToList();
        var cleanSets = clean.Select(Enabled).ToList();

        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var id in enabledNow.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cleanSets.Any(set => set.Contains(id)))
                continue;

            var with = Enumerable.Range(0, crashSets.Count).Where(i => crashSets[i].Contains(id)).ToList();

            if (with.Count < MinimumCrashes)
                continue;

            var signature = string.Join(",", with);

            if (!candidates.TryGetValue(signature, out var ids))
                candidates[signature] = ids = [];

            ids.Add(id);
        }

        return
        [
            .. candidates
                .Select(pair => new CrashSuspectSet(
                    [.. pair.Value.Order(StringComparer.OrdinalIgnoreCase)],
                    pair.Key.Split(',').Length,
                    crashes.Count,
                    clean.Count))
                .OrderByDescending(set => set.CrashingLaunchesWithThem)
                .ThenBy(set => set.ModuleIds[0], StringComparer.OrdinalIgnoreCase)
        ];
    }
}
