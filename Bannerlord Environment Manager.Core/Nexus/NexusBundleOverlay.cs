namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record NexusBundleOverlayResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> BackedUp,
    IReadOnlyList<string> Skipped);

// What a Nexus collection revision bundles: its extracted archive holds a "bundled" folder with one folder
// per bundled mod, each laid out the way the curator's files sit under the game folder (Modules\Name\...).
// They are fixes and patches laid over the mods the collection installs, so they go in after those mods.
// Only files under Modules are written; anything else in a bundle, or a path that climbs out of the game
// folder, is counted as skipped and left alone. A file this replaces is first copied beside itself as
// <name>.bem.bak (never over an existing backup), which is how BEM keeps what it replaced.
public static class NexusBundleOverlay
{
    public const string BundledFolder = "bundled";
    public const string BackupSuffix = ".bem.bak";

    public static NexusBundleOverlayResult Apply(string extractedRoot, string gameFolder)
    {
        var written = new List<string>();
        var backedUp = new List<string>();
        var skipped = new List<string>();
        var bundled = Path.Combine(extractedRoot, BundledFolder);

        if (!Directory.Exists(bundled))
            return new NexusBundleOverlayResult(written, backedUp, skipped);

        var gameRoot = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var modulesRoot = gameRoot + "Modules" + Path.DirectorySeparatorChar;

        foreach (var modFolder in Directory.EnumerateDirectories(bundled))
        {
            foreach (var file in Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(modFolder, file);
                var target = Path.GetFullPath(Path.Combine(gameRoot, relative));

                if (!target.StartsWith(modulesRoot, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(relative);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    var backup = target + BackupSuffix;

                    if (!File.Exists(backup))
                    {
                        File.Copy(target, backup);
                        backedUp.Add(relative);
                    }
                }

                File.Copy(file, target, overwrite: true);
                Io.MarkOfTheWeb.RemoveFromCode(target);
                written.Add(relative);
            }
        }

        return new NexusBundleOverlayResult(written, backedUp, skipped);
    }
}
