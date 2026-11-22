using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One line of the Library's install history. Rollback is offered only on a line whose archive is
    // still on disk and whose version is not the one currently installed.
    public sealed class InstallHistoryRowViewModel(InstallHistoryEntry entry, bool canRollback)
    {
        public InstallHistoryEntry Entry { get; } = entry;

        public string Title => Entry.ModuleId;

        public string Detail => Strings.Current.Format(
            "Install.History.Row",
            Strings.Current[$"Install.History.Kind.{Entry.Kind}"],
            string.IsNullOrWhiteSpace(Entry.Version) ? Strings.Current["Install.History.NoVersion"] : Entry.Version,
            Entry.WhenUtc.ToLocalTime().ToString("g"));

        public bool CanRollback { get; } = canRollback;
    }

    public partial class InstallViewModel
    {
        private const int HistoryRowLimit = 50;

        public ObservableCollection<InstallHistoryRowViewModel> HistoryRows { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoHistory))]
        public partial bool HasHistory { get; set; }

        public bool HasNoHistory => !HasHistory;

        [ObservableProperty]
        public partial string HistoryStatus { get; set; } = string.Empty;

        public void RefreshHistory()
        {
            try
            {
                var store = new InstallHistoryStore(InstallHistoryStore.DefaultPath(InstallDataRoot));
                var entries = store.Load().Take(HistoryRowLimit).ToList();
                var installed = InstalledVersions();

                HistoryRows.Clear();

                foreach (var entry in entries)
                {
                    var canRollback = entry.Kind != InstallHistoryKind.Removed
                        && store.RollbackCandidates(entry.ModuleId, installed.GetValueOrDefault(entry.ModuleId))
                            .Any(candidate => candidate.ArchivePath == entry.ArchivePath);

                    HistoryRows.Add(new InstallHistoryRowViewModel(entry, canRollback));
                }

                HasHistory = HistoryRows.Count > 0;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read the install history");
            }
        }

        private static Dictionary<string, string?> InstalledVersions()
        {
            var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in ShellViewModels.Instance.Environment.Modules)
            {
                if (row.Entry.Manifest is { } manifest)
                    versions[row.Entry.Id.Value] = manifest.VersionText ?? manifest.Version.ToString();
            }

            return versions;
        }

        // Reinstalls the archive a history line names through the same analyze, safety check and install
        // every other install uses, so the folder it replaces goes to the undo list, not the bin.
        [RelayCommand(CanExecute = nameof(CanRunCommands))]
        private async Task RollbackHistoryEntryAsync(InstallHistoryRowViewModel? row)
        {
            if (row is not { CanRollback: true } || string.IsNullOrWhiteSpace(row.Entry.ArchivePath))
                return;

            var path = row.Entry.ArchivePath;

            if (!File.Exists(path))
            {
                HistoryStatus = Strings.Current.Format("Install.History.ArchiveGone", Path.GetFileName(path));
                RefreshHistory();
                return;
            }

            ModArchiveEntry archive;

            try
            {
                archive = await AnalyzeArchiveAsync(path, EnsureModuleDataContext());
            }
            catch (Exception ex)
            {
                HistoryStatus = Strings.Current.Format("Install.FailedToAnalyze", Path.GetFileName(path), ex.Message);
                return;
            }

            await InstallOneArchiveAsync(archive, offerCleanup: false);

            HistoryStatus = StatusMessage;
            RefreshHistory();
        }
    }
}
