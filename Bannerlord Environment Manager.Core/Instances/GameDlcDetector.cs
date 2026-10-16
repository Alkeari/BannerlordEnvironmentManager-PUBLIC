using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// What DLC an instance actually carries, read off its own Modules folder rather than trusted from
// InstanceRecord.Dlc. A referenced or adopted instance, or one whose files were dropped in outside
// BEM, never had a chance to write that field, and offering a 31 GB re-download of a DLC that is
// already sitting on disk is worse than a slightly slower check on every refresh.
public static class GameDlcDetector
{
    // Every known DLC this game folder carries, in GameDlc.Known order. Never throws: a referenced
    // instance can point at a drive that is disconnected or slow to answer, and a refresh that faults
    // on one row must not take the whole Versions list down with it. Empty is also what this answers
    // for a game folder that cannot currently be reached at all, which reads correctly for display
    // (nothing to show) but is the wrong answer for a caller deciding whether to offer a DLC as a
    // download; TryDetect is that caller's entry point instead.
    public static GameDlcSet Detect(string gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder))
            return GameDlcSet.Empty;

        var modulesFolder = Path.Combine(gameFolder, "Modules");
        var present = GameDlc.Known.Where(dlc => IsPresent(modulesFolder, dlc)).Select(dlc => dlc.AppId);

        return new GameDlcSet(present);
    }

    // For anything that treats the result as identity, not display: whether a version-and-variant pair
    // is already installed decides both de-duplication (GameVersionList.Build) and what gets offered as
    // a downloadable variant (GameDlc.MissingFrom), so a referenced install on a drive that is merely
    // unreachable right now must not read as "no DLC" there. This tells the two cases apart and returns
    // false when the game folder itself cannot be resolved, so the caller can fall back to
    // InstanceRecord.Dlc instead of trusting an empty set it cannot back up with a look at the disk.
    public static bool TryDetect(string gameFolder, out GameDlcSet detected)
    {
        detected = GameDlcSet.Empty;

        if (string.IsNullOrWhiteSpace(gameFolder))
            return false;

        bool reachable;

        try
        {
            reachable = Directory.Exists(gameFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reachable = false;
        }

        if (!reachable)
            return false;

        detected = Detect(gameFolder);
        return true;
    }

    // Present means the DLC's own module folder holds a SubModule.xml that both parses and has
    // actually finished being written. A folder that does not exist at all is not this DLC.
    // DepotDownloader preallocates files before filling them in, so a SubModule.xml that is still an
    // empty or all-zero shell for the last 30 seconds is a download in progress, read the same way
    // ModuleScanner already reads a module mid-extract, and not yet safe to call present; a manifest
    // that is settled but never became valid XML (corrupt, truncated, or still empty past that window)
    // is not present either, since TryLoad is what tells the two apart. A module folder that merely
    // shares part of the name (TavernmaidsNavalDLC beside NavalDLC on a real install) never
    // matches: the folder name has to equal the DLC's own ModuleFolder exactly.
    private static bool IsPresent(string modulesFolder, GameDlcInfo dlc)
    {
        try
        {
            var manifestPath = Path.Combine(modulesFolder, dlc.ModuleFolder, "SubModule.xml");

            if (!File.Exists(manifestPath) || SubModuleXmlParser.LooksLikePartialWrite(manifestPath))
                return false;

            return SubModuleXmlParser.TryLoad(manifestPath, out _, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
