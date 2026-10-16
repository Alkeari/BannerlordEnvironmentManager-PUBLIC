using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The dictionary-of-ids grouping only keeps the winning manifest, so the losing folder path has to be
// captured here at merge time or it is gone by the time the validator wants to name it in a warning.
public sealed record DuplicateModule(ModuleId Id, string UsedFolderPath, IReadOnlyList<string> ShadowedFolderPaths);

public sealed record ModuleEnvironment(
    IReadOnlyList<ModuleEntry> Entries,
    IReadOnlyList<UnreadableModule> Unreadable,
    IReadOnlyList<DuplicateModule> Duplicates = null!,
    IReadOnlyList<ModuleFolderWithoutManifest> FoldersWithoutManifest = null!)
{
    public IReadOnlyList<DuplicateModule> Duplicates { get; init; } = Duplicates ?? [];

    // A folder with no manifest supplies no module, so it builds no entry and nothing above would ever
    // learn it exists. It is carried through so the validator can grade it and name its path.
    public IReadOnlyList<ModuleFolderWithoutManifest> FoldersWithoutManifest { get; init; } =
        FoldersWithoutManifest ?? [];

    public static ModuleEnvironment Empty { get; } = new([], []);

    public IReadOnlyDictionary<ModuleId, ModuleEntry> ById =>
        Entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

    public static ModuleEnvironment Merge(
        ModuleScanResult scan,
        IReadOnlyList<LauncherModEntry> launcherEntries)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(launcherEntries);

        var onDisk = scan.Modules.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());

        // The grouping itself belongs to DuplicateModuleIds, which is also what puts the same pair on
        // the Install Checks page with each folder's timestamp. Shaped into DuplicateModule here
        // unchanged: the first folder in scan order is the manifest onDisk above already picked, so
        // the validator keeps naming exactly the folder the rest of BEM uses.
        var duplicates = DuplicateModuleIds.Group(scan.Modules)
            .Select(g => new DuplicateModule(
                g.Id, g.Folders[0].FolderPath, [.. g.Folders.Skip(1).Select(m => m.FolderPath)]))
            .ToList();

        var entries = new List<ModuleEntry>();
        var placed = new HashSet<ModuleId>();
        var unreadableIds = new HashSet<ModuleId>();

        foreach (var module in scan.Unreadable)
        {
            // A recovered declared id is a best-effort text-scan result and can be wrong (a stale or
            // commented-out id in a truncated file). Treating it as a replacement for the folder name
            // rather than an additional candidate would let a bad recovery reintroduce the orphan bug
            // this exists to fix, so both are tried.
            if (module.DeclaredId is not null)
                unreadableIds.Add(new ModuleId(module.DeclaredId));

            unreadableIds.Add(new ModuleId(module.FolderName));
        }

        foreach (var launcherEntry in launcherEntries)
        {
            if (!placed.Add(launcherEntry.Id))
                continue;

            var manifest = onDisk.GetValueOrDefault(launcherEntry.Id);

            // A folder whose SubModule.xml will not parse is still an installed module. Calling it
            // an orphan would offer to prune a mod the user actually has.
            var isUnreadable = manifest is null && unreadableIds.Contains(launcherEntry.Id);

            // A module missing from disk is not necessarily uninstalled: Steam Workshop modules are
            // invisible to a folder scan while Steam is offline. Zeroing the flag here would silently
            // disable the user's whole load order for the length of that session, so an orphan carries
            // its selected flag through unchanged instead.
            entries.Add(new ModuleEntry(
                manifest?.Id ?? launcherEntry.Id,
                manifest,
                launcherEntry.IsSelected,
                isUnreadable));
        }

        foreach (var manifest in scan.Modules)
        {
            if (placed.Add(manifest.Id))
                entries.Add(new ModuleEntry(manifest.Id, manifest, IsEnabled: false));
        }

        foreach (var module in scan.Unreadable)
        {
            if (module.DeclaredId is null)
                continue;

            var declaredId = new ModuleId(module.DeclaredId);
            var folderId = new ModuleId(module.FolderName);

            // Either candidate already being placed means a launcher entry already claimed this
            // module (by declared id or by folder name). Appending again under the other candidate
            // would write a second, bogus row for the same module.
            if (placed.Contains(declaredId) || placed.Contains(folderId))
                continue;

            if (placed.Add(declaredId))
                entries.Add(new ModuleEntry(declaredId, null, IsEnabled: false, IsUnreadable: true));
        }

        return new ModuleEnvironment(entries, scan.Unreadable, duplicates, scan.FoldersWithoutManifest);
    }

    // Whether a list already built from these entries still describes this environment, position for
    // position, so a re-read that found nothing new can leave the rows on screen alone.
    //
    // A row shows nothing that is not on its own ModuleEntry, so entry equality is the whole question:
    // the module set, each one's enabled state, the order they sit in, and every manifest detail a row
    // renders. Order counts because the order IS the load order, so a reordered environment is a
    // changed one. This only says whether the rows must be rebuilt; the badges, tiers and issues
    // painted onto existing rows are refreshed either way.
    public bool DescribesSameRows(IReadOnlyList<ModuleEntry> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        if (shown.Count != Entries.Count)
            return false;

        for (var i = 0; i < shown.Count; i++)
        {
            if (shown[i] != Entries[i])
                return false;
        }

        return true;
    }

    public ModuleEnvironment WithEntries(IReadOnlyList<ModuleEntry> entries) =>
        this with { Entries = entries };

    public ModuleEnvironment WithEnabled(bool enabled, Func<ModuleEntry, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);

        return WithBatchState(where, _ => enabled);
    }

    public ModuleEnvironment WithInvertedEnabled(Func<ModuleEntry, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);

        return WithBatchState(where, entry => !entry.IsEnabled);
    }

    // Every batch path funnels through here so the two protections cannot be forgotten by a caller.
    //
    // Disabling Native, SandBoxCore, SandBox or StoryMode does not disable some mods, it breaks the
    // game, and no sweep over a search result is ever meant to reach them. Manual control is kept: a
    // per-row toggle writes its entry back through WithEntries and never comes past this guard.
    //
    // An orphan has no folder on disk, so enabling it can only write a selected entry the game cannot
    // resolve. Clearing the flag is still allowed: that is the user asking for it directly, not a scan
    // quietly zeroing a Workshop module that Steam happened to be hiding.
    private ModuleEnvironment WithBatchState(Func<ModuleEntry, bool> where, Func<ModuleEntry, bool> target) =>
        WithEntries([.. Entries.Select(entry =>
        {
            if (!where(entry) || entry.IsOfficial)
                return entry;

            var enabled = target(entry);

            return enabled && entry.IsOrphan ? entry : entry with { IsEnabled = enabled };
        })]);

    public ModuleEnvironment WithoutOrphans() =>
        WithEntries([.. Entries.Where(e => !e.IsOrphan)]);

    public ModuleEnvironment WithoutOrphan(ModuleId id) =>
        WithEntries([.. Entries.Where(e => !e.IsOrphan || e.Id != id)]);

    // A real installed divider-* module folder (from a shared modlist that ships one) is read-only in
    // this phase: recognized and removed from Entries so nothing downstream - Health, Safety,
    // update-checking, the sorter - ever sees it as a real mod, but nothing here creates, renames or
    // deletes the folder itself. SourceModuleId is set to the recognized id, so
    // EnvironmentViewModel can tell "already adopted this real folder" apart from a divider the user
    // created directly in BEM, and never adopt the same folder twice across repeated scans.
    public (ModuleEnvironment WithoutDividers, IReadOnlyList<LoadOrderDivider> Recognized) ExtractDividers()
    {
        var (realIds, recognized) = LoadOrderDividerExtraction.Extract(
            [.. Entries.Select(e => e.Id.Value)], _ => true);

        var keep = new HashSet<string>(realIds, StringComparer.OrdinalIgnoreCase);
        var withSource = recognized.Select(d => d with { SourceModuleId = FindSourceId(d) }).ToList();

        return (WithEntries([.. Entries.Where(e => keep.Contains(e.Id.Value))]), withSource);

        ModuleId FindSourceId(LoadOrderDivider divider)
        {
            // The anchor identifies where the divider sits, not what folder it came from - the source id
            // is whichever divider-* entry immediately preceded that anchor (or the end) in the original,
            // unfiltered Entries, which Extract does not hand back directly, so it is re-found here.
            var anchorIndex = divider.AnchorId is { } anchor
                ? Entries.ToList().FindIndex(e => e.Id == anchor)
                : Entries.Count;

            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                if (DividerConvention.TryParseLabel(Entries[i].Id.Value) == divider.Label)
                    return Entries[i].Id;
            }

            return Entries[Math.Max(anchorIndex - 1, 0)].Id;
        }
    }

    // Caveat: ModuleVersion.ToString omits a zero ChangeSet, so a manifest declaring v1.0.0.0 is reported
    // here as v1.0.0. If the engine compares LastKnownVersion as raw text rather than a parsed version that
    // still reads as a mismatch. Carrying the original <Version value="..."/> text on ModuleManifest and
    // echoing it back would be strictly safer.
    public IReadOnlyList<LauncherModEntry> ToLauncherEntries() =>
        [.. Entries.Select(e => new LauncherModEntry(e.Id, e.IsEnabled, e.Version.ToString()))];
}
