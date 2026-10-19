using BannerlordEnvironmentManager.Core.Localization;
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
