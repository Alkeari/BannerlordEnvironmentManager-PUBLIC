using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Launcher;

public sealed record OfficialSeedResult(bool Seeded, IReadOnlyList<ModuleId> Enabled, string Reason);

// A game version BEM has just taken in has never run the game's own launcher, so it has no
// LauncherData.xml and reads as a load order with every module off, the seven the game itself ships
// included. The Play page's Enable All cannot put that right, and should not: it refuses official
// modules by design, because turning the game's own modules on and off is the user's call and never a
// bulk action's. So a freshly downloaded version came up unplayable with no control that would fix it.
//
// The absence of the file is the entire trigger. A file that exists records a decision even when every
// module in it is off, and is never touched: only a file that was never written can be one the user has
// had no say in.
public static class OfficialModuleSeed
{
    public static OfficialSeedResult Seed(LauncherDataStore store, ModuleScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scan);

        if (store.Exists)
            return new OfficialSeedResult(false, [], $"'{store.FilePath}' already exists, so it records a choice.");

        if (scan.Failed)
            return new OfficialSeedResult(false, [], scan.Error ?? "The modules folder could not be read.");

        // Merged against no stored order at all, which is the truth here, and then sorted by the same
        // sorter the Play page uses, so the official modules land in the order their own declared
        // dependencies put them in rather than in an order written down a second time here.
        var environment = ModuleEnvironment.Merge(scan, []);

        if (environment.Entries.Count == 0)
            return new OfficialSeedResult(false, [], "No module was found on disk, so there was nothing to write down.");

        var sorted = LoadOrderSorter.Sort(environment);
        var ordered = sorted.Failed ? environment.Entries : sorted.Entries;

        var entries = ordered
            .Select(entry => new LauncherModEntry(entry.Id, entry.IsOfficial))
            .ToList();

        // No backup: the file does not exist, and a dated copy of a state that never existed would be
        // the first thing a restore offered to put back. The write itself creates the folder chain,
        // which a brand new instance does not have yet.
        store.Write(entries, backupReason: null);

        return new OfficialSeedResult(
            true,
            [.. entries.Where(entry => entry.IsSelected).Select(entry => entry.Id)],
            $"Wrote {entries.Count} module(s) to '{store.FilePath}'.");
    }
}
