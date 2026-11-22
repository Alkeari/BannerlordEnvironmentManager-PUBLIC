using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum ModuleResidueKind
{
    SettingsFolder,
    AnotherCopy,
    LoadOrderEntry
}

public sealed record ModuleResidue(ModuleResidueKind Kind, string Path, string Detail);

public enum ModuleUninstallOutcome
{
    Removed,
    FolderMissing,
    OfficialNotConfirmed,
    WorkshopSubscription,
    Failed
}

public sealed record ModuleUninstallPlan(
    ModuleId Id,
    string Name,
    string FolderPath,
    long SizeBytes,
    int FileCount,
    bool IsOfficial,
    IReadOnlyList<ModuleResidue> Residue,
    bool IsWorkshopSubscription,
    string? UnsubscribePage)
{
    public string Describe() =>
        Strings.Current.Plural(
            "Core.Modules.Uninstall.Plan.Describe", FileCount, Name, Id, ModuleUninstaller.DescribeSize(SizeBytes), FolderPath);
}

public sealed record ResidueCleanupReport(
    IReadOnlyList<ModuleResidue> Removed,
    IReadOnlyList<string> Failed,
    IReadOnlyList<ModuleResidue> Deferred,
    IReadOnlyList<ModuleResidue> Subscribed)
{
    public bool ChangedAnything => Removed.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();

        if (Removed.Count > 0)
            parts.Add(Strings.Current.Plural("Core.Modules.Uninstall.Residue.Sent", Removed.Count));

        if (Failed.Count > 0)
            parts.Add(Strings.Current.Plural("Core.Modules.Uninstall.Residue.Failed", Failed.Count, string.Join(" ", Failed)));

        if (Subscribed.Count > 0)
            parts.Add(Strings.Current.Plural("Core.Modules.Uninstall.Residue.Subscribed", Subscribed.Count));

        if (Deferred.Count > 0)
            parts.Add(Strings.Current.Plural("Core.Modules.Uninstall.Residue.Deferred", Deferred.Count));

        return parts.Count == 0 ? Strings.Current["Core.Modules.Uninstall.Residue.None"] : string.Join(" ", parts);
    }
}

public sealed record ModuleUninstallReport(
    ModuleUninstallOutcome Outcome,
    string Message,
    IReadOnlyList<ModuleResidue> Residue)
{
    public bool Removed => Outcome == ModuleUninstallOutcome.Removed;
}

// Removing the folder is delegated to the caller so Core stays free of Windows: the app hands in the
// Recycle Bin call, and a test hands in a plain delete. Nothing here reports a removal it has not
// confirmed on disk afterwards.
public static class ModuleUninstaller
{
    public static ModuleUninstallPlan Plan(ModuleManifest module, IReadOnlyList<ModuleResidue> residue)
    {
        ArgumentNullException.ThrowIfNull(module);

        var (sizeBytes, fileCount) = Measure(module.FolderPath);

        return new ModuleUninstallPlan(
            module.Id,
            module.Name,
            module.FolderPath,
            sizeBytes,
            fileCount,
            module.IsOfficial,
            residue,
            WorkshopContent.Owns(module),
            ModulePageUrl.WorkshopUnsubscribePage(module.FolderPath));
    }

    // Naming what an uninstall left behind and offering no way to clear it is half a feature. This is
    // the other half: the same list, acted on, with every path confirmed gone afterwards rather than
    // assumed. The load order entry is not touched here because it is not a file and the Environment
    // tab owns it, and saying so is better than silently leaving it out of the count.
    public static ResidueCleanupReport RemoveResidue(
        IReadOnlyList<ModuleResidue> residue,
        Action<string> removePath)
    {
        ArgumentNullException.ThrowIfNull(residue);
        ArgumentNullException.ThrowIfNull(removePath);

        var removed = new List<ModuleResidue>();
        var failed = new List<string>();
        var deferred = new List<ModuleResidue>();
        var subscribed = new List<ModuleResidue>();

        foreach (var item in residue)
        {
            if (item.Kind == ModuleResidueKind.LoadOrderEntry)
            {
                deferred.Add(item);
                continue;
            }

            // The shadowed copy of a mod installed under Modules is very often the Workshop copy, so
            // clearing leftovers reaches Steam's folders even when the module uninstalled was local.
            if (WorkshopContent.Owns(item.Path))
            {
                subscribed.Add(item);
                continue;
            }

            if (!Directory.Exists(item.Path) && !File.Exists(item.Path))
            {
                removed.Add(item);
                continue;
            }

            try
            {
                removePath(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{item.Path} could not be removed: {ex.Message}");
                continue;
            }

            if (Directory.Exists(item.Path) || File.Exists(item.Path))
                failed.Add($"{item.Path} is still on disk.");
            else
                removed.Add(item);
        }

        return new ResidueCleanupReport(removed, failed, deferred, subscribed);
    }

    public static ModuleUninstallReport Uninstall(
        ModuleUninstallPlan plan,
        string? confirmation,
        Action<string> removeFolder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(removeFolder);

        if (!Directory.Exists(plan.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.FolderMissing,
                Strings.Current.Format("Core.Modules.Uninstall.NotOnDisk", plan.FolderPath),
                plan.Residue);
        }

        // Before the confirmation, because no amount of confirming makes this work: Steam holds the
        // subscription, puts the files back, and the folder is shared by every install on the machine.
        if (plan.IsWorkshopSubscription || WorkshopContent.Owns(plan.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.WorkshopSubscription,
                Strings.Current.Format("Core.Modules.Uninstall.Subscribed", plan.Name),
                plan.Residue);
        }

        if (plan.IsOfficial && !Confirms(confirmation, plan))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.OfficialNotConfirmed,
                Strings.Current.Format("Core.Modules.Uninstall.OfficialConfirm", plan.Name),
                plan.Residue);
        }

        try
        {
            removeFolder(plan.FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.Failed,
                Strings.Current.Format("Core.Modules.Uninstall.RemoveFailed", plan.FolderPath, ex.Message),
                plan.Residue);
        }

        if (Directory.Exists(plan.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.Failed,
                Strings.Current.Format("Core.Modules.Uninstall.StillOnDisk.Named", plan.FolderPath, plan.Name),
                plan.Residue);
        }

        return new ModuleUninstallReport(
            ModuleUninstallOutcome.Removed,
            Strings.Current.Plural(
                "Core.Modules.Uninstall.Removed", plan.FileCount, plan.Name, plan.Id,
                DescribeSize(plan.SizeBytes), plan.FolderPath, DescribeResidue(plan.Residue)),
            plan.Residue);
    }

    // A folder under Modules with no SubModule.xml in it. There is no manifest to plan against, so this
    // takes the scan's own record of the folder rather than a path a caller composed.
    //
    // The manifest is re-tested immediately before the removal on purpose. A scan is a snapshot, and
    // between it and the button the user may well have reinstalled the mod into exactly this folder.
    // Removing it then would delete a working install on the strength of a stale reading, which is a far
    // worse outcome than refusing.
    public static ModuleUninstallReport RemoveFolderWithoutManifest(
        ModuleFolderWithoutManifest folder,
        Action<string> removeFolder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(removeFolder);

        if (!Directory.Exists(folder.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.FolderMissing,
                Strings.Current.Format("Core.Modules.Uninstall.NotOnDisk", folder.FolderPath),
                []);
        }

        // A Workshop item still downloading has no SubModule.xml yet, so it reads here as a folder with
        // nothing in it worth keeping. It is Steam's either way.
        if (WorkshopContent.Owns(folder.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.WorkshopSubscription,
                Strings.Current.Format("Core.Modules.Uninstall.SubscribedFolder", folder.FolderPath),
                []);
        }

        if (File.Exists(Path.Combine(folder.FolderPath, "SubModule.xml")))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.Failed,
                Strings.Current.Format("Core.Modules.Uninstall.NowHasManifest", folder.FolderPath),
                []);
        }

        try
        {
            removeFolder(folder.FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.Failed,
                Strings.Current.Format("Core.Modules.Uninstall.RemoveFailed", folder.FolderPath, ex.Message),
                []);
        }

        if (Directory.Exists(folder.FolderPath))
        {
            return new ModuleUninstallReport(
                ModuleUninstallOutcome.Failed,
                Strings.Current.Format("Core.Modules.Uninstall.StillOnDisk.Generic", folder.FolderPath),
                []);
        }

        return new ModuleUninstallReport(
            ModuleUninstallOutcome.Removed,
            Strings.Current.Plural("Core.Modules.Uninstall.FolderRemoved", folder.FileCount, folder.FolderPath, DescribeSize(folder.SizeBytes)),
            []);
    }

    // Everything of the module's that lives outside its own folder. Uninstalling never reaches any of
    // it, so it is listed with full paths rather than left for the user to find.
    public static IReadOnlyList<ModuleResidue> FindResidue(
        ModuleManifest module,
        IReadOnlyList<SettingsFolderReview> settings,
        IReadOnlyList<DuplicateModule> shadowed,
        string? loadOrderFilePath)
    {
        ArgumentNullException.ThrowIfNull(module);

        var residue = new List<ModuleResidue>();

        foreach (var review in settings.Where(review => review.MatchedModuleId == module.Id))
        {
            residue.Add(new ModuleResidue(
                ModuleResidueKind.SettingsFolder,
                review.Folder.FullPath,
                Strings.Current.Format("Core.Modules.Uninstall.Residue.Settings", module.Name, review.Evidence).TrimEnd()));
        }

        foreach (var folder in shadowed.Where(entry => entry.Id == module.Id).SelectMany(entry => entry.ShadowedFolderPaths))
        {
            residue.Add(new ModuleResidue(
                ModuleResidueKind.AnotherCopy,
                folder,
                Strings.Current.Format("Core.Modules.Uninstall.Residue.AnotherCopy", module.Id)));
        }

        if (!string.IsNullOrWhiteSpace(loadOrderFilePath))
        {
            residue.Add(new ModuleResidue(
                ModuleResidueKind.LoadOrderEntry,
                loadOrderFilePath,
                Strings.Current.Format("Core.Modules.Uninstall.Residue.LoadOrderEntry", module.Id)));
        }

        return residue;
    }

    public static string DescribeSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => Strings.Current.Plural("Core.Modules.Uninstall.Bytes", bytes)
    };

    private static string DescribeResidue(IReadOnlyList<ModuleResidue> residue) =>
        residue.Count == 0
            ? Strings.Current["Core.Modules.Uninstall.Residue.NoneElsewhere"]
            : Strings.Current.Plural(
                "Core.Modules.Uninstall.Residue.LeftAlone",
                residue.Count,
                string.Join("; ", residue.Select(item => Strings.Current.Format("Core.Modules.Uninstall.Residue.Item", item.Path, item.Detail))));

    private static bool Confirms(string? confirmation, ModuleUninstallPlan plan)
    {
        if (string.IsNullOrWhiteSpace(confirmation))
            return false;

        var typed = confirmation.Trim();

        return string.Equals(typed, plan.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typed, plan.Id.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static (long SizeBytes, int FileCount) Measure(string folder)
    {
        if (!Directory.Exists(folder))
            return (0, 0);

        long sizeBytes = 0;
        var fileCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                try
                {
                    sizeBytes += new FileInfo(file).Length;
                    fileCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (sizeBytes, fileCount);
    }
}
