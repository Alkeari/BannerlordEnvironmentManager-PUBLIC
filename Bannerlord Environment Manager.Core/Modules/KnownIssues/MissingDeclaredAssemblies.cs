using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record MissingDeclaredAssembly(
    ModuleId ModuleId,
    string DisplayName,
    IReadOnlyList<string> FileNames,
    string BinFolder)
{
    public string Describe() => Strings.Current.Plural(
        "Core.Modules.MissingAssembly.Describe", FileNames.Count, DisplayName, string.Join(", ", FileNames), BinFolder);
}

// A module naming an assembly the game must open, in a folder that does not have it. Not advice and
// not a heuristic: TaleWorlds.ModuleManager builds <module>\bin\<platform>\<DLLName>, opens that exact
// path, and stops with "Couldn't find .dll:" when the file is not there. The run ends in an error
// window rather than at the main menu.
//
// This exists because BEM caused it. Its shadowed-assembly remedy deleted a declared copy, and the
// Recycle Bin had to be swept by hand to find out how much else had gone; a sweep that cheap belongs in
// the product rather than in a person's evening.
//
// The tags are what keep it from crying wolf. Vanilla Multiplayer declares six dedicated-server
// assemblies and one GDK one, and Native declares the PlayStation and GDK platform ones; nine names
// that ship on no PC install and are missing on every healthy one. Each carries the tag that says so,
// so the manifest itself separates them and BEM never has to keep a list of vanilla exceptions.
//
// Measured on a real install on 2026-08-14: 229 modules with a manifest, 250 declared names, 9
// ruled out by their tags, 0 missing. Disproof condition: a name this reports whose absence does not
// stop the game, or a missing declared DLL it stays quiet about.
public static class MissingDeclaredAssemblies
{
    public static IReadOnlyList<MissingDeclaredAssembly> Find(
        IReadOnlyList<ModuleEntry> loadOrder,
        string? loadedPlatformFolder = null)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var platformFolder = loadedPlatformFolder ?? GameInstallLocator.StandardBinaryFolder;
        var found = new List<MissingDeclaredAssembly>();

        foreach (var entry in loadOrder)
        {
            // Only what this run loads. A disabled module cannot stop a launch it takes no part in, and
            // a module with no readable manifest is already the validator's finding rather than this
            // one.
            if (!entry.IsEnabled || entry.Manifest is not { } manifest)
                continue;

            if (manifest.DeclaredAssemblies is not { } declared || string.IsNullOrWhiteSpace(manifest.FolderPath))
                continue;

            var binFolder = Path.Combine(manifest.FolderPath, "bin", platformFolder);

            var missing = declared
                // A wildcard names a shelf of per-game-version builds and cannot be looked up as a
                // file. Whether the loader finds a build for this game is its own question.
                .Where(d => !d.IsPattern)
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                // A name declared by two SubModules is loaded when either of them loads.
                .Where(g => g.Any(d => d.Gate.SkippedIn(platformFolder) is null))
                .Select(g => g.Key)
                .Where(name => !Exists(binFolder, name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count > 0)
                found.Add(new MissingDeclaredAssembly(entry.Id, entry.DisplayName, missing, binFolder));
        }

        return found;
    }

    // A path BEM cannot reach is not a file BEM knows is absent. Reporting one as missing would send
    // the user looking for a DLL that is exactly where it should be.
    private static bool Exists(string binFolder, string fileName)
    {
        try
        {
            return File.Exists(Path.Combine(binFolder, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }
}
