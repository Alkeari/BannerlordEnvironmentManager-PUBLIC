using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record ShaderQuarantineResult(
    IReadOnlyList<QuarantinedItem> Quarantined,
    IReadOnlyList<string> Failed,
    int ModulesAffected,
    long BytesReclaimed);

public static class ShaderQuarantine
{
    public const string Reason = "shaders folder";

    // Every shaders folder a mod ships was compiled for some earlier game build, and the engine dies at
    // graphics load on one that no longer matches ("Shader cache version of the external module ... is
    // invalid"). So every launch sets them all aside first, with no question asked; the engine rebuilds
    // what it needs, and the quarantine keeps each folder restorable.
    public static ShaderQuarantineResult RunBeforeLaunch(string gameInstallPath, QuarantineStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return new ShaderQuarantineResult([], [], 0, 0);

        var roots = new List<string> { ModuleScanner.GetModulesFolder(gameInstallPath) };
        if (ModuleScanner.GetWorkshopFolder(gameInstallPath) is { } workshop)
            roots.Add(workshop);

        return Run(ShaderFolders.FindAll(roots), store);
    }

    public static ShaderQuarantineResult Run(IEnumerable<ShaderFolder> folders, QuarantineStore store)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(store);

        var quarantined = new List<QuarantinedItem>();
        var failed = new List<string>();

        foreach (var folder in folders)
        {
            // Detection already excludes engine-owned paths, but this method is also the one-click
            // action, so the guard is repeated where the move actually happens.
            if (ShaderFolders.IsEngineOwned(folder.Path))
            {
                failed.Add(Strings.Current.Format("Core.Install.ShaderQuarantine.EngineOwned", folder.Path));
                continue;
            }

            try
            {
                quarantined.Add(store.Store(folder.Path, folder.ModuleFolderName, folder.RelativePath, Reason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException)
            {
                failed.Add(Strings.Current.Format("Core.Install.ShaderQuarantine.MoveFailed", folder.Path, ex.Message));
            }
        }

        return new ShaderQuarantineResult(
            quarantined,
            failed,
            quarantined.Select(item => item.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            quarantined.Sum(item => item.SizeBytes));
    }
}
