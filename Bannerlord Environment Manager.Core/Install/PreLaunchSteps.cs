using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record PreLaunchResult(
    int Unblocked, int ShaderFoldersSetAside, int PlatformFoldersSetAside, IReadOnlyList<string> Failures)
{
    public static PreLaunchResult Nothing { get; } = new(0, 0, 0, []);

    // Other-platform folders BEM copied so a module that shipped only those loads on this install. Undo
    // for each is in the Undo list.
    public int PlatformFoldersAdopted { get; init; }

    // The one line a launch shows when this step changed something on disk, empty when it did not.
    // Unblocking files is not counted: it changes nothing a player would see.
    public string Note => PreLaunchNote.For(ShaderFoldersSetAside, PlatformFoldersSetAside, PlatformFoldersAdopted);
}

public static class PreLaunchNote
{
    public static string For(int shadersSetAside, int platformSetAside, int platformAdopted)
    {
        var parts = new List<string>();

        if (shadersSetAside > 0)
            parts.Add(Strings.Current.Plural("Core.Install.PreLaunch.Note.Shaders", shadersSetAside));

        if (platformSetAside > 0)
            parts.Add(Strings.Current.Plural("Core.Install.PreLaunch.Note.PlatformSetAside", platformSetAside));

        if (platformAdopted > 0)
            parts.Add(Strings.Current.Plural("Core.Install.PreLaunch.Note.PlatformAdopted", platformAdopted));

        return parts.Count == 0
            ? string.Empty
            : Strings.Current.Format("Core.Install.PreLaunch.Note", string.Join("; ", parts));
    }
}

/// <summary>
/// What runs before the game starts, whoever starts it: unblock every module's bin files, set aside the
/// shader folders mods ship, and set aside the other platform's binaries where this install's platform is
/// known. BEM's own Launch and the tray watcher both call this one method, so they cannot drift apart.
/// Every step is caught on its own, so one that fails never stops the next or the launch.
/// </summary>
public static class PreLaunchSteps
{
    /// <param name="gameRoot">The game install folder.</param>
    /// <param name="unblock">Removes the downloaded-from-the-internet mark from one file; true when it did.</param>
    /// <param name="store">Where set-aside folders go, restorable.</param>
    public static PreLaunchResult Run(string? gameRoot, Func<string, bool> unblock, QuarantineStore store)
    {
        ArgumentNullException.ThrowIfNull(unblock);
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return PreLaunchResult.Nothing;

        var failures = new List<string>();
        var unblocked = 0;
        var shaders = 0;
        var platform = 0;
        var adopted = 0;

        try
        {
            foreach (var file in PreLaunchUnblock.Candidates(gameRoot))
            {
                try
                {
                    if (unblock(file))
                        unblocked++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(ex.Message);
        }

        try
        {
            var result = ShaderQuarantine.RunBeforeLaunch(gameRoot, store);
            shaders = result.Quarantined.Count;
            failures.AddRange(result.Failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(ex.Message);
        }

        try
        {
            var detection = GameInstallLocator.DetectPlatform(gameRoot);

            if (detection.IsConfident)
            {
                var roots = new List<string> { ModuleScanner.GetModulesFolder(gameRoot) };
                if (ModuleScanner.GetWorkshopFolder(gameRoot) is { } workshop)
                    roots.Add(workshop);

                var result = PlatformBinaries.Quarantine(
                    PlatformBinaries.FindAll(roots, detection.FilterPlatformFolder), store);
                platform = result.Quarantined.Count;
                failures.AddRange(result.Failed);

                // A module whose only binaries are for the other platform is inert here, so the copy that
                // makes it load is made now, keeping the shipped folder, and logged for the Undo list. A
                // copy the owner undid is never made again.
                var log = new PlatformAdoptionLog(store.RootPath);
                var adoptable = PlatformBinaries.FindAllAdoptable(roots, detection.FilterPlatformFolder)
                    .Where(folder => !log.WasUndone(folder.TargetPath))
                    .ToList();

                if (adoptable.Count > 0)
                {
                    var made = PlatformBinaries.Adopt(adoptable);
                    log.Record(adoptable, made);
                    adopted = made.Created.Count;
                    failures.AddRange(made.Failed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(ex.Message);
        }

        return new PreLaunchResult(unblocked, shaders, platform, failures) { PlatformFoldersAdopted = adopted };
    }
}
