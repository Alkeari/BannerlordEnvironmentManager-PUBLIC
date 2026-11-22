using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The work a save setup restore does, through the paths BEM already has: a game version comes from
    // the Versions download (its own dialog, sign-in and progress) or from a local copy of an instance
    // already running it, a Nexus file goes through Library's update install (the Premium rule, the
    // safety scan, the overwrite questions), and the rest is SaveSetupFiles. Every write lands in the
    // copy; the resting instance is refused outright as a destination.
    internal sealed class SaveSetupActions(
        InstanceManager instanceManager,
        string? restingInstanceId,
        bool canFetchFromNexus,
        string accountRefusal) : ISaveSetupActions
    {
        public async Task<SaveSetupInstanceOutcome> PrepareInstanceAsync(
            SaveSetupTarget target, IProgress<SaveSetupProgress>? progress, CancellationToken cancellationToken)
        {
            switch (target.Kind)
            {
                case SaveSetupTargetKind.ReuseCopy when target.Instance is { } earlier:
                    return new SaveSetupInstanceOutcome(earlier,
                        Strings.Current.Format("Saves.Setup.Instance.Reused", target.CopyName));

                case SaveSetupTargetKind.CopyInstance when target.Instance is { } source:
                    return await CopyAsync(target, source, progress, cancellationToken);

                case SaveSetupTargetKind.Download:
                    return await DownloadAsync(target, progress);

                default:
                    return new SaveSetupInstanceOutcome(null, target.Reason);
            }
        }

        private async Task<SaveSetupInstanceOutcome> CopyAsync(
            SaveSetupTarget target, InstalledInstance source, IProgress<SaveSetupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var registry = new InstanceRegistry(new InstanceSettingsStore().Read().GamesRoot);
            var draft = InstanceDraft.ForDownload(
                target.Version, source.Record.Branch, source.Record.BuildId, target.Variant, target.CopyName);
            var folder = registry.FolderForDownload(draft);
            var existedBefore = Directory.Exists(folder);
            var gameFolder = InstanceLayout.GameFolder(folder);

            progress?.Report(new SaveSetupProgress(Strings.Current["Saves.Setup.Progress.Measuring"], 0, 1));

            var needed = await Task.Run(() => SaveSetupFiles.MeasureGame(source.Record.GameFolder), cancellationToken);
            var free = SaveSetupFiles.FreeBytes(registry.GamesRoot);

            // On the same drive the game's files are hard-linked, so the copy takes almost no space.
            if (SaveSetupFiles.WouldHardLink(source.Record.GameFolder, registry.GamesRoot))
                needed = 0;

            if (free is { } available && available < needed)
            {
                return new SaveSetupInstanceOutcome(null, Strings.Current.Format(
                    "Saves.Setup.Instance.NoSpace", InstanceRemovalSize.FormatBytes(needed), InstanceRemovalSize.FormatBytes(available), registry.GamesRoot));
            }

            try
            {
                await Task.Run(() => SaveSetupFiles.CopyGameAsync(source.Record.GameFolder, gameFolder, progress, cancellationToken),
                    cancellationToken);

                var registered = instanceManager.Register(
                    folder, target.Version.ToString(), source.Record.Branch, source.Record.BuildId, target.Variant,
                    target.CopyName);

                LoggingService.Log($"Save setup restore copied '{source.Record.GameFolder}' into '{gameFolder}' as '{target.CopyName}'.");

                return new SaveSetupInstanceOutcome(registered,
                    Strings.Current.Format("Saves.Setup.Instance.Copied", target.CopyName,
                        InstanceLabel.LabelOf(source.Record, restingInstanceId)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // The folder was made by this copy and holds nothing but what it wrote, so a copy that
                // did not finish takes it away again rather than leaving a broken game behind. A folder
                // that was already there is left exactly as it is.
                if (!existedBefore)
                    TryRemove(folder);

                if (ex is OperationCanceledException)
                    throw;

                LoggingService.LogException(ex, $"Save setup restore could not copy '{source.Record.GameFolder}'");

                return new SaveSetupInstanceOutcome(null, Strings.Current.Format("Saves.Setup.Instance.CopyFailed", ex.Message));
            }
        }

        private static async Task<SaveSetupInstanceOutcome> DownloadAsync(SaveSetupTarget target, IProgress<SaveSetupProgress>? progress)
        {
            var switcher = ShellViewModels.Instance.VersionSwitcher;

            if (!switcher.HasSteamBranchList)
                await switcher.RefreshIfIdleAsync();

            if (switcher.OfferFor(target.Version, target.Variant) is not { } row)
            {
                return new SaveSetupInstanceOutcome(null, Strings.Current.Format(
                    "Core.Saves.Setup.NotPublished", GameVersionLabel.For(target.Version, target.Variant)));
            }

            progress?.Report(new SaveSetupProgress(Strings.Current.Format("Saves.Setup.Progress.Download", row.Label), 0, 1));

            // The Versions download asks its own questions (the sign-in, the name, the size) and shows
            // its own progress on Play and Versions, so the restore waits on it rather than doing it again.
            if (!await switcher.DownloadAsync(row, offeredName: target.CopyName) || switcher.LastDownloaded is not { } landed)
            {
                return new SaveSetupInstanceOutcome(null, Strings.Current.Format(
                    "Saves.Setup.Instance.DownloadFailed", row.Label, switcher.StatusMessage));
            }

            return new SaveSetupInstanceOutcome(landed,
                Strings.Current.Format("Saves.Setup.Instance.Downloaded", row.Label, InstanceLabel.NameOf(landed.Record)));
        }

        public async Task<SaveSetupFetchOutcome> FetchAsync(
            SaveRollbackStep step, InstalledInstance instance, CancellationToken cancellationToken)
        {
            Refuse(instance);

            if (step.File is not { } file || step.NexusModId is not { } modId)
                return new SaveSetupFetchOutcome(false, false, Strings.Current["Core.Saves.Setup.Mod.NoNexusPage"]);

            if (AlreadyIn(instance, step.Id, step.SaveVersion))
                return new SaveSetupFetchOutcome(true, false, string.Empty);

            if (!canFetchFromNexus)
                return new SaveSetupFetchOutcome(false, true, accountRefusal);

            var results = await ShellViewModels.Instance.Install.InstallModUpdatesAsync(
                [new ModUpdateTarget(step.Id.Value, step.DisplayName, modId, file.FileId, file.FileName, file.Version)],
                null,
                cancellationToken,
                [InstallTarget.For(instance, restingInstanceId)]);

            return results.FirstOrDefault() is { } result
                ? new SaveSetupFetchOutcome(result.Installed, false, result.Message)
                : new SaveSetupFetchOutcome(false, false, Strings.Current["Saves.Setup.Mod.NothingInstalled"]);
        }

        public async Task<string?> CopyInstalledAsync(
            SaveRollbackStep step, InstalledInstance instance, CancellationToken cancellationToken)
        {
            Refuse(instance);

            if (step.InstalledFolder is not { Length: > 0 } source || !Directory.Exists(source))
                return Strings.Current["Saves.Setup.Mod.SourceGone"];

            var modules = ModuleScanner.GetModulesFolder(instance.Record.GameFolder);

            // Restoring into the copy while it is the version in use reads the module from the copy
            // itself, and copying a folder onto itself would delete it first.
            if (Path.GetFullPath(source).StartsWith(Path.GetFullPath(modules) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var folderName = WorkshopContent.IsWorkshopPath(source) ? step.Id.Value : null;

            try
            {
                await Task.Run(() => SaveSetupFiles.CopyModuleAsync(source, modules, folderName, cancellationToken), cancellationToken);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Save setup restore could not copy '{source}'");
                return ex.Message;
            }
        }

        public Task<string> CopySaveAsync(SaveFile save, InstalledInstance instance, CancellationToken cancellationToken)
        {
            Refuse(instance);

            return SaveSetupFiles.CopySaveAsync(save.Path, InstanceDataRoot.ForInstance(instance.Folder).GameSaves, cancellationToken);
        }

        // The one check every write passes: nothing a restore does may land in the player's own game.
        private void Refuse(InstalledInstance instance)
        {
            if (InstanceLabel.IsResting(instance.Record.Id, restingInstanceId))
                throw new InvalidOperationException(Strings.Current["Saves.Setup.Instance.RestingRefused"]);
        }

        // A copy made by an earlier restore may already hold the module at the recorded version, and
        // fetching it again would spend a download on bytes that are there.
        private static bool AlreadyIn(InstalledInstance instance, ModuleId id, string recordedVersion)
        {
            var scan = ModuleScanner.Scan(ModuleScanner.GetModulesFolder(instance.Record.GameFolder));
            var module = scan.Modules.FirstOrDefault(m => m.Id == id);

            return module is not null && !module.Version.IsEmpty
                && !ModuleVersion.Parse(recordedVersion).IsEmpty
                && module.Version.CompareTo(ModuleVersion.Parse(recordedVersion)) == 0;
        }

        private static void TryRemove(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Could not remove the unfinished save setup copy at {folder}");
            }
        }
    }
}
