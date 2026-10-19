using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.Launcher;

public static class LaunchTargetResolver
{
    private static readonly string SteamUri = $"steam://rungameid/{GameDlc.BaseAppId}";

    private static readonly LaunchTargetKind[] ProbeOrder =
    [
        LaunchTargetKind.BlseStandalone,
        LaunchTargetKind.GameExecutable,
        LaunchTargetKind.Steam,
        LaunchTargetKind.TaleWorldsLauncher
    ];

    // The two BLSE launchers are offered but never auto-detected: both hand the load order back to
    // the vanilla launcher UI, so picking one behind the user's back would ignore what BEM just
    // saved. They belong in the list the user chooses from, not in the fallback probe.
    //
    // The list is split into two groups: targets that go straight into the game with no launcher UI
    // (BlseStandalone, GameExecutable), then targets that open a launcher first (the BLSE shims,
    // TaleWorlds, Steam). The direct group comes first since BEM already does the job a launcher
    // does. Steam sits with the launchers: it hands off to whatever Steam is configured to start,
    // which is the TaleWorlds launcher, not the game directly.
    private static readonly LaunchTargetKind[] ListOrder =
    [
        LaunchTargetKind.BlseStandalone,
        LaunchTargetKind.GameExecutable,
        LaunchTargetKind.BlseLauncherEx,
        LaunchTargetKind.BlseLauncher,
        LaunchTargetKind.TaleWorldsLauncher,
        LaunchTargetKind.Steam
    ];

    public static IReadOnlyList<LaunchTarget> FindAvailable(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return [];

        var available = new List<LaunchTarget>();

        foreach (var kind in ListOrder)
        {
            if (TryBuild(gameInstallPath, kind, [], string.Empty, BlseCrashHandlerOptions.Default) is { } target)
                available.Add(target);
        }

        return available;
    }

    public static LaunchTarget? Resolve(
        string gameInstallPath,
        LaunchTargetKind? preferred,
        IReadOnlyList<ModuleEntry> enabledModules,
        string extraArguments,
        BlseCrashHandlerOptions? crashHandling = null)
    {
        ArgumentNullException.ThrowIfNull(enabledModules);

        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return null;

        var options = crashHandling ?? BlseCrashHandlerOptions.Default;

        if (preferred is { } kind && TryBuild(gameInstallPath, kind, enabledModules, extraArguments, options) is { } preferredTarget)
            return preferredTarget;

        foreach (var candidate in ProbeOrder)
        {
            if (TryBuild(gameInstallPath, candidate, enabledModules, extraArguments, options) is { } target)
                return target;
        }

        return null;
    }

    // Launching straight into a save reaches the game only through BLSE Standalone: it is the one
    // target that reads a command line rather than LauncherData.xml. /continuesave <SaveName> tells
    // BLSE which campaign to resume, but BLSE still refuses to start without a _MODULES_ argument on
    // the same command line - "No modules were provided as an argument!" - so one is built here from
    // the save's own recorded Modules list rather than from whatever is currently enabled in
    // Environment. That is what "the save is the order" actually requires: the exact list the save
    // was written with, not the list sitting in LauncherData.xml right now, which may have moved on.
    public static LaunchTarget? ResolveForSave(
        string gameInstallPath,
        string saveName,
        IReadOnlyList<SaveModuleRecord> modules,
        BlseCrashHandlerOptions? crashHandling = null,
        string extraArguments = "")
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (string.IsNullOrWhiteSpace(gameInstallPath) || string.IsNullOrWhiteSpace(saveName))
            return null;

        const LaunchTargetKind kind = LaunchTargetKind.BlseStandalone;

        if (GameInstallLocator.GetBinaryFolder(gameInstallPath) is not { } binaryFolder)
            return null;

        var path = Path.Combine(gameInstallPath, "bin", binaryFolder, "Bannerlord.BLSE.Standalone.exe");

        if (!File.Exists(path))
            return null;

        var options = crashHandling ?? BlseCrashHandlerOptions.Default;

        // The quoting here is doubled on purpose, and ordinary quoting around the name does not work.
        // TaleWorlds.Starter.Library.Program.Starter (read out of the game's own assembly) joins the
        // managed args array back into one string with plain spaces and no re-quoting before handing
        // it to the native engine, and BLSE's continue-save patch re-splits that joined string. So a
        // name passed as "My Save" reaches the CLR as the single arg My Save, is joined back as the
        // two bare words My Save, and BLSE reads the token after /continuesave as just My - which the
        // game then reports as "Failed to find save 'My'". Escaping literal quotes into the argument
        // itself ("\"My Save\"" on the raw command line) makes the CLR deliver "My Save" quotes and
        // all, the join reproduce a quoted token, and BLSE's splitter - which honors and strips
        // quotes - recover the full name.
        var arguments = $"/continuesave \"\\\"{saveName}\\\"\" {BuildModuleIds(modules.Select(m => m.Id.Value))}";

        if (options.ToArguments() is { Length: > 0 } crashFlags)
            arguments = $"{arguments} {crashFlags}";

        if (!string.IsNullOrWhiteSpace(extraArguments))
            arguments = $"{arguments} {extraArguments.Trim()}";

        return new LaunchTarget(kind, path, arguments, Strings.Current["Core.Launcher.Target.BlseDirectSaveOrder"]);
    }

    private static LaunchTarget? TryBuild(
        string gameInstallPath,
        LaunchTargetKind kind,
        IReadOnlyList<ModuleEntry> enabledModules,
        string extraArguments,
        BlseCrashHandlerOptions crashHandling)
    {
        // Steam is offered wherever BEM cannot rule it out, including an install it cannot read at all,
        // because it is the one target that does not depend on a file being present. The single case it
        // is withheld is a confidently detected Game Pass install, where the URI would either do nothing
        // or start a different copy of the game. A control that silently does the wrong thing is worse
        // than an absent one.
        if (kind == LaunchTargetKind.Steam)
        {
            return GameInstallLocator.DetectPlatform(gameInstallPath).IsGamePass
                ? null
                : new LaunchTarget(kind, SteamUri, string.Empty, DisplayNameFor(kind));
        }

        var (fileName, displayName) = kind switch
        {
            LaunchTargetKind.BlseStandalone => ("Bannerlord.BLSE.Standalone.exe", DisplayNameFor(kind)),
            LaunchTargetKind.BlseLauncher => ("Bannerlord.BLSE.Launcher.exe", DisplayNameFor(kind)),
            LaunchTargetKind.BlseLauncherEx => ("Bannerlord.BLSE.LauncherEx.exe", DisplayNameFor(kind)),
            LaunchTargetKind.GameExecutable => ("Bannerlord.exe", DisplayNameFor(kind)),
            _ => ("TaleWorlds.MountAndBlade.Launcher.exe", DisplayNameFor(kind))
        };

        if (GameInstallLocator.GetBinaryFolder(gameInstallPath) is not { } binaryFolder)
            return null;

        var path = Path.Combine(gameInstallPath, "bin", binaryFolder, fileName);

        if (!File.Exists(path))
            return null;

        // BLSE Standalone hands its arguments straight to TaleWorlds.Starter.Library.Program.Main
        // and never reads LauncherData.xml, so the module list must arrive on the command line
        // or nothing loads. The BLSE Launcher and LauncherEx shims start the vanilla launcher UI,
        // which reads LauncherData.xml itself, so a module list on their command line is wrong.
        var arguments = kind is LaunchTargetKind.GameExecutable or LaunchTargetKind.BlseStandalone
            ? BuildModuleArguments(enabledModules)
            : string.Empty;

        // Only Standalone's entrypoint inspects these three flags. LauncherEx takes the same three
        // settings from LauncherData.xml, BLSE Launcher hardcodes them on, and the vanilla launcher,
        // Steam and Bannerlord.exe have no BLSE crash handler to configure at all.
        if (kind == LaunchTargetKind.BlseStandalone && crashHandling.ToArguments() is { Length: > 0 } crashFlags)
            arguments = string.IsNullOrEmpty(arguments) ? crashFlags : $"{arguments} {crashFlags}";

        if (!string.IsNullOrWhiteSpace(extraArguments))
            arguments = string.IsNullOrEmpty(arguments) ? extraArguments.Trim() : $"{arguments} {extraArguments.Trim()}";

        return new LaunchTarget(kind, path, arguments, displayName);
    }

    // The same name shown in the launcher dropdown and in FindAvailable, kept as one mapping so a
    // caller that only has a kind (never having resolved a target on disk, such as InstanceManager
    // refusing a hand-off launch) still names it the way the user would recognize it.
    public static string DisplayNameFor(LaunchTargetKind kind) => kind switch
    {
        LaunchTargetKind.BlseStandalone => Strings.Current["Core.Launcher.Target.BlseStandalone"],
        LaunchTargetKind.BlseLauncher => Strings.Current["Core.Launcher.Target.BlseLauncher"],
        LaunchTargetKind.BlseLauncherEx => Strings.Current["Core.Launcher.Target.BlseLauncherEx"],
        LaunchTargetKind.GameExecutable => Strings.Current["Core.Launcher.Target.GameExecutable"],
        LaunchTargetKind.TaleWorldsLauncher => Strings.Current["Core.Launcher.Target.TaleWorldsLauncher"],
        _ => Strings.Current["Core.Launcher.Target.Steam"]
    };

    // Only LauncherEx honors BLSE's 11 settings in LauncherData.xml; Standalone reads three of them
    // as command-line flags instead, which is what BEM's crash handling settings send. The vanilla
    // launcher rewrites LauncherData.xml when the user presses Play, which can undo BEM's saved
    // order, hence its warning.
    public static string DescribeTooltip(LaunchTargetKind kind) => kind switch
    {
        LaunchTargetKind.BlseStandalone => Strings.Current["Core.Launcher.Target.Tooltip.BlseStandalone"],
        LaunchTargetKind.GameExecutable => Strings.Current["Core.Launcher.Target.Tooltip.GameExecutable"],
        LaunchTargetKind.BlseLauncherEx => Strings.Current["Core.Launcher.Target.Tooltip.BlseLauncherEx"],
        LaunchTargetKind.BlseLauncher => Strings.Current["Core.Launcher.Target.Tooltip.BlseLauncher"],
        LaunchTargetKind.TaleWorldsLauncher => Strings.Current["Core.Launcher.Target.Tooltip.TaleWorldsLauncher"],
        _ => Strings.Current["Core.Launcher.Target.Tooltip.Steam"]
    };

    // The one place a module list becomes a command line, and so the one place a repeated id can be
    // stopped for every module rather than for one of them.
    //
    // De-duplicated rather than refused: the game resolves each entry to a folder and loads it once, so
    // a repeated id has exactly one sensible reading, which is the first place it appears. Refusing
    // would let a list BEM itself built wrong decide that the game does not start, and every list BEM
    // builds is meant to be distinct already, so a repeat here is a defect upstream and not a choice
    // the user made.
    private static string BuildModuleArguments(IReadOnlyList<ModuleEntry> enabledModules) =>
        $"/singleplayer {BuildModuleIds(enabledModules.Select(m => m.Id.Value))}";

    // Shared with ResolveForSave, which builds this same _MODULES_*...*_MODULES_ segment from a save's
    // recorded module list instead of the currently enabled one.
    //
    // De-duplicated rather than refused: the game resolves each entry to a folder and loads it once, so
    // a repeated id has exactly one sensible reading, which is the first place it appears. Refusing
    // would let a list BEM itself built wrong decide that the game does not start, and every list BEM
    // builds is meant to be distinct already, so a repeat here is a defect upstream and not a choice
    // the user made.
    private static string BuildModuleIds(IEnumerable<string> ids)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids)
        {
            if (seen.Add(id))
                ordered.Add(id);
        }

        return ordered.Count == 0
            ? "_MODULES_*_MODULES_"
            : $"_MODULES_*{string.Join('*', ordered)}*_MODULES_";
    }
}
