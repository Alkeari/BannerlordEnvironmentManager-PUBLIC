using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

public enum SaveModuleChange
{
    Added,
    Removed,
    VersionChanged,
    NotRecorded
}

public sealed record SaveModuleDifference(
    SaveModuleChange Change,
    ModuleId Id,
    string DisplayName,
    string SaveVersion,
    string InstalledVersion,
    // Only meaningful on a Removed row, where "you uninstalled it" and "you turned it off" are the same
    // absence to the game and completely different problems to the user.
    bool IsInstalled = false,
    // When the module's folder appeared on disk, read from the folder itself. Set only where it decides
    // the row: on an Added row it is proof the module arrived after the save, and on a NotRecorded row
    // it is proof it was already there.
    DateTime? InstalledOn = null);

public sealed record SaveDiff(
    IReadOnlyList<SaveModuleDifference> Added,
    IReadOnlyList<SaveModuleDifference> Removed,
    IReadOnlyList<SaveModuleDifference> VersionChanged,
    int Unchanged,
    bool CanCompare = true,
    string Refusal = "",
    IReadOnlyList<SaveModuleDifference>? NotRecorded = null,
    // Installed, off, and absent from the save. Not a change, because a module that is off does not
    // load, but it is the reason "nothing has been installed since this save was made" cannot be said
    // from Added alone: Added only ever counts what is switched on.
    int InstalledButOff = 0)
{
    // On now, absent from the save, and provably installed before the save was written. That is not a
    // change the user made, so it is never counted as one.
    public IReadOnlyList<SaveModuleDifference> NotRecorded { get; init; } = NotRecorded ?? [];

    public int Count => Added.Count + Removed.Count + VersionChanged.Count;

    public int Uninstalled => Removed.Count(r => !r.IsInstalled);

    public int TurnedOff => Removed.Count(r => r.IsInstalled);

    public string Summary
    {
        get
        {
            if (!CanCompare)
                return Refusal;

            if (Count == 0)
            {
                return Strings.Current.Plural("Core.Saves.Comparison.NothingChanged", Unchanged)
                    + NotRecordedNote;
            }

            var parts = new List<string>();

            if (Added.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Saves.Comparison.AddedClause", Added.Count));

            if (Uninstalled > 0)
                parts.Add(Strings.Current.Plural("Core.Saves.Comparison.UninstalledClause", Uninstalled));

            if (TurnedOff > 0)
                parts.Add(Strings.Current.Plural("Core.Saves.Comparison.TurnedOffClause", TurnedOff));

            if (VersionChanged.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Saves.Comparison.VersionChangedClause", VersionChanged.Count));

            return string.Join(", ", parts)
                + Strings.Current.Plural("Core.Saves.Comparison.UnchangedSuffix", Unchanged)
                + NotRecordedNote;
        }
    }

    // What an empty Added list is entitled to say. Added counts enabled modules only, so with anything
    // switched off the honest sentence is about what loads rather than about what is on disk.
    public string DescribeNothingAdded() => InstalledButOff == 0
        ? Strings.Current["Core.Saves.Comparison.NothingAdded"]
        : Strings.Current.Plural("Core.Saves.Comparison.SomeAddedOff", InstalledButOff);

    private string NotRecordedNote => NotRecorded.Count == 0
        ? string.Empty
        : Strings.Current.Plural("Core.Saves.Comparison.NotRecordedNote", NotRecorded.Count);

    public static SaveDiff Refused(string reason) => new([], [], [], 0, false, reason);
}

// A save records which modules the game loaded and at what versions. Comparing that against what is
// enabled now is the question the page exists to answer: continuing a campaign with a module missing or
// replaced is how saves break.
//
// Loaded and enabled are not the same set, and reading real saves is what established it. Of
// the 195 modules enabled on that install, saveauto2 lists 188, and five of the seven missing ones have
// folders created after the save was written. The other two, StoryMode and Multiplayer, predate it by
// weeks: the launcher decided not to load them, which BEM cannot see and must not report as the user
// turning something on. Asset-only modules are not the explanation, whatever it looks like: 22 modules
// on that install declare no SubModule and ship no assembly and appear in every save regardless.
public static class SaveComparison
{
    public static SaveDiff Compare(SaveFile save, ModuleEnvironment current)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(current);

        if (save.Failed)
            return SaveDiff.Refused(Strings.Current.Format("Core.Saves.Comparison.CannotCompare", save.Error));

        // Matching is on the module id and nothing else. BLSE matches a save's list on the display name,
        // which is why it reports Bannerlord.Harmony as missing on an install that plainly has it: the
        // folder declares the id Bannerlord.Harmony and the name Harmony.
        var installed = current.ById;
        var inSave = new Dictionary<ModuleId, SaveModuleRecord>();

        foreach (var record in save.Modules)
            inSave.TryAdd(record.Id, record);

        var added = new List<SaveModuleDifference>();
        var removed = new List<SaveModuleDifference>();
        var changed = new List<SaveModuleDifference>();
        var notRecorded = new List<SaveModuleDifference>();
        var unchanged = 0;

        foreach (var record in save.Modules)
        {
            var module = installed.GetValueOrDefault(record.Id);

            if (module is { IsEnabled: true })
            {
                var installedVersion = VersionText(module);

                if (Differs(record.Version, module.Version))
                {
                    changed.Add(new SaveModuleDifference(
                        SaveModuleChange.VersionChanged, record.Id, module.DisplayName,
                        record.VersionText, installedVersion, IsInstalled: true));
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            removed.Add(new SaveModuleDifference(
                SaveModuleChange.Removed,
                record.Id,
                module?.DisplayName ?? record.Id.Value,
                record.VersionText,
                module is null ? string.Empty : VersionText(module),
                IsInstalled: module is not null));
        }

        // Only what is enabled counts as added: an installed module the user never turned on does not
        // load, so calling it a difference from the save would be a false alarm on every install.
        //
        // Beyond that, saying a module was added since the save is a claim about what the user did, and
        // it needs evidence. The only evidence available here is when the module's folder appeared: a
        // folder created after the save was written could not have been loaded into it. A folder that
        // was already there proves the opposite and moves the row out of the count. An install date BEM
        // cannot read proves nothing either way, so the row stays where it has always been.
        var installedButOff = 0;

        foreach (var module in current.Entries)
        {
            if (inSave.ContainsKey(module.Id) || IsGameModeGated(module.Id))
                continue;

            // Counted rather than dropped. It is not a change to the save, but it is a module that is
            // installed and absent from it, and "nothing has been installed since" said over a folder
            // that was installed since is a claim about disk read off the enabled flag.
            if (!module.IsEnabled)
            {
                installedButOff++;
                continue;
            }

            var installedOn = InstalledOn(module);
            var alreadyThere = save.Created is { } written && installedOn is { } created && created <= written;
            var arrivedSince = save.Created is { } madeAt && installedOn is { } appeared && appeared > madeAt;

            if (alreadyThere)
            {
                notRecorded.Add(new SaveModuleDifference(
                    SaveModuleChange.NotRecorded, module.Id, module.DisplayName,
                    string.Empty, VersionText(module), IsInstalled: true, InstalledOn: installedOn));

                continue;
            }

            added.Add(new SaveModuleDifference(
                SaveModuleChange.Added, module.Id, module.DisplayName,
                string.Empty, VersionText(module), IsInstalled: true,
                InstalledOn: arrivedSince ? installedOn : null));
        }

        return new SaveDiff(
            added, removed, changed, unchanged, NotRecorded: notRecorded, InstalledButOff: installedButOff);
    }

    // The game passes these only when the matching mode is chosen: StoryMode for a campaign, SandBox for
    // sandbox, Multiplayer for multiplayer. A save therefore lists whichever the session used, and the
    // others are absent no matter how the load order is set. Their absence is never a change.
    private static readonly HashSet<string> GameModeGated =
        new(StringComparer.OrdinalIgnoreCase) { "StoryMode", "SandBox", "Multiplayer" };

    private static bool IsGameModeGated(ModuleId id) => GameModeGated.Contains(id.Value);

    // The folder's own creation stamp, in local time, because a save's CreationTime is local ticks and
    // comparing the two in different frames would be worse than not comparing them at all. An orphan has
    // no folder, and an unreadable one is not an answer, so both come back as "not known".
    private static DateTime? InstalledOn(ModuleEntry module)
    {
        if (module.Manifest?.FolderPath is not { Length: > 0 } folder)
            return null;

        try
        {
            return Directory.Exists(folder) ? Directory.GetCreationTime(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    // The save's recorded order, handed to the same reconciliation an imported file goes through, so a
    // restore obeys every guard an import obeys and reports the ordering rules it breaks instead of
    // quietly repairing them. A save's order is what the campaign actually ran with, not a proposal.
    public static LoadOrderFileRead ToLoadOrder(SaveFile save)
    {
        ArgumentNullException.ThrowIfNull(save);

        if (save.Failed)
        {
            return LoadOrderFileRead.Unreadable(
                Strings.Current.Format("Core.Saves.Comparison.CannotRestore", save.Error));
        }

        return new LoadOrderFileRead(
            [.. save.Modules.Select(m => new LoadOrderFileEntry(m.Id.Value, m.VersionText))],
            []);
    }

    private static string VersionText(ModuleEntry module) =>
        module.Manifest?.VersionText is { Length: > 0 } text ? text : module.Version.ToString();

    // Saves record four components and manifests declare three, so the fourth is never compared. An
    // unreadable version on either side is reported as nothing rather than as a change: a false alarm on
    // every module with an odd version string would bury the real ones.
    private static bool Differs(ModuleVersion save, ModuleVersion installed) =>
        !save.IsEmpty && !installed.IsEmpty && save.CompareTo(installed) != 0;
}
