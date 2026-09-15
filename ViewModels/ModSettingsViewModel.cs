using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Settings;
using BannerlordEnvironmentManager.Services;
using Microsoft.VisualBasic.FileIO;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BannerlordEnvironmentManager.ViewModels
{
    // An installed module the user can point a settings folder at. The label carries both names
    // because a module id and its display name are routinely nothing like one another.
    public sealed record ModuleChoice(ModuleId Id, string Name)
    {
        public string Label => string.Equals(Id.Value, Name, StringComparison.Ordinal)
            ? Id.Value
            : $"{Name} ({Id})";

        public override string ToString() => Label;
    }

    public partial class SettingsCandidateRow : ObservableObject
    {
        private readonly Action<SettingsCandidateRow> assign;

        public SettingsCandidateRow(
            SettingsFolderReview review,
            IReadOnlyList<ModuleChoice> modules,
            Action<SettingsCandidateRow> assign)
        {
            ArgumentNullException.ThrowIfNull(assign);

            Review = review;
            Modules = modules;
            this.assign = assign;
            Suggestions = review.Suggestions.Count == 0
                ? Strings.Current["ModSettings.Candidate.NoSuggestions"]
                : Strings.Current.Format(
                    "ModSettings.Candidate.Suggestions",
                    string.Join(", ", review.Suggestions.Select(suggestion => suggestion.Name)));
        }

        public SettingsFolderReview Review { get; }

        public string Name => Review.Folder.Name;

        public string RelativePath => Review.Folder.RelativePath;

        public string Suggestions { get; }

        // Every installed module, with the ones this folder resembles first, because a folder BEM
        // could not match is exactly the one whose module the list will not have guessed.
        public IReadOnlyList<ModuleChoice> Modules { get; }

        public string Details => Strings.Current.Plural(
            "ModSettings.Candidate.Details",
            Review.Folder.FileCount,
            ModSettingsViewModel.DescribeSize(Review.Folder.SizeBytes),
            Review.Folder.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd"));

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AssignCommand))]
        public partial ModuleChoice? ChosenModule { get; set; }

        public bool CanAssign => ChosenModule is not null;

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud for every row.
        public override string ToString() => Name;

        [RelayCommand(CanExecute = nameof(CanAssign))]
        private void Assign() => assign(this);
    }

    // Five different claims, never blurred into one: the user stating it outright, content evidence
    // BEM read inside the folder, the folder naming the module, an attribution remembered from an
    // earlier scan whose evidence is no longer there, and BEM guessing from a substring, an acronym or
    // a file name. A wrong partial match is how a genuinely dead folder never reaches the candidate
    // list, so it is shown and named.
    public sealed partial class MatchedSettingsRow : ObservableObject
    {
        private readonly Action<MatchedSettingsRow> forget;

        public MatchedSettingsRow(SettingsFolderReview review, Action<MatchedSettingsRow> forget)
        {
            ArgumentNullException.ThrowIfNull(forget);

            Review = review;
            this.forget = forget;
        }

        public SettingsFolderReview Review { get; }

        public string RelativePath => Review.Folder.RelativePath;

        public bool IsExact => Review.Kind is SettingsMatchKind.Content or SettingsMatchKind.Exact;

        // Only a claim BEM is holding on to can be dropped. Everything else is re-derived from the
        // folder on every scan, so there would be nothing for a Forget button to do.
        public bool IsRemembered => Review.Kind is SettingsMatchKind.Stated or SettingsMatchKind.Remembered;

        public string Detail => Review.Kind switch
        {
            SettingsMatchKind.Stated => Strings.Current.Format("ModSettings.Matched.Stated", Module, Recorded),
            SettingsMatchKind.Content => Strings.Current.Format("ModSettings.Matched.Content", Module, Review.Evidence),
            SettingsMatchKind.Exact => Strings.Current.Format("ModSettings.Matched.Exact", Module),
            SettingsMatchKind.Remembered =>
                Strings.Current.Format("ModSettings.Matched.Remembered", Module, Recorded, Review.Evidence),
            _ => Strings.Current.Format("ModSettings.Matched.Partial", Module, Review.Evidence)
        };

        private string Module =>
            string.Equals(Review.MatchedModuleName, Review.MatchedModuleId.Value, StringComparison.Ordinal)
                ? Review.MatchedModuleId.Value
                : $"{Review.MatchedModuleName} ({Review.MatchedModuleId})";

        private string Recorded =>
            Review.RememberedUtc is { } recorded
                ? recorded.ToLocalTime().ToString("yyyy-MM-dd")
                : Strings.Current["ModSettings.Matched.EarlierScan"];

        public override string ToString() => RelativePath;

        [RelayCommand]
        private void Forget() => forget(this);
    }

    // A settings folder with no file anywhere under it. It is not an unidentified mystery, it is
    // empty, and the only thing left to do with it is get rid of it.
    public sealed partial class EmptySettingsRow : ObservableObject
    {
        private readonly Func<EmptySettingsRow, Task> remove;

        public EmptySettingsRow(
            ModSettingsFolder folder,
            SettingsAttribution? remembered,
            Func<EmptySettingsRow, Task> remove)
        {
            ArgumentNullException.ThrowIfNull(remove);

            Folder = folder;
            this.remove = remove;
            Note = remembered is null
                ? string.Empty
                : Strings.Current.Format(
                    "ModSettings.Empty.Note", remembered.ModuleName, remembered.RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd"));
        }

        public ModSettingsFolder Folder { get; }

        public string RelativePath => Folder.RelativePath;

        public string Details =>
            Strings.Current.Format("ModSettings.Empty.Details", Folder.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd"));

        public string Note { get; }

        public string FullPath => Folder.FullPath;

        public override string ToString() => RelativePath;

        [RelayCommand]
        private Task RemoveAsync() => remove(this);
    }

    // The row carries its own two commands rather than reaching up to the page for them. A DataTemplate
    // has its own XAML namescope, so ElementName cannot see the page it sits on and the binding
    // silently resolves to nothing, which is how both of these buttons shipped inert.
    public partial class ArchivedSettingsRow : ObservableObject
    {
        private readonly Action<ArchivedSettingsRow> restore;

        private readonly Func<ArchivedSettingsRow, Task> discard;

        public ArchivedSettingsRow(
            ArchivedSettings entry,
            Action<ArchivedSettingsRow> restore,
            Func<ArchivedSettingsRow, Task> discard)
        {
            ArgumentNullException.ThrowIfNull(restore);
            ArgumentNullException.ThrowIfNull(discard);

            Entry = entry;
            this.restore = restore;
            this.discard = discard;
        }

        public ArchivedSettings Entry { get; }

        public string Name => Entry.Name;

        public string Details => Strings.Current.Plural(
            "ModSettings.Archived.Details",
            Entry.FileCount,
            Entry.ArchivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ModSettingsViewModel.DescribeSize(Entry.SizeBytes),
            Describe(Entry.Reason));

        public string OriginPath => Entry.OriginPath;

        public override string ToString() => Name;

        [RelayCommand]
        private void Restore() => restore(this);

        [RelayCommand]
        private Task DiscardAsync() => discard(this);

        private static string Describe(ArchiveReason reason) => reason switch
        {
            ArchiveReason.ReplacedByImport => Strings.Current["ModSettings.ArchiveReason.Import"],
            ArchiveReason.ReplacedByRestore => Strings.Current["ModSettings.ArchiveReason.Restore"],
            _ => Strings.Current["ModSettings.ArchiveReason.Orphan"]
        };
    }

    public partial class BundleFileRow : ObservableObject
    {
        public BundleFileRow(BundleFileCandidate file)
        {
            RelativePath = file.RelativePath;
            Findings = string.Join(", ", file.Findings.Select(finding =>
                Strings.Current.Plural(
                    "ModSettings.Finding.Description",
                    finding.ValueLength,
                    finding.ValuePath,
                    finding.Rule,
                    finding.Preview)));
        }

        public string RelativePath { get; }

        public string Findings { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public override string ToString() => RelativePath;
    }

    public partial class ImportEntryRow : ObservableObject
    {
        public ImportEntryRow(ImportEntry entry)
        {
            Entry = entry;
            Findings = entry.IsFlagged
                ? string.Join(", ", entry.Findings.Select(finding =>
                    Strings.Current.Format(
                        "ModSettings.Finding.DescriptionNoLength", finding.ValuePath, finding.Rule, finding.Preview)))
                : string.Empty;
        }

        public ImportEntry Entry { get; }

        public string RelativePath => Entry.RelativePath;

        public bool IsFlagged => Entry.IsFlagged;

        public string Findings { get; }

        public string Details => Entry.Overwrites
            ? Strings.Current.Format("ModSettings.Import.Overwrites", ModSettingsViewModel.DescribeSize(Entry.ExistingSizeBytes))
            : Strings.Current["ModSettings.Import.NewFile"];

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public override string ToString() => RelativePath;
    }

    public partial class ModSettingsViewModel : BaseViewModel
    {
        // Built on each read rather than held: what BEM remembers about a settings folder was learned
        // from one version's module set, and the version dropdown can change between reads.
        private static SettingsAttributionStore Attributions => new(
            SettingsAttributionStore.GetDefaultPath(ShellViewModels.Instance.Environment.ActiveDataRoot));

        private bool _attributionsReadable = true;

        private BundlePlan? _exportPlan;

        private ImportPreview? _importPreview;

        public ModSettingsViewModel()
        {
            Title = "Mod Settings";
            SettingsRoot = ModSettingsScanner.DefaultRoot;
            ArchiveRoot = ModSettingsArchive.GetDefaultRoot();
            Summary = Strings.Current["ModSettings.Summary.NotScanned"];
            StatusMessage = string.Empty;
            ExportSummary = string.Empty;
            ImportSummary = string.Empty;
        }

        public ObservableCollection<SettingsCandidateRow> Candidates { get; } = new();

        public ObservableCollection<MatchedSettingsRow> Matched { get; } = new();

        public ObservableCollection<EmptySettingsRow> EmptyFolders { get; } = new();

        public ObservableCollection<ArchivedSettingsRow> Archived { get; } = new();

        public ObservableCollection<BundleFileRow> ExportFlagged { get; } = new();

        public ObservableCollection<ImportEntryRow> ImportEntries { get; } = new();

        [ObservableProperty]
        public partial string SettingsRoot { get; set; }

        [ObservableProperty]
        public partial string ArchiveRoot { get; set; }

        [ObservableProperty]
        public partial string Summary { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial string ExportSummary { get; set; }

        [ObservableProperty]
        public partial string ImportSummary { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public string AttributionsPath => Attributions.FilePath;

        [RelayCommand]
        private void Refresh() => Rescan(string.Empty);

        // Every action on this page ends in a rescan, and the sentence saying what the action did is
        // the only feedback there is, so the rescan carries that sentence rather than clearing it.
        private void Rescan(string note)
        {
            IsBusy = true;

            // Re-read on every scan rather than fixed at construction: MCM writes into the user data of
            // the version that ran, so the folder to read is the one belonging to the version picked in
            // the Play dropdown, not always the machine default. The archive follows it, or the list of
            // what was set aside would name another version's mods beside this version's folders.
            var dataRoot = ShellViewModels.Instance.Environment.ActiveDataRoot;

            SettingsRoot = ModSettingsScanner.DefaultRootFor(dataRoot);
            ArchiveRoot = ModSettingsArchive.GetDefaultRoot(dataRoot);

            try
            {
                var folders = ModSettingsScanner.Scan(SettingsRoot);
                var modules = ScanModules();
                var remembered = ReadAttributions();

                Candidates.Clear();
                Matched.Clear();
                EmptyFolders.Clear();

                foreach (var folder in folders.Where(ModSettingsScanner.IsEmpty))
                    EmptyFolders.Add(new EmptySettingsRow(folder, Recall(remembered, folder), RemoveEmptyAsync));

                var holding = folders.Where(folder => !ModSettingsScanner.IsEmpty(folder)).ToList();

                if (!Directory.Exists(SettingsRoot))
                {
                    Summary = Strings.Current.Format("ModSettings.Summary.NoFolder", SettingsRoot);
                }
                else if (modules.Count == 0)
                {
                    Summary = Strings.Current.Plural(
                            "ModSettings.Summary.FoldersCount", folders.Count, DescribeSize(folders.Sum(folder => folder.SizeBytes)))
                        + Strings.Current["ModSettings.Summary.NoModulesNote"]
                        + Strings.Current.Plural("ModSettings.Summary.EmptyFoldersCount", EmptyFolders.Count)
                        + UnreadableAttributionsNote();
                }
                else
                {
                    var choices = modules
                        .Select(module => new ModuleChoice(module.Id, module.Name))
                        .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var reviews = SettingsAttributions.Apply(OrphanSettingsFinder.Review(holding, modules), remembered);

                    foreach (var review in OrphanSettingsFinder.Candidates(reviews))
                        Candidates.Add(new SettingsCandidateRow(review, ChoicesFor(review, choices), AssignModule));

                    foreach (var review in reviews
                                 .Where(review => review.Kind != SettingsMatchKind.None)
                                 .OrderBy(Rank)
                                 .ThenBy(review => review.Folder.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        Matched.Add(new MatchedSettingsRow(review, ForgetAttribution));
                    }

                    RememberDerived(reviews);

                    var content = Count(SettingsMatchKind.Content);
                    var exact = Count(SettingsMatchKind.Exact);
                    var recalled = Count(SettingsMatchKind.Remembered);
                    var stated = Count(SettingsMatchKind.Stated);
                    var partial = Count(SettingsMatchKind.Partial);

                    Summary = Strings.Current.Plural(
                            "ModSettings.Summary.FoldersCount", folders.Count, DescribeSize(folders.Sum(folder => folder.SizeBytes)))
                        + Strings.Current.Plural("ModSettings.Summary.Attributed", content)
                        + Strings.Current.Plural("ModSettings.Summary.NamesModule", exact)
                        + Strings.Current.Plural("ModSettings.Summary.Recalled", recalled)
                        + Strings.Current.Plural("ModSettings.Summary.Stated", stated)
                        + Strings.Current.Plural("ModSettings.Summary.Partial", partial)
                        + Strings.Current.Plural("ModSettings.Summary.NoneMatched", Candidates.Count)
                        + Strings.Current.Plural("ModSettings.Summary.EmptyFoldersCount", EmptyFolders.Count)
                        + UnreadableAttributionsNote();
                }

                RefreshArchive();
                StatusMessage = note;
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("ModSettings.Rescan.Failed", ex.Message);
                LoggingService.LogException(ex, "Failed to scan the MCM settings folder");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private int Count(SettingsMatchKind kind) => Matched.Count(row => row.Review.Kind == kind);

        // A guess is listed first because it is the one worth checking, then what BEM can only
        // remember, and last the attributions nothing is going to change.
        private static int Rank(SettingsFolderReview review) => review.Kind switch
        {
            SettingsMatchKind.Partial => 0,
            SettingsMatchKind.Remembered => 1,
            SettingsMatchKind.Content => 2,
            SettingsMatchKind.Stated => 3,
            _ => 4
        };

        private static IReadOnlyList<ModuleChoice> ChoicesFor(
            SettingsFolderReview review,
            IReadOnlyList<ModuleChoice> all)
        {
            if (review.Suggestions.Count == 0)
                return all;

            var suggested = review.Suggestions.Select(suggestion => suggestion.Id).ToHashSet();

            return [.. all.Where(choice => suggested.Contains(choice.Id)), .. all.Where(choice => !suggested.Contains(choice.Id))];
        }

        private static SettingsAttribution? Recall(
            IReadOnlyList<SettingsAttribution> remembered,
            ModSettingsFolder folder) =>
            remembered.FirstOrDefault(attribution =>
                string.Equals(attribution.RelativePath, folder.RelativePath, StringComparison.OrdinalIgnoreCase));

        // A store that will not parse is not the same as an empty one, so the scan says it could not
        // look, and nothing is written back over a file BEM could not read.
        private IReadOnlyList<SettingsAttribution> ReadAttributions()
        {
            _attributionsReadable = true;

            try
            {
                return Attributions.Read();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _attributionsReadable = false;
                LoggingService.LogException(ex, $"Failed to read the settings attributions at {Attributions.FilePath}");

                return [];
            }
        }

        private string UnreadableAttributionsNote() => _attributionsReadable
            ? string.Empty
            : Strings.Current.Format("ModSettings.Summary.UnreadableAttributions", Attributions.FilePath);

        private void RememberDerived(IReadOnlyList<SettingsFolderReview> reviews)
        {
            if (!_attributionsReadable)
                return;

            var now = DateTimeOffset.UtcNow;

            var derived = reviews
                .Select(review => SettingsAttributions.Derived(review, now))
                .OfType<SettingsAttribution>()
                .ToList();

            if (derived.Count == 0)
                return;

            try
            {
                Attributions.Remember(derived);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Failed to write the settings attributions to {Attributions.FilePath}");
            }
        }

        private void AssignModule(SettingsCandidateRow row)
        {
            if (row.ChosenModule is not { } chosen)
            {
                StatusMessage = Strings.Current.Format("ModSettings.Assign.PickModule", row.RelativePath);
                return;
            }

            try
            {
                Attributions.Remember(
                    SettingsAttributions.Stated(row.RelativePath, chosen.Id, chosen.Name, DateTimeOffset.UtcNow));

                Rescan(Strings.Current.Format("ModSettings.Assign.Success", row.RelativePath, chosen.Label));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("ModSettings.Assign.Failed", row.RelativePath, chosen.Label, ex.Message);
                LoggingService.LogException(ex, $"Failed to record an attribution for {row.RelativePath}");
            }
        }

        private void ForgetAttribution(MatchedSettingsRow row)
        {
            try
            {
                Rescan(Attributions.Forget(row.RelativePath)
                    ? Strings.Current.Format("ModSettings.Forget.Success", row.RelativePath)
                    : Strings.Current.Format("ModSettings.Forget.NothingRecorded", row.RelativePath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("ModSettings.Forget.Failed", row.RelativePath, ex.Message);
                LoggingService.LogException(ex, $"Failed to forget the attribution for {row.RelativePath}");
            }
        }

        // An empty folder has no settings in it to lose, so nothing is archived first. It still goes to
        // the Recycle Bin, because a folder BEM removed is a folder the user can put back.
        private async Task RemoveEmptyAsync(EmptySettingsRow row)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("ModSettings.RemoveEmpty.Title", row.RelativePath),
                Content = new TextBlock
                {
                    Text = Strings.Current.Format("ModSettings.RemoveEmpty.Body", row.FullPath)
                        + (row.Note.Length == 0 ? string.Empty : row.Note),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["ModSettings.RemoveEmpty.PrimaryButton"],
                CloseButtonText = Strings.Current["ModSettings.RemoveEmpty.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("ModSettings.RemoveEmpty.Kept", row.RelativePath);
                return;
            }

            try
            {
                RecycleFolder(row.FullPath);
                Rescan(Strings.Current.Format("ModSettings.RemoveEmpty.Moved", row.RelativePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to move the empty settings folder {row.RelativePath} to the Recycle Bin");
                Rescan(Strings.Current.Format("ModSettings.RemoveEmpty.Failed", row.RelativePath, ex.Message));
            }
        }

        [RelayCommand]
        private void ArchiveSelected()
        {
            var chosen = Candidates.Where(candidate => candidate.IsSelected).ToList();

            if (chosen.Count == 0)
            {
                StatusMessage = Strings.Current["ModSettings.Archive.NothingSelected"];
                return;
            }

            var archive = new ModSettingsArchive(ArchiveRoot);
            var archived = 0;
            long bytes = 0;
            var failed = new List<string>();

            foreach (var candidate in chosen)
            {
                try
                {
                    var entry = archive.Archive(candidate.Review.Folder);
                    archived++;
                    bytes += entry.SizeBytes;
                }
                catch (Exception ex)
                {
                    failed.Add($"{candidate.Name} ({ex.Message})");
                    LoggingService.LogException(ex, $"Failed to archive the settings folder {candidate.RelativePath}");
                }
            }

            Rescan(archived == 0
                ? Strings.Current.Format("ModSettings.Archive.AllFailed", string.Join("; ", failed))
                : Strings.Current.Plural("ModSettings.Archive.Success", archived, DescribeSize(bytes)) +
                    (failed.Count == 0
                        ? Strings.Current["ModSettings.Archive.RestoreHint"]
                        : Strings.Current.Format("ModSettings.Archive.FailedList", string.Join("; ", failed))));
        }

        [RelayCommand]
        private void Restore(ArchivedSettingsRow? row)
        {
            if (row is null)
                return;

            string outcome;

            try
            {
                outcome = new ModSettingsArchive(ArchiveRoot).Restore(row.Entry.Id) switch
                {
                    RestoreOutcome.Restored => Strings.Current.Format("ModSettings.Restore.Restored", row.Name, row.OriginPath),
                    RestoreOutcome.RestoredOverExisting =>
                        Strings.Current.Format("ModSettings.Restore.RestoredOverExisting", row.Name, row.OriginPath),
                    RestoreOutcome.MissingArchive => Strings.Current.Format("ModSettings.Restore.MissingArchive", row.Name),
                    _ => Strings.Current.Format("ModSettings.Restore.NotInIndex", row.Name)
                };
            }
            catch (Exception ex)
            {
                outcome = Strings.Current.Format("ModSettings.Restore.Failed", row.Name, ex.Message);
                LoggingService.LogException(ex, $"Failed to restore the settings folder {row.Name}");
            }

            Rescan(outcome);
        }

        // The archive is BEM's own safety net, so the copy it holds is user data like any other. It
        // goes to the Recycle Bin, and the index entry is dropped only once the folder has actually
        // gone, so the list never shows one fewer folder than is on disk.
        [RelayCommand]
        private async Task DiscardArchivedAsync(ArchivedSettingsRow? row)
        {
            if (row is null)
                return;

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("ModSettings.DiscardArchived.Title", row.Name),
                Content = new TextBlock
                {
                    Text = Strings.Current.Plural(
                        "ModSettings.DiscardArchived.Body",
                        row.Entry.FileCount,
                        row.Name,
                        DescribeSize(row.Entry.SizeBytes),
                        row.OriginPath),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["ModSettings.DiscardArchived.PrimaryButton"],
                CloseButtonText = Strings.Current["ModSettings.DiscardArchived.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("ModSettings.DiscardArchived.Kept", row.Name);
                return;
            }

            try
            {
                StatusMessage = new ModSettingsArchive(ArchiveRoot).Discard(row.Entry.Id, RecycleFolder) switch
                {
                    DiscardOutcome.Discarded =>
                        Strings.Current.Format("ModSettings.DiscardArchived.Moved", row.Name, DescribeSize(row.Entry.SizeBytes)),
                    DiscardOutcome.IndexEntryOnly =>
                        Strings.Current.Format("ModSettings.DiscardArchived.IndexOnly", row.Name),
                    _ => Strings.Current.Format("ModSettings.DiscardArchived.NotInIndex", row.Name)
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                StatusMessage = Strings.Current.Format("ModSettings.DiscardArchived.Failed", row.Name, ex.Message);
                LoggingService.LogException(ex, $"Failed to discard the archived settings folder {row.Name}");
            }

            RefreshArchive();
        }

        private static void RecycleFolder(string path) =>
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        [RelayCommand]
        private void PrepareExport()
        {
            ExportFlagged.Clear();

            try
            {
                _exportPlan = SettingsBundle.Plan(SettingsRoot, []);

                foreach (var file in _exportPlan.Flagged)
                    ExportFlagged.Add(new BundleFileRow(file));

                var unread = _exportPlan.NotScanned;
                var read = _exportPlan.Files.Count - unread.Count;

                ExportSummary = Strings.Current.Plural("ModSettings.Export.FileCount", _exportPlan.Files.Count) +
                    Strings.Current.Plural("ModSettings.Export.FolderCount", _exportPlan.Folders.Count) +
                    (ExportFlagged.Count == 0
                        ? unread.Count == 0
                            ? Strings.Current["ModSettings.Export.Clean"]
                            : Strings.Current.Plural("ModSettings.ReadPartial.Clean", read)
                        : Strings.Current.Plural("ModSettings.Export.Flagged", ExportFlagged.Count)) +
                    DescribeUnread(unread.Select(file => file.Describe()).ToList());
            }
            catch (Exception ex)
            {
                _exportPlan = null;
                ExportSummary = Strings.Current.Format("ModSettings.Export.Failed", ex.Message);
                LoggingService.LogException(ex, "Failed to plan a settings bundle");
            }
        }

        [RelayCommand]
        private async Task ExportBundleAsync()
        {
            if (_exportPlan is null)
            {
                ExportSummary = Strings.Current["ModSettings.Export.ScanFirst"];
                return;
            }

            var picker = new FileSavePicker { SuggestedFileName = "Mod Settings Bundle" };
            picker.FileTypeChoices.Add(Strings.Current["ModSettings.Export.FileTypeLabel"], [".bemsettings"]);

            if (!TryInitializePicker(picker))
            {
                ExportSummary = Strings.Current["ModSettings.Export.NoDialog"];
                return;
            }

            var file = await picker.PickSaveFileAsync();

            if (file is null)
            {
                ExportSummary = Strings.Current["ModSettings.Export.Canceled"];
                return;
            }

            try
            {
                var approved = ExportFlagged.Where(row => row.IsSelected).Select(row => row.RelativePath).ToList();
                var result = SettingsBundle.Export(_exportPlan, approved, file.Path);

                ExportSummary = Strings.Current.Plural(
                        "ModSettings.Export.Wrote", result.Included.Count, DescribeSize(result.SizeBytes), result.BundlePath) +
                    (result.ExcludedForSecrets.Count == 0
                        ? Strings.Current["ModSettings.Export.NothingWithheld"]
                        : Strings.Current.Plural(
                            "ModSettings.Export.Excluded", result.ExcludedForSecrets.Count, string.Join(", ", result.ExcludedForSecrets))) +
                    DescribeUnread(result.NotScanned);
            }
            catch (Exception ex)
            {
                ExportSummary = Strings.Current.Format("ModSettings.Export.WriteFailed", ex.Message);
                LoggingService.LogException(ex, "Failed to export a settings bundle");
            }
        }

        [RelayCommand]
        private async Task ChooseBundleAsync()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".bemsettings");
            picker.FileTypeFilter.Add(".zip");

            if (!TryInitializePicker(picker))
            {
                ImportSummary = Strings.Current["ModSettings.Import.NoDialog"];
                return;
            }

            var file = await picker.PickSingleFileAsync();

            if (file is null)
            {
                ImportSummary = Strings.Current["ModSettings.Import.Canceled"];
                return;
            }

            ImportEntries.Clear();

            try
            {
                _importPreview = SettingsBundle.Preview(file.Path, SettingsRoot);

                foreach (var entry in _importPreview.Entries)
                    ImportEntries.Add(new ImportEntryRow(entry));

                var unread = _importPreview.NotScanned;
                var read = _importPreview.Entries.Count - unread.Count;

                ImportSummary = Strings.Current.Plural("ModSettings.Import.FileCount", _importPreview.Entries.Count) +
                    Strings.Current.Plural("ModSettings.Import.OverwriteCount", _importPreview.Overwriting.Count) +
                    (_importPreview.Flagged.Count == 0
                        ? unread.Count == 0
                            ? Strings.Current["ModSettings.Import.Clean"]
                            : Strings.Current.Plural("ModSettings.ReadPartial.Clean", read)
                        : Strings.Current.Plural("ModSettings.Import.Flagged", _importPreview.Flagged.Count)) +
                    DescribeUnread(unread.Select(entry => entry.Describe()).ToList()) +
                    Strings.Current["ModSettings.Import.NotWrittenYet"];
            }
            catch (Exception ex)
            {
                _importPreview = null;
                ImportSummary = Strings.Current.Format("ModSettings.Import.ReadFailed", ex.Message);
                LoggingService.LogException(ex, "Failed to read a settings bundle");
            }
        }

        [RelayCommand]
        private void ImportBundle()
        {
            if (_importPreview is null)
            {
                ImportSummary = Strings.Current["ModSettings.Import.ChooseFirst"];
                return;
            }

            try
            {
                var approved = ImportEntries.Where(row => row.IsSelected).Select(row => row.RelativePath).ToList();
                var result = SettingsBundle.Import(_importPreview, SettingsRoot, new ModSettingsArchive(ArchiveRoot), approved);

                ImportSummary = Strings.Current.Plural("ModSettings.Import.Wrote", result.Written.Count) +
                    (result.SkippedForSecrets.Count == 0
                        ? Strings.Current["ModSettings.Import.NothingSkipped"]
                        : Strings.Current.Plural(
                            "ModSettings.Import.Skipped", result.SkippedForSecrets.Count, string.Join(", ", result.SkippedForSecrets))) +
                    (result.BackedUp.Count == 0
                        ? Strings.Current["ModSettings.Import.NothingBackedUp"]
                        : Strings.Current.Plural("ModSettings.Import.BackedUp", result.BackedUp.Count));

                Refresh();
            }
            catch (Exception ex)
            {
                ImportSummary = Strings.Current.Format("ModSettings.Import.ApplyFailed", ex.Message);
                LoggingService.LogException(ex, "Failed to import a settings bundle");
            }
        }

        private void RefreshArchive()
        {
            Archived.Clear();

            foreach (var entry in new ModSettingsArchive(ArchiveRoot).Read().OrderByDescending(entry => entry.ArchivedUtc))
                Archived.Add(new ArchivedSettingsRow(entry, Restore, DiscardArchivedAsync));
        }

        private static IReadOnlyList<ModuleManifest> ScanModules()
        {
            var chosen = ShellViewModels.Instance.Environment.GameInstallPath;
            var path = string.IsNullOrWhiteSpace(chosen) ? GameInstallLocator.Locate() ?? string.Empty : chosen;

            return string.IsNullOrWhiteSpace(path) ? [] : ModuleScanner.ScanAll(path).Modules;
        }

        private const int MaxNamedFiles = 6;

        // A credential check that skipped files cannot be reported as a clean result, so the files it
        // never read are named next to it rather than folded into the count that was checked.
        private static string DescribeUnread(IReadOnlyList<string> files)
        {
            if (files.Count == 0)
                return string.Empty;

            var named = string.Join(", ", files.Take(MaxNamedFiles));
            var rest = files.Count > MaxNamedFiles
                ? Strings.Current.Plural("ModSettings.Unread.AndMore", files.Count - MaxNamedFiles)
                : string.Empty;

            return Strings.Current.Plural("ModSettings.Unread.Summary", files.Count) + $"{named}{rest}.";
        }

        internal static string DescribeSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
            >= 1024 => $"{bytes / 1024d:0.#} KB",
            _ => Strings.Current.Plural("ModSettings.Size.Bytes", bytes)
        };

        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;

            if (window is null)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            switch (picker)
            {
                case FileOpenPicker openPicker:
                    InitializeWithWindow.Initialize(openPicker, hwnd);
                    return true;

                case FileSavePicker savePicker:
                    InitializeWithWindow.Initialize(savePicker, hwnd);
                    return true;

                default:
                    return false;
            }
        }
    }
}
