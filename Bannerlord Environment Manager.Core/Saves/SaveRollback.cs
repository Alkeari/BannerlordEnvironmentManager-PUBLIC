using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.Core.Saves;

public enum SaveRollbackAction
{
    // Enabled here at the version the save recorded: nothing to do.
    AlreadyMatches,
    // Nexus lists a file at the recorded version, current or archived: it can be fetched into the copy.
    GetFromNexus,
    // The mod has a Nexus page but no file on it carries the recorded version (taken down, or numbered
    // differently from the module), so BEM cannot say which file to fetch.
    NexusFileNotListed,
    // Nothing links the module to a Nexus page, so there is nowhere to ask for an older version.
    NoNexusPage,
    // Steam keeps only the latest Workshop version, so an older one cannot be brought back.
    Workshop,
    // An official module: its version follows the game version, which the copy already takes.
    FollowsGame
}

public sealed record SaveRollbackStep(
    ModuleId Id,
    string DisplayName,
    string SaveVersion,
    string? InstalledVersion,
    SaveRollbackAction Action,
    int? NexusModId = null,
    NexusModFile? File = null,
    // Where the module sits on this machine now, at whatever version. A module the save's version
    // cannot be fetched for still comes along at the version it has here, because a module missing
    // from the copy stops the save loading and one at another version usually does not.
    string? InstalledFolder = null)
{
    // The file's own row on the mod page's Files tab, which is where a free account downloads it by
    // hand: Nexus serves the file to BEM unattended only for Premium.
    public string? NexusFilePage => NexusModId is { } modId && File is { } file
        ? $"{NexusArchiveName.PageUrl(modId)}?tab=files&file_id={file.FileId}"
        : NexusModId is { } page ? NexusArchiveName.PageUrl(page) : null;

    public bool CanCopyInstalled => !string.IsNullOrWhiteSpace(InstalledFolder);
}

public sealed record SaveRollbackPlan(
    string SaveGameVersion,
    string? InstalledGameVersion,
    bool GameVersionDiffers,
    IReadOnlyList<SaveRollbackStep> Steps,
    bool CanPlan = true,
    // The DLC the save was written with, read off its module list: a War Sails campaign records
    // NavalDLC. The copy has to carry the same DLC or the save will not load at all.
    GameDlcSet Variant = default)
{
    public IReadOnlyList<SaveRollbackStep> ToFetch => Steps.Where(s => s.Action is SaveRollbackAction.GetFromNexus).ToList();

    public IReadOnlyList<SaveRollbackStep> CannotRestore =>
        Steps.Where(s => s.Action is SaveRollbackAction.NexusFileNotListed
                             or SaveRollbackAction.NoNexusPage
                             or SaveRollbackAction.Workshop).ToList();

    // Nothing to restore: the game and every recorded mod already match the save.
    public bool NothingToDo => CanPlan && !Differs;

    // Whether the setup the save was written with differs from this one at all: the game version, or
    // any recorded mod that is not already here at its recorded version.
    // Whether a restore would bring anything closer to the save than this install already is: another
    // game version, or a file Nexus still lists. A setup that differs only by mods nobody can supply is
    // the same setup in a copy, so building one would cost a whole game's worth of disk for nothing.
    public bool CanImprove => CanPlan && (GameVersionDiffers || ToFetch.Count > 0);

    public bool Differs => CanPlan && (GameVersionDiffers
        || Steps.Any(s => s.Action is not (SaveRollbackAction.AlreadyMatches or SaveRollbackAction.FollowsGame)));

    public static SaveRollbackPlan Refused { get; } = new(string.Empty, null, false, [], false);
}

// Works out what bringing a save back to its setup would take, without doing any of it: the game version
// the save was written by, and for each module the save recorded, whether the file at that version can
// be fetched, is already here, or is out of reach. A Workshop module is named as unrecoverable rather
// than skipped, because the player is told which parts of the old setup cannot come back.
public static class SaveRollback
{
    public static SaveRollbackPlan Plan(
        SaveFile save,
        ModuleEnvironment current,
        string? installedGameVersion,
        IReadOnlyDictionary<string, NexusModuleLink> linksByModuleId,
        IReadOnlyDictionary<int, NexusModFileListing> listingsByNexusModId)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(linksByModuleId);
        ArgumentNullException.ThrowIfNull(listingsByNexusModId);

        if (save.Failed)
            return SaveRollbackPlan.Refused;

        var gameDiffers = save.ApplicationVersion.Length > 0
            && installedGameVersion is { Length: > 0 }
            && SaveComparison.Differs(ModuleVersion.Parse(save.ApplicationVersion), ModuleVersion.Parse(installedGameVersion));

        var steps = new List<SaveRollbackStep>();
        var seen = new HashSet<ModuleId>();

        foreach (var record in save.Modules)
        {
            if (!seen.Add(record.Id))
                continue;

            var entry = current.ById.GetValueOrDefault(record.Id);
            var installed = entry?.Manifest is { } manifest
                ? manifest.VersionText is { Length: > 0 } text ? text : manifest.Version.ToString()
                : null;
            var folder = entry?.Manifest?.FolderPath is { Length: > 0 } path ? path : null;
            var name = entry?.DisplayName ?? record.Id.Value;

            SaveRollbackStep Step(SaveRollbackAction action, int? modId = null, NexusModFile? file = null) =>
                new(record.Id, name, record.VersionText, installed, action, modId, file, folder);

            // The game's own modules come with whichever game version the copy is built from, so they
            // are never fetched, installed or copied, whether or not this install has them.
            if (entry is { IsOfficial: true } || OfficialModules.Contains(record.Id))
            {
                steps.Add(Step(SaveRollbackAction.FollowsGame));
                continue;
            }

            // Installed at the recorded version is a match whether or not it is switched on: the copy
            // takes the folder, and the save's own order decides what loads.
            if (entry?.Manifest is not null && !entry.Version.IsEmpty && !record.Version.IsEmpty
                && !SaveComparison.Differs(record.Version, entry.Version))
            {
                steps.Add(Step(SaveRollbackAction.AlreadyMatches));
                continue;
            }

            if (WorkshopContent.CameFromWorkshop(entry?.Manifest))
            {
                steps.Add(Step(SaveRollbackAction.Workshop));
                continue;
            }

            if (!linksByModuleId.TryGetValue(record.Id.Value, out var link) || link.NexusModId is not { } modId)
            {
                steps.Add(Step(SaveRollbackAction.NoNexusPage));
                continue;
            }

            var file = listingsByNexusModId.TryGetValue(modId, out var listing)
                ? PickFile(listing, record.VersionText, link)
                : null;

            steps.Add(file is null
                ? Step(SaveRollbackAction.NexusFileNotListed, modId)
                : Step(SaveRollbackAction.GetFromNexus, modId, file));
        }

        return new SaveRollbackPlan(
            save.ApplicationVersion, installedGameVersion, gameDiffers, steps, Variant: VariantOf(save));
    }

    // The mod pages whose file lists the plan has to read: every mod the save recorded at a version
    // that is not here, that has a Nexus page and did not come from the Workshop. Asking for only these
    // keeps a restore of a save with two hundred mods, most of them unchanged, to a few requests.
    public static IReadOnlyList<int> NexusIdsToAsk(
        SaveFile save, ModuleEnvironment current, IReadOnlyDictionary<string, NexusModuleLink> linksByModuleId)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(linksByModuleId);

        var probe = Plan(save, current, null, linksByModuleId, new Dictionary<int, NexusModFileListing>());

        return [.. probe.Steps
            .Where(step => step.Action is SaveRollbackAction.NexusFileNotListed && step.NexusModId is not null)
            .Select(step => step.NexusModId!.Value)
            .Distinct()];
    }

    // The DLC a save was written with: every known DLC whose module the save recorded.
    public static GameDlcSet VariantOf(SaveFile save)
    {
        ArgumentNullException.ThrowIfNull(save);

        var ids = save.Modules.Select(m => m.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GameDlcSet(GameDlc.Known.Where(dlc => ids.Contains(dlc.ModuleFolder)).Select(dlc => dlc.AppId));
    }

    // Which file on the page is the one the save ran with. A version written the same way is taken
    // before one that only means the same thing ("v1.5.0" before "1.5"), because an author who
    // uploaded both numbered them apart for a reason. Within a tier, the file the module was
    // installed from wins, then a main or archived file over an optional, update or miscellaneous one
    // carrying the same number (a patch or an add-on is not the mod), then the newest upload, which is
    // the author's last word on that version.
    public static NexusModFile? PickFile(NexusModFileListing listing, string recordedVersion, NexusModuleLink? link = null)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (string.IsNullOrWhiteSpace(recordedVersion))
            return null;

        var exact = listing.Files
            .Where(f => string.Equals(f.Version?.Trim(), recordedVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tier = exact.Count > 0
            ? exact
            : [.. listing.Files.Where(f => ModuleUpdateVerdicts.SameVersion(f.Version, recordedVersion))];

        return tier
            .OrderByDescending(f => InstalledFrom(f, link))
            .ThenByDescending(IsMainOrArchived)
            .ThenByDescending(f => f.UploadedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.FileId)
            .FirstOrDefault();
    }

    private static bool InstalledFrom(NexusModFile file, NexusModuleLink? link) =>
        link is not null
        && ((link.NexusFileId is { } id && id == file.FileId)
            || (link.NexusFileNameAtInstall is { Length: > 0 } title
                && string.Equals(title, file.Name, StringComparison.OrdinalIgnoreCase)));

    private static bool IsMainOrArchived(NexusModFile file) =>
        NexusFileCategories.IsMain(file.CategoryName, file.CategoryId)
        || file.Standing is NexusFileStanding.OldVersion;
}
