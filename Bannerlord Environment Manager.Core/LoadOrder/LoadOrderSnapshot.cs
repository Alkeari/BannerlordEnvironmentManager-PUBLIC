using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Id and enabled state only. An index would silently point at a different module the moment anything
// is installed or removed, which on this install happens constantly, so position is carried by the
// order of the list and never by a stored number.
public sealed record LoadOrderSnapshotEntry(string Id, bool IsEnabled);

public sealed record LoadOrderRestore(
    ModuleEnvironment Environment,
    int Applied,
    int Missing,
    int Kept,
    IReadOnlyList<LoadOrderDivider> RecognizedDividers = null!,
    // Entries the snapshot asked to enable or disable that kept the state they already had, because
    // ModuleEntry.EnabledFromOutside refused the change. Counted rather than swallowed: the profile
    // was not applied whole, and the sentence that reports the restore says so.
    int Refused = 0)
{
    public IReadOnlyList<LoadOrderDivider> RecognizedDividers { get; init; } = RecognizedDividers ?? [];
}

public sealed record LoadOrderDividerSnapshotEntry(
    string Label, string? AnchorId, bool Collapsed, string? SourceModuleId)
{
    public static LoadOrderDividerSnapshotEntry From(LoadOrderDivider divider)
    {
        ArgumentNullException.ThrowIfNull(divider);

        return new(divider.Label, divider.AnchorId?.Value, divider.Collapsed, divider.SourceModuleId?.Value);
    }

    public LoadOrderDivider ToDivider() =>
        new(Label,
            AnchorId is null ? null : new ModuleId(AnchorId),
            Collapsed,
            SourceModuleId is null ? null : new ModuleId(SourceModuleId));
}

public sealed record LoadOrderSnapshot(
    IReadOnlyList<LoadOrderSnapshotEntry> Entries,
    IReadOnlyList<LoadOrderDividerSnapshotEntry>? Dividers = null)
{
    public IReadOnlyList<LoadOrderDividerSnapshotEntry> Dividers { get; init; } = Dividers ?? [];

    public static LoadOrderSnapshot Empty { get; } = new([]);

    public int EnabledCount => Entries.Count(e => e.IsEnabled);

    public static LoadOrderSnapshot From(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new([.. environment.Entries.Select(e => new LoadOrderSnapshotEntry(e.Id.Value, e.IsEnabled))]);
    }

    // The overload a named profile saves through: the profile records the sections the list had as
    // well as its module order, so restoring it puts both back. Nothing else about a snapshot
    // changes, and the sections travel in their own list, never among Entries.
    public static LoadOrderSnapshot From(
        ModuleEnvironment environment, IReadOnlyList<LoadOrderDivider> dividers)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(dividers);

        return From(environment) with { Dividers = [.. dividers.Select(LoadOrderDividerSnapshotEntry.From)] };
    }

    public static LoadOrderSnapshot From(IReadOnlyList<LauncherModEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new([.. entries.Select(e => new LoadOrderSnapshotEntry(e.Id.Value, e.IsSelected))]);
    }

    // A module the snapshot never saw is kept exactly as it is and appended, not disabled and not
    // dropped: the snapshot describes the modules it knew about and says nothing about the rest.
    // A module the snapshot lists but which is no longer installed is counted, never resurrected as
    // a row the user would then have to prune.
    //
    // The enabled state a snapshot carries goes through ModuleEntry.EnabledFromOutside, the same
    // guard LoadOrderImport puts a foreign .bmlist through. A profile is no longer only ever one the
    // user made on this machine: since 3.2.0 it can arrive as a .bemprofile from a stranger, and a
    // profile that turns Native off stops the game booting.
    public LoadOrderRestore ApplyTo(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var available = environment.ById;

        var (realIds, recognizedDividers) = LoadOrderDividerExtraction.Extract(
            [.. Entries.Select(e => e.Id)],
            id => available.ContainsKey(new ModuleId(id)));

        // First occurrence wins on a repeated id, matching the dedup the loop below performs via
        // `seen`: ToDictionary would throw on the second occurrence instead of ignoring it.
        var byId = new Dictionary<string, LoadOrderSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            byId.TryAdd(entry.Id, entry);

        var orderedRealEntries = realIds.Select(id => byId[id]).ToList();

        var ordered = new List<ModuleEntry>(environment.Entries.Count);
        var seen = new HashSet<ModuleId>();
        var placed = new HashSet<ModuleId>();
        var missing = 0;
        var refused = 0;

        foreach (var entry in orderedRealEntries)
        {
            var id = new ModuleId(entry.Id);

            if (!seen.Add(id))
                continue;

            if (!available.TryGetValue(id, out var module))
            {
                missing++;
                continue;
            }

            placed.Add(id);

            var enabled = module.EnabledFromOutside(entry.IsEnabled);

            if (enabled != entry.IsEnabled)
                refused++;

            ordered.Add(module with { IsEnabled = enabled });
        }

        var applied = ordered.Count;
        var kept = 0;

        foreach (var module in environment.Entries)
        {
            if (!placed.Add(module.Id))
                continue;

            ordered.Add(module);
            kept++;
        }

        return new LoadOrderRestore(
            environment.WithEntries(ordered), applied, missing, kept, recognizedDividers, refused);
    }
}
