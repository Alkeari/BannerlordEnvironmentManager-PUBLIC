using System;
using System.IO;
using System.Reflection;

namespace BannerlordEnvironmentManager.Companion;

// Finds Harmony's library for a launch that does not have Bannerlord.Harmony on its module list. The crash
// guard needs the library and nothing else of that module, and BEM does not add a module the player did
// not choose, so the companion takes whichever Harmony the game has already loaded and otherwise the one
// in the Bannerlord.Harmony folder beside it. The folder is a contract with WatchedLaunch.HarmonyLibraryOnDisk
// in BEM's Core. With Bannerlord.Harmony on the list this is never asked anything.
internal static class HarmonyResolver
{
    private const string LibraryName = "0Harmony";

    private static bool _installed;

    public static void Ensure()
    {
        if (_installed)
            return;

        _installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            if (!string.Equals(new AssemblyName(args.Name).Name, LibraryName, StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, LibraryName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            // Modules/BEM/bin/<platform>/<this assembly>
            var platformFolder = Path.GetDirectoryName(typeof(HarmonyResolver).Assembly.Location);
            var modules = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(platformFolder)));

            if (platformFolder is null || modules is null)
                return null;

            var library = Path.Combine(
                modules, "Bannerlord.Harmony", "bin", Path.GetFileName(platformFolder), LibraryName + ".dll");

            return File.Exists(library) ? Assembly.LoadFrom(library) : null;
        }
        catch
        {
            return null;
        }
    }
}
