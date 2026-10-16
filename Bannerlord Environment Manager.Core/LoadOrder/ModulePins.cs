using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Which modules the user has said must stay where they are, by id and never by index. An index only
// means anything against one particular list, and the whole job of a pin is to survive a sort that
// renumbers everything around it; where a pinned module currently sits is read from the list at sort
// time. A pin for a module that is not installed right now is kept: a Workshop module is invisible to
// a folder scan while Steam is offline, and dropping the pin would quietly discard a decision the user
// made about a mod they still have.
public sealed class ModulePins
{
    private readonly HashSet<ModuleId> lookup;

    private ModulePins(IReadOnlyList<ModuleId> ids)
    {
        Ids = ids;
        lookup = [.. ids];
    }

    public static ModulePins Empty { get; } = new([]);

    // Insertion order, so the list the user sees does not reshuffle itself between sessions.
    public IReadOnlyList<ModuleId> Ids { get; }

    public int Count => Ids.Count;

    public bool IsEmpty => Ids.Count == 0;

    public static ModulePins Of(IEnumerable<ModuleId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var seen = new HashSet<ModuleId>();
        var ordered = new List<ModuleId>();

        foreach (var id in ids)
        {
            if (seen.Add(id))
                ordered.Add(id);
        }

        return ordered.Count == 0 ? Empty : new ModulePins(ordered);
    }

    public bool Contains(ModuleId id) => lookup.Contains(id);

    public ModulePins With(ModuleId id) => Contains(id) ? this : new ModulePins([.. Ids, id]);

    public ModulePins WithAll(IEnumerable<ModuleId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return Of([.. Ids, .. ids]);
    }

    public ModulePins Without(ModuleId id) =>
        Contains(id) ? Of([.. Ids.Where(pinned => pinned != id)]) : this;

    public ModulePins Cleared() => Empty;

    // Both directions, because a pin the list cannot show is exactly the one worth showing: it is the
    // one that looks like it was dropped.
    public IReadOnlyList<ModuleId> ActiveIn(IReadOnlyList<ModuleEntry> entries) => Split(entries, present: true);

    public IReadOnlyList<ModuleId> InactiveIn(IReadOnlyList<ModuleEntry> entries) => Split(entries, present: false);

    private IReadOnlyList<ModuleId> Split(IReadOnlyList<ModuleEntry> entries, bool present)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var installed = entries.Select(entry => entry.Id).ToHashSet();

        return [.. Ids.Where(id => installed.Contains(id) == present)];
    }
}

// A pin that the sort could not honor, with the declared edge that outranked it and where the module
// went instead. Compliance is hard-locked, so this is not a failure of the sort; it is the one thing a
// pin cannot do, and saying so is the difference between a rule and a silently dropped preference.
public sealed record UnhonoredPin(
    ModuleId Id,
    int PinnedPosition,
    int ActualPosition,
    ModuleId? Earlier = null,
    ModuleId? Later = null)
{
    public bool NamesAConstraint => Earlier is not null && Later is not null;

    public string Describe() => NamesAConstraint
        ? Strings.Current.Format(
            "Core.LoadOrder.Pins.OverriddenByConstraint",
            Id, PinnedPosition + 1, Earlier, Later, ActualPosition + 1)
        : Strings.Current.Format(
            "Core.LoadOrder.Pins.OverriddenNoRoom", Id, PinnedPosition + 1, ActualPosition + 1);
}

public sealed class ModulePinStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private sealed record PinFile(List<string> Ids);

    public string FilePath { get; } = filePath;

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "pinned-modules.json");

    // A file that will not parse throws rather than reading as no pins. "There are none" and "I could
    // not read them" mean opposite things, and answering the first when the second is true would let a
    // corrupt file wipe every pin the moment anything wrote the list back.
    public ModulePins Read()
    {
        if (!File.Exists(FilePath))
            return ModulePins.Empty;

        var file = JsonSerializer.Deserialize<PinFile>(File.ReadAllText(FilePath));

        return file?.Ids is not { } ids
            ? ModulePins.Empty
            : ModulePins.Of([.. ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => new ModuleId(id))]);
    }

    public void Write(ModulePins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(new PinFile([.. pins.Ids.Select(id => id.Value)]), Format));
    }
}
