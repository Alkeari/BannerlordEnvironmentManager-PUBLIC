using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

public enum SaveSetupTargetKind
{
    // An earlier restore of this save already built the copy: the mods go into it again.
    ReuseCopy,
    // An instance of the save's game version and DLC is on this machine: its game files are copied
    // into a new instance, so the instance it was copied from keeps its own mods untouched.
    CopyInstance,
    // Nothing here runs the save's game version: Steam's branch for it is downloaded as a new
    // instance under the copy's name.
    Download,
    // No instance runs the version and Steam no longer publishes it, or the save recorded none.
    Unavailable
}

// Where a save's setup is rebuilt. It is always an instance of its own, named after the save, and
// never the resting one: the resting install is the player's current game, and rolling its mods back
// to bring one old campaign back would break every other campaign on it.
public sealed record SaveSetupTarget(
    SaveSetupTargetKind Kind,
    string CopyName,
    ModuleVersion Version,
    GameDlcSet Variant,
    InstalledInstance? Instance = null,
    string Reason = "")
{
    public bool CanRestore => Kind is not SaveSetupTargetKind.Unavailable;
}

public static class SaveSetupTargets
{
    // The name the copy is given: the save's own name and the word that says what it is. A name an
    // instance already wears is numbered rather than shared, so two rows never read alike. The save
    // name is cut short before the suffix rather than the suffix being cut off, because the suffix is
    // what says the instance is a restored setup at all.
    public static string CopyNameFor(string saveName, IEnumerable<string> takenNames)
    {
        ArgumentNullException.ThrowIfNull(takenNames);

        var taken = takenNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = (saveName ?? string.Empty).Trim();

        for (var copy = 1; copy <= taken.Count + 1; copy++)
        {
            var candidate = Compose(stem, copy);

            if (!taken.Contains(candidate))
                return candidate;
        }

        return Compose(stem, 1);
    }

    private static string Compose(string stem, int copy)
    {
        var suffix = copy == 1
            ? Strings.Current.Format("Core.Saves.Setup.CopyName", string.Empty)
            : Strings.Current.Format("Core.Saves.Setup.CopyNameNumbered", string.Empty, copy);

        var room = Math.Max(1, InstanceNaming.MaxLength - suffix.Length);
        var shortened = stem.Length > room ? stem[..room].TrimEnd() : stem;

        return copy == 1
            ? Strings.Current.Format("Core.Saves.Setup.CopyName", shortened)
            : Strings.Current.Format("Core.Saves.Setup.CopyNameNumbered", shortened, copy);
    }

    // publishedOnSteam answers whether Steam still publishes a version and DLC pair; null means BEM
    // has not been able to ask, in which case the download is offered and Steam answers it.
    public static SaveSetupTarget Choose(
        SaveRollbackPlan plan,
        string saveName,
        IReadOnlyList<InstalledInstance> installed,
        string? restingInstanceId,
        Func<ModuleVersion, GameDlcSet, bool>? publishedOnSteam)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installed);

        var version = ModuleVersion.Parse(plan.SaveGameVersion);
        var variant = plan.Variant;
        var names = installed.Select(i => i.Record.ChosenName).OfType<string>().ToList();
        var baseName = CopyNameFor(saveName, []);

        if (version.IsEmpty)
        {
            return new SaveSetupTarget(SaveSetupTargetKind.Unavailable, baseName, version, variant,
                Reason: Strings.Current["Core.Saves.Setup.NoGameVersion"]);
        }

        bool IsResting(InstalledInstance instance) => InstanceLabel.IsResting(instance.Record.Id, restingInstanceId);

        bool Runs(InstalledInstance instance) =>
            ModuleVersion.Parse(instance.Record.RecordedGameVersion) is { IsEmpty: false } own
            && own.CompareTo(version) == 0;

        var earlier = installed.FirstOrDefault(i =>
            !IsResting(i) && Runs(i) && i.Record.Dlc == variant
            && string.Equals(i.Record.ChosenName, baseName, StringComparison.OrdinalIgnoreCase));

        if (earlier is not null)
            return new SaveSetupTarget(SaveSetupTargetKind.ReuseCopy, baseName, version, variant, earlier);

        var copyName = CopyNameFor(saveName, names);

        // The same DLC first, because a copy carrying DLC the save never used loads it but changes what
        // the instance is; one carrying more is still a working copy when nothing else is. A spare
        // instance is read before the resting one only to keep reads off the player's own game.
        var source = installed
            .Where(i => Runs(i) && !string.IsNullOrWhiteSpace(i.Record.GameFolder)
                        && variant.AppIds.All(id => i.Record.Dlc.AppIds.Contains(id)))
            .OrderByDescending(i => i.Record.Dlc == variant)
            .ThenBy(IsResting)
            .FirstOrDefault();

        if (source is not null)
            return new SaveSetupTarget(SaveSetupTargetKind.CopyInstance, copyName, version, source.Record.Dlc, source);

        if (publishedOnSteam is null || publishedOnSteam(version, variant))
            return new SaveSetupTarget(SaveSetupTargetKind.Download, copyName, version, variant);

        return new SaveSetupTarget(SaveSetupTargetKind.Unavailable, copyName, version, variant,
            Reason: Strings.Current.Format("Core.Saves.Setup.NotPublished", GameVersionLabel.For(version, variant)));
    }
}
