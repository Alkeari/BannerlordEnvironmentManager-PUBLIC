using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class SubModuleValidatorViewModel : BaseViewModel
    {
        private sealed class ValidatorSettings
        {
            public string ModulesPath { get; set; } = string.Empty;
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(ValidatorSettings))]
        private sealed partial class ValidatorSettingsJsonContext : JsonSerializerContext
        {
        }

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Environment Manager",
            "validator_settings.json");

        private const string ModulesPathSettingKey = "ModulesPathForValidator";

        private Dictionary<ModuleId, ModuleManifest> installedById = new();

        [ObservableProperty]
        public partial string ModulesPath { get; set; }

        [ObservableProperty]
#if DEV_BEM
        [NotifyCanExecuteChangedFor(nameof(UpdateAllVersionsCommand))]
        [NotifyCanExecuteChangedFor(nameof(UpdateSelectedModuleVersionsCommand))]
#endif
        [NotifyCanExecuteChangedFor(nameof(RestoreSelectedModuleLostElementsCommand))]
        [NotifyCanExecuteChangedFor(nameof(WidenWildcardVersionsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RestoreBackupsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RestoreLostElementsCommand))]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial string GameVersion { get; set; }

        public ObservableCollection<ModuleManifest> Modules { get; } = [];

        public ObservableCollection<ModuleDependency> SelectedModuleDependencies { get; } = [];

        [ObservableProperty]
#if DEV_BEM
        [NotifyCanExecuteChangedFor(nameof(UpdateAllVersionsCommand))]
        [NotifyCanExecuteChangedFor(nameof(UpdateSelectedModuleVersionsCommand))]
#endif
        [NotifyCanExecuteChangedFor(nameof(RestoreSelectedModuleLostElementsCommand))]
        public partial ModuleManifest? SelectedModule { get; set; }

        public SubModuleValidatorViewModel()
        {
            Title = "SubModule Validator";
            ModulesPath = string.Empty;
            StatusMessage = Strings.Current["Validator.Status.SelectFolder"];
            GameVersion = string.Empty;
            LoadCachedModulesPath();
            PropertyChanged += OnPropertyChangedSaveSettings;
        }

        private void LoadCachedModulesPath()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, ValidatorSettingsJsonContext.Default.ValidatorSettings);
                    if (settings != null)
                    {
                        ModulesPath = settings.ModulesPath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load cached modules path: {ex.Message}");
            }
        }

        private void SaveModulesPath()
        {
            try
            {
                var settings = new ValidatorSettings
                {
                    ModulesPath = ModulesPath
                };

                var json = JsonSerializer.Serialize(settings, ValidatorSettingsJsonContext.Default.ValidatorSettings);
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save modules path: {ex.Message}");
            }
        }

        private void OnPropertyChangedSaveSettings(object? sender, PropertyChangedEventArgs e)
        {
            // A path pushed across by the version being played is where this session's modules are,
            // not a setting. Persisting it would make one version's Modules folder the machine-wide
            // answer, which is how this page came to bulk-edit SubModule.xml in an install the user
            // was not looking at.
            if (e.PropertyName == nameof(ModulesPath) && !followingInstance)
            {
                SaveModulesPath();
            }
        }

        private bool followingInstance;

        // Point the page at the version being played. Called on every navigation, so opening the
        // validator with one version selected can never show, or rewrite, another version's modules.
        //
        // The loaded list is dropped when the folder actually changes rather than rescanned: "Update
        // all versions" and "Widen wildcard versions" write to every module in that list, and a list
        // read from the folder that was here a moment ago is the one thing they must never be given.
        // An empty list with a line saying to press Load is the honest state.
        public void FollowInstance(string modulesFolder)
        {
            if (string.IsNullOrWhiteSpace(modulesFolder)
                || ModulesPath.Equals(modulesFolder, StringComparison.OrdinalIgnoreCase))
                return;

            followingInstance = true;

            try
            {
                ModulesPath = modulesFolder;
            }
            finally
            {
                followingInstance = false;
            }

            installedById = new Dictionary<ModuleId, ModuleManifest>();
            Modules.Clear();
            SelectedModule = null;
            GameVersion = string.Empty;
            StatusMessage = Strings.Current.Format("Validator.Status.FollowingInstance", modulesFolder);
            NotifyVersionCommands();
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
        private async Task BrowseModulesFolderAsync()
        {
            var picker = new FolderPicker();
            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Validator.Status.PickerUnavailable"];
                return;
            }

            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();

            if (folder == null)
            {
                StatusMessage = Strings.Current["Validator.Status.FolderCanceled"];
                return;
            }

            ModulesPath = folder.Path;
            SaveModulesPath();
            StatusMessage = Strings.Current.Format("Validator.Status.FolderSelected", folder.Path);
        }

        [RelayCommand]
        private void LoadModules()
        {
            // Directory.GetParent throws on an empty string rather than returning null, and the
            // text box is a TwoWay binding with no CanExecute gate, so an empty ModulesPath has to
            // be handled before it ever reaches GetParent.
            if (string.IsNullOrWhiteSpace(ModulesPath))
            {
                StatusMessage = Strings.Current["Validator.Status.SelectFolder"];
                return;
            }

            IsBusy = true;

            try
            {
                // ModulesPath is the Modules/ folder itself, not the game install root that
                // ScanAll expects, so the Workshop sibling has to be found from its parent. A
                // trailing separator (a pasted or hand-edited path) makes GetParent return the
                // same folder instead of its parent, so it is trimmed first.
                var installPath = Directory.GetParent(Path.TrimEndingDirectorySeparator(ModulesPath))?.FullName
                    ?? string.Empty;
                var scan = ModuleScanner.ScanAll(installPath);

                if (scan.Failed)
                {
                    installedById = new Dictionary<ModuleId, ModuleManifest>();
                    Modules.Clear();
                    SelectedModule = null;
                    StatusMessage = Strings.Current.Format("Validator.Status.ScanFailed", scan.Error);
                    NotifyVersionCommands();
                    return;
                }

                installedById = new Dictionary<ModuleId, ModuleManifest>();
                foreach (var manifest in scan.Modules)
                    installedById.TryAdd(manifest.Id, manifest);

                Modules.Clear();
                foreach (var manifest in scan.Modules)
                    Modules.Add(manifest);

                GameVersion = GameVersionReader.Read(installPath).ToString();

                var workshopCount = scan.Modules.Count(m => m.Source == ModuleSource.Workshop);
                StatusMessage = Strings.Current.Plural(
                    "Validator.Status.Loaded", Modules.Count, workshopCount, scan.Unreadable.Count);
                NotifyVersionCommands();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void NotifyVersionCommands()
        {
#if DEV_BEM
            UpdateAllVersionsCommand.NotifyCanExecuteChanged();
            UpdateSelectedModuleVersionsCommand.NotifyCanExecuteChanged();
#endif
            WidenWildcardVersionsCommand.NotifyCanExecuteChanged();
            RestoreBackupsCommand.NotifyCanExecuteChanged();
        }

        partial void OnSelectedModuleChanged(ModuleManifest? value)
        {
            SelectedModuleDependencies.Clear();

            if (value is null)
                return;

            foreach (var dependency in value.Dependencies)
                SelectedModuleDependencies.Add(dependency);

            var issues = ModuleDependencyResolver.DescribeIssues(value, installedById);

            StatusMessage = issues.Count == 0
                ? Strings.Current.Format("Validator.Status.NoIssues", value.Name)
                : string.Join("  ", issues);
        }

        // Rewriting another author's declared requirement silences the launcher's compatibility
        // check without changing whether the mod actually works, so every entry point confirms.
        private static async Task<bool> ConfirmVersionRewriteAsync(string title, string whatChanges)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = Strings.Current.Format("Validator.RewriteDialog.Content", whatChanges),
                PrimaryButtonText = Strings.Current["Validator.RewriteDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Validator.RewriteDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private static string NothingToRewrite => Strings.Current["Validator.NothingToRewrite"];

        // Each declaration is rewritten to the version its target module declares in its own
        // SubModule.xml, so a DLC keeps its own version rather than the base game's.
        private Dictionary<ModuleId, ModuleVersion> InstalledVersions() =>
            installedById.ToDictionary(pair => pair.Key, pair => pair.Value.Version);

        // Steam owns a Workshop mod's SubModule.xml and restores the author's copy the next time the
        // mod updates, so an edit made here is undone silently. That is worth saying, not refusing.
        private static string DescribeWorkshopCost(int workshopCount) => workshopCount == 0
            ? string.Empty
            : Strings.Current.Plural("Validator.WorkshopCostNote", workshopCount);

#if DEV_BEM
        // The outer ring, both of them. Rewriting the version another author declared in their own
        // SubModule.xml silences the launcher's compatibility check without changing whether the mod
        // actually works, so it is the author's judgement about a declaration rather than a repair a
        // player can make safely. Widen, Restore Backups and Restore Lost Elements stay in every build:
        // widening keeps the declaration true and both restores put a file back the way it was.
        [RelayCommand(CanExecute = nameof(CanUpdateVersions))]
        private async Task UpdateAllVersionsAsync()
        {
            // Official TaleWorlds manifests are excluded because rewriting them is never what the
            // user wants. Workshop mods are in: they are installed mods like any other, and the one
            // thing that makes them different is said before anything is written.
            var manifests = Modules.Where(m => !m.IsOfficial).ToList();
            var workshopCount = manifests.Count(m => m.Source == ModuleSource.Workshop);

            if (!await ConfirmVersionRewriteAsync(
                    Strings.Current["Validator.UpdateAllDialog.Title"],
                    Strings.Current.Plural(
                        "Validator.UpdateAllDialog.WhatChanges", manifests.Count, DescribeWorkshopCost(workshopCount))))
            {
                return;
            }

            IsBusy = true;

            try
            {
                var installedVersions = InstalledVersions();

                var counts = await Task.Run(() => manifests
                    .Select(manifest => SubModuleXmlWriter.SetDependencyVersions(manifest.ManifestPath, installedVersions))
                    .Where(count => count > 0)
                    .ToList());

                LoadModules();
                StatusMessage = counts.Count == 0
                    ? Strings.Current.Plural("Validator.UpdateAllDialog.NoChanges", manifests.Count, NothingToRewrite)
                    : Strings.Current.Plural("Validator.UpdateAllDialog.RewroteDeclarations", counts.Sum())
                        + " " + Strings.Current.Plural("Validator.UpdateAllDialog.RewroteFiles", manifests.Count, counts.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.UpdateAllDialog.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanUpdateSelectedModule))]
        private async Task UpdateSelectedModuleVersionsAsync()
        {
            if (SelectedModule == null)
                return;

            var selected = SelectedModule;

            var whatChanges = Strings.Current.Format("Validator.UpdateModuleDialog.WhatChanges", selected.Name);

            if (selected.Source == ModuleSource.Workshop)
            {
                whatChanges += Strings.Current["Validator.UpdateModuleDialog.WorkshopNote"];
            }

            if (!await ConfirmVersionRewriteAsync(Strings.Current["Validator.UpdateModuleDialog.Title"], whatChanges))
            {
                return;
            }

            IsBusy = true;

            try
            {
                var installedVersions = InstalledVersions();

                var changed = await Task.Run(() => SubModuleXmlWriter.SetDependencyVersions(selected.ManifestPath, installedVersions));

                LoadModules();
                StatusMessage = changed == 0
                    ? Strings.Current.Format("Validator.UpdateModuleDialog.NoChanges", selected.Name, NothingToRewrite)
                    : Strings.Current.Plural("Validator.UpdateModuleDialog.Rewrote", changed, selected.Name);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.UpdateModuleDialog.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
#endif

        [RelayCommand(CanExecute = nameof(CanWidenWildcardVersions))]
        private async Task WidenWildcardVersionsAsync()
        {
            var manifests = Modules.ToList();
            var workshopCount = manifests.Count(m => m.Source == ModuleSource.Workshop);

            if (!await ConfirmVersionRewriteAsync(
                    Strings.Current["Validator.WidenDialog.Title"],
                    Strings.Current.Plural(
                        "Validator.WidenDialog.WhatChanges", manifests.Count, DescribeWorkshopCost(workshopCount))))
            {
                return;
            }

            IsBusy = true;

            try
            {
                var installedVersions = InstalledVersions();

                var counts = await Task.Run(() => manifests
                    .Select(manifest => SubModuleXmlWriter.WidenDependencyVersions(manifest.ManifestPath, installedVersions))
                    .Where(count => count > 0)
                    .ToList());

                LoadModules();
                StatusMessage = counts.Count == 0
                    ? Strings.Current["Validator.WidenDialog.NoChanges"]
                    : Strings.Current.Plural("Validator.WidenDialog.WidenedDeclarations", counts.Sum())
                        + " " + Strings.Current.Plural("Validator.WidenDialog.AcrossModules", counts.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.WidenDialog.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private const int MaximumNamesInDialog = 12;

        [RelayCommand(CanExecute = nameof(CanRestoreBackups))]
        private async Task RestoreBackupsAsync()
        {
            // The scan awaits before the dialog is shown, so the button has to be disabled up front:
            // a second click during the scan would reach ShowAsync while the first dialog is open,
            // and WinUI allows only one at a time.
            IsBusy = true;

            try
            {
                var loaded = Modules.ToList();
                var backed = await Task.Run(() => loaded.Where(m => AtomicXmlFile.HasBackup(m.ManifestPath)).ToList());

                if (backed.Count == 0)
                {
                    StatusMessage = Strings.Current["Validator.RestoreBackups.None"];
                    return;
                }

                var names = string.Join("\n", backed.Take(MaximumNamesInDialog).Select(m => m.Name));

                if (backed.Count > MaximumNamesInDialog)
                    names += Strings.Current.Plural("Validator.RestoreBackups.AndMore", backed.Count - MaximumNamesInDialog);

                var dialog = new ContentDialog
                {
                    Title = Strings.Current["Validator.RestoreBackupsDialog.Title"],
                    Content = Strings.Current.Plural("Validator.RestoreBackupsDialog.Content", backed.Count, names),
                    PrimaryButtonText = Strings.Current["Validator.RestoreBackupsDialog.PrimaryButton"],
                    CloseButtonText = Strings.Current["Validator.RestoreBackupsDialog.CloseButton"],
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
                };

                if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                    return;

                var paths = backed.Select(m => m.ManifestPath).ToList();
                var result = await Task.Run(() => AtomicXmlFile.RestoreAll(paths));

                LoadModules();
                StatusMessage = DescribeRestore(result, backed.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.RestoreBackups.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Restoring the whole backup would also undo the version rewrites the user asked for, so this
        // puts back only what the live manifest no longer has and leaves everything else as it is.
        [RelayCommand(CanExecute = nameof(CanRestoreBackups))]
        private async Task RestoreLostElementsAsync()
        {
            IsBusy = true;

            try
            {
                var loaded = Modules.ToList();
                var plans = await Task.Run(() => ManifestElementRepair.PlanAll(loaded.Select(m => m.ManifestPath)));

                if (plans.Count == 0)
                {
                    StatusMessage = Strings.Current["Validator.RestoreLostElements.None"];
                    return;
                }

                var byPath = loaded.ToDictionary(m => m.ManifestPath, m => m.Name, StringComparer.OrdinalIgnoreCase);
                var lines = plans
                    .Take(MaximumNamesInDialog)
                    .Select(plan => $"{byPath.GetValueOrDefault(plan.ManifestPath, Path.GetFileName(plan.ManifestPath))}: {string.Join(", ", plan.Lost.Select(element => element.Description))}")
                    .ToList();

                if (plans.Count > MaximumNamesInDialog)
                    lines.Add(Strings.Current.Plural("Validator.RestoreLostElementsDialog.AndMore", plans.Count - MaximumNamesInDialog));

                var elements = plans.Sum(plan => plan.Lost.Count);

                var dialog = new ContentDialog
                {
                    Title = Strings.Current["Validator.RestoreLostElementsDialog.Title"],
                    Content = Strings.Current.Plural("Validator.RestoreLostElementsDialog.ElementCount", elements)
                        + " " + Strings.Current.Plural(
                            "Validator.RestoreLostElementsDialog.Content", plans.Count, string.Join("\n", lines)),
                    PrimaryButtonText = Strings.Current["Validator.RestoreLostElementsDialog.PrimaryButton"],
                    CloseButtonText = Strings.Current["Validator.RestoreLostElementsDialog.CloseButton"],
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
                };

                if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                    return;

                var outcome = await Task.Run(() => ManifestElementRepair.RepairAll(plans));

                LoadModules();
                StatusMessage = outcome.Failed.Count == 0
                    ? Strings.Current.Plural("Validator.RestoreLostElementsDialog.PutElements", outcome.Elements)
                        + " " + Strings.Current.Plural("Validator.RestoreLostElementsDialog.IntoManifests", outcome.Modules)
                    : Strings.Current.Plural("Validator.RestoreLostElementsDialog.PutElements", outcome.Elements)
                        + " " + Strings.Current.Plural(
                            "Validator.RestoreLostElementsDialog.IntoManifestsOf", plans.Count, outcome.Modules)
                        + " " + Strings.Current.Plural(
                            "Validator.RestoreLostElementsDialog.FailedNote", outcome.Failed.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.RestoreLostElements.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Whether a missing element was lost or dropped on purpose is the author's business, not
        // BEM's, so one module can be repaired without touching the rest.
        [RelayCommand(CanExecute = nameof(CanUpdateSelectedModule))]
        private async Task RestoreSelectedModuleLostElementsAsync()
        {
            if (SelectedModule is not { } selected)
                return;

            IsBusy = true;

            try
            {
                var plan = await Task.Run(() => ManifestElementRepair.Plan(selected.ManifestPath));

                if (plan.Problem is { } problem)
                {
                    StatusMessage = Strings.Current.Format("Validator.RestoreModuleLostElements.ReadError", selected.Name, problem);
                    return;
                }

                if (!plan.HasWork)
                {
                    StatusMessage = Strings.Current.Format("Validator.RestoreModuleLostElements.NoWork", selected.Name);
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = Strings.Current["Validator.RestoreLostElementsDialog.Title"],
                    Content = Strings.Current.Format(
                        "Validator.RestoreModuleLostElementsDialog.Content",
                        selected.Name,
                        string.Join("\n", plan.Lost.Select(element => element.Description))),
                    PrimaryButtonText = Strings.Current["Validator.RestoreLostElementsDialog.PrimaryButton"],
                    CloseButtonText = Strings.Current["Validator.RestoreLostElementsDialog.CloseButton"],
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
                };

                if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                    return;

                var restored = await Task.Run(() => ManifestElementRepair.Repair(plan));

                LoadModules();
                StatusMessage = Strings.Current.Plural("Validator.RestoreModuleLostElements.Put", restored, selected.Name);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Validator.RestoreLostElements.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string DescribeRestore(BackupRestoreResult result, int attempted)
        {
            if (result.Restored == attempted)
                return Strings.Current.Plural("Validator.RestoreBackups.AllRestored", result.Restored);

            var message = Strings.Current.Plural("Validator.RestoreBackups.PartialRestored", attempted, result.Restored);

            if (result.Failed.Count > 0)
                message += Strings.Current.Plural("Validator.RestoreBackups.FailedNote", result.Failed.Count);

            if (result.Missing > 0)
                message += Strings.Current.Plural("Validator.RestoreBackups.MissingNote", result.Missing);

            return message;
        }

        private bool CanExecuteCommands() => !IsBusy;

        private bool CanRestoreBackups() => !IsBusy && Modules.Count > 0;

        private bool CanWidenWildcardVersions() => !IsBusy && Modules.Count > 0;

#if DEV_BEM
        private bool CanUpdateVersions() => !IsBusy && Modules.Count > 0;
#endif

        private bool CanUpdateSelectedModule() => !IsBusy && SelectedModule is not null;

        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;
            if (window is null)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            if (picker is FolderPicker folderPicker)
            {
                InitializeWithWindow.Initialize(folderPicker, hwnd);
                return true;
            }

            return false;
        }
    }
}
