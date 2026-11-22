using Microsoft.VisualBasic.FileIO;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Butr;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Safety;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class InstallViewModel : BaseViewModel, IDisposable
    {
        private sealed class AppSettings
        {
            public string GameInstallPath { get; set; } = string.Empty;
            public string ArchivesFolderPath { get; set; } = string.Empty;
            public bool DeleteArchivesAfterInstall { get; set; }
            public bool? MoveKeptArchivesToInstalled { get; set; }
            public bool SearchArchivesRecursively { get; set; }
            public bool? ReplaceExistingModuleFolders { get; set; }
            public bool InstallSelectedOnly { get; set; }
            public bool? KeepReplacedModuleFolders { get; set; }
            public int? ReplacedModuleKeepCount { get; set; }
            public bool IncludeOptionalCollectionMods { get; set; }
            public bool? FollowCuratorUpdateChoice { get; set; }
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(AppSettings))]
        private sealed partial class AppSettingsJsonContext : JsonSerializerContext
        {
        }

        private static string SettingsFilePath => Path.Combine(AppDataRoot.Folder, "settings.json");

        private const string InstallPathSettingKey = "InstallPath";
        private const string ArchivesFolderSettingKey = "ArchivesFolder";
        private const string DeleteAfterInstallSettingKey = "DeleteArchivesAfterInstall";
        private const string RecurseArchivesSettingKey = "RecurseArchives";

        private sealed record ArchiveInstallOutcome(bool Installed, string Status);

        // The outcome of one autonomous update, for the caller to report without touching the install
        // machinery it does not own.
        public sealed record ModUpdateInstallResult(string ModuleId, string ModuleName, bool Installed, string Message);

        private sealed record ManifestFacts(string? Name, string? Url, string? Id = null)
        {
            public static ManifestFacts None { get; } = new(null, null);
        }

        private sealed record ModuleDataContext(string ModulesFolderPath, IReadOnlyCollection<string> OfficialModuleFolders)
        {
            public static ModuleDataContext Empty { get; } = new(string.Empty, []);
        }

        private readonly string defaultInstallPath;
        private string installArchiveLabel = string.Empty;
        private string installStageLabel = string.Empty;
        private FileSystemWatcher? archiveFolderWatcher;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> archiveAnalysisGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim globalAnalysisSemaphore = new(2, 2); // Limit concurrent analyses
        private CancellationTokenSource? loadArchivesCts;
        private readonly NexusDownloadCancellation nexusDownloads = new();
        private readonly DownloadQueue<NxmDownloadResult> downloadQueue;

        private static readonly TimeSpan ArchiveSearchQuietPeriod = TimeSpan.FromMilliseconds(300);
        private readonly SupersedingDebounce archiveSearchDebounce = new(ArchiveSearchQuietPeriod);

        private const int SettleAttempts = 120;
        private const int SettleDelayMs = 500;

        private readonly ConcurrentDictionary<string, byte> settlingArchives = new(StringComparer.OrdinalIgnoreCase);
        private ModuleDataContext moduleDataContext = ModuleDataContext.Empty;
        private bool mergeFileReplacements = true;

        // Both are built on each read rather than held, for the reason Diagnostics and Mod safety are:
        // the version dropdown can change while this page is open, and a store resolved once would go
        // on writing one version's records into the folder of the version that is no longer selected.
        private static BinBackupStore BinBackupsStore => new(BinBackupStore.GetDefaultRoot(ActiveDataRoot));

        private static ModuleArchiveLinkStore ArchiveLinks => ModuleArchiveLinkStore.For(ActiveDataRoot);

        private static InstanceDataRoot? ActiveDataRoot => ShellViewModels.Instance.Environment.ActiveDataRoot;

        // One quarantine store holds every installed version's folders, because it has to put each one
        // back at the absolute path it came from. Which version an entry belongs to is read off that
        // path, so the lists and the prune are scoped here rather than by splitting the store.
        // Null is every version, which is what listing with no install selected has always shown.
        private string? ModulesFolderScope => string.IsNullOrWhiteSpace(GameInstallPath)
            ? null
            : ModuleScanner.GetModulesFolder(GameInstallPath);

        // The shader and platform scans read the workshop folder as part of this install, so a folder
        // taken out of it belongs to the version on screen and has to stay restorable from here.
        private IReadOnlyList<string> QuarantineScopeRoots
        {
            get
            {
                if (ModulesFolderScope is not { } modules)
                    return [];

                return ModuleScanner.GetWorkshopFolder(GameInstallPath) is { } workshop
                    ? [modules, workshop]
                    : [modules];
            }
        }

        private readonly ArchiveProvenanceStore archiveProvenance = new(ArchiveProvenanceStore.DefaultPath());

        // One request per module, against a daily budget shared with whatever other mod manager the
        // user runs. A press identifies a batch and says how many were left for the next one.
        private const int FileIdentificationsPerPress = 40;

        private readonly IDeletedArchiveNames deletedArchives = new RecycleBinArchiveNames();

        private readonly LearnedNexusIdStore confirmedNexusIds = new(LearnedNexusIdStore.DefaultPath());

        private readonly RejectedNexusIdStore rejectedNexusIds = new(RejectedNexusIdStore.DefaultPath());

        private readonly UnrecognizedArchiveHashStore unrecognizedHashes = new(UnrecognizedArchiveHashStore.DefaultPath());

        private bool disposed;
        
        public InstallViewModel()
        {
            LoggingService.Log("Initializing InstallViewModel");
            downloadQueue = new DownloadQueue<NxmDownloadResult>(
                RunQueuedDownloadAsync, result => result.Status == NxmDownloadStatus.Downloaded);
            downloadQueue.Changed += OnDownloadQueueChanged;
            Title = Strings.Current["Install.ViewModelTitle"];
            StatusMessage = string.Empty;
            ConfirmedNexusIdSummary = string.Empty;
            GameInstallPath = string.Empty;
            ArchivesFolderPath = string.Empty;
            ReplaceExistingModuleFolders = true;
            InstallSelectedOnly = true;
            // Always on, with no switch: a replaced folder is kept, and the newest DefaultKeep stay.
            KeepReplacedModuleFolders = true;
            // Off by default: only the archives folder itself is read unless the user turns this on.
            SearchArchivesRecursively = false;
            defaultInstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Mount & Blade II Bannerlord");

            LoadCachedSettings();

            if (string.IsNullOrWhiteSpace(GameInstallPath) && Directory.Exists(defaultInstallPath))
            {
                GameInstallPath = defaultInstallPath;
            }

            PropertyChanged += OnInstallViewModelPropertyChanged;

            // Start watching the archives folder if it's set
            if (!string.IsNullOrWhiteSpace(ArchivesFolderPath) && Directory.Exists(ArchivesFolderPath))
            {
                _ = LoadArchivesFromFolderAsync(ArchivesFolderPath);
                StartWatchingArchivesFolder(ArchivesFolderPath);
            }

            // Independent of the archive folder on purpose: an owner who deletes archives after
            // installing still has the quarantine and BEM's own install log to go on.
            _ = BackfillArchiveLinksAsync();

            // Any route that writes a mod id redraws the two lists Find Mod IDs shows.
            NexusIdsChanged.Raised += (sender, _) =>
            {
                ListConfirmedNexusIds();
                ListRejectedNexusIds();
            };

            // Read from disk on the way in, so the list is what the file says rather than only what
            // this session happened to record.
            _ = ListNexusIdsFromDiskAsync();
        }

        public ObservableCollection<ModArchiveEntry> Archives { get; } = [];
        public ObservableCollection<ModArchiveEntry> FilteredArchives { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallModsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RestoreUndoEntryCommand))]
        [NotifyCanExecuteChangedFor(nameof(InstallButrStackCommand))]
        [NotifyCanExecuteChangedFor(nameof(RollbackHistoryEntryCommand))]
        public partial bool IsBusy { get; set; }

        // The bar is the page's only progress renderer, so every busy operation has to reach it.
        // Work that reports a percentage turns the indeterminate sweep off when it sets its first
        // value; work that reports none leaves it on rather than showing nothing at all.
        partial void OnIsBusyChanged(bool value)
        {
            IsProgressVisible = value;
            IsProgressIndeterminate = value;

            // Starting work clears the last operation's words here rather than in each caller, because
            // the caption is read from StatusMessage and a message about a finished, unrelated
            // operation sitting over a running bar reads as a report of the run in front of the user.
            // Every operation that sets IsBusy writes its own line straight afterward.
            if (value)
                StatusMessage = string.Empty;
            else
            {
                ProgressValue = 0;
                RefreshHistory();
            }

            ButrStackBlocker = ButrStackBlockedBecause();
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCaption))]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial string ConfirmedNexusIdSummary { get; set; }

        // Recording a mod id was reachable and taking one back was not, so a wrong id said a mod was
        // out of date forever. Nothing listed the ids either, which is why a wrong one could sit there
        // unnoticed: a decision the user cannot see is one they cannot correct.
        public ObservableCollection<ConfirmedNexusIdRow> ConfirmedNexusIds { get; } = [];

        public bool HasConfirmedNexusIds => ConfirmedNexusIds.Count > 0;

        // The same argument in the other direction. Rejecting a page is a decision BEM keeps, so it is
        // shown and it can be taken back; otherwise a page turned down by mistake is unproposable
        // forever with nothing on screen saying why.
        public ObservableCollection<RejectedNexusIdRow> RejectedNexusIds { get; } = [];

        public bool HasRejectedNexusIds => RejectedNexusIds.Count > 0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RestoreRejectedNexusIdCommand))]
        public partial RejectedNexusIdRow? SelectedRejectedNexusId { get; set; }

        public bool HasSelectedRejectedNexusId => SelectedRejectedNexusId is not null;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenConfirmedNexusIdPageCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyConfirmedNexusIdRowCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyConfirmedNexusIdPageUrlCommand))]
        public partial ConfirmedNexusIdRow? SelectedConfirmedNexusId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCaption))]
        public partial int ProgressValue { get; set; }
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCaption))]
        public partial bool IsProgressVisible { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCaption))]
        public partial bool IsProgressIndeterminate { get; set; }

        // What the bar is doing, beside the bar itself: a filling bar with no words says how far along
        // something is without saying what.
        public string ProgressCaption => IsProgressVisible
            ? InstallProgress.Caption(StatusMessage, ProgressValue, IsProgressIndeterminate, CultureInfo.CurrentCulture)
            : string.Empty;

        [ObservableProperty]
        public partial string GameInstallPath { get; set; }

        private bool followingInstance;

        // Install into the version being played. The Play page calls this every time it resolves the
        // active instance, so a mod added while a managed version is selected lands in that version's
        // Modules folder rather than in whichever install was configured first.
        public void FollowInstance(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)
                || GameInstallPath.Equals(gameFolder, StringComparison.OrdinalIgnoreCase))
                return;

            followingInstance = true;

            try
            {
                GameInstallPath = gameFolder;
            }
            finally
            {
                followingInstance = false;
            }
        }

        // Refresh() is what normally calls FollowInstance, so every write path here used to depend on
        // Play having already refreshed under the active instance in this process; reached first (an
        // install started right after switching a version, before Play visits it again), GameInstallPath
        // was still whatever the resting install last left it at, and the mod landed there instead.
        // Resolving directly against the active instance removes that dependency.
        private void ResolveActiveInstanceInstallTarget()
        {
            if (ShellViewModels.Instance.Environment.ActiveInstanceGameFolder is { } activeGameFolder)
                FollowInstance(activeGameFolder);
        }

        // ---- Where an archive goes ----

        // The registry is read here rather than through Play's view model: an nxm:// link opens BEM
        // straight onto this page, where Play has never run, and the destinations have to stand before
        // anything here is pressed.
        private readonly InstanceManager destinationInstances =
            new(CanonicalPathSet.ForMachine(), new InstanceSettingsStore());

        // Every registered instance, and which of them the next install writes into. One archive into
        // several versions in one press is what this removes four visits to this page for; the resting
        // instance is offered like any other and says so in its own label.
        public ObservableCollection<InstallDestinationRow> InstallDestinations { get; } = [];

        public bool HasInstallDestinations => InstallDestinations.Count > 0;

        // The instance whose tick this list placed rather than the user. Ticking nothing of your own
        // means following the active instance, so that tick moves when the active instance does; a
        // tick placed anywhere else is the user's and a later switch leaves it exactly where it is.
        private string? followedActiveInstanceId;

        // Which destination the install in flight is writing into, and null at every other moment.
        // This page's own panels answer a different question - what the version on screen holds - so
        // they go on reading the configured folder and the active instance's data root.
        private InstallTarget? currentDestination;

        private string InstallRoot => currentDestination?.GameFolder ?? GameInstallPath;

        private InstanceDataRoot? InstallDataRoot => currentDestination?.DataRoot ?? ActiveDataRoot;

        private BinBackupStore InstallBinBackups => new(BinBackupStore.GetDefaultRoot(InstallDataRoot));

        private ModuleArchiveLinkStore InstallArchiveLinks => ModuleArchiveLinkStore.For(InstallDataRoot);

        public void RefreshInstallDestinations()
        {
            var ticked = InstallDestinations
                .Where(row => row.IsSelected)
                .Select(row => row.InstanceId)
                .ToHashSet(StringComparer.Ordinal);

            IReadOnlyList<InstalledInstance> installed;
            string? resting;
            string? active;

            try
            {
                installed = destinationInstances.List();
                resting = destinationInstances.RestingInstanceId;
                active = destinationInstances.ActiveInstanceId;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A registry that cannot be read leaves this page on its configured folder, which is
                // the only destination it had before there was a choice of them.
                LoggingService.LogException(ex, "The install destinations could not be listed");
                InstallDestinations.Clear();
                OnPropertyChanged(nameof(HasInstallDestinations));
                return;
            }

            if (followedActiveInstanceId is { } followed && ticked.Count == 1 && ticked.Contains(followed))
            {
                ticked.Clear();

                if (active is not null)
                    ticked.Add(active);
            }

            foreach (var row in InstallDestinations)
                row.PropertyChanged -= OnInstallDestinationChanged;

            InstallDestinations.Clear();

            foreach (var instance in installed)
            {
                var row = new InstallDestinationRow(
                    InstallTarget.For(instance, resting),
                    InstanceLabel.LabelOf(instance.Record, resting),
                    InstanceLabel.IsResting(instance.Record.Id, resting))
                {
                    IsSelected = ticked.Contains(instance.Record.Id)
                };

                // Install BUTR Stack writes into these ticks now, so unticking the last one has to
                // reach the button: a control that stays pressable and then reports that there was
                // nowhere to write is one that answered a question it should have shown.
                row.PropertyChanged += OnInstallDestinationChanged;
                InstallDestinations.Add(row);
            }

            // Nothing ticked is a press with nowhere to write, and the active instance is where every
            // install on this page went before there was anything to tick.
            if (InstallDestinations.Count > 0 && InstallDestinations.All(row => !row.IsSelected))
            {
                var fallback = InstallDestinations
                    .FirstOrDefault(row => string.Equals(row.InstanceId, active, StringComparison.Ordinal))
                    ?? InstallDestinations.FirstOrDefault(row => row.IsResting);

                if (fallback is not null)
                    fallback.IsSelected = true;
            }

            var chosen = InstallDestinations.Where(row => row.IsSelected).ToList();

            followedActiveInstanceId =
                chosen.Count == 1 && string.Equals(chosen[0].InstanceId, active, StringComparison.Ordinal)
                    ? active
                    : null;

            OnPropertyChanged(nameof(HasInstallDestinations));
        }

        private void OnInstallDestinationChanged(object? sender, PropertyChangedEventArgs e)
        {
            _ = sender;

            if (e.PropertyName is nameof(InstallDestinationRow.IsSelected))
            {
                RefreshButrStackReadiness();
                RefreshCollectionReadiness();
            }
        }

        // What the next install writes into: the ticked instances, or the folder configured on this
        // page when no instance is registered at all, which is the only destination a plain machine
        // install has ever had. Its data root is the machine's, which is where this page's bin backups
        // and archive links already sit for that case.
        private IReadOnlyList<InstallTarget> ChosenDestinations()
        {
            if (InstallDestinations.Count == 0)
                return [ConfiguredFolderDestination()];

            return [.. InstallDestinations.Where(row => row.IsSelected).Select(row => row.Target)];
        }

        private InstallTarget ConfiguredFolderDestination() => new(
            string.Empty,
            GameInstallPath,
            GameInstallPath,
            ActiveDataRoot ?? InstanceDataRoot.ForMachine(),
            Strings.Current["Install.Destinations.ConfiguredFolder"]);

        // What each destination did with the archives, which is the only place a run into four versions
        // can be read: a total says a mod is installed without saying where, and not having to go and
        // look is the whole of what the press buys.
        public ObservableCollection<string> InstallResults { get; } = [];

        public bool HasInstallResults => InstallResults.Count > 0;

        [ObservableProperty]
        public partial string ArchivesFolderPath { get; set; }

        [ObservableProperty]
        public partial bool DeleteArchivesAfterInstall { get; set; }

        // On by default: an archive kept after its mod installed leaves the list of archives waiting to
        // install for the INSTALLED folder, where it stays on disk to install again or roll back to.
        [ObservableProperty]
        public partial bool MoveKeptArchivesToInstalled { get; set; } = true;

        [ObservableProperty]
        public partial bool SearchArchivesRecursively { get; set; }      

        [ObservableProperty]
        public partial bool ReplaceExistingModuleFolders { get; set; }

        [ObservableProperty]
        public partial string ArchiveSearchText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool InstallSelectedOnly { get; set; }

        // The Library's Downloads panel: every Nexus download this session started, running first as they
        // arrive and finished ones kept until dismissed, so a result is never lost behind the next link.
        public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

        public bool HasDownloads => Downloads.Count > 0;

        public bool HasFinishedDownloads => Downloads.Any(d => d.IsFinished);

        [ObservableProperty]
        public partial bool IsNexusKeyNeeded { get; set; }

        private void RaiseDownloadsChanged()
        {
            OnPropertyChanged(nameof(HasDownloads));
            OnPropertyChanged(nameof(HasFinishedDownloads));
        }

        private void DismissDownload(DownloadItemViewModel item)
        {
            if (item.IsRunning)
                return;

            Downloads.Remove(item);

            if (item.QueueItem is { } queued)
                downloadQueue.Remove(queued);

            RaiseDownloadsChanged();
        }

        [RelayCommand]
        private void ClearFinishedDownloads()
        {
            foreach (var item in Downloads.Where(d => d.IsFinished).ToList())
                DismissDownload(item);

            RaiseDownloadsChanged();
        }

        // The one list of everything BEM did that can be put back, newest first.
        public ObservableCollection<UndoEntry> UndoEntries { get; } = [];

        [ObservableProperty]
        public partial string UndoStatus { get; set; } = Strings.Current["Install.Status.NotCheckedYet"];

        [ObservableProperty]
        public partial bool HasUndoEntries { get; set; }

        [ObservableProperty]
        public partial bool KeepReplacedModuleFolders { get; set; }

        [ObservableProperty]
        public partial double ReplacedModuleKeepCount { get; set; }

        // A fixed number of kept copies: the newest DefaultKeep. There is no setting for it, so no
        // edited field can ever make Prune delete every kept folder.
        private static int KeptModuleFolderLimit => ReplacedModuleQuarantine.DefaultKeep;


        // The archive a right-click landed on. Every item on the archive flyout reads it, and its
        // CanExecute is what grays an item out, so an action with no archive behind it cannot be
        // clicked at all.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallContextArchiveCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShowContextArchiveContentsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShowContextArchiveInExplorerCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyContextArchiveNameCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyContextArchivePathCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenContextArchivePageCommand))]
        [NotifyCanExecuteChangedFor(nameof(RecycleContextArchiveCommand))]
        [NotifyCanExecuteChangedFor(nameof(ArchiveContextArchiveCommand))]
        public partial ModArchiveEntry? ContextArchive { get; set; }

        public void SetContextArchive(ModArchiveEntry? archive)
        {
            ContextArchive = null;
            ContextArchive = archive;
            RefreshArchiveContextCommands();
        }

        // Called again as the flyout opens, so what an item offers is computed from the archive that
        // is recorded at the moment it appears rather than from whatever was true when it was bound.
        public void RefreshArchiveContextCommands()
        {
            InstallContextArchiveCommand.NotifyCanExecuteChanged();
            ShowContextArchiveContentsCommand.NotifyCanExecuteChanged();
            ShowContextArchiveInExplorerCommand.NotifyCanExecuteChanged();
            CopyContextArchiveNameCommand.NotifyCanExecuteChanged();
            CopyContextArchivePathCommand.NotifyCanExecuteChanged();
            OpenContextArchivePageCommand.NotifyCanExecuteChanged();
            RecycleContextArchiveCommand.NotifyCanExecuteChanged();
            ArchiveContextArchiveCommand.NotifyCanExecuteChanged();
        }

        // Sets the archive aside in ARCHIVED: off the list of archives waiting to install, still on disk,
        // so nothing is asked first. Moving it back out of the folder lists it again.
        [RelayCommand(CanExecute = nameof(CanReachContextArchiveFile))]
        private void ArchiveContextArchive()
        {
            if (ContextArchive is not { } archive)
                return;

            if (MoveIntoSetAsideFolder([archive.FilePath], ArchiveFolders.Archived) > 0)
            {
                Archives.Remove(archive);
                RefreshFilteredArchives();
                SetContextArchive(null);
            }
        }

        private bool HasContextArchive => ContextArchive is not null;

        private bool CanReachContextArchiveFile => ContextArchive is { } archive && File.Exists(archive.FilePath);

        private bool CanInstallContextArchive =>
            !IsBusy && CanReachContextArchiveFile && Directory.Exists(GameInstallPath);

        private bool CanOpenContextArchivePage => ContextArchive?.ModPageUrl is not null;

        [RelayCommand(CanExecute = nameof(CanInstallContextArchive))]
        private async Task InstallContextArchiveAsync()
        {
            if (ContextArchive is not { } archive)
                return;

            await InstallOneArchiveAsync(archive, offerCleanup: true);
        }

        public static readonly string[] DroppableArchiveExtensions = [".zip", ".7z", ".rar"];

        public static bool IsDroppableArchive(string path) =>
            DroppableArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        // Install by dragging: each dropped archive goes through the same analyze, safety check and
        // install the Library's own Install button uses. The files are the owner's from wherever they
        // were dragged, so they are never recycled or offered for deletion afterwards.
        public async Task InstallDroppedArchivesAsync(IReadOnlyList<string> paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            var archives = paths.Where(IsDroppableArchive).ToList();

            if (archives.Count == 0)
                return;

            if (IsBusy)
            {
                StatusMessage = Strings.Current["Install.Drop.Busy"];
                return;
            }

            foreach (var path in archives)
            {
                ModArchiveEntry entry;

                try
                {
                    entry = await AnalyzeArchiveAsync(path, EnsureModuleDataContext());
                }
                catch (Exception ex)
                {
                    StatusMessage = Strings.Current.Format("Install.FailedToAnalyze", Path.GetFileName(path), ex.Message);
                    continue;
                }

                await InstallOneArchiveAsync(entry, offerCleanup: false);
            }
        }

        private async Task InstallOneArchiveAsync(ModArchiveEntry archive, bool offerCleanup)
        {
            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();

            // Its two siblings (InstallModsAsync, InstallModUpdatesAsync) both revalidate right after
            // resolving the target, since retargeting can point at a folder that no longer exists (the
            // instance was deactivated between CanInstallContextArchive's last check and this running).
            if (InstallDestinations.Count == 0 && (string.IsNullOrWhiteSpace(GameInstallPath) || !Directory.Exists(GameInstallPath)))
            {
                StatusMessage = Strings.Current["Install.SelectInstallFolder"];
                return;
            }

            if (ChosenDestinations().Count == 0)
            {
                StatusMessage = Strings.Current["Install.Destinations.NoneSelected"];
                return;
            }

            ClearInstallResults();
            IsBusy = true;
            IsProgressVisible = true;
            IsProgressIndeterminate = false;
            ProgressValue = 0;

            try
            {
                List<ModArchiveEntry> targets = [archive];

                if (!(await CheckArchiveSafetyAsync(targets)).Proceed)
                {
                    StatusMessage = Strings.Current.Format("Install.Archive.SkippedTrojanized", archive.DisplayName);
                    return;
                }

                var run = await PrepareInstallRunAsync(targets);
                var refusal = ShowRunNotes(run);

                if (!run.HasDestination)
                {
                    StatusMessage = refusal;
                    return;
                }

                installArchiveLabel = Strings.Current.Format("Install.Archive.Installing", archive.DisplayName);
                installStageLabel = string.Empty;
                StatusMessage = installArchiveLabel;

                var outcome = await InstallArchiveAsync(
                    archive, run, new Progress<int>(ReportInstallPercent), new Progress<string>(ReportInstallStage));

                archive.Status = outcome.Status;

                // The outcome carries what happened to the folder that was already there, so a status
                // line that said only "Installed" would hide where the previous copy went.
                StatusMessage = outcome.Installed
                    ? Strings.Current.Format("Install.Archive.InstalledStatus", archive.DisplayName, outcome.Status)
                    : Strings.Current.Format("Install.Archive.NotInstalledStatus", archive.DisplayName, outcome.Status);

                ShowReplacedModuleFolders();

                if (!outcome.Installed)
                    return;

                await ConfirmInstalledModulesAsync([archive], run.Destinations);

                if (!offerCleanup)
                    return;

                if (DeleteArchivesAfterInstall)
                    MoveToRecycleBin([archive.FilePath]);
                else
                    await OfferDeleteArchivesAsync([archive.FilePath]);
            }
            catch (Exception ex)
            {
                archive.Status = Strings.Current.Format("Install.Archive.InstallFailed", ex.Message);
                StatusMessage = Strings.Current.Format("Install.Archive.FailedToInstall", archive.DisplayName, ex.Message);
                LoggingService.LogException(ex, $"Failed to install archive: {archive.DisplayName}");
            }
            finally
            {
                installArchiveLabel = string.Empty;
                installStageLabel = string.Empty;
                IsBusy = false;
                IsProgressVisible = false;
                ProgressValue = 0;
            }
        }

        // What the archive would do, without doing any of it. The same three lists the installer acts
        // on, so what is listed here is what would be written.
        [RelayCommand(CanExecute = nameof(HasContextArchive))]
        private void ShowContextArchiveContents()
        {
            if (ContextArchive is not { } archive)
                return;

            var lines = new List<string> { Strings.Current.Format("Install.Archive.NameAndSize", archive.FileName, archive.FileSizeDisplay) };

            foreach (var module in archive.Modules)
                lines.Add(Strings.Current.Format("Install.Archive.ContentsModule", module.DisplayLabel));

            foreach (var payload in archive.BinPayloads)
                lines.Add(Strings.Current.Plural("Install.Archive.ContentsBinPayload", payload.Files.Count, payload.PlatformFolder));

            foreach (var replacement in archive.FileReplacements)
            {
                lines.Add(replacement.Target is { } target
                    ? Strings.Current.Format("Install.Archive.ContentsReplacesInto", replacement.FileName, target.ModuleFolderName)
                    : Strings.Current.Format("Install.Archive.ContentsReplacesAmbiguous", replacement.FileName));
            }

            if (!string.IsNullOrWhiteSpace(archive.UnrecognizedLayout))
                lines.Add(archive.UnrecognizedLayout);

            if (lines.Count == 1)
                lines.Add(Strings.Current["Install.Archive.ContentsNothingRecognizable"]);

            StatusMessage = string.Join(Environment.NewLine, lines);
        }

        [RelayCommand(CanExecute = nameof(CanReachContextArchiveFile))]
        private void ShowContextArchiveInExplorer()
        {
            if (ContextArchive is not { } archive)
                return;

            Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{archive.FilePath}\"") { UseShellExecute = false },
                Strings.Current.Format("Install.Archive.ShowedInExplorer", archive.FileName));
        }

        [RelayCommand(CanExecute = nameof(HasContextArchive))]
        private void CopyContextArchiveName()
        {
            if (ContextArchive is { } archive)
                CopyToClipboard(archive.FileName, Strings.Current.Format("Install.CopiedToClipboard", archive.FileName));
        }

        [RelayCommand(CanExecute = nameof(HasContextArchive))]
        private void CopyContextArchivePath()
        {
            if (ContextArchive is { } archive)
                CopyToClipboard(archive.FilePath, Strings.Current.Format("Install.CopiedToClipboard", archive.FilePath));
        }

        [RelayCommand(CanExecute = nameof(CanOpenContextArchivePage))]
        private void OpenContextArchivePage()
        {
            if (ContextArchive?.ModPageUrl is not { } page)
                return;

            Start(new ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Install.Opened", page));
        }

        // Recycle Bin, never an unrecoverable delete, and never without being asked first. An archive
        // is the only copy of a mod some people have.
        [RelayCommand(CanExecute = nameof(CanReachContextArchiveFile))]
        private async Task RecycleContextArchiveAsync()
        {
            if (ContextArchive is not { } archive)
                return;

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.RecycleArchiveTitle"],
                Content = Strings.Current.Format("Install.Confirm.RecycleArchiveContent", archive.FileName),
                PrimaryButtonText = Strings.Current["Install.Confirm.MoveToRecycleBin"],
                CloseButtonText = Strings.Current["Install.Confirm.KeepIt"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("Install.Archive.Kept", archive.FileName);
                return;
            }

            try
            {
                FileSystem.DeleteFile(archive.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to recycle archive: {archive.FilePath}");
                StatusMessage = Strings.Current.Format("Install.Archive.CouldNotRecycle", archive.FileName, ex.Message);
                return;
            }

            Archives.Remove(archive);
            RefreshFilteredArchives();
            SetContextArchive(null);
            StatusMessage = Strings.Current.Format("Install.Archive.MovedToRecycleBin", archive.FileName);
        }

        private void CopyToClipboard(string text, string success)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);

            // Whatever else has the clipboard open (a clipboard manager, an RDP session) makes this
            // throw, and an unhandled throw here would close the app.
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = success;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy an archive detail to the clipboard");
                StatusMessage = Strings.Current["Install.ClipboardBusy"];
            }
        }

        private void Start(ProcessStartInfo startInfo, string success)
        {
            try
            {
                using var process = Process.Start(startInfo);
                StatusMessage = success;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LoggingService.LogException(ex, "Failed to open an archive path");
                StatusMessage = Strings.Current.Format("Install.CouldNotOpen", startInfo.FileName, ex.Message);
            }
        }

        [RelayCommand]
        private static async Task OpenLogsFolderAsync()
        {
            await LoggingService.OpenLogFolderAsync();
        }

        private void OnInstallViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(GameInstallPath):
                    // A path pushed across by the Play page is where this session's mods go, not a
                    // setting. Saving it would replace the configured machine install the moment a
                    // managed version was selected, and picking the machine install again would then
                    // have nothing to go back to.
                    if (!followingInstance)
                        SaveSettings();

                    break;
                case nameof(ArchivesFolderPath):
                case nameof(DeleteArchivesAfterInstall):
                case nameof(MoveKeptArchivesToInstalled):
                case nameof(ReplaceExistingModuleFolders):
                case nameof(InstallSelectedOnly):
                case nameof(IncludeOptionalCollectionMods):
                case nameof(FollowCuratorUpdateChoice):
                    SaveSettings();
                    break;
            }

            if (e.PropertyName == nameof(ArchivesFolderPath))
            {
                LoggingService.Log($"Archives folder changed to: {ArchivesFolderPath}");
                HandleArchivesFolderPathChanged();
            }

            if (e.PropertyName == nameof(SearchArchivesRecursively))      
            {
                RestartArchiveFolderWatcher();
            }
        }

        partial void OnArchiveSearchTextChanged(string value)
        {
            RefreshFilteredArchivesAfterSearchPause();
        }

        private void HandleArchivesFolderPathChanged()
        {
            LoggingService.Log($"Archives folder changed to: {ArchivesFolderPath}");
            StopWatchingArchivesFolder();
            loadArchivesCts?.Cancel();
            Archives.Clear();
            FilteredArchives.Clear();

            if (!string.IsNullOrWhiteSpace(ArchivesFolderPath) && Directory.Exists(ArchivesFolderPath))
            {
                _ = LoadArchivesFromFolderAsync(ArchivesFolderPath);      
                StartWatchingArchivesFolder(ArchivesFolderPath);
            }
        }

        private void RestartArchiveFolderWatcher()
        {
            if (!string.IsNullOrWhiteSpace(ArchivesFolderPath) && Directory.Exists(ArchivesFolderPath))
            {
                StopWatchingArchivesFolder();
                StartWatchingArchivesFolder(ArchivesFolderPath);
            }
        }

        private void StartWatchingArchivesFolder(string folderPath)
        {
            try
            {
                LoggingService.Log($"Starting to watch archives folder: {folderPath}");
                archiveFolderWatcher = new FileSystemWatcher
                {
                    Path = folderPath,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    IncludeSubdirectories = SearchArchivesRecursively,
                    EnableRaisingEvents = true
                };

                archiveFolderWatcher.Created += OnArchiveFileChanged;
                archiveFolderWatcher.Deleted += OnArchiveFileChanged;
                archiveFolderWatcher.Renamed += OnArchiveFileRenamed;
                archiveFolderWatcher.Changed += OnArchiveFileChanged;
                archiveFolderWatcher.Error += OnArchiveWatcherError;

                StatusMessage = Strings.Current.Format("Install.Watching", folderPath);
                LoggingService.Log($"Started watching folder: {folderPath}");
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, $"Failed to start watching archives folder: {folderPath}");
            }
        }

        private void StopWatchingArchivesFolder()
        {
            if (archiveFolderWatcher != null)
            {
                archiveFolderWatcher.EnableRaisingEvents = false;
                archiveFolderWatcher.Created -= OnArchiveFileChanged;
                archiveFolderWatcher.Deleted -= OnArchiveFileChanged;
                archiveFolderWatcher.Renamed -= OnArchiveFileRenamed;
                archiveFolderWatcher.Changed -= OnArchiveFileChanged;
                archiveFolderWatcher.Error -= OnArchiveWatcherError;
                archiveFolderWatcher.Dispose();
                archiveFolderWatcher = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    StopWatchingArchivesFolder();
                    loadArchivesCts?.Cancel();
                    loadArchivesCts?.Dispose();
                    nexusDownloads.CancelAll();
                    archiveSearchDebounce.Dispose();

                    foreach (var gate in archiveAnalysisGates.Values)
                    {
                        gate.Dispose();
                    }
                    archiveAnalysisGates.Clear();
                }
                disposed = true;
            }
        }

        ~InstallViewModel()
        {
            Dispose(false);
        }

        // A file BEM is still assembling is not an archive to inspect. The extension test already
        // skips the .part itself; this also skips the folder a Nexus download is built in.
        private static bool IsStillBeingWritten(string fullPath) =>
            fullPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(NxmDownload.StagingFolderName, StringComparison.OrdinalIgnoreCase));

        private void OnArchiveFileChanged(object sender, FileSystemEventArgs e)
        {
            var extension = Path.GetExtension(e.FullPath);
            if (!IsZipArchive(extension) && !IsSevenZipArchive(extension))
            {
                return;
            }

            // An archive moved into ARCHIVED or INSTALLED raises Created there under a recursive watch,
            // and must not come back as one waiting to install. Its Deleted at the old path still arrives.
            if (e.ChangeType != WatcherChangeTypes.Deleted && ArchiveFolders.IsSetAside(e.FullPath, ArchivesFolderPath))
            {
                return;
            }

            if (IsStillBeingWritten(e.FullPath))
            {
                return;
            }

            LoggingService.Log($"File system event: {e.ChangeType} on {e.FullPath}");

            App.AppWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    switch (e.ChangeType)
                    {
                        case WatcherChangeTypes.Created:
                            if (await WaitUntilSettledAsync(e.FullPath))
                                await HandleArchiveAddedAsync(e.FullPath);
                            break;

                        case WatcherChangeTypes.Deleted:
                            HandleArchiveRemoved(e.FullPath);
                            break;

                        case WatcherChangeTypes.Changed:
                            if (await WaitUntilSettledAsync(e.FullPath))
                                await HandleArchiveChangedAsync(e.FullPath);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = Strings.Current.Format("Install.ArchiveWatcherError", ex.Message);
                    LoggingService.LogException(ex, $"Error handling file system event {e.ChangeType} on {e.FullPath}");
                }
            });
        }

        // The watcher can lose events without any of the handlers above ever firing: its internal
        // buffer overflows when many files land at once (several downloads finishing together, or an
        // Explorer copy of a mod collection into the folder), and the watch itself dies if the folder
        // is moved or its drive disconnects. Both arrive here and nowhere else, and before this
        // handler existed both were dropped in silence - BEM's status still said "Watching..." while
        // it saw nothing, which is exactly a download that was observed to happen but never processed.
        // A missed event cannot be recovered, so the folder is re-read from disk to reconcile, and the
        // watcher is restarted in case it stopped raising events altogether.
        private void OnArchiveWatcherError(object sender, ErrorEventArgs e)
        {
            LoggingService.LogException(e.GetException(),
                "The archive folder watcher lost events (buffer overflow or a dead watch); rescanning to reconcile");

            App.AppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                RestartArchiveFolderWatcher();

                if (!string.IsNullOrWhiteSpace(ArchivesFolderPath) && Directory.Exists(ArchivesFolderPath))
                    _ = LoadArchivesFromFolderAsync(ArchivesFolderPath);
            });
        }

        private void OnArchiveFileRenamed(object sender, RenamedEventArgs e)
        {
            var oldExtension = Path.GetExtension(e.OldFullPath);
            var newExtension = Path.GetExtension(e.FullPath);

            if (IsStillBeingWritten(e.FullPath) && IsStillBeingWritten(e.OldFullPath))
            {
                return;
            }

            LoggingService.Log($"File system event: Renamed from {e.OldFullPath} to {e.FullPath}");

            App.AppWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (IsZipArchive(oldExtension) || IsSevenZipArchive(oldExtension))
                    {
                        HandleArchiveRemoved(e.OldFullPath);
                    }

                    if ((IsZipArchive(newExtension) || IsSevenZipArchive(newExtension))
                        && !ArchiveFolders.IsSetAside(e.FullPath, ArchivesFolderPath)
                        && !IsStillBeingWritten(e.FullPath)
                        && await WaitUntilSettledAsync(e.FullPath))
                    {
                        await HandleArchiveAddedAsync(e.FullPath);
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = Strings.Current.Format("Install.ArchiveWatcherError", ex.Message);
                    LoggingService.LogException(ex, $"Error handling file rename from {e.OldFullPath} to {e.FullPath}");
                }
            });
        }

        // The watcher fires the moment a file appears, and whoever is writing it is usually still
        // writing. Opening it then races a browser download, an Explorer copy and BEM's own Nexus
        // download alike, and the loser sees "the process cannot access the file". Waiting until the
        // size has stopped moving and the file opens for reading is what makes that safe.
        // One look at an archive being written: Verdict is set when the wait is over (true: settled and
        // readable, false: gone or refused), Length when the file is still changing or locked.
        private static (bool? Verdict, long? Length) ProbeSettling(string filePath, long previousLength)
        {
            var info = new FileInfo(filePath);

            if (!info.Exists)
                return (false, null);

            if (info.Length == previousLength && info.Length > 0)
            {
                try
                {
                    using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return (true, null);
                }
                catch (IOException)
                {
                    return (null, null);
                }
                catch (UnauthorizedAccessException)
                {
                    return (false, null);
                }
            }

            return (null, info.Length);
        }

        private async Task<bool> WaitUntilSettledAsync(string filePath)
        {
            if (!settlingArchives.TryAdd(filePath, 0))
                return false;

            try
            {
                long previousLength = -1;

                for (var attempt = 0; attempt < SettleAttempts; attempt++)
                {
                    await Task.Delay(SettleDelayMs);

                    // The stat and the open probe wait on the disk and on whatever still holds the
                    // file (a browser finishing a download), so they run off the UI thread: the dump of
                    // 2026-11-21 14:33 caught the UI thread in this FileStream open.
                    var probed = await Task.Run(() => ProbeSettling(filePath, previousLength));

                    if (probed.Verdict is { } verdict)
                        return verdict;

                    if (probed.Length is { } length)
                        previousLength = length;
                }

                LoggingService.Log($"Gave up waiting for {filePath} to stop changing", LogLevel.Warn);
                return false;
            }
            finally
            {
                settlingArchives.TryRemove(filePath, out _);
            }
        }

        private async Task HandleArchiveAddedAsync(string filePath)
        {
            LoggingService.Log($"Adding archive: {filePath}");
            if (!File.Exists(filePath))
            {
                LoggingService.Log($"File does not exist (anymore): {filePath}", LogLevel.Warn);
                return;
            }

            // Wait for global concurrency limit
            await globalAnalysisSemaphore.WaitAsync();
            
            var gate = archiveAnalysisGates.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                StatusMessage = Strings.Current.Format("Install.Analyzing", Path.GetFileName(filePath));

                await WaitForFileStableAsync(filePath, stableFor: TimeSpan.FromSeconds(1), timeout: TimeSpan.FromSeconds(30));
                if (!File.Exists(filePath))
                {
                    return;
                }

                var existing = Archives.FirstOrDefault(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    return; // Already added
                }

                var placeholder = new ModArchiveEntry(Path.GetFileName(filePath), filePath, Strings.Current["Install.AnalyzingPlaceholder"], []);
                Archives.Add(placeholder);
                RefreshFilteredArchives();

                ModArchiveEntry entry;
                try
                {
                    entry = await AnalyzeArchiveAsync(filePath, EnsureModuleDataContext());
                }
                catch (Exception ex)
                {
                    entry = new ModArchiveEntry(Path.GetFileName(filePath), filePath, Strings.Current.Format("Install.Archive.ScanError", ex.Message), []);
                }

                entry.IsSelected = placeholder.IsSelected;
                var index = Archives.IndexOf(placeholder);
                if (index >= 0)
                {
                    Archives[index] = entry;
                }
                else
                {
                    Archives.Add(entry);
                }

                RefreshFilteredArchives();
                StatusMessage = Strings.Current.Format("Install.Added", Path.GetFileName(filePath));
            }
            catch (TimeoutException)
            {
                StatusMessage = Strings.Current.Format("Install.TimedOutWaiting", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Install.FailedToAnalyze", Path.GetFileName(filePath), ex.Message);
            }
            finally
            {
                gate.Release();
                globalAnalysisSemaphore.Release();
            }
        }

        private void HandleArchiveRemoved(string filePath)
        {
            var existing = Archives.FirstOrDefault(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Archives.Remove(existing);
                RefreshFilteredArchives();
                StatusMessage = Strings.Current.Format("Install.Removed", Path.GetFileName(filePath));
            }
        }

        private async Task HandleArchiveChangedAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                HandleArchiveRemoved(filePath);
                return;
            }

            // Wait for global concurrency limit
            await globalAnalysisSemaphore.WaitAsync();
            
            var gate = archiveAnalysisGates.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                StatusMessage = Strings.Current.Format("Install.ReAnalyzing", Path.GetFileName(filePath));

                await WaitForFileStableAsync(filePath, stableFor: TimeSpan.FromSeconds(1), timeout: TimeSpan.FromSeconds(30));
                if (!File.Exists(filePath))
                {
                    HandleArchiveRemoved(filePath);
                    return;
                }

                var existing = Archives.FirstOrDefault(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    return;
                }

                existing.Status = Strings.Current["Install.AnalyzingPlaceholder"];

                ModArchiveEntry entry;
                try
                {
                    entry = await AnalyzeArchiveAsync(filePath, EnsureModuleDataContext());
                }
                catch (Exception ex)
                {
                    entry = new ModArchiveEntry(Path.GetFileName(filePath), filePath, Strings.Current.Format("Install.Archive.ScanError", ex.Message), []);
                }

                entry.IsSelected = existing.IsSelected;
                var index = Archives.IndexOf(existing);
                Archives[index] = entry;
                RefreshFilteredArchives();
                StatusMessage = Strings.Current.Format("Install.Updated", Path.GetFileName(filePath));
            }
            catch (TimeoutException)
            {
                StatusMessage = Strings.Current.Format("Install.TimedOutWaiting", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Install.FailedToAnalyze", Path.GetFileName(filePath), ex.Message);
            }
            finally
            {
                gate.Release();
                globalAnalysisSemaphore.Release();
            }
        }

        private void LoadCachedSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        GameInstallPath = settings.GameInstallPath;      
                        ArchivesFolderPath = settings.ArchivesFolderPath;
                        DeleteArchivesAfterInstall = settings.DeleteArchivesAfterInstall;
                        MoveKeptArchivesToInstalled = settings.MoveKeptArchivesToInstalled ?? true;
                        SearchArchivesRecursively = settings.SearchArchivesRecursively;
                        ReplaceExistingModuleFolders = settings.ReplaceExistingModuleFolders ?? true;
                        InstallSelectedOnly = settings.InstallSelectedOnly;
                        IncludeOptionalCollectionMods = settings.IncludeOptionalCollectionMods;
                        FollowCuratorUpdateChoice = settings.FollowCuratorUpdateChoice ?? true;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to load settings.json; defaults are in use");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new AppSettings
                {
                    GameInstallPath = GameInstallPath,
                    ArchivesFolderPath = ArchivesFolderPath,
                    DeleteArchivesAfterInstall = DeleteArchivesAfterInstall,
                    MoveKeptArchivesToInstalled = MoveKeptArchivesToInstalled,
                    SearchArchivesRecursively = SearchArchivesRecursively,
                    ReplaceExistingModuleFolders = ReplaceExistingModuleFolders,
                    InstallSelectedOnly = InstallSelectedOnly,
                    IncludeOptionalCollectionMods = IncludeOptionalCollectionMods,
                    FollowCuratorUpdateChoice = FollowCuratorUpdateChoice
                };

                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
                var directory = Path.GetDirectoryName(SettingsFilePath);        
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to save settings.json; this change will not survive a restart");
            }
        }

        private bool CanRunCommands() => !IsBusy;

        [RelayCommand]
        private void CheckAllArchives()
        {
            foreach (var archive in Archives)
            {
                archive.IsSelected = true;
            }
        }

        [RelayCommand]
        private void UncheckAllArchives()
        {
            foreach (var archive in Archives)
            {
                archive.IsSelected = false;
            }
        }

        [RelayCommand]
        private void CheckMatchingSearch()
        {
            var query = ArchiveSearchText?.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                // If no search text, check all
                CheckAllArchives();
                return;
            }

            foreach (var archive in Archives)
            {
                archive.IsSelected = ArchiveMatchesSearch(archive, query);
            }
        }

        [RelayCommand(CanExecute = nameof(CanRunCommands))]
        private async Task BrowseInstallFolderAsync()
        {
            var picked = await FolderPick.PickAsync();
            if (picked.Error is not null)
            {
                StatusMessage = Strings.Current["Install.UnableToOpenFolderPicker"];
                return;
            }

            if (picked.Path is null)
            {
                StatusMessage = Strings.Current["Install.InstallFolderSelectionCanceled"];
                return;
            }

            GameInstallPath = picked.Path;
            StatusMessage = Strings.Current["Install.InstallFolderSet"];
        }

        // A tester did not expect to remember the archives path a month later, so Settings opens it.
        [RelayCommand]
        private void OpenArchivesFolder()
        {
            if (string.IsNullOrWhiteSpace(ArchivesFolderPath) || !Directory.Exists(ArchivesFolderPath))
            {
                StatusMessage = Strings.Current["Settings.OpenArchivesFolderButton.Missing"];
                return;
            }

            Start(new ProcessStartInfo("explorer.exe", $"\"{ArchivesFolderPath}\"") { UseShellExecute = false },
                Strings.Current.Format("Install.Opened", ArchivesFolderPath));
        }

        [RelayCommand(CanExecute = nameof(CanRunCommands))]
        private async Task BrowseArchivesFolderAsync()
        {
            var picked = await FolderPick.PickAsync();
            if (picked.Error is not null)
            {
                StatusMessage = Strings.Current["Install.UnableToOpenArchiveFolderPicker"];
                return;
            }

            if (picked.Path is null)
            {
                StatusMessage = Strings.Current["Install.ArchiveFolderSelectionCanceled"];
                return;
            }

            ArchivesFolderPath = picked.Path;
            StatusMessage = Strings.Current["Install.ArchiveFolderSetMonitoring"];
        }

        // What happens when the user clicks Mod Manager Download on a Bannerlord mod page. It runs
        // here rather than behind a dialog so the archive list, the folder box and the rest of the
        // page stay usable while several hundred megabytes come down.
        //
        // The token is the download's own, not the page's. One token for every download made two
        // independent activities one: a link the user clicked themselves during a guided BUTR Stack walk
        // canceled the walk's transfer, and the walk then reported that mod as failed without having
        // attempted it. cancellationToken is what the activity that started this download stops it
        // with, which for the walk is the walk's own token.
        public async Task<NxmDownloadResult> DownloadFromNexusAsync(
            NxmLink link, CancellationToken cancellationToken = default)
        {
            var item = new DownloadItemViewModel(DownloadName(link), DismissDownload, MoveDownload);
            Downloads.Add(item);
            IsNexusKeyNeeded = false;
            RaiseDownloadsChanged();

            // The progress is made here, on the page's own thread, so what it reports lands where the row
            // is bound; the queue runs the transfer itself on another.
            var job = new QueuedDownload(link, item, new Progress<NxmDownloadProgress>(item.Report));
            var queued = downloadQueue.Enqueue(item.Name, cancellationToken, job);

            item.Attach(downloadQueue, queued);

            return await queued.Finished
                ?? new NxmDownloadResult(NxmDownloadStatus.Canceled, Strings.Current["Install.Nexus.DownloadCanceled"]);
        }

        private sealed record QueuedDownload(NxmLink Link, DownloadItemViewModel Row, IProgress<NxmDownloadProgress> Progress);

        // The queue's runner, on a worker thread. It owns the transfer; everything the page shows or
        // changes afterward is done on the page's thread. A stop is thrown out of here, because only the
        // queue knows whether it was a pause or a cancel.
        private async Task<NxmDownloadResult> RunQueuedDownloadAsync(
            DownloadQueueItem<NxmDownloadResult> queued, CancellationToken stopped)
        {
            var job = (QueuedDownload)queued.Tag!;
            using var download = nexusDownloads.Start(stopped);

            NxmDownloadResult result;

            try
            {
                result = await NxmLinks.DownloadAsync(job.Link, job.Progress, download.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "The Nexus download failed");
                result = new NxmDownloadResult(NxmDownloadStatus.Failed, Strings.Current.Format("Install.Nexus.DownloadFailed", ex.Message));
            }

            if (result.Status == NxmDownloadStatus.Canceled)
                download.Token.ThrowIfCancellationRequested();

            await OnPageThreadAsync(async () =>
            {
                IsNexusKeyNeeded = result.Status == NxmDownloadStatus.NoApiKey;

                job.Row.Finish(result.Status == NxmDownloadStatus.Downloaded, TitleFor(result.Status), result.Message);

                // The link states the mod id and the file id outright, which no Nexus filename grammar
                // is obliged to carry. Written down now so the install that follows knows them even if
                // the file arrived under a name nothing can be parsed out of.
                RecordDownloadProvenance(job.Link, result);
                NotifyDownload(job.Row.Name, result);

                // Nothing is installed. The folder watcher normally notices the archive on its own; the
                // refresh is here so the list is right even when the watcher is not running.
                if (result.Status == NxmDownloadStatus.Downloaded
                    && !string.IsNullOrWhiteSpace(ArchivesFolderPath)
                    && Directory.Exists(ArchivesFolderPath))
                    await LoadArchivesFromFolderAsync(ArchivesFolderPath);
            });

            return result;
        }

        private static Task OnPageThreadAsync(Func<Task> work)
        {
            var dispatcher = App.AppWindow?.DispatcherQueue;

            if (dispatcher is null || dispatcher.HasThreadAccess)
                return work();

            var done = new TaskCompletionSource();

            if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        await work();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }))
                return work();

            return done.Task;
        }

        private void OnDownloadQueueChanged() => _ = OnPageThreadAsync(() =>
        {
            foreach (var row in Downloads)
                row.Refresh();

            RaiseDownloadsChanged();
            return Task.CompletedTask;
        });

        // Moves a waiting row one place within the queue and shows the queue's order.
        private void MoveDownload(DownloadItemViewModel row, int direction)
        {
            if (row.QueueItem is not { } queued || !downloadQueue.Move(queued, direction))
                return;

            var order = downloadQueue.Items.ToList();

            foreach (var target in order)
            {
                var shown = Downloads.FirstOrDefault(download => ReferenceEquals(download.QueueItem, target));

                if (shown is not null)
                    Downloads.Move(Downloads.IndexOf(shown), Math.Min(order.IndexOf(target), Downloads.Count - 1));
            }
        }

        private void RecordDownloadProvenance(NxmLink link, NxmDownloadResult result)
        {
            if (result.Path is not { Length: > 0 } path || link.ModId is not { } modId)
                return;

            try
            {
                archiveProvenance.Record(new ArchiveIdentity(
                    Path.GetFileName(path),
                    NexusModId: modId,
                    NexusFileId: link.FileId,
                    ObservedUtc: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to record where a Nexus download came from");
            }
        }

        // The autonomous counterpart to the overload above: an update target states its mod id and file
        // id outright too, because ModUpdatePlanner picked this exact file off Nexus's own published
        // list, so there is nothing to look up that is not already in hand.
        private void RecordDownloadProvenance(string archivePath, ModUpdateTarget target)
        {
            try
            {
                archiveProvenance.Record(new ArchiveIdentity(
                    Path.GetFileName(archivePath),
                    NexusModId: target.NexusModId,
                    NexusFileId: target.FileId,
                    NexusFileName: target.FileName,
                    NexusVersion: target.Version,
                    ObservedUtc: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to record where a Nexus update download came from");
            }
        }

        private static string DownloadName(NxmLink link) =>
            link.ModId is { } mod
                ? Strings.Current.Format("Install.Downloads.ItemName", mod, link.FileId?.ToString(CultureInfo.InvariantCulture) ?? "?")
                : Strings.Current["Install.Downloads.ItemNameUnknown"];

        // One heading per outcome. Two outcomes sharing a heading is how "it did nothing" and "it
        // refused" end up looking the same to the person reading it.
        private static string TitleFor(NxmDownloadStatus status) => status switch
        {
            NxmDownloadStatus.Downloaded => Strings.Current["Install.Nexus.Title.Downloaded"],
            NxmDownloadStatus.DownloadedButNotPlaced => Strings.Current["Install.Nexus.Title.DownloadedButNotPlaced"],
            NxmDownloadStatus.Forwarded => Strings.Current["Install.Nexus.Title.Forwarded"],
            NxmDownloadStatus.NoHandlerToForwardTo => Strings.Current["Install.Nexus.Title.NoHandlerToForwardTo"],
            NxmDownloadStatus.NotOurLink => Strings.Current["Install.Nexus.Title.NotOurLink"],
            NxmDownloadStatus.NoApiKey => Strings.Current["Install.Nexus.Title.NoApiKey"],
            NxmDownloadStatus.NoDestinationFolder => Strings.Current["Install.Nexus.Title.NoDestinationFolder"],
            NxmDownloadStatus.Expired => Strings.Current["Install.Nexus.Title.Expired"],
            NxmDownloadStatus.Refused => Strings.Current["Install.Nexus.Title.Refused"],
            NxmDownloadStatus.Unreachable => Strings.Current["Install.Nexus.Title.Unreachable"],
            NxmDownloadStatus.Canceled => Strings.Current["Install.Nexus.Title.Canceled"],
            _ => Strings.Current["Install.Nexus.Title.Failed"]
        };

        private async Task LoadArchivesFromFolderAsync(string folderPath)
        {
            LoggingService.Log($"Loading archives from: {folderPath}");
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                LoggingService.Log("Invalid folder path or folder does not exist.", LogLevel.Warn);
                return;
            }

            loadArchivesCts?.Cancel();
            loadArchivesCts = new CancellationTokenSource();
            var cancellationToken = loadArchivesCts.Token;

            // A rescan runs inside other work as well as on its own: the folder watcher fires during
            // an install, and a Nexus download that just landed an archive asks for one. So it takes
            // the page's busy flag only when nothing else holds it, and puts back what it found rather
            // than what it took: clearing another operation's flag is what let a BUTR Stack walk
            // unlock Install Selected halfway through itself.
            var pageWasAlreadyBusy = IsBusy;

            if (!pageWasAlreadyBusy)
            {
                IsBusy = true;
                IsProgressVisible = true;
                IsProgressIndeterminate = false;
                ProgressValue = 0;
            }

            try
            {
                StatusMessage = Strings.Current["Install.ScanningArchiveFolder"];

                var moduleContext = await EnsureModuleDataContextAsync();
                var searchOption = SearchArchivesRecursively ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                var files = await Task.Run(
                    () => Directory.EnumerateFiles(folderPath, "*", searchOption)
                        .Where(f => (IsZipArchive(Path.GetExtension(f)) || IsSevenZipArchive(Path.GetExtension(f)))
                            && !ArchiveFolders.IsSetAside(f, folderPath)
                            && !IsStillBeingWritten(f))
                        .ToList(),
                    cancellationToken);

                if (files.Count == 0)
                {
                    StatusMessage = Strings.Current["Install.NoArchivesFoundInFolder"];
                    return;
                }

                for (var i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = files[i];
                    StatusMessage = Strings.Current.Format("Install.ScanningProgress", i + 1, files.Count, Path.GetFileName(file));
                    ProgressValue = (i + 1) * 100 / files.Count;

                    var existing = Archives.FirstOrDefault(a => a.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        existing = new ModArchiveEntry(Path.GetFileName(file), file, Strings.Current["Install.AnalyzingPlaceholder"], []);
                        Archives.Add(existing);
                    }
                    else
                    {
                        existing.Status = Strings.Current["Install.AnalyzingPlaceholder"];
                    }

                    ModArchiveEntry entry;
                    try
                    {
                        entry = await AnalyzeArchiveAsync(file, moduleContext, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        entry = new ModArchiveEntry(Path.GetFileName(file), file, Strings.Current.Format("Install.Archive.ScanError", ex.Message), []);
                    }

                    entry.IsSelected = existing.IsSelected;
                    var index = Archives.IndexOf(existing);
                    if (index >= 0)
                    {
                        Archives[index] = entry;
                    }
                    else
                    {
                        Archives.Add(entry);
                    }

                    if (i % 5 == 0 || i == files.Count - 1)
                    {
                        RefreshFilteredArchives();
                    }
                }

                StatusMessage = Strings.Current.Plural("Install.LoadedMonitoring", files.Count);

                await BackfillArchiveLinksAsync();
            }
            catch (OperationCanceledException)
            {
                StatusMessage = Strings.Current["Install.ArchiveScanCanceled"];
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Install.ErrorLoadingArchives", ex.Message);
                LoggingService.LogException(ex, "Error loading archives from folder");
            }
            finally
            {
                if (!pageWasAlreadyBusy)
                {
                    IsBusy = false;
                    IsProgressVisible = false;
                    ProgressValue = 0;
                }
            }
        }

        // Archive links BEM can work out from what it already holds, run on the way in and after every
        // install so the free answers are there before anyone asks. Find Mod IDs runs the same step on
        // demand as the first link in its chain.
        private async Task BackfillArchiveLinksAsync()
        {
            if (string.IsNullOrWhiteSpace(GameInstallPath) || !Directory.Exists(GameInstallPath))
                return;

            try
            {
                var added = await LinkArchivesByFileNameAsync(GameInstallPath) + await LinkArchivesByNameCoreAsync(GameInstallPath, null);

                if (added > 0)
                    NexusIdsChanged.Raise(this);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to link installed modules to their archives by name");
            }
        }

        // Every archive whose own file name states its Nexus page, at any depth of the archives folder and
        // in Vortex's and MO2's download folders for this game: opened only when its name parses, and
        // recorded only for modules no archive disagrees about. Spends nothing.
        private async Task<int> LinkArchivesByFileNameAsync(string gamePath)
        {
            var roots = new List<string>();

            if (!string.IsNullOrWhiteSpace(ArchivesFolderPath) && Directory.Exists(ArchivesFolderPath))
                roots.Add(ArchivesFolderPath);

            roots.AddRange(ModManagerDetector.OtherManagerDownloadFolders(gamePath));

            try
            {
                var moduleContext = await EnsureModuleDataContextAsync();

                // What the page already lists is copied here, on the UI thread, so the sweep below can
                // run entirely on a worker: it used to resume on the UI thread after every await to scan
                // the list, sniff and open each archive, then read and rewrite the links file.
                var listedModules = Archives.ToDictionary(
                    archive => archive.FilePath,
                    archive => (IReadOnlyList<string>)[.. archive.Modules.Select(module => module.ModuleId).OfType<string>()],
                    StringComparer.OrdinalIgnoreCase);

                var recursePage = SearchArchivesRecursively;
                var archivesFolder = ArchivesFolderPath;

                return await Task.Run(async () =>
                {
                    var candidates = new List<SweepCandidate>();

                    foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                            {
                                RecurseSubdirectories = recursePage || !string.Equals(root, archivesFolder, StringComparison.OrdinalIgnoreCase), IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.ReparsePoint,
                            })
                            .Where(f => (IsZipArchive(Path.GetExtension(f)) || IsSevenZipArchive(Path.GetExtension(f)))
                                && NexusArchiveName.TryGetModId(f) is not null
                                && !Path.GetRelativePath(root, f).StartsWith(".bem-", StringComparison.OrdinalIgnoreCase)
                                && !IsStillBeingWritten(f))
                            .ToList();

                        foreach (var file in files)
                        {
                            var modules = listedModules.TryGetValue(file, out var known)
                                ? known
                                : [.. (await AnalyzeArchiveAsync(file, moduleContext)).Modules.Select(module => module.ModuleId).OfType<string>()];

                            candidates.Add(new SweepCandidate(file, Path.GetFileName(file), [.. modules]));
                        }
                    }

                    var links = ArchiveNameLinks.For(candidates, ArchiveLinks.Load(), DateTimeOffset.UtcNow);

                    return links.Count == 0 ? 0 : ArchiveLinks.Record(links).Added;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LoggingService.LogException(ex, "Failed to link archives to modules by their names");
                return 0;
            }
        }

        private List<ArchiveModuleCandidate> ArchiveCandidates() =>
        [
            .. Archives
                .Select(archive => new ArchiveModuleCandidate(
                    archive.FileName,
                    [.. archive.Modules.Select(module => module.ModuleId).OfType<string>()]))
                .Where(candidate => candidate.ModuleIds.Count > 0)
        ];

        // Find Mod IDs, step 1 (free): the archive names in the watched folder and in quarantine, and
        // BEM's own install log, for the modules still without an id.
        internal async Task<string?> LinkArchivesByNameAsync(string gamePath, IReadOnlyList<string> moduleIds)
        {
            var added = await LinkArchivesByFileNameAsync(gamePath) + await LinkArchivesByNameCoreAsync(gamePath, moduleIds);

            return Strings.Current.Plural("Install.DeletedArchives.RecordedNewLinks", added);
        }

        private async Task<int> LinkArchivesByNameCoreAsync(string gamePath, IReadOnlyList<string>? moduleIds)
        {
            var fromArchives = ArchiveCandidates();

            return await Task.Run(() =>
            {
                var scan = ModuleScanner.ScanAll(gamePath);

                if (scan.Failed)
                    return 0;

                var wanted = moduleIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);

                var community = scan.Modules
                    .Where(module => !module.IsOfficial && (wanted is null || wanted.Contains(module.Id.Value)))
                    .Select(module => module.Id.Value)
                    .ToList();

                if (community.Count == 0)
                    return 0;

                var backfill = ModuleArchiveRecovery.Recover(
                    [.. fromArchives, .. ModuleArchiveRecovery.FromQuarantine(
                        new QuarantineStore(QuarantineStore.DefaultRoot), ModuleScanner.GetModulesFolder(gamePath))],
                    community,
                    ArchiveLinks.Load(),
                    DateTimeOffset.UtcNow);

                var added = ArchiveLinks.Record(backfill.Recovered).Added;

                var fromLog = InstallLogArchiveLinks.Recover(
                    LoggingService.GetLogDirectory(),
                    ModuleScanner.GetModulesFolder(gamePath),
                    community,
                    ArchiveLinks.Load(),
                    DateTimeOffset.UtcNow);

                return added + ArchiveLinks.Record(fromLog.Recovered).Added;
            });
        }

        // Find Mod IDs, step 2 (free): an archive the owner deleted still carries the mod id in its
        // name while it sits in the Recycle Bin. Nothing here writes, moves or restores a file.
        internal Task<string?> ReadDeletedArchiveNamesAsync(string gamePath, IReadOnlyList<string> moduleIds) =>
            Task.Run<string?>(() => RecoverFromDeletedArchives(gamePath, moduleIds));

        private string RecoverFromDeletedArchives(string gamePath, IReadOnlyList<string> moduleIds)
        {
            try
            {
                var listing = deletedArchives.ReadNames();

                if (listing.Availability == DeletedArchiveAvailability.NotSupported)
                    return Strings.Current["Install.DeletedArchives.NoRecycleBin"];

                if (!listing.CouldRead)
                    return Strings.Current["Install.DeletedArchives.RecycleBinNotReadable"];

                var scan = ModuleScanner.ScanAll(gamePath);

                if (scan.Failed)
                    return Strings.Current.Format("Install.DeletedArchives.ModulesCouldNotBeRead", scan.Error);

                var wanted = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var community = scan.Modules.Where(module => !module.IsOfficial && wanted.Contains(module.Id.Value)).ToList();

                var claims = DeletedArchiveMatching.Match(listing, community);

                var recovery = ModuleArchiveRecovery.Recover(
                    claims.Candidates,
                    [.. community.Select(module => module.Id.Value)],
                    ArchiveLinks.Load(),
                    DateTimeOffset.UtcNow,
                    ModuleArchiveEvidence.RecycleBin);

                var written = ArchiveLinks.Record(recovery.Recovered);

                return Strings.Current.Plural("Install.DeletedArchives.RecordedNewLinks", written.Added);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to recover archive links from deleted archives");

                return Strings.Current["Install.DeletedArchives.CouldNotBeRead"];
            }
        }

        // BEM names its own downloads nexus-{modId}-{fileId}, which carries an id with certainty and
        // no mod name at all, so nothing in the filename says which module it installed. Those ids
        // are still readable in the Recycle Bin, and BUTR's index is what turns them into a module.
        internal IReadOnlyList<int> DownloadedModIdsWithNoModule()
        {
            try
            {
                var listing = deletedArchives.ReadNames();

                if (!listing.CouldRead)
                    return [];

                return
                [
                    .. listing.FileNames
                        .Where(name => NexusArchiveName.TryGetModName(name) is null)
                        .Select(NexusArchiveName.TryGetModId)
                        .OfType<int>()
                        .Distinct()
                ];
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read deleted archive names for mod id attribution");
                return [];
            }
        }

        // A bare mod id asks the owner to judge a number, and for a module several pages publish it
        // asks them to judge several. Nexus is asked what each candidate page is called so the choice
        // is between names. A page already in the ledger costs nothing.
        internal static async Task<IReadOnlyDictionary<int, string>> LearnCandidateModNamesAsync(
            NexusApiKey key, IReadOnlyList<int> candidates, CancellationToken token)
        {
            if (candidates.Count == 0)
                return new Dictionary<int, string>();

            var cache = new NexusUpdateCache(Path.Combine(NexusApiKeyStore.DefaultDirectory, NexusUpdateCache.FileName));

            var sweep = new NexusVersionSweep(
                new NexusClient(new NexusHttpTransport()),
                NexusVersionStore.Beside(cache));

            var request = new NexusUpdateRequest([], key, true, NexusUpdatePeriod.Week, TimeSpan.Zero, DateTimeOffset.UtcNow);

            var names = await sweep.LearnNamesAsync(
                key,
                [.. candidates.Distinct()],
                request.VersionLookupLimit,
                request.RateLimitReserve,
                NexusRateLimit.Unknown,
                DateTimeOffset.UtcNow,
                token);

            return names.Names;
        }

        // Find Mod IDs, step 4: an archive in the watched folder BEM never installed. Its hash names
        // its own mod page and the SubModule.xml inside it names the module, so both ends are evidence.
        internal IReadOnlyList<SweepCandidate> UnlinkedArchiveCandidates(IReadOnlyList<string> moduleIds)
        {
            var wanted = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A SubModule.xml with no Id element leaves DetectedModule.ModuleId null, and a null here
            // reaches a case-insensitive hash lookup inside Worth, which throws on it.
            return
            [
                .. UnlinkedArchiveSweep.Worth(
                        Archives.Select(archive => new SweepCandidate(
                            archive.FilePath,
                            archive.FileName,
                            [.. archive.Modules.Select(module => module.ModuleId).OfType<string>()])),
                        ArchiveLinks.Load())
                    .Where(candidate => candidate.ModuleIds.Any(wanted.Contains))
            ];
        }

        internal async Task<string?> IdentifyUnlinkedArchivesAsync(NexusApiKey key, IReadOnlyList<string> moduleIds, CancellationToken token)
        {
            var candidates = UnlinkedArchiveCandidates(moduleIds);

            if (candidates.Count == 0)
                return Strings.Current["Install.Nexus.EveryArchiveAlreadyLinked"];

            var hashed = await Task.Run(() =>
                candidates
                    .Select(candidate => (candidate, md5: ArchiveIdentityReader.TryHash(candidate.ArchivePath)))
                    .Where(pair => pair.md5 is { Length: > 0 })
                    .SelectMany(pair => UnlinkedArchiveSweep.LinksFor(pair.candidate, pair.md5!, DateTimeOffset.UtcNow))
                    .ToList(), token);

            if (hashed.Count == 0)
                return Strings.Current["Install.Nexus.NoneCouldBeRead"];

            return await IdentifyAsync(hashed, key, token);
        }

        // Find Mod IDs, steps 5 and 6: archives BEM recorded a hash for. Step 5 asks only about hashes
        // Nexus has not already refused; step 6 forgets those refusals for the modules still unsolved
        // and asks again.
        internal FileIdentificationCandidates FileHashCandidates(IReadOnlyList<string> moduleIds)
        {
            var wanted = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return NexusFileIdentification.Select(
                ArchiveLinks.Load().Where(link => wanted.Contains(link.ModuleId)),
                NexusVersionStore.Default.Load().FilesByModId(),
                unrecognizedHashes.Remembered(DateTimeOffset.UtcNow));
        }

        internal async Task<string?> IdentifyByFileHashAsync(NexusApiKey key, IReadOnlyList<string> moduleIds, CancellationToken token)
        {
            var selection = FileHashCandidates(moduleIds);

            if (selection.Askable.Count == 0)
            {
                return selection.AnsweredFromCache.Count > 0
                    ? Strings.Current.Plural("Install.Nexus.EverythingAlreadyAnswered", selection.AnsweredFromCache.Count)
                    : Strings.Current["Install.NothingWasSentToNexus"];
            }

            return await IdentifyAsync(selection.Askable, key, token);
        }

        internal async Task<string?> AskAgainAboutUnrecognizedAsync(NexusApiKey key, IReadOnlyList<string> moduleIds, CancellationToken token)
        {
            var refused = FileHashCandidates(moduleIds).Unrecognized;

            if (refused.Count == 0)
                return Strings.Current["Install.NothingWasSentToNexus"];

            unrecognizedHashes.Forget(refused.Select(link => link.ArchiveMd5!));

            return await IdentifyAsync(refused, key, token);
        }

        private async Task<string> IdentifyAsync(IReadOnlyList<ModuleArchiveLink> candidates, NexusApiKey key, CancellationToken token)
        {
            var outcome = await new NexusFileIdentification(new NexusClient(new NexusHttpTransport()))
                .RunAsync(candidates, key, FileIdentificationsPerPress, token);

            var written = ArchiveLinks.Record(outcome.Learned);

            // The same bytes get the same answer whichever step asked, so a 404 collected here is one
            // no later step has to spend a request on.
            unrecognizedHashes.Record(outcome.Unrecognized, DateTimeOffset.UtcNow);

            return $"{outcome.Message} {Strings.Current.Plural("Install.Nexus.WroteDownFor", written.Replaced + written.Added)}";
        }

        internal static int FileIdentificationLimit => FileIdentificationsPerPress;

        // Recording a mod id was reachable and taking one back was not, so the ids BEM holds are listed
        // where Find Mod IDs is, and refreshed whenever any route writes one.
        // Start-up form of the two lists: the files are read on a worker and only the rows are put in
        // on the UI thread.
        private async Task ListNexusIdsFromDiskAsync()
        {
            try
            {
                var (confirmed, rejected) = await Task.Run(() => (confirmedNexusIds.Load(), rejectedNexusIds.Load()));

                if (disposed)
                    return;

                ListConfirmedNexusIds(confirmed);
                ListRejectedNexusIds(rejected);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to list the recorded Nexus mod ids");
            }
        }

        internal void ListConfirmedNexusIds(IReadOnlyList<LearnedNexusId>? preloaded = null)
        {
            var reading = SelectedConfirmedNexusId?.ModuleId;

            ConfirmedNexusIds.Clear();

            foreach (var learned in (preloaded ?? confirmedNexusIds.Load()).OrderBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase))
                ConfirmedNexusIds.Add(new ConfirmedNexusIdRow(learned));

            SelectedConfirmedNexusId = reading is null
                ? null
                : ConfirmedNexusIds.FirstOrDefault(row => row.ModuleId.Equals(reading, StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(HasConfirmedNexusIds));
        }

        internal void ListRejectedNexusIds(IReadOnlyList<RejectedNexusId>? preloaded = null)
        {
            var reading = SelectedRejectedNexusId?.Headline;

            RejectedNexusIds.Clear();

            foreach (var rejected in (preloaded ?? rejectedNexusIds.Load())
                .OrderBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.NexusModId))
            {
                RejectedNexusIds.Add(new RejectedNexusIdRow(rejected));
            }

            SelectedRejectedNexusId = reading is null
                ? null
                : RejectedNexusIds.FirstOrDefault(row => row.Headline == reading);

            OnPropertyChanged(nameof(HasRejectedNexusIds));
        }

        // Rejecting a page is a decision BEM keeps and acts on, so it opens both ways: without this, a
        // page turned down by mistake is unproposable forever and the list beside it can only say so.
        [RelayCommand(CanExecute = nameof(HasSelectedRejectedNexusId))]
        private void RestoreRejectedNexusId()
        {
            if (SelectedRejectedNexusId is not { } row)
                return;

            ConfirmedNexusIdSummary = rejectedNexusIds
                .Forget([new RejectedNexusId(row.ModuleId, row.NexusModId, DateTimeOffset.UtcNow)])
                .Describe();

            ListRejectedNexusIds();
        }

        [RelayCommand(CanExecute = nameof(HasSelectedConfirmedNexusId))]
        private void OpenConfirmedNexusIdPage()
        {
            if (SelectedConfirmedNexusId is { } row)
                Start(new ProcessStartInfo(row.ModPageUrl) { UseShellExecute = true }, Strings.Current.Format("Install.Opened", row.ModPageUrl));
        }

        // The row selects on click, so its text cannot offer inline selection without swallowing it.
        // The sentence that persuaded the user is exactly what gets pasted into a bug report.
        [RelayCommand(CanExecute = nameof(HasSelectedConfirmedNexusId))]
        private void CopyConfirmedNexusIdRow()
        {
            if (SelectedConfirmedNexusId is { } row)
            {
                CopyToClipboard(
                    $"{row.Headline}{Environment.NewLine}{row.Basis}",
                    Strings.Current["Install.CopiedConfirmationLines"]);
            }
        }

        [RelayCommand(CanExecute = nameof(HasSelectedConfirmedNexusId))]
        private void CopyConfirmedNexusIdPageUrl()
        {
            if (SelectedConfirmedNexusId is { } row)
                CopyToClipboard(row.ModPageUrl, Strings.Current.Format("Install.CopiedToClipboard", row.ModPageUrl));
        }

        private bool HasSelectedConfirmedNexusId => SelectedConfirmedNexusId is not null;

        private static string? InstalledModuleId(string moduleFolderPath) =>
            SubModuleXmlParser.TryRecoverDeclaredId(Path.Combine(moduleFolderPath, "SubModule.xml"));

        private static string? InstalledModuleVersion(string moduleFolderPath) =>
            SubModuleXmlParser.TryLoad(Path.Combine(moduleFolderPath, "SubModule.xml"), out var manifest, out _)
                ? manifest.VersionText ?? manifest.Version.ToString()
                : null;

        // Everything identifying about the archive, read at the one moment it exists: most users have
        // delete-after-install on, and a manual download has nothing else left behind it. A manual
        // archive is read exactly as a nxm download is, and what the nxm link stated outright is
        // merged on top when there is a record of it.
        //
        // The link is a hint and nothing depends on it, so a failure to write one never touches the
        // install that just succeeded.
        private async Task RecordArchiveLinksAsync(
            string archivePath,
            IReadOnlyList<(string ModuleId, string FolderPath)> installedModules)
        {
            if (installedModules.Count == 0)
                return;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var fileName = Path.GetFileName(archivePath);

                var links = await Task.Run(() =>
                {
                    var identity = ArchiveIdentityReader.Read(archivePath, now).Fill(archiveProvenance.Find(fileName));

                    return installedModules
                        .DistinctBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
                        .Select(module => new ModuleArchiveLink(module.ModuleId, fileName, now)
                            .With(identity) with
                            {
                                ModuleVersion = InstalledModuleVersion(module.FolderPath),
                                InstalledFingerprint = InstalledModuleContents.Read(module.FolderPath)?.ToString()
                            })
                        .ToList();
                });

                InstallArchiveLinks.Record(links);
                NexusIdsChanged.Raise(this);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex,
                    $"Failed to record which archive installed {string.Join(", ", installedModules.Select(module => module.ModuleId))}");
            }
        }

        // The search box raises a change per keystroke, so the visible list is rebuilt only once the
        // typist pauses; a later keystroke supersedes the pending rebuild instead of adding a second
        // one beside it. Nothing here can be awaited by the property-changed callback that starts it,
        // so the wait is detached and its fault is logged rather than lost.
        private void RefreshFilteredArchivesAfterSearchPause() =>
            DetachedWork.Start(
                RefreshFilteredArchivesWhenSettledAsync(),
                "Failed to refresh the filtered archive list after a search change",
                LoggingService.LogException);

        private async Task RefreshFilteredArchivesWhenSettledAsync()
        {
            if (await archiveSearchDebounce.RequestAsync())
                RefreshFilteredArchives();
        }

        private void RefreshFilteredArchives()
        {
            App.AppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    using var tag = UiStallMonitor.Op("Install: refresh filtered archive list");

                    // Applied as edits: clearing and re-adding every row raised one change per row for
                    // the bound list to realize, on every fifth archive of a scan.
                    var query = ArchiveSearchText?.Trim();
                    var wanted = string.IsNullOrWhiteSpace(query)
                        ? Archives.ToList()
                        : Archives.Where(archive => ArchiveMatchesSearch(archive, query)).ToList();

                    ListSync.Apply(FilteredArchives, wanted);
                }
                catch (Exception ex)
                {
                    LoggingService.LogException(ex, "Error refreshing filtered archives");
                }
            });
        }

        private static bool ArchiveMatchesSearch(ModArchiveEntry entry, string query)
        {
            if (entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entry.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var module in entry.Modules)
            {
                if (!string.IsNullOrWhiteSpace(module.ModuleName)
                    && module.ModuleName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(module.InstallFolderName)
                    && module.InstallFolderName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task WaitForFileStableAsync(string filePath, TimeSpan stableFor, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            long lastSize = -1;
            DateTime lastWrite = DateTime.MinValue;
            DateTime stableSince = DateTime.MinValue;

            while (sw.Elapsed < timeout)
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(filePath);
                }
                catch
                {
                    await Task.Delay(250);
                    continue;
                }

                var size = info.Length;
                var write = info.LastWriteTimeUtc;

                if (size == lastSize && write == lastWrite)
                {
                    stableSince = stableSince == DateTime.MinValue ? DateTime.UtcNow : stableSince;
                    if (DateTime.UtcNow - stableSince >= stableFor)
                    {
                        return;
                    }
                }
                else
                {
                    stableSince = DateTime.MinValue;
                    lastSize = size;
                    lastWrite = write;
                }

                await Task.Delay(250);
            }
        }

        [RelayCommand(CanExecute = nameof(CanRunCommands))]
        private async Task InstallModsAsync()
        {
            LoggingService.Log("Starting mod installation process.");
            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();
            var swTotal = Stopwatch.StartNew();
            if (Archives.Count == 0)
            {
                StatusMessage = Strings.Current["Install.AddAtLeastOneArchive"];
                return;
            }

            if (InstallDestinations.Count == 0 && (string.IsNullOrWhiteSpace(GameInstallPath) || !Directory.Exists(GameInstallPath)))
            {
                StatusMessage = Strings.Current["Install.SelectInstallFolder"];
                return;
            }

            if (ChosenDestinations().Count == 0)
            {
                StatusMessage = Strings.Current["Install.Destinations.NoneSelected"];
                return;
            }

            ClearInstallResults();
            IsBusy = true;
            IsProgressVisible = true;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            var installedArchivePaths = new List<string>();
            var installedArchives = new List<ModArchiveEntry>();
            try
            {
                var targets = InstallSelectedOnly
                    ? Archives.Where(a => a.IsSelected).ToList()
                    : Archives.ToList();

                if (targets.Count == 0)
                {
                    StatusMessage = Strings.Current["Install.SelectAtLeastOneArchive"];
                    return;
                }

                var safety = await CheckArchiveSafetyAsync(targets);

                if (!safety.Proceed)
                {
                    StatusMessage = Strings.Current["Install.StoppedTrojanized"];
                    return;
                }

                foreach (var skipped in safety.Skip)
                    skipped.Status = Strings.Current["Install.SkippedTrojanized"];

                targets = [.. targets.Except(safety.Skip)];

                if (targets.Count == 0)
                {
                    StatusMessage = Strings.Current["Install.InstalledNothingTrojanized"];
                    return;
                }

                if (!await ConfirmOwnedFilesAsync(targets))
                {
                    StatusMessage = Strings.Current["Install.Ownership.Stopped"];
                    return;
                }

                var run = await PrepareInstallRunAsync(targets);
                var refusal = ShowRunNotes(run);

                if (!run.HasDestination)
                {
                    StatusMessage = refusal;
                    return;
                }

                var extractionProgress = new Progress<int>(ReportInstallPercent);
                var stageProgress = new Progress<string>(ReportInstallStage);

                var installedCount = 0;
                var failedCount = 0;

                for (var i = 0; i < targets.Count; i++)
                {
                    var archive = targets[i];
                    var position = Strings.Current.Format("Install.Progress.PositionOf", i + 1, targets.Count);
                    LoggingService.Log($"Processing archive: {archive.DisplayName} ({archive.FilePath})");
                    installArchiveLabel = Strings.Current.Format("Install.Archive.InstallingAt", position, archive.DisplayName);
                    installStageLabel = string.Empty;
                    StatusMessage = installArchiveLabel;
                    ProgressValue = 0;

                    try
                    {
                        var outcome = await InstallArchiveAsync(archive, run, extractionProgress, stageProgress);
                        archive.Status = outcome.Status;

                        if (outcome.Installed)
                        {
                            installedCount++;
                            installedArchives.Add(archive);
                            StatusMessage = Strings.Current.Format("Install.Archive.InstalledAt", position, archive.DisplayName);
                            LoggingService.Log($"{InstallLogArchiveLinks.ArchiveInstalled}{archive.DisplayName}");

                            if (DeleteArchivesAfterInstall)
                            {
                                var recycleFailures = MoveToRecycleBin([archive.FilePath]);

                                if (recycleFailures.Count == 0)
                                {
                                    LoggingService.Log($"Deleted archive after install: {archive.DisplayName}");
                                }
                                else
                                {
                                    // The install just succeeded and is already counted as such above;
                                    // only the archive's own cleanup failed, and that must read as its
                                    // own, smaller problem rather than as the install having failed.
                                    StatusMessage = Strings.Current.Format(
                                        "Install.Archive.InstalledButNotRecycled", position, archive.DisplayName, recycleFailures[0].Reason);
                                    LoggingService.Log(
                                        $"Installed but could not recycle archive: {archive.DisplayName} ({recycleFailures[0].Reason})",
                                        LogLevel.Warn);
                                }
                            }
                            else
                            {
                                installedArchivePaths.Add(archive.FilePath);
                            }
                        }
                        else
                        {
                            failedCount++;
                            StatusMessage = Strings.Current.Format("Install.Archive.SkippedAt", position, archive.DisplayName, outcome.Status);
                            LoggingService.Log($"Failed or skipped archive: {archive.DisplayName}", LogLevel.Warn);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        archive.Status = Strings.Current.Format("Install.Archive.InstallFailed", ex.Message);
                        StatusMessage = Strings.Current.Format("Install.Archive.FailedAt", position, archive.DisplayName, ex.Message);
                        LoggingService.LogException(ex, $"Failed to install archive: {archive.DisplayName}");
                    }
                }

                if (installedCount > 0)
                {
                    LoggingService.Log($"Installation complete. {installedCount} archives installed.");
                }
                else
                {
                    LoggingService.Log("No archives were installed.", LogLevel.Warn);
                }

                StatusMessage = SummarizeInstall(installedCount, failedCount, targets.Count);
                ShowReplacedModuleFolders();

                await ConfirmInstalledModulesAsync(installedArchives, run.Destinations);

                if (installedCount > 0)
                {
                    await BackfillArchiveLinksAsync();

                    // Play reads the Modules folder once and keeps what it read. A module installed
                    // here changes exactly that folder, so without this the tab that decides the load
                    // order still showed the install as never having happened until BEM was restarted.
                    await ShellViewModels.Instance.Environment.Refresh();
                }

                if (installedArchivePaths.Count > 0)
                {
                    await OfferDeleteArchivesAsync(installedArchivePaths);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Install.InstallationFailed", ex.Message);
                LoggingService.LogException(ex, "Global error during installation process");
            }
            finally
            {
                swTotal.Stop();
                LoggingService.Log($"Total mod installation process took {swTotal.Elapsed.TotalSeconds:F2}s");
                installArchiveLabel = string.Empty;
                installStageLabel = string.Empty;
                IsBusy = false;
                IsProgressVisible = false;
                ProgressValue = 0;
            }
        }

        // What the safety check decided about a batch. Skip is what the user chose to leave out; an
        // empty Skip with Proceed true is the ordinary case and costs a press of nothing.
        private sealed record SafetyDecision(bool Proceed, IReadOnlyList<ModArchiveEntry> Skip)
        {
            public static SafetyDecision GoAhead { get; } = new(true, []);
        }

        // Reads every archive about to be installed against the trojanized-mod fingerprint, before a
        // single byte of it is extracted, which is the one moment a payload can be stopped before it
        // lands. Finding nothing says nothing and shows nothing.
        //
        // A scan that could not run never blocks an install: "could not look" and "found something"
        // are different statements and only the second one is allowed to stop anything.
        private async Task<SafetyDecision> CheckArchiveSafetyAsync(IReadOnlyList<ModArchiveEntry> targets)
        {
            var safety = ShellViewModels.Instance.ModSafety;

            if (!safety.CheckArchivesBeforeInstalling || targets.Count == 0)
                return SafetyDecision.GoAhead;

            var blocklist = await safety.CachedBlocklistAsync(CancellationToken.None);
            var results = new List<SafetyScanResult>();
            var alarms = new List<(ModArchiveEntry Archive, SafetyScanResult Result)>();

            StatusMessage = targets.Count == 1
                ? Strings.Current.Format("Install.Safety.CheckingOne", targets[0].DisplayName)
                : Strings.Current.Plural("Install.Safety.CheckingMany", targets.Count);

            foreach (var archive in targets)
            {
                SafetyScanResult result;

                try
                {
                    var path = archive.FilePath;

                    result = await Task.Run(() =>
                        ModSafetyScanner.ScanArchive(path, blocklist, ExtractCodeFilesForSafetyScan));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LoggingService.LogException(ex, $"The safety check on '{archive.FilePath}' could not run");

                    // "Could not look" is exactly the statement NothingCouldBeRead already exists to
                    // make - the comment above this method says a failed scan must never block an
                    // install, but that only holds if the failure is still visible somewhere. Dropping
                    // the archive here instead left it silently unscanned: it reached the extractor with
                    // no trace anywhere that the trojanized-mod check never ran on it at all, which is
                    // how the FireArchers RAR-as-.zip failure would have looked here too if it had had
                    // anything to alarm on.
                    result = new SafetyScanResult(
                        new SafetyTarget(archive.FileName, Strings.Current["Install.Safety.DownloadedArchive"], archive.FilePath, SafetyTargetKind.Archive),
                        SafetyVerdict.NothingFound,
                        [],
                        [],
                        [],
                        [new UnreadableFile(archive.FilePath, ex.Message)],
                        []);
                }

                results.Add(result);

                if (result.IsAlarm)
                    alarms.Add((archive, result));
            }

            safety.Absorb(results);

            if (alarms.Count == 0)
                return SafetyDecision.GoAhead;

            return await AskAboutFlaggedArchivesAsync(alarms, targets.Count);
        }

        // The user decides, every time. Installing anyway is offered because a heuristic is not proof,
        // and stopping is the default because the finding might be exactly what it looks like.
        private static async Task<SafetyDecision> AskAboutFlaggedArchivesAsync(
            IReadOnlyList<(ModArchiveEntry Archive, SafetyScanResult Result)> alarms,
            int attempted)
        {
            var text = new StringBuilder();

            foreach (var (archive, result) in alarms)
            {
                text.AppendLine($"[{result.VerdictText}] {archive.FileName}");

                foreach (var file in result.Flagged)
                {
                    foreach (var evidence in file.Evidence)
                        text.AppendLine($"  {evidence.Headline}: {evidence.Matched}");

                    text.AppendLine($"    in {file.Location}");

                    if (file.Sha256 is { } hash)
                        text.AppendLine($"    sha256 {hash}");
                }

                text.AppendLine();
            }

            text.Append(Strings.Current["Install.Safety.HeuristicNotice"]);

            var canSkipSome = alarms.Count < attempted;

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Plural("Install.Safety.TrojanizedTitle", alarms.Count),
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBox
                    {
                        Text = text.ToString(),
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                PrimaryButtonText = Strings.Current["Install.Safety.InstallAnyway"],
                SecondaryButtonText = canSkipSome ? Strings.Current["Install.Safety.SkipInstallRest"] : null,
                CloseButtonText = Strings.Current["Install.Safety.Stop"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) switch
            {
                ContentDialogResult.Primary => SafetyDecision.GoAhead,
                ContentDialogResult.Secondary => new SafetyDecision(true, [.. alarms.Select(alarm => alarm.Archive)]),
                _ => new SafetyDecision(false, [])
            };
        }

        // The same check again on what actually landed on disk, which is cheap and catches anything
        // that only materializes once an archive is unpacked. It never blocks: the mod is installed by
        // now, so the honest thing is to say what was found and let it show on the Mod Safety tab.
        private async Task ConfirmInstalledModulesAsync(
            IReadOnlyList<ModArchiveEntry> installed, IReadOnlyList<InstallTarget> destinations)
        {
            var safety = ShellViewModels.Instance.ModSafety;

            if (!safety.CheckArchivesBeforeInstalling || installed.Count == 0)
                return;

            // Every folder the run wrote, across every destination it wrote into: a payload that only
            // materializes once unpacked is as present in the third version as in the first.
            var folders = destinations
                .SelectMany(destination => installed
                    .SelectMany(archive => archive.Modules)
                    .Select(module => Path.Combine(destination.ModulesFolder, NormalizeInstallFolderName(module.InstallFolderName))))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (folders.Count == 0)
                return;

            var blocklist = await safety.CachedBlocklistAsync(CancellationToken.None);
            AuthenticodeSignerLookup? signer = OperatingSystem.IsWindows() ? TaleWorldsSignature.Signer : null;

            List<SafetyScanResult> results;

            try
            {
                results = await Task.Run(() => ScanInstalledFolders(folders, blocklist, signer));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The safety check on the newly installed modules could not run");
                return;
            }

            safety.Absorb(results);

            var alarms = results.Where(result => result.IsAlarm).ToList();

            if (alarms.Count > 0)
            {
                StatusMessage = $"{StatusMessage} {Strings.Current.Plural(
                    "Install.Safety.JustInstalledMatch", alarms.Count, string.Join(", ", alarms.Select(a => a.Target.Name)))}";
            }
        }

        private static List<SafetyScanResult> ScanInstalledFolders(
            IReadOnlyList<string> folders,
            SafetyBlocklistIndex blocklist,
            AuthenticodeSignerLookup? signer)
        {
            var results = new List<SafetyScanResult>();

            foreach (var folder in folders)
            {
                if (SubModuleXmlParser.TryLoad(Path.Combine(folder, "SubModule.xml"), out var manifest, out _))
                    results.Add(ModSafetyScanner.ScanModule(manifest, blocklist, signer));
            }

            return results;
        }

        private static string SummarizeInstall(int installedCount, int failedCount, int attemptedCount)
        {
            if (installedCount == 0)
            {
                return Strings.Current.Plural("Install.Summary.InstalledNothing", attemptedCount, failedCount);
            }

            return failedCount == 0
                ? Strings.Current.Plural("Install.Summary.InstalledCount", attemptedCount, installedCount)
                : $"{Strings.Current.Plural("Install.Summary.InstalledCount", attemptedCount, installedCount)} "
                    + Strings.Current.Plural("Install.Summary.FailedOrSkipped", failedCount);
        }

        private void ReportInstallStage(string stage)
        {
            installStageLabel = stage;
            ProgressValue = 0;
            StatusMessage = InstallProgress.StatusLine(installArchiveLabel, installStageLabel);
        }

        // The number belongs to the bar and its caption, which is where it is formatted for the
        // language the user is reading. Writing it into the line as well printed it twice.
        private void ReportInstallPercent(int percent)
        {
            ProgressValue = percent;
            StatusMessage = InstallProgress.StatusLine(installArchiveLabel, installStageLabel);
        }

        // What one press writes into, settled before a byte of it is written: the destinations that are
        // not held back, the archive's loose-file replacements resolved against each of them on its
        // own, and the answer each destination gave to its own overwrite questions.
        private sealed record InstallRun(
            IReadOnlyList<InstallTarget> Destinations,
            IReadOnlyDictionary<ModArchiveEntry, ArchiveInstallPlan> Plans,
            IReadOnlyDictionary<InstallTarget, bool> Merge,
            IReadOnlyList<string> Notes)
        {
            public bool HasDestination => Destinations.Count > 0;

            public bool Several => Destinations.Count > 1;

            public InstallTargetPlan PlanFor(ModArchiveEntry archive, InstallTarget destination) =>
                Plans[archive].Targets.First(plan => plan.Target == destination);
        }

        // Plans the run and asks what has to be asked, destination by destination. A bin overwrite and
        // a file replacement are questions about one folder, so each is asked about that folder: one
        // answer covering three versions would be an answer to a question nobody put.
        private async Task<InstallRun> PrepareInstallRunAsync(
            IReadOnlyList<ModArchiveEntry> archives, IReadOnlyList<InstallTarget>? destinations = null)
        {
            var notes = new List<string>();
            var chosen = destinations ?? ChosenDestinations();

            // Library never asked this, while Play, Versions and Settings all do. A module folder
            // written under a running game replaces files the game holds open, and what the player
            // meets is a crash pointing nowhere near here. A destination is held back only when a running
            // game's executable lies inside its folder (or its path cannot be read), so playing one version
            // never blocks installing into another; each held destination is named, not quietly dropped.
            if (chosen.Count > 0 && RunningGame.AnyGameProcessRunning())
            {
                var running = RunningGame.RunningGamePaths();
                var held = chosen.Where(destination => RunningGame.HoldsFolder(running, destination.GameFolder)).ToList();

                if (held.Count > 0)
                {
                    notes.Add(Strings.Current.Format(
                        "Install.Destinations.GameRunning",
                        string.Join(", ", held.Select(destination => destination.DisplayName))));

                    chosen = [.. chosen.Where(destination => !held.Contains(destination))];

                    if (chosen.Count == 0)
                        return new InstallRun([], new Dictionary<ModArchiveEntry, ArchiveInstallPlan>(), new Dictionary<InstallTarget, bool>(), notes);
                }
            }

            var plans = new Dictionary<ModArchiveEntry, ArchiveInstallPlan>();

            foreach (var archive in archives)
            {
                plans[archive] = ArchiveInstallPlan.For(
                    await ReadArchiveEntryPathsAsync(archive), chosen, Path.GetFileName(archive.FilePath));

                foreach (var refused in plans[archive].Targets.Where(plan => plan.Refusal is not null))
                    notes.Add($"{refused.Target.DisplayName}: {refused.Refusal}");
            }

            var accepted = new List<InstallTarget>();
            var merge = new Dictionary<InstallTarget, bool>();

            foreach (var destination in chosen)
            {
                var heading = chosen.Count > 1
                    ? Strings.Current.Format("Install.Confirm.IntoInstance", destination.DisplayName)
                    : null;

                if (!await ConfirmBinOverwritesAsync(archives, destination, heading))
                {
                    notes.Add(Strings.Current.Format("Install.Destinations.DeclinedBin", destination.DisplayName));
                    continue;
                }

                var planned = archives
                    .Select(archive => (Archive: archive, Plan: plans[archive].Targets.First(plan => plan.Target == destination)))
                    .ToList();

                if (await ConfirmFileReplacementsAsync(planned, heading) is not { } answer)
                {
                    notes.Add(Strings.Current.Format("Install.Destinations.DeclinedFileReplacements", destination.DisplayName));
                    continue;
                }

                accepted.Add(destination);
                merge[destination] = answer;
            }

            return new InstallRun(accepted, plans, merge, notes);
        }

        // One archive into every destination the run accepted, each with its own plan. A resolution
        // holds absolute paths built from the Modules folder it was resolved against, so a set reused
        // for the next version writes that version's loose files into this one's module folders, over
        // real files, with the installer's own backup taken over the wrong original.
        private async Task<ArchiveInstallOutcome> InstallArchiveAsync(
            ModArchiveEntry archive, InstallRun run, IProgress<int>? progress, IProgress<string>? stage)
        {
            var plan = new ArchiveInstallPlan([.. run.Destinations.Select(destination => run.PlanFor(archive, destination))]);
            var outcomes = new Dictionary<InstallTarget, ArchiveInstallOutcome>();
            var archiveLabel = installArchiveLabel;

            // The report is read for what happened to each destination rather than for a file count:
            // what this step writes is module folders and bin payloads, so it answers the one thing
            // the count is read for, whether anything landed at all.
            var report = await Task.Run(() => plan.Install(destination =>
            {
                currentDestination = destination.Target;
                mergeFileReplacements = run.Merge[destination.Target];

                installArchiveLabel = run.Several
                    ? Strings.Current.Format("Install.Destinations.InstallingInto", archiveLabel, destination.Target.DisplayName)
                    : archiveLabel;

                try
                {
                    var outcome = InstallSingleArchiveAsync(archive, destination, progress, stage).GetAwaiter().GetResult();
                    outcomes[destination.Target] = outcome;

                    return outcome.Installed ? 1 : 0;
                }
                finally
                {
                    currentDestination = null;
                }
            }));

            installArchiveLabel = archiveLabel;

            ShowInstallResults(archive, run, report, outcomes);

            if (report.Targets.Count == 1)
            {
                return outcomes.TryGetValue(report.Targets[0].Target, out var only)
                    ? only
                    : new ArchiveInstallOutcome(false, report.Targets[0].Error ?? Strings.Current["Install.NothingWasInstalled"]);
            }

            return new ArchiveInstallOutcome(
                report.Installed > 0,
                Strings.Current.Format("Install.Destinations.Summary", report.Installed, report.Targets.Count));
        }

        // One line per destination, in the words the archive row would have carried had there been one
        // destination. A single destination that took the archive says so in the status line already,
        // so it is not repeated here; anything else is.
        private void ShowInstallResults(
            ModArchiveEntry archive,
            InstallRun run,
            MultiInstanceInstallReport report,
            IReadOnlyDictionary<InstallTarget, ArchiveInstallOutcome> outcomes)
        {
            if (!run.Several && report.Targets.Count == 1 && report.Targets[0].Outcome == InstallTargetOutcome.Installed)
                return;

            foreach (var result in report.Targets)
            {
                var status = outcomes.TryGetValue(result.Target, out var outcome) ? outcome.Status : string.Empty;

                InstallResults.Add(result.Outcome switch
                {
                    InstallTargetOutcome.Installed => Strings.Current.Format(
                        "Install.Destinations.ResultInstalled", result.Target.DisplayName, archive.DisplayName, status),
                    InstallTargetOutcome.Failed => Strings.Current.Format(
                        "Install.Destinations.ResultFailed", result.Target.DisplayName, archive.DisplayName, result.Error ?? status),
                    _ => Strings.Current.Format(
                        "Install.Destinations.ResultNothingToDo", result.Target.DisplayName, archive.DisplayName, status)
                });
            }

            OnPropertyChanged(nameof(HasInstallResults));
        }

        private void ClearInstallResults()
        {
            InstallResults.Clear();
            OnPropertyChanged(nameof(HasInstallResults));
        }

        // The results are laid over the bottom of the page rather than given a row of their own, so
        // they cover the band underneath until they are put away. Reading them is what they are for
        // and the next install clears them anyway; this is the same move made when they have been
        // read rather than when the next press happens to come.
        [RelayCommand]
        private void DismissInstallResults() => ClearInstallResults();

        // Why a destination is not in the run, which is a result of the press as much as an install is.
        // A single reason is the whole answer, so it is the status line rather than a list of one.
        private string ShowRunNotes(InstallRun run)
        {
            foreach (var note in run.Notes)
                InstallResults.Add(note);

            OnPropertyChanged(nameof(HasInstallResults));

            return run.Notes.Count == 1
                ? run.Notes[0]
                : Strings.Current["Install.Destinations.NothingInstalled"];
        }

        // Only an archive that ships neither a module nor a bin payload can replace a loose file, so
        // the rest are planned from an empty listing: reading one costs a 7-Zip run per install for an
        // answer already on the row. A listing that cannot be read plans no replacements either, which
        // leaves the install itself to report the missing 7-Zip in words the user can act on.
        private static async Task<IReadOnlyList<string>> ReadArchiveEntryPathsAsync(ModArchiveEntry archive)
        {
            if (archive.Modules.Count > 0 || archive.BinPayloads.Count > 0)
                return [];

            try
            {
                if (IsZipArchive(EffectiveArchiveExtension(archive.FilePath)))
                {
                    using var zip = ZipFile.OpenRead(archive.FilePath);

                    return [.. zip.Entries.Select(entry => entry.FullName)];
                }

                return await ListWith7ZipAsync(archive.FilePath, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or InvalidOperationException)
            {
                LoggingService.LogException(ex, $"Could not list the contents of {archive.FilePath}");
                return [];
            }
        }

        private async Task<ArchiveInstallOutcome> InstallSingleArchiveAsync(
            ModArchiveEntry archive, InstallTargetPlan destination, IProgress<int>? progress, IProgress<string>? stage)
        {
            LoggingService.Log($"Installing single archive: {archive.FilePath}");
            if (!File.Exists(archive.FilePath))
            {
                LoggingService.Log($"Archive file not found: {archive.FilePath}", LogLevel.Error);
                return new ArchiveInstallOutcome(false, Strings.Current["Install.Archive.Missing"]);
            }

            // Before anything is extracted, because a .7z or .rar that BEM cannot open fails several
            // steps later with an exception, and the user is then told what went wrong rather than what
            // to do about it. Every install path runs through here, so this is the one place it is asked.
            var sevenZipNote = string.Empty;

            if (IsSevenZipArchive(EffectiveArchiveExtension(archive.FilePath)))
            {
                var provision = await EnsureSevenZipAsync(CancellationToken.None);

                if (!provision.Found)
                    return new ArchiveInstallOutcome(false, provision.Message);

                sevenZipNote = provision.Message;
            }

            // An install BEM had to install 7-Zip for says so in its own result, not only in the log:
            // a program appeared on the machine, and that belongs in front of whoever pressed the button.
            ArchiveInstallOutcome WithSevenZipNote(ArchiveInstallOutcome outcome) =>
                sevenZipNote.Length == 0 ? outcome : outcome with { Status = $"{sevenZipNote} {outcome.Status}" };

            if (archive.Modules.Count == 0 && archive.BinPayloads.Count == 0)
            {
                if (destination.HasFileReplacements)
                {
                    var (written, notes) = await InstallFileReplacementsAsync(archive, destination.FileReplacements, stage);

                    return WithSevenZipNote(written == 0
                        ? new ArchiveInstallOutcome(false, Strings.Current.Format("Install.ReplacedNothing", string.Join(". ", notes)))
                        : new ArchiveInstallOutcome(true, Strings.Current.Plural("Install.ReplacedFileCount", written, string.Join(". ", notes))));
                }

                var reason = archive.UnrecognizedLayout ?? archive.Status;
                LoggingService.Log($"Archive matched no known layout, skipping: {reason}", LogLevel.Warn);
                return WithSevenZipNote(new ArchiveInstallOutcome(false, reason));
            }

            LoggingService.Log($"Detected {archive.Modules.Count} module(s) and {archive.BinPayloads.Count} bin payload(s) in archive.");
            var contentsOutcome = await InstallArchiveContentsAsync(archive, progress, stage);

            if (contentsOutcome.Installed)
                RecordInstallHistory(archive);

            return WithSevenZipNote(contentsOutcome);
        }

        // One history line per module the archive brought, read from the manifest it left in the
        // destination's Modules folder. A history that cannot be written never fails an install.
        private void RecordInstallHistory(ModArchiveEntry archive)
        {
            try
            {
                var store = new InstallHistoryStore(InstallHistoryStore.DefaultPath(InstallDataRoot));
                var modules = ModuleScanner.GetModulesFolder(InstallRoot);
                var identity = archiveProvenance.Find(Path.GetFileName(archive.FilePath));

                foreach (var module in archive.Modules)
                {
                    var folder = Path.Combine(modules, module.InstallFolderName);
                    var id = module.ModuleId ?? InstalledModuleId(folder);

                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    store.RecordArrival(id, InstalledModuleVersion(folder), archive.FilePath, identity?.NexusModId, identity?.NexusFileId);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to record the install history");
            }
        }

        // Autonomous update install: for each planned update, fetch the newer file from Nexus and then
        // run it through the exact same safe pipeline the Library page uses for a normal install. No
        // new write logic lives here - it reuses the safety scan, overwrite confirmations and the
        // install itself, so an update is no less protected than a manual one.
        //
        // Where it writes is carried in rather than assumed. An update from Play completes the install
        // it was found in, which is the one destination this page is pointed at and stays the default;
        // the BUTR stack is a different press with a different question behind it, and it writes into
        // the instances ticked under Install Into.
        public async Task<IReadOnlyList<ModUpdateInstallResult>> InstallModUpdatesAsync(
            IReadOnlyList<ModUpdateTarget> targets,
            IProgress<string>? progress,
            CancellationToken cancellationToken,
            IReadOnlyList<InstallTarget>? destinations = null)
        {
            ResolveActiveInstanceInstallTarget();

            var writeInto = destinations ?? [ConfiguredFolderDestination()];
            var results = new List<ModUpdateInstallResult>();
            var apiKey = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();
            var transport = new NexusHttpTransport();
            var downloader = new ModUpdateDownloader(new NexusClient(transport), transport);

            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // One mod's exception ends that mod, not the run and not the report. Letting it
                // unwind out of here threw away every result already collected, so mods that had
                // been downloaded and written to disk were reported nowhere and the ones after them
                // were never attempted.
                try
                {
                    var done = await InstallOneModUpdateAsync(target, writeInto, downloader, apiKey, progress, cancellationToken);

                    results.Add(done);
                    NotifyUpdate(done);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LoggingService.LogException(ex, $"Could not install the update for {target.DisplayName}");
                    var failed = new ModUpdateInstallResult(target.ModuleId, target.DisplayName, false, ex.Message);

                    results.Add(failed);
                    NotifyUpdate(failed);
                }
            }

            // A patch listed before the mod it patches is refused for want of that mod, which the same run
            // has installed by now: it gets one more try, so the order a collection lists them in does not
            // decide whether the patch lands.
            for (var index = 0; index < results.Count && !cancellationToken.IsCancellationRequested; index++)
            {
                if (results[index].Installed || !OwnModulePatch.IsOwnerMissing(results[index].Message)
                    || !results.Any(other => other.Installed))
                    continue;

                // One result per target, in the order of the targets, so the index pairs them even when two
                // targets (a mod and its patch) share a module id.
                if (index >= targets.Count)
                    break;

                var retryTarget = targets[index];

                try
                {
                    results[index] = await InstallOneModUpdateAsync(retryTarget, writeInto, downloader, apiKey, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    LoggingService.LogException(ex, $"The second try for {retryTarget.DisplayName} failed");
                }
            }

            return results;
        }

        private async Task<ModUpdateInstallResult> InstallOneModUpdateAsync(
            ModUpdateTarget target,
            IReadOnlyList<InstallTarget> destinations,
            ModUpdateDownloader downloader,
            NexusApiKey? apiKey,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(Strings.Current.Format("Install.Update.Fetching", target.DisplayName));

            var download = await downloader.DownloadAsync(
                target.NexusModId, target.FileId, apiKey, ArchivesFolderPath, null, cancellationToken);

            if (download.Status != ModUpdateDownloadStatus.Succeeded || download.ArchivePath is not { } archivePath)
                return new ModUpdateInstallResult(target.ModuleId, target.DisplayName, false, download.Message);

            // The target already states the mod id and the file id outright - that is what BEM just
            // fetched by asking for them - so this is the exact same fact RecordDownloadProvenance
            // writes for a manual nxm:// download. Without it, the archive-link record the install
            // below writes carries neither, silently replacing whatever richer record the module's
            // original install left (RecordArchiveLinksAsync always keys on the archive's own file
            // name, and an update always arrives under a different one). That downgrade demoted two
            // modules from a real "probably out of date" - powered by comparing Nexus's own recorded
            // file version against what it lists today - to "BEM cannot tell", because without a file
            // id the only comparison left standing is the module's own version string against the mod
            // page's headline version, which the two are free to disagree about for reasons that have
            // nothing to do with being current.
            RecordDownloadProvenance(archivePath, target);

            progress?.Report(Strings.Current.Format("Install.Update.Installing", target.DisplayName));

            try
            {
                var outcome = await InstallUpdateArchiveAsync(archivePath, destinations, progress, cancellationToken);

                return new ModUpdateInstallResult(target.ModuleId, target.DisplayName, outcome.Installed, outcome.Status);
            }
            finally
            {
                // The staging folder exists only to hold a download between fetching it and installing
                // it, per ModUpdateDownloader's own comment - nothing ever reads from it afterward, on
                // success or on failure, since a retry always downloads fresh rather than reusing what
                // is already there. Nothing deleted it, so every update this ever ran left its download
                // behind; the folder had years of updates sitting in it, one of them 800 MB, with nothing
                // to ever remove any of it. It goes in a finally because an install that throws leaves
                // the same file behind an install that failed does.
                TryDeleteStagedArchive(archivePath);
            }
        }

        private static void TryDeleteStagedArchive(string archivePath)
        {
            try
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);

                if (Path.GetDirectoryName(archivePath) is { } folder
                    && Path.GetFileName(Path.GetDirectoryName(folder)) == ModUpdateDownloader.StagingFolderName
                    && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Could not remove the staged update download at {archivePath}");
            }
        }

        // One downloaded archive into every destination it was asked for, through the same run the
        // Install Mods button builds: the plan is resolved against each destination's own Modules
        // folder, each is asked its own overwrite questions, one that refuses or fails does not stop
        // the rest, and what each one did lands in the results the page shows per instance.
        private async Task<ArchiveInstallOutcome> InstallUpdateArchiveAsync(
            string archivePath,
            IReadOnlyList<InstallTarget> destinations,
            IProgress<string>? stage,
            CancellationToken cancellationToken)
        {
            if (destinations.Count == 0)
                return new ArchiveInstallOutcome(false, Strings.Current["Install.Destinations.NoneSelected"]);

            if (!destinations.Any(destination => Directory.Exists(destination.GameFolder)))
                return new ArchiveInstallOutcome(false, Strings.Current["Install.Update.NoGameInstallSet"]);

            var archive = await AnalyzeArchiveAsync(archivePath, EnsureModuleDataContext(), cancellationToken);
            var targets = new List<ModArchiveEntry> { archive };

            // The same gates a manual Library install runs. An update can overwrite the game's own bin
            // or a file inside an installed module, and those are exactly the moments worth a check even
            // though this run is autonomous.
            if (!(await CheckArchiveSafetyAsync(targets)).Proceed)
                return new ArchiveInstallOutcome(false, Strings.Current["Install.Update.MatchedTrojanized"]);

            var run = await PrepareInstallRunAsync(targets, destinations);
            var refusal = ShowRunNotes(run);

            if (!run.HasDestination)
                return new ArchiveInstallOutcome(false, refusal);

            // The label the run reports progress under. Left as whatever the last press set it to, a
            // run into several instances names the archive from the install before this one.
            installArchiveLabel = Strings.Current.Format("Install.Archive.Installing", archive.DisplayName);
            installStageLabel = string.Empty;

            var outcome = await InstallArchiveAsync(archive, run, null, stage);

            if (outcome.Installed)
                await ConfirmInstalledModulesAsync(targets, run.Destinations);

            return outcome;
        }

        // ---- The BUTR stack, in one press ----

        [ObservableProperty]
        public partial bool IsButrStackVisible { get; set; }

        // The bar is the whole of the walk's face: its title says which mod BEM is waiting for and it
        // carries the only controls that can end one. So the walk lives exactly as long as the bar
        // does, and every route that takes the bar off screen - the close button, a binding, code -
        // is the same teardown Stop runs, rather than leaving a walk nothing on screen can reach.
        partial void OnIsButrStackVisibleChanged(bool value)
        {
            if (!value)
                StopButrStackRun();
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsButrStackClosable))]
        [NotifyPropertyChangedFor(nameof(CanAcceptButrStack))]
        public partial bool IsButrStackRunning { get; set; }

        // Stop and Skip live inside the bar, so its close button was a third control that ended the
        // walk without ending anything: it took away the only two that could. The bar is dismissable
        // once the run is over, which is the only time there is nothing left to end.
        public bool IsButrStackClosable => !IsButrStackRunning;

        // Only the guided walk can be skipped a mod at a time, because only it is waiting on a person.
        [ObservableProperty]
        public partial bool IsButrStackGuided { get; set; }

        [ObservableProperty]
        public partial string ButrStackTitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ButrStackMessage { get; set; } = string.Empty;

        // A run marks the page busy, and busy turns the page's progress bar on. The bar reads its
        // caption from StatusMessage, so without this a stack install is a sweeping bar with no words
        // for as long as it lasts.
        partial void OnButrStackMessageChanged(string value)
        {
            HideAcceptedButrStack();
            if (IsButrStackRunning)
                StatusMessage = value;
        }

        [ObservableProperty]
        public partial InfoBarSeverity ButrStackSeverity { get; set; } = InfoBarSeverity.Informational;

        // Accept the risk: a warning or error this bar showed and the user accepted stays hidden while
        // its words are the same. AcceptedNotices holds the record, and Settings lists it to forgive.
        public bool CanAcceptButrStack => AcceptedNotices.IsWarning(ButrStackSeverity) && !IsButrStackRunning;

        partial void OnButrStackSeverityChanged(InfoBarSeverity value) => HideAcceptedButrStack();

        private void HideAcceptedButrStack()
        {
            OnPropertyChanged(nameof(CanAcceptButrStack));
            if (!IsButrStackRunning && AcceptedNotices.IsAccepted("ButrStack", ButrStackSeverity, ButrStackTitle, ButrStackMessage))
                IsButrStackVisible = false;
        }

        [RelayCommand]
        private void AcceptButrStack()
        {
            AcceptedNotices.Accept("ButrStack", AcceptedNotices.Identity(ButrStackTitle, ButrStackMessage));
            IsButrStackVisible = false;
        }

        // Why the button cannot run, shown beside it. A disabled control with no stated reason is the
        // same dead end as a missing one.
        [ObservableProperty]
        public partial string ButrStackBlocker { get; set; } = string.Empty;

        public ObservableCollection<string> ButrStackResults { get; } = [];

        // Which instances each stack member is being written into, settled when the plan is made. The
        // guided walk installs a member whenever its download arrives rather than in one pass, so the
        // answer has to outlive the loop that worked it out.
        private IReadOnlyDictionary<string, IReadOnlyList<InstallTarget>> butrStackDestinations =
            new Dictionary<string, IReadOnlyList<InstallTarget>>(StringComparer.OrdinalIgnoreCase);

        private ButrStackFlow? butrStackFlow;

        // The one place a walk is started, replaced or dropped, so ending the outgoing one is a rule
        // of the field rather than something each new flow has to remember. Whatever the walk is
        // dropped for - finishing, Stop, an exception, the bar being dismissed - the walk that is
        // being let go is ended first, and an ended walk answers no to IsWaitingFor forever. That is
        // what keeps a dead walk from skipping a later download's destination confirmation.
        private ButrStackFlow? ButrStackWalk
        {
            get => butrStackFlow;
            set
            {
                if (ReferenceEquals(butrStackFlow, value))
                    return;

                butrStackFlow?.End();
                butrStackFlow = value;
            }
        }
        private CancellationTokenSource? butrStackCts;

        public void RefreshButrStackReadiness()
        {
            ButrStackBlocker = ButrStackBlockedBecause();
            InstallButrStackCommand.NotifyCanExecuteChanged();
            SkipButrStackModCommand.NotifyCanExecuteChanged();
        }

        // The destinations are resolved the way every install on this page resolves them, so a machine
        // install with no managed versions is as valid a destination as four ticked instances. What
        // blocks the button is having nowhere at all to install to.
        private string ButrStackBlockedBecause()
        {
            if (IsButrStackRunning)
                return Strings.Current["Install.ButrStack.AlreadyRunning"];

            if (IsBusy)
                return Strings.Current["Install.ButrStack.Busy"];

            var chosen = ChosenDestinations();

            if (chosen.Count == 0)
                return Strings.Current["Install.Destinations.NoneSelected"];

            return chosen.Any(destination => Directory.Exists(destination.GameFolder))
                ? string.Empty
                : Strings.Current["Install.ButrStack.NoDestination"];
        }

        private bool CanInstallButrStack() => ButrStackBlockedBecause().Length == 0;

        [RelayCommand(CanExecute = nameof(CanInstallButrStack))]
        private async Task InstallButrStackAsync()
        {
            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();

            butrStackCts?.Dispose();
            butrStackCts = new CancellationTokenSource();

            ButrStackWalk = null;
            butrStackDestinations = new Dictionary<string, IReadOnlyList<InstallTarget>>(StringComparer.OrdinalIgnoreCase);
            ButrStackResults.Clear();
            ClearInstallResults();
            IsButrStackVisible = true;
            IsButrStackGuided = false;
            IsButrStackRunning = true;

            // A stack install writes into the Modules folder the rest of this page writes into, so it
            // marks the page busy the way every other long operation on it does. Without this, Install
            // Selected stayed pressable for the whole of a premium run and for the whole of a guided
            // walk, and two installs into one Modules folder is a wrecked install rather than a
            // crowded screen. The other direction was already covered: ButrStackBlockedBecause holds
            // this button while an ordinary install is running.
            IsBusy = true;
            RefreshButrStackReadiness();
            ShowButrStack(InfoBarSeverity.Informational, Strings.Current["Install.ButrStack.Title"],
                Strings.Current["Install.ButrStack.CheckingAccount"]);

            try
            {
                await RunButrStackAsync(butrStackCts.Token);
            }
            catch (OperationCanceledException)
            {
                FinishButrStack(InfoBarSeverity.Informational, Strings.Current["Install.ButrStack.Canceled"]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "The BUTR Stack install failed");
                FinishButrStack(InfoBarSeverity.Error, Strings.Current.Format("Install.ButrStack.Failed", ex.Message));
            }
            finally
            {
                // A guided walk outlives this method: it is still running once the first mod's page
                // has been opened, and it is FinishButrStack that lets the page go. This covers the
                // runs that are over by the time the await returns, so no path can leave the page
                // locked busy.
                if (!IsButrStackRunning)
                    IsBusy = false;
            }
        }

        private async Task RunButrStackAsync(CancellationToken cancellationToken)
        {
            var transport = new NexusHttpTransport();
            var client = new NexusClient(transport);
            var apiKey = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();

            if (apiKey is not { } key)
            {
                IsNexusKeyNeeded = true;
                FinishButrStack(InfoBarSeverity.Warning, Strings.Current["Install.ButrStack.NoApiKey"]);
                return;
            }

            // Which route applies is what the account is, asked of Nexus, not a setting the user has to
            // find and keep correct. users/validate is the one call Nexus documents as exempt from the
            // hourly limit, so asking it every press costs nothing.
            var account = await client.ValidateAsync(key, cancellationToken);

            if (!account.IsOk || account.Value is not { } user)
            {
                IsNexusKeyNeeded = account.Outcome is NexusOutcome.Unauthorized;
                FinishButrStack(InfoBarSeverity.Warning,
                    Strings.Current.Format("Install.ButrStack.AccountUnreadable", account.Message));
                return;
            }

            var listings = new Dictionary<int, NexusModFileListing>();
            var unreadMessages = new List<string>();

            foreach (var member in ButrStack.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ButrStackMessage = Strings.Current.Format("Install.ButrStack.Reading", member.Name);

                var files = await client.GetModFilesAsync(key, member.NexusModId, cancellationToken);

                // A page BEM could not read is one member of five, not the run. The plan reports it as
                // unread and the other four still go ahead.
                if (files.IsOk && files.Value is { } listing)
                {
                    listings[member.NexusModId] = listing;
                }
                else
                {
                    ButrStackResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", member.Name, files.Message));
                    unreadMessages.Add(files.Message);
                }
            }

            var destinations = ChosenDestinations();

            if (destinations.Count == 0)
            {
                FinishButrStack(InfoBarSeverity.Warning, Strings.Current["Install.Destinations.NoneSelected"]);
                return;
            }

            // What the stack already holds is asked of every ticked instance on its own. One instance
            // carrying MCM at the offered version says nothing about the three that do not, so a
            // single plan read off the active install would leave the other three without the stack
            // and report the run as having had nothing to do.
            var plans = new List<(InstallTarget Destination, ButrStackPlan Plan)>();

            foreach (var destination in destinations)
            {
                var instanceScan = ModuleScanner.ScanAll(destination.GameFolder);

                plans.Add((
                    destination,
                    ButrStack.Plan(listings, ButrStackInstallState.Read(destination.GameFolder, instanceScan.Modules))));
            }

            // Which instances each member is going into, kept so the guided walk can look it up when
            // the download for that member finally arrives, hours later in the worst case.
            butrStackDestinations = plans
                .SelectMany(entry => entry.Plan.Fetches.Select(item => (item.Prerequisite.Id, entry.Destination)))
                .GroupBy(pair => pair.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<InstallTarget>)[.. group.Select(pair => pair.Destination)],
                    StringComparer.OrdinalIgnoreCase);

            // Decided rather than every member that needs no fetch: an unread page was already
            // reported above with the transport's own account of what went wrong, and adding the
            // plan's weaker line for it says the same thing twice.
            foreach (var (destination, instancePlan) in plans)
            {
                foreach (var item in instancePlan.Decided)
                    ButrStackResults.Add(ButrStackResultLine(item.Name, destination, item.Reason, destinations.Count));
            }

            // One entry per member any instance needs, taken from the first instance that needs it:
            // Nexus offers the same file to all of them, so the download and the walk are about the
            // member while the writing is about the instances behind it.
            var fetches = plans
                .SelectMany(entry => entry.Plan.Fetches)
                .GroupBy(item => item.Prerequisite.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // Which pages were read is the same answer for every instance, so the run's verdict on
            // having read nothing is taken from the first plan rather than counted again per instance.
            var plan = plans[0].Plan;

            if (fetches.Count == 0)
            {
                // Nothing to fetch is two different outcomes and only one of them is a success. Every
                // page read with nothing out of date is work that happened; not one page read is work
                // that did not, and reporting the second as the first told a user with no connection
                // that a stack they do not have is installed and current.
                if (plan.EverythingCurrent)
                {
                    FinishButrStack(InfoBarSeverity.Success, Strings.Current["Install.ButrStack.NothingToFetch"]);
                }
                else if (plan.NothingCouldBeRead)
                {
                    // Not one page read is not a run that failed at one thing: quoting the first
                    // mod's transport message as though it were the run's own reason hides that the
                    // other four say the same, each on its own line above.
                    FinishButrStack(
                        InfoBarSeverity.Error, Strings.Current["Install.ButrStack.NothingCouldBeRead"]);
                }
                else
                {
                    FinishButrStack(
                        InfoBarSeverity.Warning,
                        Strings.Current.Format("Install.ButrStack.Failed", unreadMessages.Count > 0
                            ? unreadMessages[0]
                            : Strings.Current["Core.Nexus.ButrStack.ListingNotRead"]));
                }

                return;
            }

            if (user.IsPremium)
                await InstallButrStackDirectlyAsync(fetches, cancellationToken);
            else
                StartGuidedButrStack(fetches);
        }

        // Every instance this member is going into. The lookup is built when the plan is made; a
        // member that reaches here without one is a walk that outlived its plan, and the ticks as they
        // stand now are a better answer than nowhere at all.
        private IReadOnlyList<InstallTarget> ButrStackDestinationsFor(ButrStackItem item) =>
            butrStackDestinations.TryGetValue(item.Prerequisite.Id, out var chosen) ? chosen : ChosenDestinations();

        // One instance is the case this page had before it could write into several, and naming it on
        // every line then is noise; several is the case where a line that does not say which instance
        // it is about answers nothing.
        private static string ButrStackResultLine(string name, InstallTarget destination, string reason, int destinationCount) =>
            destinationCount > 1
                ? Strings.Current.Format("Install.ButrStack.ResultLineForInstance", name, destination.DisplayName, reason)
                : Strings.Current.Format("Install.ButrStack.ResultLine", name, reason);

        // Premium: Nexus serves a download link to the account itself, so the whole stack is fetched and
        // installed by the same machinery Install Available Updates uses, which already carries on past
        // a failure and reports one result per mod.
        private async Task InstallButrStackDirectlyAsync(
            IReadOnlyList<ButrStackItem> fetches, CancellationToken cancellationToken)
        {
            ButrStackMessage = Strings.Current.Plural("Install.ButrStack.Installing", fetches.Count);

            // Grouped by where they go, so each member is downloaded once and written into exactly the
            // instances that asked for it rather than over one that already holds it current.
            var results = new List<ModUpdateInstallResult>();

            foreach (var group in fetches.GroupBy(
                item => string.Join('\n', ButrStackDestinationsFor(item).Select(destination => destination.InstanceId)),
                StringComparer.Ordinal))
            {
                results.AddRange(await InstallModUpdatesAsync(
                    [.. group.Select(item => item.Target!)],
                    new Progress<string>(message => ButrStackMessage = message),
                    cancellationToken,
                    ButrStackDestinationsFor(group.First())));
            }

            foreach (var result in results)
                ButrStackResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", result.ModuleName, result.Message));

            var installed = results.Count(result => result.Installed);

            FinishButrStack(
                installed == results.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Strings.Current.Format("Install.ButrStack.Done", installed, results.Count - installed));
        }

        // Not premium: Nexus will not mint a download link for this account without a click on its own
        // website, so the stack is walked one mod at a time. The page for the mod BEM is waiting on is
        // opened, and the nxm:// link that click produces is what advances the walk.
        private void StartGuidedButrStack(IReadOnlyList<ButrStackItem> fetches)
        {
            ButrStackWalk = new ButrStackFlow(fetches);
            IsButrStackGuided = true;
            RefreshButrStackReadiness();
            OpenCurrentButrStackPage();
        }

        private void OpenCurrentButrStackPage()
        {
            if (ButrStackWalk is not { Current: { } item } flow)
            {
                CompleteGuidedButrStack();
                return;
            }

            ShowButrStack(InfoBarSeverity.Informational, flow.Describe(),
                Strings.Current.Format("Install.ButrStack.ClickModManagerDownload", item.Name));

            var url = ButrStack.ModManagerDownloadUrl(item.NexusModId, item.File!.FileId);

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                LoggingService.LogException(ex, $"Could not open the Nexus page for {item.Name}");
                ButrStackMessage = Strings.Current.Format("Install.ButrStack.BrowserFailed", item.Name, url);
            }
        }

        // Whether a link that just arrived is the one the guided walk is waiting for, which is also
        // what lets that download skip the destination confirmation. Only the walk itself is asked: a
        // flag kept beside it is one that can be left armed after the walk is gone, and this question
        // decides whether a download hours later installs into whatever version happens to be selected
        // with no dialog. A walk that has ended answers no, whatever still holds a reference to it.
        //
        // A link for anything else is left to the ordinary nxm handling, so a download the user
        // started themselves during the walk is not swallowed by it.
        public bool IsButrStackWaitingFor(NxmLink link) =>
            link.ModId is { } modId && ButrStackWalk?.IsWaitingFor(modId, link.FileId) == true;

        // The guided walk's own arrival path: download the file the click just authorized, install it
        // through the same pipeline every other install uses, then open the next mod's page.
        public async Task ContinueButrStackAsync(NxmLink link)
        {
            if (ButrStackWalk is not { Current: { } step })
                return;

            // Nothing on this path may throw. Its only caller is fire and forget from the nxm handler,
            // so an exception here is never observed: the walk would sit armed behind a spinning bar
            // with no error text and no gesture that caused it. A corrupt or truncated archive and a
            // dialog that cannot be shown both reach this.
            try
            {
                // Started on the walk's own token, so Stop reaches this transfer directly and nothing
                // else the user starts meanwhile can end it.
                var download = await DownloadFromNexusAsync(link, butrStackCts?.Token ?? CancellationToken.None);

                if (download.Status != NxmDownloadStatus.Downloaded || download.Path is not { Length: > 0 } archivePath)
                {
                    RecordButrStackStep(step, false, download.Message);
                    return;
                }

                ButrStackMessage = Strings.Current.Format("Install.Update.Installing", step.Name);

                var outcome = await InstallUpdateArchiveAsync(
                    archivePath, ButrStackDestinationsFor(step), null, butrStackCts?.Token ?? CancellationToken.None);

                RecordButrStackStep(step, outcome.Installed, outcome.Status);
            }
            catch (OperationCanceledException)
            {
                RecordButrStackStep(step, false, Strings.Current["Install.ButrStack.Canceled"]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, $"The BUTR Stack step for {step.Name} failed");
                RecordButrStackStep(step, false, ex.Message);
            }
        }

        // The step is carried in rather than read back, because Stop and Skip are both live for the
        // whole of a download: the line always names the mod this result is about, and the walk only
        // advances when the step it was started for is still the one being waited on.
        private void RecordButrStackStep(ButrStackItem step, bool installed, string message)
        {
            ButrStackResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", step.Name, message));

            if (ButrStackWalk?.Record(step, installed, message) == true)
                OpenCurrentButrStackPage();
        }

        private bool CanSkipButrStackMod() => IsButrStackRunning && IsButrStackGuided;

        [RelayCommand(CanExecute = nameof(CanSkipButrStackMod))]
        private void SkipButrStackMod()
        {
            if (ButrStackWalk is not { Current: { } item })
                return;

            ButrStackResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", item.Name,
                Strings.Current["Core.Nexus.ButrStack.Flow.Skipped"]));

            ButrStackWalk.Skip();
            OpenCurrentButrStackPage();
        }

        [RelayCommand]
        private void CancelButrStack() => StopButrStackRun();

        // Every way out of a run other than finishing it comes through here, so there is one teardown
        // to keep correct rather than one per gesture.
        private void StopButrStackRun()
        {
            if (!IsButrStackRunning && ButrStackWalk is null)
                return;

            // A guided step's download was started on this token, so cancelling it stops that transfer
            // and only that one. Reaching for the download bar's own Cancel here instead would stop
            // whichever download is newest, which during a walk can be one the user started themselves.
            butrStackCts?.Cancel();

            if (ButrStackWalk is { Finished: false } flow)
            {
                flow.End();
                CompleteGuidedButrStack();
                return;
            }

            FinishButrStack(InfoBarSeverity.Informational, Strings.Current["Install.ButrStack.Canceled"]);
        }

        private void CompleteGuidedButrStack()
        {
            var outcomes = ButrStackWalk?.Outcomes ?? [];
            var installed = outcomes.Count(outcome => outcome.Installed);

            FinishButrStack(
                outcomes.Count > 0 && installed == outcomes.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Strings.Current.Format("Install.ButrStack.Done", installed, outcomes.Count - installed));
        }

        private void ShowButrStack(InfoBarSeverity severity, string title, string message)
        {
            ButrStackSeverity = severity;
            ButrStackTitle = title;
            ButrStackMessage = message;
        }

        // Whatever landed is left where it is. Pressing the button again re-plans against what is on
        // disk, so a walk abandoned halfway is finished rather than started over.
        private void FinishButrStack(InfoBarSeverity severity, string message)
        {
            ButrStackWalk = null;
            IsButrStackRunning = false;
            IsButrStackGuided = false;

            // Every run ends here, the guided walk's included, so this is where the page stops being
            // busy for it.
            IsBusy = false;
            ShowButrStack(severity, Strings.Current["Install.ButrStack.Title"], message);
            RefreshButrStackReadiness();
        }


        private async Task<ArchiveInstallOutcome> InstallArchiveContentsAsync(ModArchiveEntry archive, IProgress<int>? progress, IProgress<string>? stage)
        {
            LoggingService.Log($"{InstallLogArchiveLinks.ArchiveStarted}{archive.DisplayName}");
            var installed = new List<string>();
            var installedPaths = new List<string>();
            var installedModuleIds = new List<(string ModuleId, string FolderPath)>();
            var notes = new List<string>();
            var installPlatformFolder = GameInstallLocator.DetectPlatform(InstallRoot).FilterPlatformFolder;
            var skippedPlatformFolders = new List<string>();
            var keptPlatformFolders = new List<string>();
            var skippedPlatformFiles = 0;
            long skippedPlatformBytes = 0;

            for (var moduleIndex = 0; moduleIndex < archive.Modules.Count; moduleIndex++)
            {
                var module = archive.Modules[moduleIndex];
                var modulePrefix = archive.Modules.Count > 1
                    ? Strings.Current.Format("Install.Progress.ModulePrefix", moduleIndex + 1, archive.Modules.Count)
                    : string.Empty;

                LoggingService.Log($"Processing module: {module.ModuleName}", LogLevel.Debug);

                var installFolderName = NormalizeInstallFolderName(module.InstallFolderName);
                var targetPath = Path.Combine(InstallRoot, "Modules", installFolderName);

                var folderWasAlreadyThere = Directory.Exists(targetPath);

                if (folderWasAlreadyThere)
                {
                    var replacing = ReplaceExistingModuleFolders;

                    stage?.Report(modulePrefix + Strings.Current.Format("Install.Stage.ExistingFolder", ExistingFolderStage(replacing), installFolderName));

                    try
                    {
                        notes.Add(replacing
                            ? ReplaceExistingModuleFolder(targetPath, installFolderName, archive.DisplayName)
                            : PreserveExistingModuleFolder(targetPath, installFolderName, archive.DisplayName));

                        // Replacing moved the old folder into the quarantine, so what the extract is
                        // about to fill is this install's own folder and nothing of the user's is in it.
                        if (replacing)
                            folderWasAlreadyThere = false;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogException(ex, $"Failed to deal with {targetPath} before installing over it");

                        var what = replacing
                            ? Strings.Current.Format("Install.Error.FailedToClear", installFolderName)
                            : Strings.Current.Format("Install.Error.FailedToKeepCopy", installFolderName);

                        return new ArchiveInstallOutcome(
                            false,
                            Strings.Current.Format("Install.Error.LeftAsItWas", what, ex.Message));
                    }
                }

                Directory.CreateDirectory(targetPath);
                stage?.Report(modulePrefix + Strings.Current.Format("Install.Stage.Extracting", installFolderName));

                PlatformSkip skip;

                try
                {
                    skip = await ExtractModuleFolderAnyAsync(archive.FilePath, module, targetPath, installPlatformFolder, progress, stage, modulePrefix);
                }
                catch
                {
                    // The destination is created before the first file is written, so an archive that
                    // cannot be read left an empty module folder standing where a module was never
                    // installed. The failure itself is reported by the caller; this only makes sure the
                    // game folder is as it was before the attempt.
                    var undo = InstallRollback.UndoFailedModuleInstall(targetPath, folderWasAlreadyThere);

                    if (undo.Problem is { } problem)
                        LoggingService.Log(problem, LogLevel.Warn);
                    else if (undo.Removed)
                        LoggingService.Log($"Removed {targetPath}, which the failed install had created");

                    throw;
                }
                skippedPlatformFiles += skip.FileCount;
                skippedPlatformBytes += skip.SizeBytes;
                skippedPlatformFolders.AddRange(skip.PlatformFolders.Except(skippedPlatformFolders, StringComparer.OrdinalIgnoreCase));
                keptPlatformFolders.AddRange(skip.Kept.Except(keptPlatformFolders, StringComparer.OrdinalIgnoreCase));

                installed.Add(Strings.Current.Format("Install.Content.ModuleEntry", installFolderName));
                installedPaths.Add(targetPath);
                // The manifest that is now on disk, falling back to the one read out of the archive. A
                // module whose id neither of them declares is left unrecorded rather than keyed on its
                // folder name, because a link under the wrong key is a link to the wrong mod page.
                if ((InstalledModuleId(targetPath) ?? module.ModuleId) is { } moduleId)
                    installedModuleIds.Add((moduleId, targetPath));
            }

            await RecordArchiveLinksAsync(archive.FilePath, installedModuleIds);

            if (skippedPlatformFiles > 0)
            {
                notes.Add(Strings.Current.Format(
                    "Install.Notes.SkippedPlatformSaving",
                    new PlatformSkip(skippedPlatformFolders, skippedPlatformFiles, skippedPlatformBytes).Describe(installPlatformFolder!),
                    FormatFileSize(skippedPlatformBytes)));
            }

            if (keptPlatformFolders.Count > 0)
            {
                notes.Add(new PlatformSkip([], 0, 0, keptPlatformFolders).DescribeKept());
            }

            var payloads = ArchiveLayout.ForPlatform(archive.BinPayloads, installPlatformFolder);

            if (archive.BinPayloads.Count > 0 && payloads.Count == 0)
            {
                var offered = string.Join(", ", archive.BinPayloads.Select(payload => payload.PlatformFolder));
                notes.Add(Strings.Current.Format("Install.Notes.BinPayloadSkipped", offered, installPlatformFolder));
            }

            foreach (var payload in payloads)
            {
                var destination = BinPayloadInstaller.DestinationFolder(InstallRoot, payload.PlatformFolder);
                var injectorsAlreadyThere = BinPayloadInstaller.FindInstalledInjectors(InstallRoot, payload.PlatformFolder);

                stage?.Report(Strings.Current.Format("Install.Stage.ExtractingBinPayload", payload.PlatformFolder));
                var replaced = await ExtractBinPayloadAsync(archive.FilePath, payload, destination, InstallRoot, InstallBinBackups, progress, stage);

                installed.Add(Strings.Current.Plural("Install.Content.BinPayloadEntry", payload.Files.Count, payload.PlatformFolder));

                if (replaced > 0)
                {
                    notes.Add(Strings.Current.Plural("Install.Notes.ReplacedExisting", replaced));
                }

                if (payload.InjectorProxies.Count > 0)
                {
                    notes.Add(Strings.Current.Format("Install.Notes.GraphicsInjectorInstalled", string.Join(", ", payload.InjectorProxies)));

                    var others = injectorsAlreadyThere
                        .Except(payload.InjectorProxies, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (others.Count > 0)
                    {
                        notes.Add(Strings.Current.Format("Install.Notes.AnotherInjectorPresent", string.Join(", ", others)));
                    }
                }
            }

            if (installedPaths.Count > 0)
            {
                stage?.Report(Strings.Current["Install.Stage.UnblockingFiles"]);
                await UnblockInstalledFilesAsync(installedPaths);
            }

            if (installed.Count == 0)
            {
                return new ArchiveInstallOutcome(false, notes.Count > 0
                    ? Strings.Current.Format("Install.InstalledNothingWithNotes", string.Join(". ", notes))
                    : Strings.Current["Install.NothingWasInstalled"]);
            }

            var status = Strings.Current.Format("Install.InstalledList", string.Join(", ", installed));
            return new ArchiveInstallOutcome(true, notes.Count > 0
                ? Strings.Current.Format("Install.StatusWithNotes", status, string.Join(". ", notes))
                : status);
        }

        // Returns the number of existing files that were replaced.
        private static async Task<int> ExtractBinPayloadAsync(
            string archivePath,
            BinPayload payload,
            string destinationRoot,
            string gameInstallPath,
            BinBackupStore backups,
            IProgress<int>? progress,
            IProgress<string>? stage)
        {
            LoggingService.Log($"Extracting bin payload from {archivePath} to {destinationRoot}");
            Directory.CreateDirectory(destinationRoot);

            return IsZipArchive(EffectiveArchiveExtension(archivePath))
                ? await ExtractBinPayloadZipAsync(archivePath, payload, destinationRoot, gameInstallPath, backups, progress)
                : await ExtractBinPayload7ZipAsync(archivePath, payload, destinationRoot, gameInstallPath, backups, progress, stage);
        }

        private static async Task<int> ExtractBinPayloadZipAsync(
            string archivePath,
            BinPayload payload,
            string destinationRoot,
            string gameInstallPath,
            BinBackupStore backups,
            IProgress<int>? progress)
        {
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in payload.Files)
            {
                wanted[file.EntryPath] = file.RelativePath;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var replaced = 0;
            var extracted = 0;
            var lastPercent = -1;

            foreach (var entry in archive.Entries)
            {
                if (!wanted.TryGetValue(entry.FullName.Replace('\\', '/'), out var relativePath))
                {
                    continue;
                }

                var destinationPath = GetSafeDestinationPath(destinationRoot, relativePath);
                if (backups.Capture(gameInstallPath, payload.PlatformFolder, relativePath, "install") is not null)
                {
                    replaced++;
                }

                await ExtractEntryAsync(entry, destinationPath);
                extracted++;
                lastPercent = ReportPercentIfChanged(progress, extracted * 100 / wanted.Count, lastPercent);
            }

            return replaced;
        }

        private static async Task<int> ExtractBinPayload7ZipAsync(
            string archivePath,
            BinPayload payload,
            string destinationRoot,
            string gameInstallPath,
            BinBackupStore backups,
            IProgress<int>? progress,
            IProgress<string>? stage)
        {
            var temp = CreateTempFolder();
            try
            {
                await ExtractWith7ZipAsync(archivePath, temp, progress: progress);

                var replaced = 0;
                foreach (var file in payload.Files)
                {
                    var source = Path.Combine(temp, file.EntryPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var destinationPath = GetSafeDestinationPath(destinationRoot, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                    if (backups.Capture(gameInstallPath, payload.PlatformFolder, file.RelativePath, "install") is not null)
                    {
                        replaced++;
                    }

                    Core.Io.InstanceFiles.Copy(source, destinationPath);
                }

                return replaced;
            }
            finally
            {
                stage?.Report(Strings.Current["Install.Stage.CleaningUpTempFiles"]);
                TryDeleteDirectory(temp);
            }
        }

        // bin is the game's own directory, so replacing a file there is a different risk from creating a
        // module folder and is worth naming before anything is written.
        private async Task<bool> ConfirmBinOverwritesAsync(
            IReadOnlyList<ModArchiveEntry> targets, InstallTarget destination, string? heading)
        {
            const int NamesShown = 12;

            var platformFolder = GameInstallLocator.GetBinaryFolder(destination.GameFolder);
            var overwrites = targets
                .SelectMany(target => BinPayloadInstaller.FindOverwrites(destination.GameFolder, ArchiveLayout.ForPlatform(target.BinPayloads, platformFolder)))
                .ToList();

            if (overwrites.Count == 0)
            {
                return true;
            }

            var names = overwrites
                .Select(overwrite => Path.Combine("bin", overwrite.PlatformFolder, overwrite.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();

            var listing = string.Join(Environment.NewLine, names.Take(NamesShown));
            if (names.Count > NamesShown)
            {
                listing += Environment.NewLine + Strings.Current.Plural("Install.AndMoreCount", names.Count - NamesShown);
            }

            var backups = new BinBackupStore(BinBackupStore.GetDefaultRoot(destination.DataRoot));

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.ReplaceBinFilesTitle"],
                Content = WithHeading(
                    heading,
                    Strings.Current.Plural("Install.Confirm.ReplaceBinFilesContent", names.Count, backups.RootPath, listing)),
                PrimaryButtonText = Strings.Current["Install.Confirm.Replace"],
                CloseButtonText = Strings.Current["Install.Confirm.Cancel"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // An update archive rewrites a file the game or another mod already installed, so what it would
        // change, add and drop is shown before anything is written. Merging is the default; the whole
        // file goes only if the user asks for that.
        // Null is a refusal. True merges and false writes the whole file, which is the answer this
        // destination gave rather than one the next destination inherits: the files are different
        // files and the question was asked about these.
        private async Task<bool?> ConfirmFileReplacementsAsync(
            IReadOnlyList<(ModArchiveEntry Archive, InstallTargetPlan Plan)> planned, string? heading)
        {
            var descriptions = new List<string>();
            var anyMergeable = false;
            var touchesBaseGame = false;

            foreach (var (archive, plan) in planned.Where(entry => entry.Plan.HasFileReplacements))
            {
                foreach (var resolution in plan.FileReplacements.Where(resolution => resolution.Candidates.Count > 0))
                {
                    var bytes = await ReadArchiveEntryBytesAsync(archive.FilePath, resolution.EntryPath);

                    if (bytes is null)
                    {
                        continue;
                    }

                    var target = FileReplacementInstaller.BestTarget(resolution, bytes);

                    if (target is null)
                    {
                        var names = string.Join(", ", resolution.Candidates.Select(candidate => candidate.ModuleFolderName));
                        descriptions.Add(Strings.Current.Format("Install.FileReplace.Ambiguous", resolution.FileName, names));
                        continue;
                    }

                    var preview = FileReplacementInstaller.Preview(target.TargetPath, bytes, target.IsOfficialModule);
                    anyMergeable |= preview.CanMerge;
                    touchesBaseGame |= target.IsOfficialModule;
                    descriptions.Add(Strings.Current.Format(
                        "Install.FileReplace.ArchiveInto", archive.DisplayName, target.ModuleFolderName, preview.Describe()));
                }
            }

            if (descriptions.Count == 0)
            {
                return true;
            }

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.ReplaceModuleFilesTitle"],
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock
                    {
                        Text = WithHeading(heading, string.Join($"{Environment.NewLine}{Environment.NewLine}", descriptions)),
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                PrimaryButtonText = anyMergeable ? Strings.Current["Install.Confirm.Merge"] : Strings.Current["Install.Confirm.Replace"],
                SecondaryButtonText = anyMergeable ? Strings.Current["Install.Confirm.ReplaceWholeFile"] : null,
                CloseButtonText = Strings.Current["Install.Confirm.Cancel"],
                DefaultButton = touchesBaseGame ? ContentDialogButton.Close : ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            var result = await DialogText.ShowAsync(dialog);

            if (result == ContentDialogResult.None)
            {
                return null;
            }

            return result == ContentDialogResult.Primary;
        }

        // Which folder the question is about, above the question. Nothing is prepended when there is
        // one destination, because the page has always had exactly one and the dialog read correctly
        // without it.
        private static string WithHeading(string? heading, string content) =>
            heading is null ? content : $"{heading}{Environment.NewLine}{Environment.NewLine}{content}";

        private async Task<(int Written, List<string> Notes)> InstallFileReplacementsAsync(
            ModArchiveEntry archive, IReadOnlyList<FileReplacementResolution> replacements, IProgress<string>? stage)
        {
            var notes = new List<string>();
            var written = 0;

            foreach (var resolution in replacements)
            {
                if (resolution.Candidates.Count == 0)
                {
                    notes.Add(Strings.Current.Format("Install.FileReplace.MatchesNoFile", resolution.FileName));
                    continue;
                }

                var bytes = await ReadArchiveEntryBytesAsync(archive.FilePath, resolution.EntryPath);

                if (bytes is null)
                {
                    notes.Add(Strings.Current.Format("Install.FileReplace.CouldNotBeRead", resolution.FileName));
                    continue;
                }

                var target = FileReplacementInstaller.BestTarget(resolution, bytes);

                if (target is null)
                {
                    var names = string.Join(", ", resolution.Candidates.Select(candidate => candidate.ModuleFolderName));
                    notes.Add(Strings.Current.Format("Install.FileReplace.WillNotGuess", resolution.FileName, names));
                    continue;
                }

                stage?.Report(Strings.Current.Format("Install.Stage.ReplacingFile", resolution.FileName, target.ModuleFolderName));

                var preview = FileReplacementInstaller.Preview(target.TargetPath, bytes, target.IsOfficialModule);
                var outcome = FileReplacementInstaller.Install(target.TargetPath, bytes, mergeFileReplacements);
                written++;

                notes.Add(outcome.Merged && preview.Plan is not null
                    ? Strings.Current.Plural(
                        "Install.FileReplace.Merged", preview.Plan.Changed.Count,
                        resolution.FileName, target.ModuleFolderName, preview.Plan.Added.Count, preview.Plan.Preserved.Count)
                    : Strings.Current.Format("Install.FileReplace.ReplacedWhole", resolution.FileName, target.ModuleFolderName));

                notes.Add(Strings.Current.Format("Install.FileReplace.BackedUpAs", Path.GetFileName(outcome.BackupPath ?? target.TargetPath)));

                if (target.IsOfficialModule)
                {
                    notes.Add(Strings.Current.Format("Install.FileReplace.OfficialModuleNote", target.ModuleFolderName));
                }
            }

            return (written, notes);
        }

        private static async Task<byte[]?> ReadArchiveEntryBytesAsync(string archivePath, string entryPath)
        {
            if (IsZipArchive(EffectiveArchiveExtension(archivePath)))
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var entry = archive.Entries.FirstOrDefault(candidate =>
                    candidate.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    return null;
                }

                await using var stream = entry.Open();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                return buffer.ToArray();
            }

            var temp = CreateTempFolder();
            try
            {
                await ExtractWith7ZipAsync(archivePath, temp);
                var path = Path.Combine(temp, entryPath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            }
            finally
            {
                TryDeleteDirectory(temp);
            }
        }

        [RelayCommand]
        private Task RefreshUndoList() => RefreshUndoListAsync(describe: true);

        // Reads every store that can put something back and shows them as the one list, newest first.
        // The stores are read off the UI thread, because the replaced-file scan walks the whole Modules
        // folder.
        private async Task RefreshUndoListAsync(bool describe = true)
        {
            var installPath = GameInstallPath;
            var hasInstall = !string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath);
            var modulesScope = ModulesFolderScope;
            var roots = QuarantineScopeRoots;
            var dataRoot = ActiveDataRoot;

            var entries = await Task.Run<IReadOnlyList<UndoEntry>>(() =>
            {
                try
                {
                    IReadOnlyList<ReplacedFile> files = [];
                    IReadOnlyList<BinBackup> bins = [];

                    if (hasInstall)
                    {
                        files = FileReplacementInstaller.List(ModuleScanner.GetModulesFolder(installPath));

                        var binStore = new BinBackupStore(BinBackupStore.GetDefaultRoot(dataRoot));
                        binStore.Migrate(installPath);
                        bins = binStore.List(installPath);
                    }

                    var quarantined = new QuarantineStore(QuarantineStore.DefaultRoot).Read()
                        .Where(item => UndoList.KindOf(item) switch
                        {
                            UndoKind.ModuleFolder => QuarantineScope.IsUnder(item.OriginPath, modulesScope),
                            not null => QuarantineScope.IsUnderAny(item.OriginPath, roots),
                            _ => false
                        })
                        .ToList();

                    var setAside = new BundledStackSetAsideStore(BundledStackSetAsideStore.GetDefaultRoot(dataRoot)).List();

                    var adopted = new PlatformAdoptionLog(QuarantineStore.DefaultRoot).Active()
                        .Where(record => QuarantineScope.IsUnderAny(record.TargetPath, roots))
                        .ToList();

                    return UndoList.Build(files, bins, quarantined, setAside, adopted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, "Could not list what BEM can put back");
                    return [];
                }
            });

            UndoEntries.Clear();

            foreach (var entry in entries)
                UndoEntries.Add(entry);

            HasUndoEntries = UndoEntries.Count > 0;

            if (describe)
            {
                UndoStatus = UndoEntries.Count == 0
                    ? Strings.Current["Install.Undo.Empty"]
                    : Strings.Current.Plural("Install.Undo.Summary", UndoEntries.Count);
            }
        }

        // The restore behind a row's button: the one restore its kind already had, run on the one thing
        // the row stands for.
        [RelayCommand(CanExecute = nameof(CanRunCommands))]
        private async Task RestoreUndoEntryAsync(UndoEntry? entry)
        {
            if (entry is null)
                return;

            ResolveActiveInstanceInstallTarget();

            UndoStatus = entry.Source switch
            {
                ReplacedFile file => await RestoreUndoModuleFileAsync(file),
                BinBackup backup => await RestoreUndoBinFileAsync(backup),
                QuarantinedItem item when entry.Kind == UndoKind.ModuleFolder => await RestoreUndoModuleFolderAsync(item),
                QuarantinedItem item => await RestoreUndoQuarantinedAsync(entry, item),
                SetAsideRecord record => RestoreUndoBundledCopies(record),
                AdoptedPlatformRecord adopted => await RestoreUndoAdoptedAsync(adopted),
                _ => Strings.Current["Install.NothingWasRestored"]
            };

            var status = UndoStatus;

            await RefreshUndoListAsync(describe: false);

            UndoStatus = status;
        }

        private async Task<string> RestoreUndoModuleFileAsync(ReplacedFile replaced)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.RestoreOriginalFileTitle"],
                Content = Strings.Current.Format("Install.Confirm.RestoreOriginalFileContent", replaced.RelativePath),
                PrimaryButtonText = Strings.Current["Install.Confirm.Restore"],
                CloseButtonText = Strings.Current["Install.Confirm.Cancel"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return Strings.Current["Install.NothingWasRestored"];

            MoveToRecycleBin([replaced.TargetPath]);

            var restored = await Task.Run(() => FileReplacementInstaller.RestoreOriginal(replaced.TargetPath));

            return restored
                ? Strings.Current.Format("Install.ReplacedFile.OneRestoredToRecycleBin", replaced.RelativePath)
                : Strings.Current.Format("Install.ReplacedFile.OneCouldNotBeRestored", replaced.RelativePath);
        }

        private async Task<string> RestoreUndoBinFileAsync(BinBackup backup)
        {
            if (!await ConfirmBinRestoreAsync([backup]))
                return Strings.Current["Install.NothingWasRestored"];

            var result = await Task.Run(() => BinBackupsStore.RestoreAll(GameInstallPath, [backup]));

            return DescribeBinRestore(result, 1);
        }

        private async Task<string> RestoreUndoModuleFolderAsync(QuarantinedItem item)
        {
            var folder = new ReplacedModuleFolder(item);

            if (!await ConfirmReplacedModuleRestoreAsync(folder))
                return Strings.Current["Install.NothingWasRestored"];

            var store = new QuarantineStore(QuarantineStore.DefaultRoot);
            var result = await Task.Run(() => ReplacedModuleQuarantine.Restore(store, folder.Id));

            return result.Describe();
        }

        private static async Task<string> RestoreUndoQuarantinedAsync(UndoEntry entry, QuarantinedItem item)
        {
            var store = new QuarantineStore(QuarantineStore.DefaultRoot);
            var restored = await Task.Run(() => store.Restore(item.Id));

            return Strings.Current.Format(restored ? "Install.Undo.RestoredOne" : "Install.Undo.NotRestored", entry.Title);
        }

        // Undoing a launch-time adoption deletes the copy BEM made and nothing else; the folder the module
        // shipped is untouched, and the next launch does not copy it again.
        private static async Task<string> RestoreUndoAdoptedAsync(AdoptedPlatformRecord record)
        {
            var undone = await Task.Run(() => new PlatformAdoptionLog(QuarantineStore.DefaultRoot).Undo(record.Id));

            return Strings.Current.Format(
                undone ? "Install.Undo.RestoredOne" : "Install.Undo.NotRestored",
                Strings.Current.Format("Core.Install.Undo.AdoptedPlatformFolder", record.ModuleFolderName, record.TargetPlatformFolder));
        }

        private static string RestoreUndoBundledCopies(SetAsideRecord record)
        {
            try
            {
                var store = new BundledStackSetAsideStore(BundledStackSetAsideStore.GetDefaultRoot(ActiveDataRoot));

                return store.Undo(record) switch
                {
                    UndoOutcome.Restored => Strings.Current.Format("Environment.SetAside.Restored", record.Bundler),
                    UndoOutcome.ModChangedSince => Strings.Current.Format("Environment.SetAside.ModChangedSince", record.Bundler),
                    _ => Strings.Current.Format("Environment.SetAside.BackupMissing", record.Bundler, record.BackupFolder)
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Could not put back the bundled copies of {record.Bundler}");
                return Strings.Current.Format("Environment.SetAside.Failed", record.Bundler, ex.Message);
            }
        }

        private async Task<bool> ConfirmBinRestoreAsync(IReadOnlyList<BinBackup> backups)
        {
            const int NamesShown = 12;

            var listing = string.Join(Environment.NewLine, backups.Take(NamesShown).Select(backup => backup.DisplayName));

            if (backups.Count > NamesShown)
            {
                listing += Environment.NewLine + Strings.Current.Plural("Install.AndMoreCount", backups.Count - NamesShown);
            }

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.RestoreBinFilesTitle"],
                Content = Strings.Current.Plural("Install.Confirm.RestoreBinFilesContent", backups.Count, listing),
                PrimaryButtonText = Strings.Current["Install.Confirm.Restore"],
                CloseButtonText = Strings.Current["Install.Confirm.Cancel"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private static string DescribeBinRestore(BackupRestoreResult result, int attempted)
        {
            var text = Strings.Current.Plural("Install.BinBackup.RestoredOf", attempted, result.Restored);

            if (result.Missing > 0)
            {
                text += " " + Strings.Current.Plural("Install.BinBackup.NoLongerThere", result.Missing);
            }

            if (result.Failed.Count > 0)
            {
                text += " " + Strings.Current.Plural("Install.BinBackup.CouldNotBeWritten", result.Failed.Count, string.Join(", ", result.Failed));
            }

            return text;
        }

        private async Task<bool> ConfirmReplacedModuleRestoreAsync(ReplacedModuleFolder folder)
        {
            var installed = Directory.Exists(folder.Item.OriginPath);

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Install.Confirm.RestoreReplacedModuleTitle", folder.ModuleFolderName),
                Content = Strings.Current.Format("Install.Confirm.RestoreReplacedModuleContent", folder.DisplayName, folder.Item.OriginPath)
                    + (installed
                        ? Environment.NewLine + Environment.NewLine + Strings.Current.Format("Install.Confirm.RestoreReplacedModuleInstalledNote", folder.ModuleFolderName)
                        : string.Empty),
                PrimaryButtonText = Strings.Current["Install.Confirm.Restore"],
                CloseButtonText = Strings.Current["Install.Confirm.Cancel"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private void ShowReplacedModuleFolders()
        {
            _ = RefreshUndoListAsync(describe: false);
        }

        private string ExistingFolderStage(bool replacing) => replacing
            ? KeepReplacedModuleFolders ? Strings.Current["Install.Stage.SettingAside"] : Strings.Current["Install.Stage.Removing"]
            : KeepReplacedModuleFolders ? Strings.Current["Install.Stage.Copying"] : Strings.Current["Install.Stage.WritingOver"];

        // Not replacing the folder means writing over it in place, which destroys every file the new
        // version happens to overwrite just as completely as deleting the folder would. A copy goes into
        // the same quarantine a replaced folder goes to, and is restored from the same list.
        private string PreserveExistingModuleFolder(string targetPath, string installFolderName, string archiveName)
        {
            if (!KeepReplacedModuleFolders)
            {
                return Strings.Current.Format("Install.Preserve.WrittenOverNoCopy", installFolderName);
            }

            var store = new QuarantineStore(QuarantineStore.DefaultRoot);
            var kept = ReplacedModuleQuarantine.Preserve(store, targetPath, archiveName);

            var pruned = ReplacedModuleQuarantine.Prune(
                store, KeptModuleFolderLimit, ModuleScanner.GetModulesFolder(InstallRoot));

            var note = Strings.Current.Format("Install.Preserve.WrittenOverWithCopy", installFolderName, kept.Item.StoredPath);

            return pruned.Removed.Count == 0 ? note : $"{note}. {pruned.Describe()}";
        }

        // The folder that is already there is moved into the quarantine, not deleted, unless the user has
        // turned that off. A failure here throws, so the caller abandons the install with the folder intact.
        //
        // targetPath is not re-resolved here against the active instance, unlike the undo restores
        // above: this method is only ever reached through InstallArchiveContentsAsync <-
        // InstallSingleArchiveAsync <- one of InstallModsAsync, InstallContextArchiveAsync or
        // InstallModUpdatesAsync, and all three of those already call ResolveActiveInstanceInstallTarget
        // at their own entry, before targetPath is built from GameInstallPath further down that same call
        // chain. Resolving again here would read the same, already-current value a second time.
        private string ReplaceExistingModuleFolder(string targetPath, string installFolderName, string archiveName)
        {
            if (!KeepReplacedModuleFolders)
            {
                DeleteDirectoryForReplace(targetPath);
                return Strings.Current.Format("Install.Replace.DeletedNoCopy", installFolderName);
            }

            var store = new QuarantineStore(QuarantineStore.DefaultRoot);
            var kept = ReplacedModuleQuarantine.Store(store, targetPath, archiveName);

            var pruned = ReplacedModuleQuarantine.Prune(
                store, KeptModuleFolderLimit, ModuleScanner.GetModulesFolder(InstallRoot));

            var note = Strings.Current.Format("Install.Replace.KeptCopy", installFolderName, kept.Item.StoredPath);

            return pruned.Removed.Count == 0 ? note : $"{note}. {pruned.Describe()}";
        }

        private async Task OfferDeleteArchivesAsync(List<string> archivePaths)
        {
            if (archivePaths.Count == 0)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Confirm.DeleteInstalledArchivesTitle"],
                Content = Strings.Current.Plural("Install.Confirm.DeleteInstalledArchivesContent", archivePaths.Count),
                PrimaryButtonText = Strings.Current["Install.Confirm.Delete"],
                CloseButtonText = Strings.Current["Install.Confirm.Keep"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            var result = await DialogText.ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                if (MoveKeptArchivesToInstalled)
                    MoveIntoSetAsideFolder(archivePaths, ArchiveFolders.Installed);

                return;
            }

            var failures = MoveToRecycleBin(archivePaths);

            if (failures.Count > 0)
            {
                StatusMessage = Strings.Current.Plural(
                    "Install.Archive.MovedToRecycleBinPartial",
                    archivePaths.Count,
                    archivePaths.Count - failures.Count,
                    failures.Count,
                    string.Join("; ", failures.Select(f => $"{Path.GetFileName(f.Path)} ({f.Reason})")));
            }
        }

        // Moves each archive into ARCHIVED or INSTALLED and takes it off the list of archives waiting to
        // install. Like MoveToRecycleBin it runs after whatever the file belonged to has succeeded, so one
        // locked file is reported and logged, never thrown, and never stops the rest of the batch.
        private int MoveIntoSetAsideFolder(IReadOnlyList<string> archivePaths, string folderName)
        {
            var failures = new List<(string Path, string Reason)>();
            var moved = 0;

            foreach (var path in archivePaths.Where(File.Exists))
            {
                try
                {
                    var destination = ArchiveFolders.MoveInto(path, ArchiveFolders.FolderFor(path, ArchivesFolderPath, folderName));
                    LoggingService.Log($"Moved archive into {folderName}: {path} -> {destination}");
                    HandleArchiveRemoved(path);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, $"Could not move '{path}' into {folderName}");
                    failures.Add((path, ex.Message));
                }
            }

            StatusMessage = failures.Count == 0
                ? Strings.Current.Plural("Install.Archive.MovedIntoFolder", moved, folderName)
                : Strings.Current.Plural(
                    "Install.Archive.MovedIntoFolderPartial",
                    moved + failures.Count,
                    moved,
                    failures.Count,
                    string.Join("; ", failures.Select(f => $"{Path.GetFileName(f.Path)} ({f.Reason})")),
                    folderName);

            return moved;
        }

        // Every file BEM gets rid of on the user's behalf goes here, so a wrong call costs one trip to
        // the Recycle Bin rather than the file. A failure on one path - locked by an antivirus scan,
        // still open in another program - must never stop the rest of the batch, and must never
        // propagate as an exception: every call site here runs after whatever the file belonged to has
        // already succeeded, so a caller that let this throw was catching its own unrelated "install
        // failed" handler around a cleanup step that has nothing to do with whether the install worked.
        private static IReadOnlyList<(string Path, string Reason)> MoveToRecycleBin(IEnumerable<string> paths)
        {
            var failures = new List<(string Path, string Reason)>();

            foreach (var path in paths.Where(File.Exists))
            {
                try
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    LoggingService.LogException(ex, $"Could not move '{path}' to the Recycle Bin");
                    failures.Add((path, ex.Message));
                }
            }

            return failures;
        }

        // The installed modules decide whether an archive of loose files has anywhere to go, so the
        // ModuleData index is read once per install folder rather than once per archive.
        private ModuleDataContext EnsureModuleDataContext()
        {
            var modulesFolder = string.IsNullOrWhiteSpace(GameInstallPath)
                ? string.Empty
                : ModuleScanner.GetModulesFolder(GameInstallPath);

            if (moduleDataContext.ModulesFolderPath.Equals(modulesFolder, StringComparison.OrdinalIgnoreCase))
            {
                return moduleDataContext;
            }

            moduleDataContext = new ModuleDataContext(modulesFolder, ModuleScanner.OfficialModuleFolderNames(modulesFolder));
            return moduleDataContext;
        }

        // The same context with the manifest scan on a worker: the first call at start-up opens every
        // SubModule.xml in the Modules folder, which held the UI thread for seconds when it ran inline.
        private async Task<ModuleDataContext> EnsureModuleDataContextAsync()
        {
            var modulesFolder = string.IsNullOrWhiteSpace(GameInstallPath)
                ? string.Empty
                : ModuleScanner.GetModulesFolder(GameInstallPath);

            if (moduleDataContext.ModulesFolderPath.Equals(modulesFolder, StringComparison.OrdinalIgnoreCase))
            {
                return moduleDataContext;
            }

            var official = await Task.Run(() => ModuleScanner.OfficialModuleFolderNames(modulesFolder));

            moduleDataContext = new ModuleDataContext(modulesFolder, official);
            return moduleDataContext;
        }

        private static async Task<ModArchiveEntry> AnalyzeArchiveAsync(string filePath, ModuleDataContext context, CancellationToken cancellationToken = default)
        {
            // Existence, length and the content sniff each touch the file, and callers resume on the UI
            // thread between archives, so a slow or busy disk froze the window one archive at a time.
            var (present, fileLength, effectiveExtension) = await Task.Run(() =>
                File.Exists(filePath)
                    ? (true, new FileInfo(filePath).Length, EffectiveArchiveExtension(filePath))
                    : (false, 0L, string.Empty), cancellationToken);

            if (!present)
            {
                return new ModArchiveEntry(Path.GetFileName(filePath), filePath, Strings.Current["Install.FileNotFound"], []);
            }

            // A claimed extension can lie - the exact "RAR saved as .zip" failure the auto-update fix
            // closed for BEM's own downloads still reaches this method from anywhere else an archive can
            // arrive: a file a user renamed by hand, or one downloaded through a browser rather than
            // through BEM. Sniffing the real content and letting it override a claimed extension, rather
            // than only consulting it when the extension is unrecognized, is what catches both that case
            // and a merely-missing extension in one check.
            ModArchiveEntry entry;
            if (IsZipArchive(effectiveExtension))
            {
                entry = await AnalyzeZipArchiveAsync(filePath, context, cancellationToken);
            }
            else if (IsSevenZipArchive(effectiveExtension))
            {
                entry = await AnalyzeExternalArchiveAsync(filePath, context, cancellationToken);
            }
            else
            {
                entry = new ModArchiveEntry(Path.GetFileName(filePath), filePath, Strings.Current["Install.UnsupportedArchiveFormat"], []);
            }

            entry.FileSizeDisplay = FormatFileSize(fileLength);
            return entry;
        }

        private static Task<ModArchiveEntry> AnalyzeZipArchiveAsync(string filePath, ModuleDataContext context, CancellationToken cancellationToken = default)
        {
            var displayName = Path.GetFileName(filePath);

            // ZipFile/OpenRead can block for a long time on huge archives; keep it off the UI thread.
            return Task.Run(async () =>
            {
                var modules = new List<DetectedModule>();
                try
                {
                    using var stream = new FileStream(
                        filePath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                            Options = FileOptions.SequentialScan
                        });
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                    var entryPaths = archive.Entries.Select(entry => entry.FullName).ToList();
                    var subModuleEntries = archive.Entries
                        .Where(entry => GetArchiveEntryFileName(entry.FullName).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var entry in subModuleEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var folderPath = GetEntryFolder(entry.FullName);
                        var manifest = ManifestFacts.None;
                        try
                        {
                            manifest = await ReadManifestFactsAsync(entry).ConfigureAwait(false);
                        }
                        catch
                        {
                            // best-effort
                        }

                        var installFolder = DetermineInstallFolder(folderPath, manifest.Name, displayName);
                        modules.Add(new DetectedModule(folderPath, installFolder, manifest.Name, manifest.Url, manifest.Id));
                    }

                    return BuildArchiveEntry(displayName, filePath, modules, ArchiveLayout.FindBinPayloads(entryPaths), entryPaths, context);
                }
                catch (InvalidDataException)
                {
                    return new ModArchiveEntry(displayName, filePath, Strings.Current["Install.UnsupportedArchiveFormat"], []);
                }
            }, cancellationToken);
        }

        private static ModArchiveEntry BuildArchiveEntry(
            string displayName,
            string filePath,
            IReadOnlyList<DetectedModule> modules,
            IReadOnlyList<BinPayload> binPayloads,
            IReadOnlyList<string> entryPaths,
            ModuleDataContext context)
        {
            if (modules.Count == 0 && binPayloads.Count == 0)
            {
                var loose = context.ModulesFolderPath.Length > 0 && FileReplacementArchive.IsLooseFileArchive(entryPaths);

                // An archive carrying an installed mod's own Nexus id patches that mod, never the base game.
                var own = loose
                    ? OwnModulePatch.Resolve(
                        entryPaths, Path.GetFileName(filePath), context.ModulesFolderPath, ModuleArchiveLinkStore.Default.ByModuleId())
                    : null;

                if (own?.Refusal is { } refusal)
                    return new ModArchiveEntry(displayName, filePath, refusal, []) { UnrecognizedLayout = refusal };

                var replacements = own?.Files
                    ?? (loose
                        ? FileReplacementArchive.Resolve(entryPaths, context.ModulesFolderPath, context.OfficialModuleFolders)
                        : []);

                if (replacements.Any(resolution => resolution.Candidates.Count > 0))
                {
                    return new ModArchiveEntry(displayName, filePath, DescribeFileReplacements(replacements), [])
                    {
                        FileReplacements = replacements
                    };
                }

                var reason = ArchiveLayout.DescribeUnrecognizedLayout(entryPaths);
                return new ModArchiveEntry(displayName, filePath, reason, []) { UnrecognizedLayout = reason };
            }

            return new ModArchiveEntry(displayName, filePath, DescribeArchiveContents(modules, binPayloads), modules)
            {
                BinPayloads = binPayloads
            };
        }

        private static string DescribeFileReplacements(IReadOnlyList<FileReplacementResolution> replacements)
        {
            var matched = replacements.Where(resolution => resolution.Candidates.Count > 0).ToList();
            var unmatched = replacements.Count - matched.Count;
            var modules = matched
                .SelectMany(resolution => resolution.Candidates.Select(candidate => candidate.ModuleFolderName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var description = Strings.Current.Plural("Install.Describe.ReplacesFiles", matched.Count, string.Join(", ", modules));

            return unmatched == 0
                ? description
                : $"{description} {Strings.Current.Plural("Install.Describe.UnmatchedFiles", unmatched)}";
        }

        private static string DescribeArchiveContents(IReadOnlyList<DetectedModule> modules, IReadOnlyList<BinPayload> binPayloads)
        {
            var parts = new List<string>();

            if (modules.Count > 0)
            {
                parts.Add(Strings.Current.Plural("Install.Describe.ModuleCount", modules.Count));
            }

            foreach (var payload in binPayloads)
            {
                parts.Add(Strings.Current.Plural("Install.Describe.BinPayloadFor", payload.Files.Count, payload.PlatformFolder));
            }

            var description = Strings.Current.Format("Install.Describe.Detected", string.Join(" and ", parts));
            var injectors = binPayloads
                .SelectMany(payload => payload.InjectorProxies)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return injectors.Count > 0
                ? Strings.Current.Format("Install.Describe.ContainsInjector", description, string.Join(", ", injectors))
                : description;
        }

        private static async Task<ModArchiveEntry> AnalyzeExternalArchiveAsync(string filePath, ModuleDataContext context, CancellationToken cancellationToken = default)
        {
            var displayName = Path.GetFileName(filePath);

            // This is where the reported failure actually happened: reading a .7z or .rar needs 7-Zip
            // before anything is installed, so an absent 7-Zip surfaced as a raw exception on the row
            // rather than as an instruction. Answered here as a status the user can act on, and, only
            // if the box in Settings, Toolkit is ticked, by installing 7-Zip and carrying on.
            var provision = await EnsureSevenZipAsync(cancellationToken);

            if (!provision.Found)
                return new ModArchiveEntry(displayName, filePath, provision.Message, []);

            var temp = CreateTempFolder();
            try
            {
                // The listing is what decides the layout; only SubModule.xml is worth unpacking, and
                // unpacking the whole archive just to read it would cost minutes on a large mod.
                var entryPaths = await ListWith7ZipAsync(filePath, cancellationToken);

                await ExtractWith7ZipAsync(
                    filePath,
                    temp,
                    includePatterns: ["SubModule.xml"],
                    cancellationToken: cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var extracted = Directory.EnumerateFiles(temp, "*", System.IO.SearchOption.AllDirectories).ToList();
                var modules = new List<DetectedModule>();

                foreach (var subModulePath in extracted.Where(f => Path.GetFileName(f).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folder = Path.GetDirectoryName(subModulePath)!;
                    var relativeFolderRaw = Path.GetRelativePath(temp, folder);
                    var relativeFolder = string.IsNullOrWhiteSpace(relativeFolderRaw) || relativeFolderRaw == "." ? string.Empty : relativeFolderRaw;
                    var manifest = await ReadManifestFactsFromFileAsync(subModulePath).ConfigureAwait(false);
                    var installFolder = DetermineInstallFolderForPath(relativeFolder, manifest.Name, displayName);
                    modules.Add(new DetectedModule(relativeFolder.Replace(Path.DirectorySeparatorChar, '/'), installFolder, manifest.Name, manifest.Url, manifest.Id));
                }

                var entry = BuildArchiveEntry(
                    displayName, filePath, modules, ArchiveLayout.FindBinPayloads(entryPaths), entryPaths, context);

                if (provision.Message.Length > 0)
                    entry.Status = $"{provision.Message} {entry.Status}";

                return entry;
            }
            finally
            {
                TryDeleteDirectory(temp);
            }
        }

        private static string DetermineInstallFolder(string folderPath, string? moduleName, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                return Path.GetFileName(folderPath.TrimEnd('/', '\\'));
            }

            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                return moduleName;
            }

            return Path.GetFileNameWithoutExtension(displayName);
        }

        private static string DetermineInstallFolderForPath(string relativeFolder, string? moduleName, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(relativeFolder))
            {
                return Path.GetFileName(relativeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                return moduleName;
            }

            return Path.GetFileNameWithoutExtension(displayName);
        }

        private static string CreateTempFolder()
        {
            var path = Path.Combine(Path.GetTempPath(), "BLModInstaller", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private static string NormalizeInstallFolderName(string installFolderName)
        {
            var trimmed = (installFolderName ?? string.Empty).Trim();
            trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
            var fileName = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            {
                throw new InvalidOperationException("Invalid install folder name.");
            }

            return fileName;
        }

        private static void DeleteDirectoryForReplace(string path)
        {
            // Installing over a subscribed copy by deleting it is the same breakage as uninstalling it:
            // the folder is Steam's, shared by every install on the machine, and comes back anyway.
            WorkshopContent.Refuse(path);

            if (!Directory.Exists(path))
            {
                return;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = System.IO.FileAttributes.ReparsePoint
            };

            static void TrySetNormalAttributes(string entryPath)
            {
                try
                {
                    File.SetAttributes(entryPath, System.IO.FileAttributes.Normal);
                }
                catch
                {
                }
            }

            foreach (var entryPath in Directory.EnumerateFiles(path, "*", options))
            {
                TrySetNormalAttributes(entryPath);
            }

            foreach (var entryPath in Directory.EnumerateDirectories(path, "*", options))
            {
                TrySetNormalAttributes(entryPath);
            }

            TrySetNormalAttributes(path);
            Directory.Delete(path, recursive: true);
        }

        private static string GetEntryFolder(string entryFullName)
        {
            var normalized = entryFullName.Replace('\\', '/');
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                return string.Empty;
            }

            return normalized[..lastSlash];
        }

        private static string GetArchiveEntryFileName(string entryFullName)
        {
            if (string.IsNullOrWhiteSpace(entryFullName))
            {
                return string.Empty;
            }

            var normalized = entryFullName.Replace('\\', '/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        }

        private static async Task<ManifestFacts> ReadManifestFactsAsync(ZipArchiveEntry entry)
        {
            await using var stream = entry.Open();
            return ReadManifestFacts(await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None));
        }

        private static async Task<ManifestFacts> ReadManifestFactsFromFileAsync(string filePath)
        {
            await using var stream = File.OpenRead(filePath);
            return ReadManifestFacts(await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None));
        }

        private static ManifestFacts ReadManifestFacts(XDocument document) =>
            new(ReadElement(document.Root, "Name"), ReadElement(document.Root, "Url"), ReadElement(document.Root, "Id"));

        private static string? ReadElement(XElement? root, string name)
        {
            var element = root?.Element(name);
            var value = element?.Attribute("value")?.Value ?? element?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static async Task<PlatformSkip> ExtractModuleFolderAnyAsync(
            string archivePath,
            DetectedModule module,
            string destinationRoot,
            string? platformFolder,
            IProgress<int>? progress,
            IProgress<string>? stage,
            string modulePrefix)
        {
            var extension = EffectiveArchiveExtension(archivePath);
            return IsZipArchive(extension)
                ? await ExtractModuleFolderZipAsync(archivePath, module, destinationRoot, platformFolder, progress)
                : await ExtractModuleFolder7ZipAsync(archivePath, module, destinationRoot, platformFolder, progress, stage, modulePrefix);
        }

        private static async Task<PlatformSkip> ExtractModuleFolderZipAsync(
            string archivePath,
            DetectedModule module,
            string destinationRoot,
            string? platformFolder,
            IProgress<int>? progress)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var wanted = new List<(ZipArchiveEntry Entry, string RelativePath)>();
            var skippedFolders = new List<string>();
            var skippedFiles = 0;
            long skippedBytes = 0;

            var inModule = archive.Entries
                .Where(entry => IsFileEntry(entry) && IsEntryWithin(entry.FullName, module.ArchiveFolderPath))
                .Select(entry => (Entry: entry, RelativePath: TrimFolderPrefix(entry.FullName, module.ArchiveFolderPath)))
                .ToList();

            // A folder is only spare when the same bin folder also carries the platform this install
            // loads, and an archive is read as a stream, so the whole entry list decides before a
            // single file is written.
            var redundantBinFolders = PlatformBinaries.RedundantBinFolders(
                inModule.Select(item => item.RelativePath),
                platformFolder);

            foreach (var (entry, relativePath) in inModule)
            {
                var foreignFolderName = PlatformBinaries.ForeignPlatformFolderName(relativePath, platformFolder, redundantBinFolders);

                if (foreignFolderName is not null)
                {
                    skippedFiles++;
                    skippedBytes += entry.Length;

                    if (!skippedFolders.Contains(foreignFolderName, StringComparer.OrdinalIgnoreCase))
                    {
                        skippedFolders.Add(foreignFolderName);
                    }

                    continue;
                }

                wanted.Add((entry, relativePath));
            }

            var lastPercent = -1;
            for (int i = 0; i < wanted.Count; i++)
            {
                var (entry, relativePath) = wanted[i];
                var destinationPath = GetSafeDestinationPath(destinationRoot, relativePath);
                await ExtractEntryAsync(entry, destinationPath);

                lastPercent = ReportPercentIfChanged(progress, (i + 1) * 100 / wanted.Count, lastPercent);
            }

            return new PlatformSkip(skippedFolders, skippedFiles, skippedBytes);
        }

        private static async Task<PlatformSkip> ExtractModuleFolder7ZipAsync(
            string archivePath,
            DetectedModule module,
            string destinationRoot,
            string? platformFolder,
            IProgress<int>? progress,
            IProgress<string>? stage,
            string modulePrefix)
        {
            var temp = CreateTempFolder();
            try
            {
                await ExtractWith7ZipAsync(archivePath, temp, progress: progress);
                var sourceFolder = Path.Combine(temp, module.ArchiveFolderPath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(sourceFolder))
                {
                    sourceFolder = temp;
                }

                // The archive is already unpacked into a throwaway folder, so the binaries this install
                // cannot load are dropped from there rather than copied into the game. Whatever survives
                // the delete is copied with the rest of the module, and the skip counts only what went.
                var skip = PlatformBinaries.Remove(PlatformBinaries.FindInModule(sourceFolder, platformFolder));

                stage?.Report(modulePrefix + Strings.Current.Format("Install.Stage.CopyingFilesIntoPlace", Path.GetFileName(destinationRoot)));
                CopyDirectory(sourceFolder, destinationRoot, progress);

                return skip;
            }
            finally
            {
                stage?.Report(modulePrefix + Strings.Current["Install.Stage.CleaningUpTempFiles"]);
                TryDeleteDirectory(temp);
            }
        }

        // Reporting every file would post one dispatcher callback per entry and starve the UI thread on
        // archives with tens of thousands of files, so only whole-percent changes are forwarded.
        private static int ReportPercentIfChanged(IProgress<int>? progress, int percent, int lastPercent)
        {
            if (progress is null || percent == lastPercent)
            {
                return lastPercent;
            }

            progress.Report(percent);
            return percent;
        }

        private static string GetSafeDestinationPath(string root, string relativePath)
        {
            // Normalize path to handle both forward and backward slashes
            var normalizedPath = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

            // Check for null bytes and other dangerous characters
            if (normalizedPath.Contains('\0'))
            {
                throw new InvalidOperationException("Invalid path: null byte detected.");
            }

            // Check for directory traversal patterns
            if (normalizedPath.Contains("..\\", StringComparison.Ordinal) ||
                normalizedPath.Contains("..//", StringComparison.Ordinal) ||
                normalizedPath.Contains(":\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid path: directory traversal detected.");
            }

            // Validate path length (Windows MAX_PATH is 260, but we allow some buffer)
            if (normalizedPath.Length > 200)
            {
                throw new InvalidOperationException($"Path too long: {normalizedPath.Length} chars.");
            }

            var combined = Path.Combine(root, normalizedPath);
            var fullPath = Path.GetFullPath(combined);
            var fullRoot = Path.GetFullPath(root);

            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Archive entry attempted to extract outside target directory.");
            }

            // Check for relative path components after normalization
            if (fullPath.Contains("..\\"))
            {
                throw new InvalidOperationException("Invalid path: relative path after normalization.");
            }

            return fullPath;
        }

        private static bool IsFileEntry(ZipArchiveEntry entry) => !string.IsNullOrWhiteSpace(entry.Name);

        private static bool IsEntryWithin(string entryName, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return true;
            }

            var normalizedEntry = entryName.Replace('\\', '/');
            var normalizedFolder = folderPath.TrimEnd('/');
            return normalizedEntry.StartsWith(normalizedFolder + '/', StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimFolderPrefix(string entryFullName, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return entryFullName.Replace('/', Path.DirectorySeparatorChar);
            }

            var normalizedEntry = entryFullName.Replace('\\', '/');
            var prefix = folderPath.TrimEnd('/') + '/';
            var trimmed = normalizedEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalizedEntry[prefix.Length..]
                : normalizedEntry;

            return trimmed.Replace('/', Path.DirectorySeparatorChar);
        }

        private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destinationPath)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Core.Io.InstanceFiles.BreakLink(destinationPath);
            await using var destinationStream = File.Create(destinationPath);
            await using var sourceStream = entry.Open();
            await sourceStream.CopyToAsync(destinationStream);
            await destinationStream.DisposeAsync();
            Core.Io.MarkOfTheWeb.RemoveFromCode(destinationPath);
        }

        private static async Task ExtractWith7ZipAsync(
            string archivePath,
            string destinationFolder,
            IReadOnlyList<string>? includePatterns = null,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var sevenZipPath = Find7ZipExecutable();
            var includeArgs = string.Empty;
            if (includePatterns is { Count: > 0 })
            {
                includeArgs = " " + string.Join(" ", includePatterns.Select(BuildSevenZipRecursiveIncludeSwitch));
            }

            // -bsp2 sends progress percentages to stderr; -bso0 suppresses file-listing stdout.
            var result = await RunProcessCaptureAsync(
                fileName: sevenZipPath,
                arguments: $"x -y -o\"{destinationFolder}\" -bso0 -bsp2{includeArgs} \"{archivePath}\"",
                stderrProgress: progress,
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "7-Zip failed to extract archive." : error.Trim());
            }

            // 7-Zip carries the archive's downloaded-from-the-internet mark onto every file it extracts.
            Core.Io.MarkOfTheWeb.RemoveFromTree(destinationFolder);
        }

        private static async Task<IReadOnlyList<string>> ListWith7ZipAsync(string archivePath, CancellationToken cancellationToken)
        {
            var sevenZipPath = Find7ZipExecutable();

            // -slt prints one key/value block per entry; -ba drops the banner so every block is an entry.
            var result = await RunProcessCaptureAsync(
                fileName: sevenZipPath,
                arguments: $"l -slt -ba -y \"{archivePath}\"",
                stderrProgress: null,
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "7-Zip failed to list archive." : error.Trim());
            }

            return ParseSevenZipListing(result.StdOut);
        }

        private static List<string> ParseSevenZipListing(string output)
        {
            const string pathPrefix = "Path = ";
            const string attributesPrefix = "Attributes = ";

            var paths = new List<string>();
            string? path = null;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                if (line.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    path = line[pathPrefix.Length..];
                }
                else if (line.StartsWith(attributesPrefix, StringComparison.Ordinal) && path is not null)
                {
                    if (!line[attributesPrefix.Length..].TrimStart().StartsWith('D'))
                    {
                        paths.Add(path);
                    }

                    path = null;
                }
            }

            return paths;
        }

        private static string BuildSevenZipRecursiveIncludeSwitch(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return string.Empty;
            }

            // -ir!pattern = include filenames (recursive wildcard search).
            // NOTE: We intentionally do not quote the pattern here; our callsites use safe, fixed filenames.
            return $"-ir!{pattern}";
        }

        private sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

        private static async Task<ProcessRunResult> RunProcessCaptureAsync(
            string fileName,
            string arguments,
            IProgress<int>? stderrProgress,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = ReadStderrAsync(process.StandardError, stderrProgress, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort
                }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessRunResult(process.ExitCode, stdout, stderr);
        }

        private static async Task<string> ReadStderrAsync(
            StreamReader stderr,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var buffer = new char[256];
            var segment = new StringBuilder();
            var fullOutput = new StringBuilder();
            var lastPercent = -1;

            while (true)
            {
                int read;
                try
                {
                    read = await stderr.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (read == 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    fullOutput.Append(c);

                    if (c == '\r' || c == '\n')
                    {
                        if (segment.Length > 0 && progress != null)
                            lastPercent = TryReportSevenZipProgress(segment.ToString(), progress, lastPercent);
                        segment.Clear();
                    }
                    else
                    {
                        segment.Append(c);
                    }
                }
            }

            if (segment.Length > 0 && progress != null)
                TryReportSevenZipProgress(segment.ToString(), progress, lastPercent);

            return fullOutput.ToString();
        }

        private static int TryReportSevenZipProgress(string segment, IProgress<int> progress, int lastPercent)
        {
            // 7-Zip outputs segments like "  42%" or "100%"
            var trimmed = segment.TrimStart();
            var percentIndex = trimmed.IndexOf('%');
            if (percentIndex <= 0)
                return lastPercent;

            var numPart = trimmed[..percentIndex].TrimEnd();
            if (int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) && pct is >= 0 and <= 100)
                return ReportPercentIfChanged(progress, pct, lastPercent);

            return lastPercent;
        }

        // The mod safety scanner reads a .zip itself and has no way at all to open a .7z or a .rar,
        // which is why those used to be reported "could not look" rather than checked. This hands it
        // the same 7-Zip the installer extracts with, pulling out only the code and script files the
        // scanner would read: the textures and meshes that are the bulk of a mod are never written.
        //
        // Null means it could not be opened, which stays "could not look". Nothing extracted here is
        // ever run, and the scanner removes the folder as soon as it has read it.
        public static string? ExtractCodeFilesForSafetyScan(
            string archivePath,
            IReadOnlyList<string> includePatterns,
            CancellationToken cancellationToken)
        {
            if (!TryFind7ZipExecutable(out var sevenZipPath))
                return null;

            var temp = CreateTempFolder();
            var includeArgs = includePatterns is { Count: > 0 }
                ? " " + string.Join(" ", includePatterns.Select(BuildSevenZipRecursiveIncludeSwitch))
                : string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x -y -o\"{temp}\" -bso0{includeArgs} \"{archivePath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(startInfo);

                if (process is null)
                {
                    TryDeleteDirectory(temp);
                    return null;
                }

                using var kill = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        _ = ex;
                    }
                });

                // stdout is suppressed by -bso0, so reading stderr to the end cannot deadlock on a
                // full stdout buffer.
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                    return temp;

                LoggingService.Log(
                    $"7-Zip could not open '{archivePath}' for the safety scan: {error.Trim()}",
                    LogLevel.Warn);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                LoggingService.LogException(ex, $"Failed to open '{archivePath}' for the safety scan");
            }

            TryDeleteDirectory(temp);
            return null;
        }

        private static string Find7ZipExecutable()
        {
            if (TryFind7ZipExecutable(out var path))
            {
                return path;
            }

            throw new FileNotFoundException(Core.Toolkit.SevenZipRequirement.NotInstalled);
        }

        // One probe order, shared with the Toolkit section, so the two can never disagree about whether
        // 7-Zip is there.
        private static bool TryFind7ZipExecutable(out string path)
        {
            path = BannerlordEnvironmentManager.Core.Toolkit.SevenZipLocator.Locate() ?? string.Empty;

            return path.Length > 0;
        }

        // The archive's real format, not its claimed one, for every reader dispatch: analysis read an
        // archive one way and extraction another, and while only analysis sniffed, a 7z named .zip
        // listed its modules and then failed on extraction with "End of Central Directory record could
        // not be found."
        private static string EffectiveArchiveExtension(string filePath) =>
            ArchiveFormatSniffer.EffectiveExtension(filePath);

        // A folder of mods is analyzed several archives at a time, so without this two .rar files would
        // start two winget installs of the same package at once.
        private static readonly SemaphoreSlim SevenZipGate = new(1, 1);

        // Only ever runs an installer when the user ticked the box in Settings, Toolkit, and only when
        // an archive BEM has been asked to read cannot be opened without it.
        private static async Task<Core.Toolkit.SevenZipProvision> EnsureSevenZipAsync(CancellationToken cancellationToken)
        {
            await SevenZipGate.WaitAsync(cancellationToken);

            try
            {
                return await ProvisionSevenZipAsync(cancellationToken);
            }
            finally
            {
                SevenZipGate.Release();
            }
        }

        private static async Task<Core.Toolkit.SevenZipProvision> ProvisionSevenZipAsync(CancellationToken cancellationToken)
        {
            var ledger = new Core.Toolkit.ToolkitLedger();
            var installer = new Core.Toolkit.ToolkitInstaller(
                Core.Toolkit.WinGetRunner.RunAsync,
                Core.Toolkit.ToolkitCatalog.Locate,
                Core.Toolkit.WinGetLocator.Locate,
                ledger);

            var tool = Core.Toolkit.ToolkitCatalog.ById(Core.Toolkit.ToolkitCatalog.SevenZipId);

            var provision = await Core.Toolkit.SevenZipRequirement.EnsureAsync(
                Core.Toolkit.SevenZipLocator.Locate,
                InstallSevenZipOnDemand() && tool is not null,
                token => installer.InstallAsync(tool!, token),
                cancellationToken);

            if (provision.Message.Length > 0)
                LoggingService.Log(provision.Message, provision.Found ? LogLevel.Info : LogLevel.Warn);

            return provision;
        }

        // Read at the moment of need rather than held, so ticking the box in Settings takes effect on
        // the very next install instead of on the next launch.
        private static bool InstallSevenZipOnDemand() =>
            new Core.Toolkit.ToolkitOptionsStore(Core.Toolkit.ToolkitOptionsStore.DefaultPath)
                .Load().InstallSevenZipOnDemand;

        private static void CopyDirectory(string source, string destination, IProgress<int>? progress)
        {
            LoggingService.Log(
                $"{InstallLogArchiveLinks.DirectoryCopied}{source}{InstallLogArchiveLinks.CopyDestinationSeparator}{destination}");
            var dirs = Directory.GetDirectories(source, "*", System.IO.SearchOption.AllDirectories);
            LoggingService.Log($"Creating {dirs.Length} directories...");
            foreach (var dir in dirs)
            {
                var relative = Path.GetRelativePath(source, dir);
                var destDir = Path.Combine(destination, relative);
                Directory.CreateDirectory(destDir);
            }

            var files = Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories);
            LoggingService.Log($"Copying {files.Length} files...");
            var lastPercent = -1;
            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var relative = Path.GetRelativePath(source, file);
                var destPath = GetSafeDestinationPath(destination, relative);

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrWhiteSpace(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                Core.Io.InstanceFiles.Copy(file, destPath);

                if (i > 0 && i % 500 == 0)
                {
                    LoggingService.Log($"Copied {i}/{files.Length} files...", LogLevel.Debug);
                }

                lastPercent = ReportPercentIfChanged(progress, (i + 1) * 100 / files.Length, lastPercent);
            }
            LoggingService.Log("Directory copy complete.");
        }


        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;
            if (window is null)
            {
                return false;
            }

            var hwnd = WindowNative.GetWindowHandle(window);
            switch (picker)
            {
                case FileOpenPicker openPicker:
                    InitializeWithWindow.Initialize(openPicker, hwnd);
                    return true;

                case FolderPicker folderPicker:
                    InitializeWithWindow.Initialize(folderPicker, hwnd);
                    return true;

                default:
                    return false;
            }
        }

        // Defined once, in Core.Install.ArchiveExtensions, and shared with RecycleBinArchiveNames: two
        // independent copies of "what counts as an archive" is what let the deleted-archive sweep miss
        // formats this file already knew how to install.
        private static bool IsZipArchive(string? extension) => Core.Install.ArchiveExtensions.IsZip(extension);

        private static bool IsSevenZipArchive(string? extension) => Core.Install.ArchiveExtensions.IsExternal(extension);

        private static string FormatFileSize(long bytes)
        {
            const long gb = 1024L * 1024 * 1024;
            const long mb = 1024L * 1024;
            const long kb = 1024L;

            if (bytes >= gb)
                return $"{bytes / (double)gb:F1} GB";
            if (bytes >= mb)
                return $"{bytes / (double)mb:F0} MB";
            return $"{bytes / (double)kb:F0} KB";
        }

        /// <summary>
        /// Automatically unblock files that were just installed.
        /// </summary>
        private static async Task UnblockInstalledFilesAsync(List<string> paths)
        {
            // Each folder is unblocked on its own rather than under one try around the whole loop: one
            // folder's failure used to stop unblocking for every folder after it in the same install,
            // and the only trace was Debug.WriteLine, which nothing in a Release build ever reads - not
            // even BEM's own log file. Unblocking is still a nice-to-have that never throws past this
            // method, but "nice-to-have" is not the same as "invisible."
            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                    continue;

                try
                {
                    await FileUnblocker.UnblockDirectoryAsync(path);
                }
                catch (Exception ex)
                {
                    LoggingService.LogException(ex, $"Failed to unblock installed files in '{path}'");
                }
            }
        }
    }

    // One destination as the chooser shows it. The target is carried whole rather than rebuilt from
    // the id, so the row hands the installer the same four facts the registry was read for, and the
    // game folder a referenced instance points at cannot be re-derived from anything the row shows.
    public partial class InstallDestinationRow : ObservableObject
    {
        public InstallDestinationRow(InstallTarget target, string label, bool isResting)
        {
            Target = target;
            Label = label;
            IsResting = isResting;
            Tooltip = Strings.Current.Format("Install.Destinations.Row.Tooltip", target.GameFolder);
        }

        public InstallTarget Target { get; }

        public string InstanceId => Target.InstanceId;

        public string Label { get; }

        public bool IsResting { get; }

        public string Tooltip { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }
    }
}



