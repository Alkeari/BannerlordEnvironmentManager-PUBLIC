using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum PrerequisiteKind
{
    // Installs into Modules\ like any other mod, and is loaded by the game.
    Module,

    // Installs into the game's own bin folder and is not a module at all. It has no SubModule.xml, so
    // nothing that reads the module list can see whether it is there.
    Loader
}

public sealed record Prerequisite(
    string Id,
    string Name,
    PrerequisiteKind Kind,
    int NexusModId,
    string SourceRepository,
    string Needs,
    string WithoutIt)
{
    public string NexusPage => Install.NexusArchiveName.PageUrl(NexusModId);
}

public sealed record PrerequisiteState(Prerequisite Prerequisite, bool IsInstalled, string Evidence);

// The handful of things almost every Bannerlord mod is built against. They are not BEM's to bundle and
// they are not optional to the mods that declare them: a mod requiring ButterLib does not degrade
// without it, it fails to load.
//
// The mod ids are recorded here rather than looked up, because they were verified by hand and a lookup
// that guessed one would send somebody to the wrong page. The repository is kept beside each one as the
// second source, so an id that ever goes stale can be checked against something.
//
// This list is also the BUTR stack Library's Install BUTR Stack button fetches, through ButrStack, so a
// member added or corrected here is added or corrected there with no other change.
public static class Prerequisites
{
    public static IReadOnlyList<Prerequisite> All { get; } =
    [
        new("Bannerlord.BLSE", "BLSE", PrerequisiteKind.Loader, 1,
            "https://github.com/BUTR/Bannerlord.BLSE",
            Strings.Current["Core.Modules.Prereq.Blse.Needs"],
            Strings.Current["Core.Modules.Prereq.Blse.WithoutIt"]),

        new("Bannerlord.Harmony", "Harmony", PrerequisiteKind.Module, 2006,
            "https://github.com/BUTR/Bannerlord.Harmony",
            Strings.Current["Core.Modules.Prereq.Harmony.Needs"],
            Strings.Current["Core.Modules.Prereq.Harmony.WithoutIt"]),

        new("Bannerlord.ButterLib", "ButterLib", PrerequisiteKind.Module, 2018,
            "https://github.com/BUTR/Bannerlord.ButterLib",
            Strings.Current["Core.Modules.Prereq.ButterLib.Needs"],
            Strings.Current["Core.Modules.Prereq.ButterLib.WithoutIt"]),

        new("Bannerlord.UIExtenderEx", "UIExtenderEx", PrerequisiteKind.Module, 2102,
            "https://github.com/BUTR/Bannerlord.UIExtenderEx",
            Strings.Current["Core.Modules.Prereq.UIExtenderEx.Needs"],
            Strings.Current["Core.Modules.Prereq.UIExtenderEx.WithoutIt"]),

        new("Bannerlord.MBOptionScreen", "MCM", PrerequisiteKind.Module, 612,
            "https://github.com/Aragas/Bannerlord.MBOptionScreen",
            Strings.Current["Core.Modules.Prereq.Mcm.Needs"],
            Strings.Current["Core.Modules.Prereq.Mcm.WithoutIt"])
    ];

    public static Prerequisite? ById(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    // BLSE is looked for by its own files rather than in the module list, because it is not a module and
    // never appears there. Reporting it missing on that basis would be wrong on every install that has it.
    public static IReadOnlyList<PrerequisiteState> Inspect(
        string? gameInstallPath,
        IEnumerable<ModuleId> installedModuleIds)
    {
        ArgumentNullException.ThrowIfNull(installedModuleIds);

        var installed = installedModuleIds.Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. All.Select(item => item.Kind switch
            {
                PrerequisiteKind.Loader => InspectLoader(item, gameInstallPath),
                _ => new PrerequisiteState(
                    item,
                    installed.Contains(item.Id),
                    installed.Contains(item.Id)
                        ? Strings.Current.Format("Core.Modules.Prereq.InModuleList", item.Id)
                        : Strings.Current.Format("Core.Modules.Prereq.NotInstalled", item.Id))
            })
        ];
    }

    public const string LoaderFileName = "Bannerlord.BLSE.Standalone.exe";

    // Where the loader actually is, for anything that needs the file rather than a yes or no. Both PC
    // platforms are looked in. GetBinaryFolders returns folder names rather than paths, and an install
    // can carry both, so finding the loader under either one counts.
    public static string? FindLoaderPath(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return null;

        foreach (var folder in GameInstallLocator.GetBinaryFolders(gameInstallPath))
        {
            var launcher = Path.Combine(gameInstallPath, "bin", folder, LoaderFileName);

            if (File.Exists(launcher))
                return launcher;
        }

        return null;
    }

    private static PrerequisiteState InspectLoader(Prerequisite item, string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return new PrerequisiteState(item, false, Strings.Current["Core.Modules.Prereq.NoGameFolder"]);

        var folders = GameInstallLocator.GetBinaryFolders(gameInstallPath);

        if (folders.Count == 0)
            return new PrerequisiteState(item, false, Strings.Current["Core.Modules.Prereq.NoBinFolder"]);

        if (FindLoaderPath(gameInstallPath) is { } launcher)
            return new PrerequisiteState(item, true, Strings.Current.Format("Core.Modules.Prereq.FoundLoader", launcher));

        return new PrerequisiteState(item, false,
            Strings.Current.Format("Core.Modules.Prereq.LoaderMissing", string.Join(" or bin\\", folders)));
    }
}
