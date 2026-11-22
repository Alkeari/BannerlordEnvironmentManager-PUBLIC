using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace BannerlordEnvironmentManager.Core.DryRun;

// One module loaded into the game process: a native DLL or a managed assembly, and for a managed one the
// Bannerlord mod folder it sits in, when it sits in one.
public sealed record LoadedModule(string Name, string Path, bool IsManaged, string? ModFolder);

// The live loaded-modules list of the Exception Manager, split native from managed by the PE file's CLI
// header, the way the runtime itself tells an assembly from a native image.
public static class LoadedModules
{
    // Every module the process has loaded, mod assemblies first, then other managed ones, then native,
    // each group by name. A module whose file cannot be read stays in the list as native with no mod.
    public static IReadOnlyList<LoadedModule> Read(int processId, string? gameInstallPath)
    {
        using var process = Process.GetProcessById(processId);
        var paths = new List<string>();

        foreach (ProcessModule module in process.Modules)
        {
            try
            {
                paths.Add(module.FileName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The module unloaded while the list was being read.
            }
        }

        return Describe(paths, gameInstallPath);
    }

    public static IReadOnlyList<LoadedModule> Describe(IEnumerable<string> paths, string? gameInstallPath) =>
    [
        .. paths
            .Select(path =>
            {
                var managed = IsManaged(path);

                return new LoadedModule(System.IO.Path.GetFileName(path), path, managed, managed ? ModFolderOf(path, gameInstallPath) : null);
            })
            .OrderBy(m => m.ModFolder is not null ? 0 : m.IsManaged ? 1 : 2)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
    ];

    // One row of the tab: kind, file name, the mod folder where there is one, and the full path.
    public static string Row(Localization.ILocalizer strings, LoadedModule module)
    {
        var kind = strings[module.IsManaged ? "Core.DryRun.Exception.Module.Managed" : "Core.DryRun.Exception.Module.Native"];

        return module.ModFolder is null
            ? strings.Format("Core.DryRun.Exception.Module.Row", kind, module.Name, module.Path)
            : strings.Format("Core.DryRun.Exception.Module.RowMod", kind, module.Name, module.ModFolder, module.Path);
    }

    // Managed when the PE file carries a CLI header. A file that cannot be read or is not a PE image is
    // native, which is the safe thing to say about a module BEM cannot open.
    public static bool IsManaged(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);

            return reader.HasMetadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    // The mod folder name when the file is under <game>\Modules\<mod>\; with no install path, under the
    // last "Modules" folder in the path. Null for the game's own bin folders and anything else.
    public static string? ModFolderOf(string path, string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            var modules = System.IO.Path.Combine(gameInstallPath, "Modules") + System.IO.Path.DirectorySeparatorChar;

            return path.StartsWith(modules, StringComparison.OrdinalIgnoreCase) ? FirstFolder(path[modules.Length..]) : null;
        }

        var parts = path.Split('\\', '/');
        var at = Array.FindLastIndex(parts, p => p.Equals("Modules", StringComparison.OrdinalIgnoreCase));

        return at >= 0 && at + 2 < parts.Length ? parts[at + 1] : null;
    }

    // "<mod>\bin\Win64_Shipping_Client\X.dll" gives "<mod>"; a file straight in Modules has no mod folder.
    private static string? FirstFolder(string relative)
    {
        var parts = relative.Split('\\', '/');

        return parts.Length >= 2 && parts[0].Length > 0 ? parts[0] : null;
    }
}
