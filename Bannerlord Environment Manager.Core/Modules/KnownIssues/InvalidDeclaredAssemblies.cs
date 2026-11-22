using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// IsSubModuleDll: the file is a SubModule's DLLName, which the game must load as the module's code.
public sealed record InvalidDeclaredAssembly(
    ModuleId ModuleId, string DisplayName, string FilePath, string Reason, bool IsSubModuleDll = false);

// A declared assembly that is on disk but is not a loadable .NET assembly: empty, truncated, or a native
// image. The engine logs "Could not load file or assembly ... The module was expected to contain an
// assembly manifest" (zzzUniversalPatch, rgl_log_32380.txt line 1154, a partial copy) and the module's
// code never runs. MissingDeclaredAssemblies only checks that the file exists.
//
// What the game does with it depends on which declaration named the file (TaleWorlds.MountAndBlade
// Module.cs LoadSubModules, with AssemblyLoader.LoadFrom in TaleWorlds.Library):
//   - A SubModule's DLLName that exists but will not load: LoadFrom returns CriticalError, LoadSubModules
//     hands it to HandleSubmoduleLoadError, which collects it, and after the loop LoadSubModules shows the
//     error box and throws. The game does not start.
//   - An <Assemblies> entry that will not load: LoadFrom shows "Cannot load:" and the entry is listed as
//     LoadedWithErrors. The game goes on.
// The engine does not simply log the failed load and go on without the module's code.
//
// Only the PE headers and the presence of metadata are read. An obfuscated assembly whose metadata BEM
// cannot fully read (AliveScenes, Warlord) still has both and is not reported here.
public static class InvalidDeclaredAssemblies
{
    public static IReadOnlyList<InvalidDeclaredAssembly> Find(IReadOnlyList<ModuleEntry> loadOrder, string? loadedPlatformFolder = null)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var platform = loadedPlatformFolder ?? GameInstallLocator.StandardBinaryFolder;
        var found = new List<InvalidDeclaredAssembly>();

        foreach (var entry in loadOrder)
        {
            if (!entry.IsEnabled || entry.Manifest is not { } manifest || manifest.DeclaredAssemblies is not { } declared
                || string.IsNullOrWhiteSpace(manifest.FolderPath))
            {
                continue;
            }

            var bin = Path.Combine(manifest.FolderPath, "bin", platform);

            foreach (var group in declared
                         .Where(d => !d.IsPattern && d.Gate.SkippedIn(platform) is null)
                         .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(bin, group.Key);

                if (Problem(path) is { } reason)
                    found.Add(new InvalidDeclaredAssembly(entry.Id, entry.DisplayName, path, reason, group.Any(d => d.IsSubModuleDll)));
            }
        }

        return found;
    }

    // Null when the file is absent (MissingDeclaredAssemblies reports that), unreachable, or a valid
    // managed image.
    public static string? Problem(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
                return null;

            if (info.Length == 0)
                return Strings.Current["Core.Modules.InvalidAssembly.Empty"];

            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);

            _ = pe.PEHeaders;

            return pe.HasMetadata ? null : Strings.Current["Core.Modules.InvalidAssembly.NoMetadata"];
        }
        catch (BadImageFormatException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
