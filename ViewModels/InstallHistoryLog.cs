using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Writes an uninstall into the install history. The version is read before the folder goes, and a
    // history that cannot be written never fails the uninstall.
    internal static class InstallHistoryLog
    {
        public static string? VersionBefore(ModuleUninstallPlan plan) =>
            SubModuleXmlParser.TryLoad(Path.Combine(plan.FolderPath, "SubModule.xml"), out var manifest, out _)
                ? manifest.VersionText ?? manifest.Version.ToString()
                : null;

        public static void Removed(ModuleUninstallPlan plan, string? version)
        {
            try
            {
                new InstallHistoryStore(InstallHistoryStore.DefaultPath(ShellViewModels.Instance.Environment.ActiveDataRoot))
                    .RecordRemoval(plan.Id.Value, version);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to record an uninstall in the install history");
            }
        }
    }
}
