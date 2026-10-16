using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.DryRun;

public sealed record CompanionInstallResult(
    bool Installed,
    string ModuleFolder,
    string AssemblyPath,
    string ManifestPath,
    string? Reason = null);

public sealed record CompanionRemovalResult(
    bool Removed,
    IReadOnlyList<string> RemainingFolders,
    string? Error);

// The only place BEM writes into the user's game folder, and it writes exactly one module folder of
// its own and nothing else. Removal refuses anything it did not write.
public sealed class CompanionInstaller(string gameInstallPath)
{
    public string GameInstallPath { get; } = gameInstallPath;

    public string ModuleFolder => Path.Combine(GameInstallPath, "Modules", CompanionManifest.ModuleId);

    // Where builds of BEM from before each rename put the same module. It is BEM's own folder either
    // way, so every path that recognizes, lists or deletes the companion covers all of them.
    public IReadOnlyList<string> LegacyModuleFolders =>
        [.. CompanionManifest.LegacyModuleIds.Select(id => Path.Combine(GameInstallPath, "Modules", id))];

    public string ManifestPath => Path.Combine(ModuleFolder, "SubModule.xml");

    public bool IsInstalled => InstalledModuleId is not null;

    public bool IsUnderLegacyName => !IsOurs(ModuleFolder) && LegacyModuleFolders.Any(IsOurs);

    // Which id the game would have to be given to load what is actually on disk. A launch built from
    // the current id while an old folder is the one installed would find nothing and watch nothing,
    // and a watch that silently records an empty session is the failure this feature exists to avoid.
    public string? InstalledModuleId =>
        IsOurs(ModuleFolder)
            ? CompanionManifest.ModuleId
            : CompanionManifest.LegacyModuleIds.FirstOrDefault(
                id => IsOurs(Path.Combine(GameInstallPath, "Modules", id)));

    public IReadOnlyList<string> InstalledFolders =>
        [.. AllFolders.Where(IsOurs)];

    // What is still on disk after a removal, which is not the same question as what is still installed:
    // a folder can survive the delete while no longer answering to a manifest read, and a folder BEM
    // cannot delete is exactly what the user has to be told about.
    public IReadOnlyList<string> RemainingFolders =>
        [.. AllFolders.Where(Directory.Exists)];

    private IEnumerable<string> AllFolders => new[] { ModuleFolder }.Concat(LegacyModuleFolders);

    public CompanionInstallResult Install(string payloadPath)
    {
        if (GameInstallLocator.GetBinaryFolder(GameInstallPath) is not { } binaryFolder)
        {
            return new CompanionInstallResult(
                false, ModuleFolder, string.Empty, ManifestPath,
                Strings.Current.Format("Core.DryRun.CompanionInstaller.NoBinaryFolder", GameInstallPath));
        }

        var assemblyPath = Path.Combine(ModuleFolder, "bin", binaryFolder, CompanionManifest.AssemblyFileName);

        if (!File.Exists(payloadPath))
        {
            return new CompanionInstallResult(
                false, ModuleFolder, assemblyPath, ManifestPath,
                Strings.Current.Format("Core.DryRun.CompanionInstaller.PayloadMissing", payloadPath));
        }

        if (Directory.Exists(ModuleFolder) && !IsOurs(ModuleFolder))
        {
            return new CompanionInstallResult(
                false, ModuleFolder, assemblyPath, ManifestPath,
                Strings.Current.Format("Core.DryRun.CompanionInstaller.FolderNotOurs", ModuleFolder));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
            File.Copy(payloadPath, assemblyPath, overwrite: true);
            File.WriteAllText(ManifestPath, CompanionManifest.Build());

            // Installing under the current name while an old one is still there would leave two copies
            // of BEM's own module in the game. The old ones go, and only ever when their manifest says
            // they are BEM's.
            foreach (var legacy in LegacyModuleFolders.Where(IsOurs))
                Directory.Delete(legacy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CompanionInstallResult(
                false, ModuleFolder, assemblyPath, ManifestPath,
                Strings.Current.Format("Core.DryRun.CompanionInstaller.WriteFailed", ModuleFolder, ex.Message));
        }

        return new CompanionInstallResult(true, ModuleFolder, assemblyPath, ManifestPath);
    }

    // Both names, so a watch armed by an older build of BEM is still fully reversible.
    public bool Remove() => RemoveAll().Removed;

    // The same removal, with what went wrong kept rather than flattened into false. A folder BEM
    // cannot delete has a reason Windows already gave, and the user can only act on it if they are
    // told it. Every folder is attempted: one that will not go must not strand the others.
    public CompanionRemovalResult RemoveAll()
    {
        var folders = InstalledFolders;

        if (folders.Count == 0)
            return new CompanionRemovalResult(false, RemainingFolders, null);

        var removed = false;
        string? error = null;

        foreach (var folder in folders)
        {
            try
            {
                Delete(folder);
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error ??= ex.Message;
            }
        }

        return new CompanionRemovalResult(removed, RemainingFolders, error);
    }

    // The manifest goes last, and only once everything else is already gone. A plain recursive delete
    // that fails partway on a locked assembly can take the manifest with it, and a companion folder
    // with no manifest is one nothing can recognize as BEM's afterwards: the folder stays in the game
    // and the notice that would have offered to remove it goes quiet.
    private static void Delete(string folder)
    {
        var manifest = Path.Combine(folder, "SubModule.xml");

        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (string.Equals(entry, manifest, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }

        Directory.Delete(folder, recursive: true);
    }

    // Deleting a folder inside the user's game is the one destructive step in a dry run, so it only
    // ever happens to a folder whose manifest declares one of the companion's own ids.
    private static bool IsOurs(string folder)
    {
        try
        {
            var manifestPath = Path.Combine(folder, "SubModule.xml");

            if (!File.Exists(manifestPath))
                return false;

            return CompanionManifest.IsCompanionId(
                Modules.SubModuleXmlParser.TryRecoverDeclaredId(manifestPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
