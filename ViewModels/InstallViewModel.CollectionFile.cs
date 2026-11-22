using Windows.Storage.Pickers;
using WinRT.Interop;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Settings;
using BannerlordEnvironmentManager.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.ViewModels
{
    // A collection BEM wrote itself. Create a Collection saves the enabled mods in load order with where
    // each downloads from and the settings its mods wrote; Install a Collection reads the file back
    // through the same plan and fetch code a Nexus collection uses, then applies the curator's load order
    // and settings once the mods are on disk. The format and every decision live in Core
    // (BemCollection.cs); this file only gathers the inputs and shows the result.
    public partial class InstallViewModel
    {
        // The file read by the last Read Collection, kept so Install Collection applies exactly what the
        // player just looked at. Dropped whenever the link box changes.
        private BemCollection? bemCollection;

        // The unpacked bundle of a Nexus collection revision with files in it, laid over the installed mods
        // when the collection is applied.
        private string? nexusBundleFolder;

        // Paths pasted from Explorer arrive quoted.
        private static string? CollectionFileIn(string? text)
        {
            var path = text?.Trim().Trim('"');

            return !string.IsNullOrEmpty(path)
                && (path.EndsWith(BemCollection.FileExtension, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(path), VortexCollectionJson.FileName, StringComparison.OrdinalIgnoreCase))
                && File.Exists(path)
                    ? path
                    : null;
        }

        // A Nexus collection revision can carry bundled files (the curator's settings). They are not in the
        // GraphQL file list: the revision names a signed-in download, a 7z holding collection.json and the
        // bundled folder. This fetches it, unpacks it into a temporary folder and reads the settings out
        // through the same reader a collection.json on disk goes through. Null when there is nothing to
        // apply or the download could not be made; the reason goes to the log, never to a failed read.
        // BEM_COLLECTION_BUNDLE_KEEP names a folder the unpacked bundle is copied to for inspection.
        private async Task<BemCollection?> ReadNexusBundleAsync(NexusCollectionRevision revision)
        {
            if (revision.BundleLink is not { Length: > 0 } link || revision.BundleBytes <= 0)
                return null;

            var work = CreateTempFolder();

            try
            {
                var apiKey = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();

                if (apiKey is not { } key)
                    return null;

                var transport = new NexusHttpTransport();
                var urls = await new NexusClient(transport).GetCollectionBundleUrlsAsync(key, link, CancellationToken.None);

                if (!urls.IsOk || urls.Value is not { Count: > 0 } locations)
                {
                    LoggingService.Log($"Collection bundle link refused: {urls.Outcome} {urls.Message}");
                    return null;
                }

                var archive = Path.Combine(work, "bundle.7z");
                var downloaded = await transport.DownloadAsync(locations[0].Uri, archive, null, CancellationToken.None);

                if (!downloaded.Ok)
                {
                    LoggingService.Log($"Collection bundle download failed: {downloaded.Message}");
                    return null;
                }

                var provision = await EnsureSevenZipAsync(CancellationToken.None);

                if (!provision.Found)
                    return null;

                var unpacked = Path.Combine(work, "unpacked");
                await ExtractWith7ZipAsync(downloaded.Path ?? archive, unpacked);

                if (Environment.GetEnvironmentVariable("BEM_COLLECTION_BUNDLE_KEEP") is { Length: > 0 } keep)
                    await Task.Run(() => CopyFolder(unpacked, keep));

                var carried = VortexCollectionJson.ReadFile(Path.Combine(unpacked, VortexCollectionJson.FileName));
                var hasFiles = Directory.Exists(Path.Combine(unpacked, NexusBundleOverlay.BundledFolder))
                    && Directory.EnumerateFiles(Path.Combine(unpacked, NexusBundleOverlay.BundledFolder), "*", SearchOption.AllDirectories).Any();

                if (hasFiles)
                {
                    // Kept until the install applies it over the mods the collection brings in.
                    var kept = Path.Combine(Path.GetTempPath(), "BLModInstaller", "collection-bundles", revision.Slug + "-" + revision.RevisionNumber);
                    TryDeleteDirectory(kept);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    Directory.Move(unpacked, kept);
                    nexusBundleFolder = kept;
                }

                // Only the curator's settings are taken from a Nexus collection.json: its mod list is the
                // plan Nexus already gave, and a load order is never invented from it.
                return carried is null && !hasFiles
                    ? null
                    : new BemCollection(BemCollection.CurrentFormat, revision.Name, DateTimeOffset.UtcNow, [], carried?.Settings ?? [], []);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Reading a Nexus collection's bundle failed");
                return null;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static void CopyFolder(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        private async Task ReadCollectionFileAsync(string path)
        {
            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();
            CollectionResults.Clear();
            collectionPlans = [];
            bemCollection = null;
            nexusBundleFolder = null;
            OnPropertyChanged(nameof(HasCollectionPlan));

            var fileCollection = string.Equals(Path.GetFileName(path), VortexCollectionJson.FileName, StringComparison.OrdinalIgnoreCase)
                ? VortexCollectionJson.ReadFile(path)
                : BemCollectionFile.Read(path);

            if (fileCollection is not { } collection)
            {
                ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                    Strings.Current["Install.Collection.FileUnreadable"]);
                return;
            }

            IsBusy = true;

            try
            {
                var revision = collection.ToRevision();
                var destinations = ChosenDestinations();
                var plans = new List<(InstallTarget, NexusCollectionPlan)>();

                foreach (var destination in destinations)
                {
                    var scan = await Task.Run(() => ModuleScanner.ScanAll(destination.GameFolder));

                    var links = NexusModuleMatching.Link(
                        scan.Modules.Where(module => !module.IsOfficial),
                        recorded: ArchiveLinks,
                        confirmed: confirmedNexusIds);

                    plans.Add((destination, NexusCollectionPlanner.Plan(revision, links)));
                }

                collectionPlans = plans;
                bemCollection = collection;
                OnPropertyChanged(nameof(HasCollectionPlan));

                foreach (var (destination, plan) in plans)
                {
                    foreach (var item in plan.Items)
                        CollectionResults.Add(CollectionResultLine(item.Mod.ModName, destination, StepText(item), destinations.Count));
                }

                ShowCollectionBar(InfoBarSeverity.Informational,
                    Strings.Current.Format("Install.Collection.FileReadTitle", collection.Name),
                    DescribePlans(revision, null) + " " + Strings.Current.Format(
                        "Install.Collection.FileApplies", collection.Mods.Count, collection.Settings.Count));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Reading a BEM collection file failed");
                ShowCollectionBar(InfoBarSeverity.Error, Strings.Current["Install.Collection.Title"],
                    Strings.Current.Format("Install.Collection.Failed", ex.Message));
            }
            finally
            {
                IsBusy = false;
                RefreshCollectionReadiness();
            }
        }

        // After the mods are on disk: the curator's load order and settings go in, per ticked instance,
        // and the line returned is added to the bar. Anything the settings step overwrote was copied to
        // the settings archive first, which is what the Undo list restores from.
        private async Task<string> ApplyBemCollectionAsync(BemCollection collection)
        {
            var lines = new List<string>();

            foreach (var destination in ChosenDestinations())
            {
                var result = await Task.Run(() =>
                {
                    var scan = ModuleScanner.ScanAll(destination.GameFolder);
                    var installed = scan.Modules.Select(module => module.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var launcher = new LauncherDataStore(
                        LauncherDataStore.GetDefaultPath(destination.DataRoot),
                        new LoadOrderBackupStore(LoadOrderBackupStore.GetDefaultRoot(destination.DataRoot)));

                    return BemCollectionApplier.Apply(
                        collection, installed, launcher, destination.DataRoot.Configs,
                        new ModSettingsArchive(ModSettingsArchive.GetDefaultRoot(destination.DataRoot)));
                });

                var line = Strings.Current.Format("Install.Collection.Applied",
                    result.OrderApplied.Count, result.SettingsWritten.Count, result.BackedUp.Count);

                if (nexusBundleFolder is { } bundle && Directory.Exists(destination.GameFolder))
                {
                    var overlay = await Task.Run(() => NexusBundleOverlay.Apply(bundle, destination.GameFolder));
                    LoggingService.Log($"Collection bundle: {overlay.Written.Count} files written, {overlay.BackedUp.Count} replaced files kept, {overlay.Skipped.Count} skipped in {destination.DisplayName}");
                    line += $" {overlay.Written.Count} bundled files written ({overlay.BackedUp.Count} replaced kept as .bem.bak).";
                }

                if (result.NotInstalled.Count > 0)
                    line += " " + Strings.Current.Format("Install.Collection.AppliedMissing", string.Join(", ", result.NotInstalled));

                if (result.SettingsSkipped.Count > 0)
                    line += " " + Strings.Current.Format("Install.Collection.AppliedSkipped", result.SettingsSkipped.Count);

                lines.Add(line);
            }

            return string.Join(" ", lines);
        }

        [RelayCommand]
        private async Task BrowseCollectionFileAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(BemCollection.FileExtension);

            if (!TryInitializeCollectionPicker(picker))
            {
                ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                    Strings.Current["Install.Collection.PickerUnavailable"]);
                return;
            }

            if (await picker.PickSingleFileAsync() is { } file)
                CollectionLinkText = file.Path;
        }

        [RelayCommand]
        private async Task CreateCollectionAsync()
        {
            if (IsBusy || IsCollectionRunning)
                return;

            // A scripted run names the file with --collection-out, because the save dialog is a window a
            // drive on the hidden desktop cannot type into.
            var scripted = CollectionOutFromCommandLine(Environment.GetCommandLineArgs());
            string? savedPath = scripted;
            string? savedName = scripted is null ? null : Path.GetFileName(scripted);

            if (scripted is null)
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Strings.Current["Install.Collection.DefaultName"]
                };
                picker.FileTypeChoices.Add(Strings.Current["Install.Collection.FileType"], [BemCollection.FileExtension]);

                if (!TryInitializeCollectionPicker(picker))
                {
                    ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                        Strings.Current["Install.Collection.PickerUnavailable"]);
                    return;
                }

                if (await picker.PickSaveFileAsync() is not { } file)
                    return;

                savedPath = file.Path;
                savedName = file.Name;
            }

            var filePath = savedPath!;
            var fileName = savedName!;

            IsBusy = true;

            try
            {
                ResolveActiveInstanceInstallTarget();
                RefreshInstallDestinations();

                var destination = ChosenDestinations().FirstOrDefault(target => Directory.Exists(target.GameFolder));

                if (destination is null)
                {
                    ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                        Strings.Current["Install.ButrStack.NoDestination"]);
                    return;
                }

                var collection = await Task.Run(() =>
                {
                    var entries = new LauncherDataStore(LauncherDataStore.GetDefaultPath(destination.DataRoot)).Read();
                    var scan = ModuleScanner.ScanAll(destination.GameFolder);

                    var links = NexusModuleMatching.Link(
                        scan.Modules.Where(module => !module.IsOfficial),
                        recorded: ArchiveLinks,
                        confirmed: confirmedNexusIds);

                    var created = BemCollectionBuilder.Create(
                        Path.GetFileNameWithoutExtension(fileName), entries, links, [], destination.DataRoot.Configs);

                    if (string.Equals(fileName, VortexCollectionJson.FileName, StringComparison.OrdinalIgnoreCase))
                        VortexCollectionJson.Write(Path.GetDirectoryName(filePath)!, created);
                    else
                        BemCollectionFile.Write(filePath, created);
                    return created;
                });

                var unlinked = collection.Mods.Count(mod => mod.NexusModId is null || mod.NexusFileId is null);
                var message = Strings.Current.Format("Install.Collection.Created",
                    fileName, collection.Mods.Count, collection.Settings.Count);

                if (unlinked > 0)
                    message += " " + Strings.Current.Format("Install.Collection.CreatedUnlinked", unlinked);

                if (collection.ExcludedForSecrets.Count > 0)
                    message += " " + Strings.Current.Format("Install.Collection.CreatedExcluded", collection.ExcludedForSecrets.Count);

                ShowCollectionBar(InfoBarSeverity.Success, Strings.Current["Install.Collection.Title"], message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Writing a BEM collection file failed");
                ShowCollectionBar(InfoBarSeverity.Error, Strings.Current["Install.Collection.Title"],
                    Strings.Current.Format("Install.Collection.CreateFailed", fileName, ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string? CollectionOutFromCommandLine(string[] args)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, "--collection-out", StringComparison.OrdinalIgnoreCase));

            if (index >= 0 && index + 1 < args.Length && Path.IsPathRooted(args[index + 1]))
                return args[index + 1];

            // Run-Scenario starts the app itself, so the same path may arrive in the environment.
            return Environment.GetEnvironmentVariable("BEM_COLLECTION_OUT") is { Length: > 0 } fromEnvironment
                && Path.IsPathRooted(fromEnvironment) ? fromEnvironment : null;
        }

        private static bool TryInitializeCollectionPicker(object picker)
        {
            if (App.AppWindow is not { } window)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            switch (picker)
            {
                case FileOpenPicker open:
                    InitializeWithWindow.Initialize(open, hwnd);
                    return true;

                case FileSavePicker save:
                    InitializeWithWindow.Initialize(save, hwnd);
                    return true;

                default:
                    return false;
            }
        }
    }
}
