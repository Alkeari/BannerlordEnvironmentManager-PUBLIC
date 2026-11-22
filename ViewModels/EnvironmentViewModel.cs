using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using BannerlordEnvironmentManager.Core.Butr;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.GameSettings;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Settings;
using BannerlordEnvironmentManager.Services;
using Microsoft.VisualBasic.FileIO;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class EnvironmentViewModel : BaseViewModel
    {
        private LaunchSettingsStore launchSettingsStore = new(LaunchSettingsStore.GetDefaultPath());

        private LoadOrderBackupStore backupStore = new(LoadOrderBackupStore.GetDefaultRoot());

        private LoadOrderProfileStore profileStore = new(LoadOrderProfileStore.GetDefaultRoot());

        // Whether dividers are shown is a display preference, so it stays machine-wide, and it is named
        // off the machine root directly: the profile folder it used to take a parent of is per version
        // now, which would have moved this file under whichever version happened to be selected.
        private readonly LoadOrderDividerOptionsStore dividerOptionsStore =
            new(Path.Combine(InstanceStateFolder.MachineRoot(), "load-order-settings.json"));

        private LoadOrderDividerStore dividerStore = new(LoadOrderDividerStore.GetDefaultPath());

        private ModulePinStore pinStore = new(ModulePinStore.GetDefaultPath());

        // The same two files NexusModuleMatching reads by default, held explicitly here because this
        // tab now writes them as well as reading them.
        private readonly NotOnNexusStore notOnNexus = new(NotOnNexusStore.DefaultPath());

        private readonly BuiltLocallyStore builtLocally = new(BuiltLocallyStore.DefaultPath());

        private readonly LearnedNexusIdStore confirmedNexusIds = new(LearnedNexusIdStore.DefaultPath());

        private readonly RejectedNexusIdStore rejectedNexusIds = new(RejectedNexusIdStore.DefaultPath());

        private readonly UnrecognizedArchiveHashStore unrecognizedHashes = new(UnrecognizedArchiveHashStore.DefaultPath());

        // Rebuilt whenever the active instance changes. It was fixed to the machine's own Documents
        // folder for the life of the view model, while the module list beside it came from whichever
        // instance was active: selecting an unmodded version and saving wrote that version's seven
        // modules straight over the resting version's real load order. A version's load order lives
        // in that version's own store, and only the resting version's store is the canonical path.
        private LauncherDataStore store;

        // A launch routes through this so the active instance's data is what the game actually sees;
        // the resting instance is the common case and costs nothing extra (ActivationSession creates
        // no junctions when the target is already resting).
        private readonly InstanceManager instanceManager = new(CanonicalPathSet.ForMachine(), new InstanceSettingsStore())
        {
            Log = message => LoggingService.Log(message)
        };

        // Sharing the base game's settings across instances, off unless the user turned it on in
        // Settings. Read fresh at each launch rather than cached, because the toggle lives on another
        // page and this view model outlives a visit to it.
        private readonly InstanceSettingsStore instanceSettingsStore = new();

        private readonly GameSettingsSync gameSettingsSync =
            GameSettingsSync.Beside(InstanceSettingsStore.DefaultPath);

        // Set by the launch hooks, which run where the status line is about to be overwritten or off
        // the UI thread entirely, and reported once the run is over on the thread that owns the window.
        private string pendingGameSettingsNotice = string.Empty;

        // Half a second: an archive extraction is many file events in quick succession, and this is
        // long enough that they settle into one before the debouncer fires, while being short enough
        // that nobody watching Play would call the result slow.
        private static readonly TimeSpan ModulesFolderQuietPeriod = TimeSpan.FromMilliseconds(500);

        private FileSystemWatcher? modulesFolderWatcher;
        private ModulesFolderChangeDebouncer? modulesFolderChangeDebouncer;
        private string? watchedModulesFolder;
        private readonly HeldChangeCatchUp heldChangeCatchUp = new();

        private readonly LoadOrderUndoStack undoStack = new();

        // The version dropdown at the top of this page. Reached through the shell rather than owned
        // here, because the Versions page shows the same instances and the two must never disagree;
        // it is a property rather than a field so it is read after the shell has finished building.
        public VersionSwitcherViewModel VersionSwitcher => Views.ShellViewModels.Instance.VersionSwitcher;

        // What every launch did, so a preflight can say the one genuinely useful thing: whether this
        // exact load order has run before and how it ended. Bisection runs are deliberately not in
        // here: they launch a set BEM chose to break, and recording them would poison the answer.
        private LaunchHistoryStore launchHistory = new(LaunchHistoryStore.GetDefaultPath());

        // Preflight findings the user has already read and chosen to launch past. Loaded fresh on
        // every preflight rather than cached, so accepting one from the dialog is seen by the very next
        // run without this view model having to invalidate anything.
        private AcceptedRiskStore acceptedRisks = new(AcceptedRiskStore.DefaultPath());

        // Per version, like the module folders whose files it holds.
        private BundledStackSetAsideStore setAsideStore = new(BundledStackSetAsideStore.GetDefaultRoot());

        // The same armed watch the Watch page reads, because there is only one: it is a file, so it
        // outlives the window and both pages are looking at the same state.
        private readonly WatchSession watch = new(WatchSession.DefaultStateFilePath);

        private ModuleEnvironment environment = ModuleEnvironment.Empty;

        // The list as it stood after the last change of any kind. A tick or a drag changes the rows
        // without going through Apply, so environment can be several presses behind; this is what one
        // press of Undo puts back.
        private ModuleEnvironment standing = ModuleEnvironment.Empty;

        private UndeclaredDependencyScan? undeclaredScan;

        private bool isScanningUndeclared;

        private bool isRebuildingDisplayRows;

        // Read by EnvironmentPage's drag-completion handler, which only has a change to DisplayRows
        // to go on and cannot otherwise tell a drop apart from this view model replacing the list.
        internal bool IsRebuildingDisplayRows => isRebuildingDisplayRows;

        // What the last load or save left on disk. Everything the launcher file records is in here,
        // so an operation that ends with a matching sequence has genuinely changed nothing.
        private List<(string Id, bool IsEnabled)> savedOrder = [];

        // Assigning the loaded values fires the generated On*Changed partials, which save. The
        // partials cannot be unsubscribed, so a guard is what stops every start from rewriting the
        // file it just read.
        private bool loadingLaunchSettings;

        // Restoring the dropdown is not the user choosing a target. Without this the fallback picked
        // when the saved executable is missing would be written straight back over their choice.
        private bool restoringSelection;

        // Reading the armed watch back into the checkbox is not the user pressing it, and without this
        // the read would install or remove the companion all over again.
        private bool syncingWatch;

        // What the last target refresh had to say, kept so a Refresh that ends with its own status
        // line does not swallow it.
        private string launchTargetNotice = string.Empty;

        // How the last run ended as far as BEM could tell. LostTrack means the process handle stopped
        // answering and the folders were released on the strength of the process list instead, which
        // the user is told rather than left to infer from a launch that looked ordinary.
        private LaunchWaitOutcome LastRunEnd { get; set; } = LaunchWaitOutcome.RunEnded;

        // What the run's own error log said ended it, when it said so. Set where the process end is seen.
        private string? LastRunCrashEvidence { get; set; }

        // Which chip the list is narrowed to. Never null, so every scope decision has an answer
        // without a fallback that could quietly widen a bulk action.

        // Which keys the last sort used, in the precedence the user clicked them. A sort writes the real
        // load order, so this is cleared the moment the user reorders by hand: leaving it lit would
        // claim the list is in an order it no longer is.
        private ModuleSortPlan sortPlan = ModuleSortPlan.Empty;

        // Which modules the user has said must stay where they are, by id. A sort keeps them there
        // wherever the declared constraints leave it free to, and says so when they do not.
        private ModulePins pins = ModulePins.Empty;
        private LoadOrderChoiceStore loadOrderChoiceStore = new(LoadOrderChoiceStore.GetDefaultPath());
        private LoadOrderChoices loadOrderChoices = LoadOrderChoices.Empty;
        private LoadOrderRuleStore orderRuleStore = new(LoadOrderRuleStore.GetDefaultPath());
        private LoadOrderRules orderRules = LoadOrderRules.Empty;

        private readonly IReadOnlyList<ModuleFilterViewModel> nexusVerdictFilters;

        // What the last answer from Nexus said, by module id, and which modules could be asked about
        // at all. The second is what stops the first from reading as a clean bill of health for a
        // load order most of which Nexus was never asked about.
        private Dictionary<string, NexusModuleUpdate> nexusUpdates = new(StringComparer.OrdinalIgnoreCase);

        // What Nexus recorded a new file for is not what needs updating: a new file can be older than
        // the copy installed, or the same version repackaged. The verdicts are the judged answer, one
        // per installed community module, and they are what the chip selects on.
        private Dictionary<string, ModuleUpdateVerdict> nexusVerdicts = new(StringComparer.OrdinalIgnoreCase);

        // What the last answer said, kept so the summary line can be rebuilt from the rows whenever they change.
        private NexusUpdateReport? lastNexusReport;

        private IReadOnlyList<NexusModuleLink> nexusLinks = [];

        // Every Nexus mod id BEM has learned for the modules on disk, from a manifest, from the archive
        // a module was installed from, or from a page the user confirmed. Read from BEM's own files
        // and never from Nexus, so opening a mod page needs no key, no network and no update check.
        // Null means it has not been worked out since the load order last changed.
        private IReadOnlyDictionary<string, int>? nexusModIds;

        private bool hasNexusAnswer;

        private CancellationTokenSource? nexusCancellation;

        // Held by reference rather than looked up by group.Name: that Name is now a localized string,
        // and comparing it against a fixed English literal would silently stop matching under any
        // other language.
        private ModuleFilterGroupViewModel nexusFilterGroup = null!;

        private ModuleFilterGroupViewModel attentionFilterGroup = null!;

        // One option per label the rows actually show, rebuilt whenever the labels change, so the menu
        // never offers a category no row has.
        private ModuleFilterGroupViewModel categoryFilterGroup = null!;

        public EnvironmentViewModel()
        {
            store = new LauncherDataStore(LauncherDataStore.GetDefaultPath(), backupStore);

            // An id saved by any other route (Library's backfill, an install) redraws Play's rows
            // and Library's, which are the same row objects, without a press of Check for Updates.
            NexusIdsChanged.Raised += OnNexusIdsChanged;

            ModuleFilterViewModel Option(ModuleFilter kind, string name, string description) =>
                new(kind, name, description, OnFilterToggled);

            // Every option is a mark the row itself draws, so anything visible on a row can be asked
            // for here. The one closed set the marks describe is exposed completely: a mark you can see
            // and cannot filter by is half a feature.
            var state = new ModuleFilterGroupViewModel(Strings.Current["Environment.Filter.State.Name"],
                Strings.Current["Environment.Filter.State.Description"],
                [
                    Option(ModuleFilter.Enabled, Strings.Current["Environment.Filter.Enabled.Name"], Strings.Current["Environment.Filter.Enabled.Description"]),
                    Option(ModuleFilter.Disabled, Strings.Current["Environment.Filter.Disabled.Name"], Strings.Current["Environment.Filter.Disabled.Description"]),
                    Option(ModuleFilter.Pinned, Strings.Current["Environment.Filter.Pinned.Name"], Strings.Current["Environment.Filter.Pinned.Description"])
                ]);

            var origin = new ModuleFilterGroupViewModel(Strings.Current["Environment.Filter.Origin.Name"],
                Strings.Current["Environment.Filter.Origin.Description"],
                [
                    Option(ModuleFilter.Official, Strings.Current["Environment.Filter.Official.Name"], Strings.Current["Environment.Filter.Official.Description"]),
                    Option(ModuleFilter.Manual, Strings.Current["Environment.Filter.Manual.Name"], Strings.Current["Environment.Filter.Manual.Description"]),
                    Option(ModuleFilter.Workshop, Strings.Current["Environment.Filter.Workshop.Name"], Strings.Current["Environment.Filter.Workshop.Description"])
                ]);

            var attention = new ModuleFilterGroupViewModel(Strings.Current["Environment.Filter.Attention.Name"],
                Strings.Current["Environment.Filter.Attention.Description"],
                [
                    Option(ModuleFilter.Issues, Strings.Current["Environment.Filter.Issues.Name"], Strings.Current["Environment.Filter.Issues.Description"]),
                    Option(ModuleFilter.Orphans, Strings.Current["Environment.Filter.Orphans.Name"], Strings.Current["Environment.Filter.Orphans.Description"]),
                    Option(ModuleFilter.Unreadable, Strings.Current["Environment.Filter.Unreadable.Name"], Strings.Current["Environment.Filter.Unreadable.Description"]),
                    Option(ModuleFilter.Notes, Strings.Current["Environment.Filter.Notes.Name"], Strings.Current["Environment.Filter.Notes.Description"]),
                    Option(ModuleFilter.Updates, Strings.Current["Environment.Filter.Updates.Name"], Strings.Current["Environment.Filter.Updates.Description"])
                ]);

            // The one group that comes and goes. With update checking off, or with no key stored, the
            // verdict options are not options reading zero, they are not there at all, exactly as the
            // Updates chip was not. What BEM knows without asking anybody stays either way.
            nexusVerdictFilters =
            [
                Option(ModuleFilter.OutOfDate, Strings.Current["Environment.Filter.OutOfDate.Name"], Strings.Current["Environment.Filter.OutOfDate.Description"]),
                Option(ModuleFilter.ProbablyOutOfDate, Strings.Current["Environment.Filter.ProbablyOutOfDate.Name"],
                    Strings.Current["Environment.Filter.ProbablyOutOfDate.Description"]),
                Option(ModuleFilter.UpdateUnknown, Strings.Current["Environment.Filter.UpdateUnknown.Name"], Strings.Current["Environment.Filter.UpdateUnknown.Description"])
            ];

            var nexus = new ModuleFilterGroupViewModel(Strings.Current["Environment.Filter.Nexus.Name"],
                Strings.Current["Environment.Filter.Nexus.Description"],
                [
                    Option(ModuleFilter.IdConflict, Strings.Current["Environment.Filter.IdConflict.Name"],
                        Strings.Current["Environment.Filter.IdConflict.Description"]),
                    Option(ModuleFilter.NoNexusId, Strings.Current["Environment.Filter.NoNexusId.Name"], Strings.Current["Environment.Filter.NoNexusId.Description"]),
                    Option(ModuleFilter.NotOnNexus, Strings.Current["Environment.Filter.NotOnNexus.Name"], Strings.Current["Environment.Filter.NotOnNexus.Description"]),
#if DEV_BEM
                    // The read side of Mark as built on this machine, and in the outer ring with it:
                    // a build that cannot make the mark would ship a filter nothing can match. The
                    // row badge stays in every build, because a machine sharing its ledger with
                    // Dev-BEM has modules carrying the mark already.
                    Option(ModuleFilter.BuiltHere, Strings.Current["Environment.Filter.BuiltHere.Name"], Strings.Current["Environment.Filter.BuiltHere.Description"]),
#endif
                ]);

            nexusFilterGroup = nexus;
            attentionFilterGroup = attention;

            categoryFilterGroup = new ModuleFilterGroupViewModel(Strings.Current["Environment.Filter.Category.Name"],
                Strings.Current["Environment.Filter.Category.Description"], []);

            FilterGroups = [state, origin, categoryFilterGroup, attention, nexus];

            Filters = [.. FilterGroups.SelectMany(group => group.Options), .. nexusVerdictFilters];

            // Built from the enum rather than listed by hand, so a sort key can never be added to
            // Core and then be missing from the menu that is supposed to offer all of them.
            SortOptions =
            [
                .. Enum.GetValues<ModuleSortKey>()
                    .Select(kind => new ModuleSortViewModel(kind, SortName(kind), ToggleSort, ToggleSortDirection))
            ];

            // After SortOptions: a cached Nexus answer refreshes the visible list, which reads them.
            // Running this first crashed the app on boot for anyone who had ever checked for updates.
            RefreshUndo();

            // Loaded synchronously, before LoadStartupStateAsync below returns control to the caller,
            // so Dividers already holds the saved sections by the time Refresh() runs its own scan and
            // dedupes newly recognized ones against what is already here.
            foreach (var divider in dividerStore.Load())
                Dividers.Add(divider);

            // Everything from here down touches disk, and none of it is required for the first frame.
            // The cached update answer in particular reads whole module folders, which is seconds on a
            // large load order; the smaller reads below add up too. They used to run synchronously
            // here, on the thread building the first page, so the constructor is now a fast in-memory
            // setup and each of these applies itself on the UI thread when it is ready.
            _ = LoadStartupStateAsync();
        }

        // The constructor's disk I/O, run after the constructor has returned. Everything it reads is
        // what the first frame would otherwise wait on; each piece applies its own result on the UI
        // thread, so the Play page renders first and fills in as the state arrives.
        private async Task LoadStartupStateAsync()
        {
            try
            {
                await Task.Yield();

                // First, so a failure below never leaves the Accepted risks Expander invisible for the
                // whole session with no way to un-accept something.
                RefreshAcceptedRiskRows();
                RefreshSetAsideRows();

                LoadLaunchSettings();
                LoadPins();
                RefreshSafety();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "Failed to load the Play page's startup state");
            }
        }

        public ObservableCollection<ModuleRowViewModel> Modules { get; } = [];

        public ObservableCollection<ModuleRowViewModel> VisibleModules { get; } = [];

        // The live view's sections, loaded from LoadOrderDividerStore and never written to
        // LauncherData.xml: LoadOrderDividerStore is where they are loaded and saved.
        public ObservableCollection<LoadOrderDivider> Dividers { get; } = [];

        // What ModuleList actually binds to. Rebuilt at the end of
        // RefreshVisibleModules from VisibleModules + Dividers, so it always reflects the current search
        // filter exactly the way VisibleModules itself already does.
        public ObservableCollection<ILoadOrderRowViewModel> DisplayRows { get; } = [];

        // One button per question, each holding its own answers. A flat row of chips could only ever
        // ask one thing at a time, so "the Workshop mods that are out of date" was not a question this
        // tab could be asked.
        public IReadOnlyList<ModuleFilterGroupViewModel> FilterGroups { get; }

        public IReadOnlyList<ModuleFilterViewModel> Filters { get; }

        public bool IsFiltering => Filters.Any(filter => filter.IsSelected);

        // What is being narrowed by, written out, because a button reading "Nexus: 2 selected" says a
        // number and not which two, and the reason a row is missing has to be readable without opening
        // three menus to find it.
        public string FilterSummary
        {
            get
            {
                var orWord = Strings.Current["Environment.FilterSummary.ListOr"];
                var andWord = Strings.Current["Environment.FilterSummary.ListAnd"];

                var narrowing = FilterGroups
                    .Where(group => group.IsNarrowing)
                    .Select(group => Strings.Current.Format("Environment.FilterSummary.GroupNarrowing", group.Name,
                        string.Join($" {orWord} ", group.Selected.Select(option => option.Name))))
                    .ToList();

                return narrowing.Count == 0
                    ? string.Empty
                    : Strings.Current.Plural("Environment.FilterSummary.Showing", Modules.Count, VisibleModules.Count,
                        string.Join($", {andWord} ", narrowing));
            }
        }

        // Suspended around the loop, or clearing eight ticks rebuilds the visible list eight times and
        // seven of those are of a state nobody asked to see.
        [RelayCommand]
        public void ClearFilters()
        {
            suspendFilterRefresh = true;

            try
            {
                foreach (var filter in Filters)
                    filter.SetSelected(false);
            }
            finally
            {
                suspendFilterRefresh = false;
            }

            RefreshVisibleModules();
        }

        public IReadOnlyList<ModuleSortViewModel> SortOptions { get; }

        public ObservableCollection<IssueRowViewModel> Issues { get; } = [];

        public ObservableCollection<IssueRowViewModel> PanelIssues { get; } = [];

        [ObservableProperty]
        public partial string GameInstallPath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GameVersionText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        // True only while a write is being held back because a launcher is running. It is not a pending
        // edit the user has to commit: every other change is already on disk.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowHeldChangesNote))]
        public partial bool HasHeldChanges { get; set; }

        // The held-changes note always says the same thing, so accepting it once silences it for good.
        private const string HeldChangesCategory = "HeldChanges";

        private bool? heldChangesAccepted;

        public bool ShowHeldChangesNote =>
            HasHeldChanges && !(heldChangesAccepted ??= AcceptedNotices.IsAccepted(HeldChangesCategory, HeldChangesCategory));

        [RelayCommand]
        private void AcceptHeldChanges()
        {
            StatusMessage = AcceptedNotices.Accept(HeldChangesCategory, HeldChangesCategory);
            heldChangesAccepted = true;
            OnPropertyChanged(nameof(ShowHeldChangesNote));
        }

        // The earliest honest point at which a hold has cleared, whatever cleared it (the next edit's
        // own PersistOrder, an explicit Refresh, or a confirmed discard on navigating away) - not a
        // timer guessing at it. If OnModulesFolderSettled had to skip a refresh while this was true,
        // catch it up now; otherwise this is a no-op on every ordinary transition to false.
        partial void OnHasHeldChangesChanged(bool value)
        {
            if (value || !heldChangeCatchUp.ShouldCatchUp())
                return;

            _ = CatchUpAfterHeldChangeClearedAsync();
        }

        // A background refresh, like the watcher's own: it must not clear the undo stack any more than
        // the change it is catching up on already did, and it must not fight RefreshCoreAsync's
        // re-entrancy guard, so it goes through the exact same path OnModulesFolderSettled uses.
        private async Task CatchUpAfterHeldChangeClearedAsync()
        {
            try
            {
                await RefreshFromWatcherAsync();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to refresh Play after a held load-order change cleared");
            }
        }

        // A dated backup is taken on the first write after each load, not on every change.
        private bool hasBackedUpThisBurst;

        // The standing evidence that the file matches the screen. Stays on display after the write, so
        // it answers "did that take" at any point, not only in the second the status message appears.
        [ObservableProperty]
        public partial string LastWrittenText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
        public partial bool CanUndo { get; set; }

        // Bound to the tooltip on the wrapper around the Undo button, so a grayed-out Undo still says
        // what it would undo or why there is nothing to undo. It is never silently inert.
        [ObservableProperty]
        public partial string UndoReason { get; set; } = LoadOrderUndoStack.NothingToUndo;

        private void RefreshUndo()
        {
            CanUndo = undoStack.CanUndo;
            UndoReason = undoStack.Reason;
        }

        private void RecordUndo(ModuleEnvironment before, string description)
        {
            undoStack.Record(before, description);
            RefreshUndo();
        }

        [RelayCommand(CanExecute = nameof(CanUndo))]
        public void Undo()
        {
            if (undoStack.Take() is not { } step)
            {
                StatusMessage = undoStack.Reason;
                RefreshUndo();
                return;
            }

            Apply(step.Order);
            PersistOrder();
            RefreshUndo();

            StatusMessage = HasHeldChanges
                ? Strings.Current.Format("Environment.Undo.HeldMessage", step.Description)
                : Strings.Current.Format("Environment.Undo.WrittenMessage", step.Description);
        }

        // Writing an environment that was never loaded would replace every launcher entry with
        // nothing, so the write commands stay dead until a scan has actually succeeded.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
        [NotifyCanExecuteChangedFor(nameof(CheckLoadOrderBootsCommand))]
        [NotifyCanExecuteChangedFor(nameof(AutoSortCommand))]
        [NotifyCanExecuteChangedFor(nameof(PinEverythingInPlaceCommand))]
        [NotifyCanExecuteChangedFor(nameof(PruneOrphansCommand))]
        [NotifyCanExecuteChangedFor(nameof(FixAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(SelectAllShownCommand))]
        [NotifyCanExecuteChangedFor(nameof(InvertEnabledCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReturnToKnownGoodCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableWithDependenciesCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableWithDependentsCommand))]
        public partial bool IsLoaded { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(FixAllCommand))]
        public partial bool HasAutoFixableIssues { get; set; }

        private bool CanFixAll => IsLoaded && HasAutoFixableIssues;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IssueCountText))]
        public partial int ErrorCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IssueCountText))]
        public partial int WarningCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PruneButtonText))]
        public partial int OrphanCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IssueCountText))]
        public partial int InformationCount { get; set; }

        // Built here rather than from three <Run> fragments around a bound number, so a translator can
        // move the count and the word order stays theirs to choose.
        public string PruneButtonText => Strings.Current.Format("Environment.PruneButtonText", OrphanCount);

        public string IssueCountText
        {
            get
            {
                var text = Strings.Current.Format("Environment.IssueCountText",
                    Strings.Current.Plural("Environment.IssueCountText.Errors", ErrorCount),
                    Strings.Current.Plural("Environment.IssueCountText.Warnings", WarningCount),
                    Strings.Current.Plural("Environment.IssueCountText.Notes", InformationCount));

                return AcceptedIssueCount == 0
                    ? text
                    : Strings.Current.Format("Environment.IssueCountText.WithAccepted", text, AcceptedIssueCount);
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IssueCountText))]
        public partial int AcceptedIssueCount { get; set; }

        [ObservableProperty]
        public partial string LaunchTargetText { get; set; } = string.Empty;

        // Why the launch-target list is short, shown beside the list rather than in the status line.
        // Empty whenever every installed target is offered.
        [ObservableProperty]
        public partial string LaunchTargetNote { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool CanReorder { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CrashHandlingAppliesToTarget))]
        [NotifyPropertyChangedFor(nameof(CrashHandlingNote))]
        [NotifyPropertyChangedFor(nameof(LaunchButtonText))]
        [NotifyPropertyChangedFor(nameof(LaunchTooltip))]
        public partial LaunchTargetKind? PreferredTarget { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CrashHandlingAppliesToTarget))]
        [NotifyPropertyChangedFor(nameof(CrashHandlingNote))]
        [NotifyPropertyChangedFor(nameof(LaunchButtonText))]
        [NotifyPropertyChangedFor(nameof(LaunchTooltip))]
        public partial LaunchTarget? SelectedTarget { get; set; }

        [ObservableProperty]
        public partial string ExtraArguments { get; set; } = string.Empty;

        // Kept on the load order screen itself. Overflowing this is the one launch failure that leaves
        // nothing behind to diagnose: the C runtime kills the process before the CLR loads, so there is
        // no crash report, no log and nothing in Diagnostics. A preflight that can say "not checked" is
        // not enough on its own, and the number only means anything next to the list that drives it.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CommandLineText))]
        [NotifyPropertyChangedFor(nameof(CommandLineIsTight))]
        public partial CommandLineBudget? CommandLine { get; set; }

        public string CommandLineText => CommandLine is { } budget
            ? Strings.Current.Format("Environment.CommandLineText.WithBudget", budget.Summary)
            : Strings.Current["Environment.CommandLineText.NoTarget"];

        public bool CommandLineIsTight => CommandLine is { } budget && (!budget.Fits || budget.IsNearTheLimit);

        public string CommandLineTooltip => Strings.Current["Environment.CommandLineTooltip"];

        [ObservableProperty]
        public partial bool MinimizeOnLaunch { get; set; }

        [ObservableProperty]
        public partial bool CheatMode { get; set; }

        // What every launch passes on: the typed arguments plus the cheat mode switch when it is on.
        public string LaunchArguments => ProfileLaunchArguments(CheatModeArgument.Apply(ExtraArguments, CheatMode));

        // BLSE Standalone takes these three as command-line flags. BEM keeps them in its own settings
        // file rather than in LauncherData.xml: LauncherEx appends its eleven elements there without
        // de-duplicating, so a second writer would leave the file with two of each.
        [ObservableProperty]
        public partial bool UseVanillaCrashHandler { get; set; }

        [ObservableProperty]
        public partial bool DisableAutoGeneratedExceptionCatching { get; set; }

        // Named after the flag it sends, /enablecrashhandlerwhendebuggerisattached, and not after
        // LauncherEx's element DisableCrashHandlerWhenDebuggerIsAttached, which means the opposite.
        [ObservableProperty]
        public partial bool KeepCrashHandlerUnderDebugger { get; set; }

        // Off means the lock is on, which is the default: a drag that would break a declared load-before,
        // load-after or dependency is refused and the reason named. It governs dragging only. Sorting is
        // locked to compliance whatever this says, and an imported load order outranks both.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ManualOverrideNote))]
        public partial bool PermitManualLoadOrderOverride { get; set; }

        // On, a mod that carries its own copies of the BUTR stack beside the installed stack modules is
        // offered a fix that sets those copies aside. Off, the offered fix disables the installed
        // stack modules instead, which is what BEM did before.
        [ObservableProperty]
        public partial bool PreferInstalledButrStack { get; set; } = true;

        public string ManualOverrideNote => PermitManualLoadOrderOverride
            ? Strings.Current["Environment.ManualOverrideNote.On"]
            : Strings.Current["Environment.ManualOverrideNote.Off"];

        public BlseCrashHandlerOptions CrashHandling => new(
            KeepCrashHandlerUnderDebugger,
            DisableAutoGeneratedExceptionCatching,
            UseVanillaCrashHandler);

        private LaunchTargetKind? EffectiveTargetKind => SelectedTarget?.Kind ?? PreferredTarget;

        public bool CrashHandlingAppliesToTarget => EffectiveTargetKind == LaunchTargetKind.BlseStandalone;

        public string CrashHandlingNote => CrashHandlingAppliesToTarget
            ? Strings.Current["Environment.CrashHandlingNote.Applies"]
            : Strings.Current.Format("Environment.CrashHandlingNote.NotApplies",
                EffectiveTargetKind is { } kind ? DescribeTarget(kind) : Strings.Current["Environment.CrashHandlingNote.UnknownTarget"]);

        // Arming, from the tab the user launches from. It is the same armed watch the Watch page owns:
        // checking it installs BEM's companion into the game folder and leaves it there, unchecking it
        // takes it back out, and the state survives a restart because it is a file on disk.
        //
        // Off by default and never turned on by BEM, because installing a module into someone else's
        // game folder is a change outside BEM's own state.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchButtonText))]
        [NotifyPropertyChangedFor(nameof(LaunchTooltip))]
        public partial bool IsWatchArmed { get; set; }

        // A launcher target reads LauncherData.xml rather than a module list, and BEM will not write
        // its own module into the saved load order to reach one. So an armed watch does not apply to
        // every target, and the button has to say which run it is actually about to watch. No target
        // chosen means the resolver picks a direct one first, which does carry the companion.
        public bool WatchAppliesToTarget =>
            EffectiveTargetKind is not { } kind || WatchedLaunch.CarriesModuleList(kind);

        // A launch on a version that is not the resting one holds the canonical folders for the whole
        // run, so the command really is unavailable from the press until the folders are back. Disabled
        // and silent is indistinguishable from broken, which is exactly how it was read, so the button
        // names the phase it is in and the tooltip names what BEM is waiting for.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchButtonText))]
        [NotifyPropertyChangedFor(nameof(LaunchTooltip))]
        [NotifyPropertyChangedFor(nameof(IsLaunchIdle))]
        public partial LaunchPhase Phase { get; set; }

        // Record Mod Patches installs or removes the companion, which must never happen under a running game.
        public bool IsLaunchIdle => Phase == LaunchPhase.Idle;

        // The most used button in the app says what it is about to do. Whether the launch records mod
        // patches is said by the Record Mod Patches checkbox right beside it, so the button itself
        // stays "Launch" (his order 2026-11-21: a checkbox left of Launch instead of "Launch, Watched").
        public string LaunchButtonText => Phase switch
        {
            LaunchPhase.Starting => Strings.Current["Environment.LaunchButtonText.Starting"],
            LaunchPhase.Playing => Strings.Current["Environment.LaunchButtonText.Playing"],
            LaunchPhase.Releasing => Strings.Current["Environment.LaunchButtonText.Releasing"],
            _ => Strings.Current["Environment.LaunchButtonText.Normal"]
        };

        public string LaunchTooltip => Phase switch
        {
            LaunchPhase.Starting => Strings.Current["Environment.LaunchTooltip.Starting"],
            LaunchPhase.Playing => Strings.Current["Environment.LaunchTooltip.Playing"],
            LaunchPhase.Releasing => Strings.Current["Environment.LaunchTooltip.Releasing"],
            _ => !IsWatchArmed
                ? Strings.Current["Environment.LaunchTooltip.Unwatched"]
                : WatchAppliesToTarget
                    ? Strings.Current["Environment.LaunchTooltip.Watched"]
                    : Strings.Current["Environment.LaunchTooltip.NotApplicable"]
        };

        partial void OnIsWatchArmedChanged(bool value)
        {
            if (syncingWatch)
                return;

            // Recording mod patches installs or removes the companion, which must never happen under a
            // running game. The checkbox is disabled then, and this also refuses any other path that
            // reaches here, and the finally block puts the box back to what is on disk.
            if (Phase != LaunchPhase.Idle)
            {
                StatusMessage = Strings.Current["Environment.Watch.NotDuringLaunch"];
                RefreshWatchArmed();
                return;
            }

            try
            {
                StatusMessage = value ? ArmWatch() : DisarmWatch();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to change whether the next launch is watched");
                StatusMessage = Strings.Current.Format("Environment.Watch.ArmToggleFailed", ex.Message);
            }
            finally
            {
                // The checkbox states what is true on disk rather than what was asked for: an arming
                // that failed must not leave a checked box promising a watch that is not there.
                RefreshWatchArmed();
                RefreshCompanionLeftBehind();

                // The Watch page's IsWatching is what the shell's Forensics gate listens to, and in
                // Basic mode arming here is what brings that destination into the navigation - it
                // must appear the moment the box is ticked, not when the Watch page next refreshes
                // itself. Safe against re-entry: its refresh path reaches RefreshWatchArmed, which
                // sets IsWatchArmed only under the syncingWatch guard.
                ShellViewModels.Instance.Watch.Refresh();
            }
        }

        private string ArmWatch()
        {
            var payload = CompanionPayload.LocateForInstall(GameInstallPath);

            if (!payload.Found)
                return Strings.Current.Format("Environment.Watch.NotArmed", payload.Reason);

            var result = watch.Arm(new WatchRequest(
                GameInstallPath,
                EnabledForLaunch(),
                payload.Path!,
                LaunchArguments,
                PreferredTarget,
                CrashHandling));

            LoggingService.Log($"Watch arming from the Play tab: {result.Status}");

            return result.Status is WatchStartStatus.Armed && !payload.MatchesInstallPlatform
                ? $"{result.Message} {payload.Reason}"
                : result.Message;
        }

        private string DisarmWatch()
        {
            var result = watch.Stop(GameInstallPath, store);

            LoggingService.Log($"Watch disarming from the Play tab: {result.Status}");

            return result.Message;
        }

        // Called when the tab is opened as well as after a refresh, because the watch can be armed or
        // stopped from the Watch page and the companion can be deleted from outside BEM entirely.
        public void RefreshWatchArmed()
        {
            syncingWatch = true;

            try
            {
                IsWatchArmed = !string.IsNullOrWhiteSpace(GameInstallPath) && watch.IsWatching(GameInstallPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read whether a watch is armed");
            }
            finally
            {
                syncingWatch = false;
            }

            ArmWatchOnce();
        }

        // Watching launches is always on: the first time an install is seen with no watch armed, BEM
        // arms it, once per install path per session, so a watch the owner stops from the Watch page
        // stays stopped until BEM next opens. The attempt goes through the same property change the
        // checkbox used, so the companion, status line and Forensics gate behave exactly as before.
        private string? watchArmAttemptedFor;

        internal void ArmWatchOnce()
        {
            // The path must already name a real install: startup state is read before Refresh settles
            // which install this page is on, and an attempt spent on a path that is not (yet) one
            // logs NoInstall and is never made again for the real one. Refresh calls this again once
            // the path is set.
            // And the module list must already be read: arming wants Bannerlord.Harmony among the enabled
            // modules, and an attempt made against the still empty list answered NoHarmony and was
            // never made again for this install.
            if (IsWatchArmed || !IsLoaded || string.IsNullOrWhiteSpace(GameInstallPath)
                || !GameInstallLocator.IsValidInstall(GameInstallPath)
                || string.Equals(watchArmAttemptedFor, GameInstallPath, StringComparison.OrdinalIgnoreCase)
                || Phase != LaunchPhase.Idle)
                return;

            watchArmAttemptedFor = GameInstallPath;
            IsWatchArmed = true;
        }

        // PersistOrder writes every enabled ghost to LauncherData.xml, since Steam being offline can make a
        // real Workshop mod look absent. BLSE reads no such file, only the command line built from this
        // list, and an id with no folder on disk is unresolvable to it, so orphans are dropped here only.
        private List<ModuleEntry> EnabledForLaunch() => EnabledForLaunch(BuildEnvironment());

        private static List<ModuleEntry> EnabledForLaunch(ModuleEnvironment source) =>
            [.. source.Entries.Where(e => e.IsEnabled && !e.IsOrphan)];

        [ObservableProperty]
        public partial bool ShowNotes { get; set; } = LaunchSettings.ShowNotesDefault;

        public ObservableCollection<LaunchTarget> AvailableTargets { get; } = [];

        public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

        public ObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

        public ObservableCollection<DeletedProfileRowViewModel> DeletedProfiles { get; } = [];

        public ObservableCollection<string> ComparisonLines { get; } = [];

        [ObservableProperty]
        public partial string ComparisonTitle { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
        public partial string NewProfileName { get; set; } = string.Empty;

        // The box is capped at what the store will write, so nothing typed here has to be cut down
        // afterwards and no name reaches a .bemprofile that the import would then refuse.
        public int MaxProfileNameLength => LoadOrderProfileStore.MaxNameLength;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KnownGoodLabel))]
        [NotifyCanExecuteChangedFor(nameof(ReturnToKnownGoodCommand))]
        public partial string KnownGoodName { get; set; } = string.Empty;

        public string KnownGoodLabel => KnownGoodName.Length == 0
            ? Strings.Current["Environment.KnownGoodLabel.None"]
            : Strings.Current.Format("Environment.KnownGoodLabel.Named", KnownGoodName);

        public string BackupRootPath => backupStore.RootPath;

        // The one LauncherData.xml BEM writes, backups and all. The Watch page needs it so that
        // stopping a watch can take BEM's own module out of the saved order through the same store
        // that would back it up first.
        public LauncherDataStore SavedLoadOrder => store;

        private bool CanSaveProfile => IsLoaded && !string.IsNullOrWhiteSpace(NewProfileName);

        private bool CanReturnToKnownGood => IsLoaded && KnownGoodName.Length > 0;

        // A guided bisection asks the user to leave BEM and play, so BEM being closed or crashing
        // between experiments is ordinary. When that happens the load order left on disk is an
        // experiment's, not theirs, and neither the backup list nor the profile list says which one
        // to go back to. This is that offer, and it lives here because this is where the danger is.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInterruptedBisection))]
        [NotifyCanExecuteChangedFor(nameof(RestoreInterruptedBisectionOrderCommand))]
        [NotifyCanExecuteChangedFor(nameof(DismissInterruptedBisectionCommand))]
        public partial string InterruptedBisectionNotice { get; set; } = string.Empty;

        public bool HasInterruptedBisection => InterruptedBisectionNotice.Length > 0;

        private BisectionSnapshot? interruptedBisection;

        private void RefreshInterruptedBisection()
        {
            IReadOnlyList<BisectionSnapshot> stranded;

            try
            {
                stranded = new BisectionStore(BisectionStore.GetDefaultRoot()).ListInterrupted();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to look for an interrupted bisection");
                stranded = [];
            }

            interruptedBisection = stranded.Count > 0 ? stranded[0] : null;

            if (interruptedBisection is null)
            {
                InterruptedBisectionNotice = string.Empty;
                return;
            }

            // More than one is rare and worth saying out loud: this notice handles the newest, and the
            // next takes its place here once this one is answered. Saying nothing would leave the older
            // searches' backups looking as though they did not exist.
            var others = stranded.Count > 1
                ? Strings.Current.Plural("Environment.InterruptedBisection.OlderSearches", stranded.Count - 1)
                : string.Empty;

            InterruptedBisectionNotice = AcceptedNotices.Unless(InterruptedBisectionCategory,
                Strings.Current.Format("Environment.InterruptedBisection.Notice",
                    interruptedBisection.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), others));
        }

        // Accept the risk on the interrupted search: the backup stays where it is, and the same
        // notice is not raised again. Settings lists it to forgive.
        private const string InterruptedBisectionCategory = "InterruptedBisection";

        [RelayCommand(CanExecute = nameof(HasInterruptedBisection))]
        private void AcceptInterruptedBisection()
        {
            StatusMessage = AcceptedNotices.Accept(InterruptedBisectionCategory, InterruptedBisectionNotice);
            InterruptedBisectionNotice = string.Empty;
        }

        [RelayCommand(CanExecute = nameof(HasInterruptedBisection))]
        private async Task RestoreInterruptedBisectionOrderAsync()
        {
            if (interruptedBisection is not { } stranded)
                return;

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Environment.GameRunning.CloseBeforeRestoreBisection"];
                return;
            }

            try
            {
                var safety = backupStore.Restore(stranded.SafetyBackupPath, store.FilePath);

                new BisectionStore(BisectionStore.GetDefaultRoot()).ClearInterrupted(stranded.Id);

                await Refresh();
                RefreshSafety();

                var safetyNote = safety is null
                    ? Strings.Current["Environment.RestoreInterruptedBisection.NoBackup"]
                    : Strings.Current.Format("Environment.RestoreInterruptedBisection.Backup", safety.DisplayName);

                StatusMessage = Strings.Current.Format("Environment.RestoreInterruptedBisection.Message", safetyNote);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                LoggingService.LogException(ex, "Failed to restore the order from before an interrupted bisection");
                StatusMessage = Strings.Current.Format("Environment.RestoreInterruptedBisection.Failed", ex.Message);
            }
        }

        [RelayCommand(CanExecute = nameof(HasInterruptedBisection))]
        private void DismissInterruptedBisection()
        {
            if (interruptedBisection is not { } stranded)
                return;

            new BisectionStore(BisectionStore.GetDefaultRoot()).ClearInterrupted(stranded.Id);

            RefreshInterruptedBisection();

            StatusMessage = Strings.Current["Environment.DismissInterruptedBisection.Message"];
        }

        // BEM writes exactly one module of its own into the game folder, and every path that writes it
        // takes it out again in a finally. A finally that could not delete the folder used to say
        // nothing at all, so this is the one surface that reads the disk rather than a report: it
        // stays up for as long as the folder is there, and goes when the folder does.
        //
        // It also covers BEM's own id sitting in the saved load order, because the module list no
        // longer shows the companion at all and this is now the only place that entry can be seen or
        // got rid of. The two go wrong together and are put right together.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCompanionLeftBehind))]
        [NotifyCanExecuteChangedFor(nameof(RemoveCompanionLeftBehindCommand))]
        [NotifyCanExecuteChangedFor(nameof(AcceptCompanionLeftBehindCommand))]
        public partial string CompanionLeftBehindNotice { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string CompanionLeftBehindTitle { get; set; } = string.Empty;

        public bool HasCompanionLeftBehind => CompanionLeftBehindNotice.Length > 0;

        private IReadOnlyList<CompanionLeftover> companionLeftovers = [];

        private IReadOnlyList<string> companionOrderEntries = [];

        // Every game folder BEM knows about, not only the one being played. The registered instances
        // come first so a folder one of them owns is named with that version rather than with the
        // generic label the configured install falls back to.
        private IReadOnlyList<CompanionSearchTarget> CompanionSearchTargets()
        {
            var targets = new List<CompanionSearchTarget>();

            try
            {
                // The name the rest of the app calls that version, not the one its folder was named
                // after: two installs of one version and variant carry the same DisplayName, and
                // naming the folder in this notice is pointless if both read alike.
                var restingId = instanceManager.RestingInstanceId;

                foreach (var instance in instanceManager.List())
                {
                    targets.Add(new CompanionSearchTarget(
                        instance.Record.GameFolder, InstanceLabel.LabelOf(instance.Record, restingId)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read the registered versions while looking for BEM's own module");
            }

            if (!string.IsNullOrWhiteSpace(GameInstallPath))
                targets.Add(new CompanionSearchTarget(GameInstallPath, string.Empty));

            return targets;
        }

        private void RefreshCompanionLeftBehind()
        {
            CompanionLeftBehindNotice = string.Empty;
            CompanionLeftBehindTitle = string.Empty;
            companionLeftovers = [];
            companionOrderEntries = [];

            try
            {
                // A companion that is there because a watch session is running is where it belongs.
                // Its id in the saved load order never is, armed or not: BEM puts the companion on one
                // run's command line and never in that file.
                // With the Bannerlord Exception Manager on, the folder is in every game BEM has launched
                // and belongs there: it is put back at the next launch. Off, it is litter again, and
                // this notice is how the owner clears it.
                companionLeftovers = new CrashGuardSwitch(CrashGuardSwitch.DefaultStateFilePath).IsOn
                    ? []
                    : CompanionLeftoverScan.Find(
                        CompanionSearchTargets(), new WatchSession(WatchSession.DefaultStateFilePath).Read());

                companionOrderEntries = CompanionLoadOrder.Find(store);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, "Failed to look for anything of BEM's own left in the game");
                return;
            }

            var folderNotice = companionLeftovers.Count > 0
                ? Strings.Current.Format("Environment.CompanionLeftBehind.FolderNotice",
                    string.Join(", ", companionLeftovers.SelectMany(Describe)))
                : string.Empty;

            var orderNotice = companionOrderEntries.Count > 0
                ? Strings.Current.Format("Environment.CompanionLeftBehind.OrderNotice", string.Join(", ", companionOrderEntries))
                : string.Empty;

            CompanionLeftBehindTitle = companionLeftovers.Count > 0
                ? Strings.Current["Environment.CompanionLeftBehind.TitleFolder"]
                : Strings.Current["Environment.CompanionLeftBehind.TitleOrder"];

            var notice = string.Join(" ", new[] { folderNotice, orderNotice }.Where(s => s.Length > 0));

            // Accepted risks are keyed on the whole notice, which names every folder and entry, so a
            // different leftover is a different notice and comes back. Settings lists it to forgive.
            if (AcceptedBanners().Contains(AcceptedFindingStore.KeyFor(CompanionLeftBehindCategory, notice)))
            {
                CompanionLeftBehindTitle = string.Empty;
                return;
            }

            CompanionLeftBehindNotice = notice;
        }

        private const string CompanionLeftBehindCategory = "CompanionLeftBehind";

        private IReadOnlySet<string> AcceptedBanners() =>
            new AcceptedFindingStore(AcceptedFindingStore.DefaultPath(ActiveDataRoot)).LoadKeys();

        [RelayCommand(CanExecute = nameof(HasCompanionLeftBehind))]
        private void AcceptCompanionLeftBehind()
        {
            var notice = CompanionLeftBehindNotice;
            var record = new AcceptedFindingStore(AcceptedFindingStore.DefaultPath(ActiveDataRoot))
                .Accept([new AcceptedFinding(CompanionLeftBehindCategory, notice, DateTimeOffset.UtcNow)]);

            RefreshCompanionLeftBehind();
            StatusMessage = record.Describe();
        }

        // The last 256 KB of this run's rgl_log_<pid>.txt, where the engine lists each assembly as it loads it.
        private static string ReadRglLogTail(string installPath, int processId)
        {
            try
            {
                var log = LogLocator.Find(installPath).Logs.FirstOrDefault(l =>
                    string.Equals(Path.GetFileName(l.Path), $"rgl_log_{processId}.txt", StringComparison.OrdinalIgnoreCase));

                if (log is null)
                    return string.Empty;

                using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = (int)Math.Min(stream.Length, 262_144);
                stream.Seek(-length, SeekOrigin.End);
                var buffer = new byte[length];
                stream.ReadExactly(buffer);

                return System.Text.Encoding.UTF8.GetString(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static IEnumerable<string> Describe(CompanionLeftover leftover) =>
            Describe(leftover.Folders, leftover.VersionLabel);

        private static IEnumerable<string> Describe(IEnumerable<string> folders, string versionLabel) =>
            folders.Select(folder => versionLabel.Length > 0
                ? Strings.Current.Format("Environment.CompanionLeftBehind.FolderEntry", folder, versionLabel)
                : Strings.Current.Format("Environment.CompanionLeftBehind.FolderEntryNoVersion", folder));

        [RelayCommand(CanExecute = nameof(HasCompanionLeftBehind))]
        private void RemoveCompanionLeftBehind()
        {
            var said = new List<string>();

            // Per folder rather than once for all of them: a delete that failed in one version's
            // folder and succeeded in another's is two different sentences, and the folder that is
            // still there stays on the notice below because the notice re-reads the disk.
            foreach (var removal in CompanionLeftoverScan.Remove(companionLeftovers))
            {
                said.Add(removal.Removed
                    ? Strings.Current.Format("Environment.CompanionLeftBehind.RemovedFolder",
                        string.Join(", ", Describe(removal.Folders, removal.VersionLabel)))
                    : Strings.Current.Format("Environment.CompanionLeftBehind.CouldNotDeleteFolder",
                        string.Join(", ", Describe(removal.RemainingFolders, removal.VersionLabel)),
                        removal.Error ?? Strings.Current["Environment.CompanionLeftBehind.StillThere"]));
            }

            if (companionOrderEntries.Count > 0)
            {
                try
                {
                    var removed = CompanionLoadOrder.Prune(store);

                    said.Add(removed.Count > 0
                        ? Strings.Current.Format("Environment.CompanionLeftBehind.RemovedFromOrder", string.Join(" and ", removed))
                        : Strings.Current["Environment.CompanionLeftBehind.NothingToRemove"]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                               or System.Xml.XmlException)
                {
                    LoggingService.LogException(ex, "Failed to take BEM's own entry out of the saved load order");
                    said.Add(Strings.Current.Format("Environment.CompanionLeftBehind.WriteFailed", ex.Message));
                }
            }

            RefreshSafety();

            StatusMessage = string.Join(" ", said);
        }

        // Everything about the companion that lives outside the module list: whether the next launch is
        // watched, and whether anything of BEM's own is left in the game or in the saved load order.
        // Both change from the Watch page as well as from here, so both are re-read together.
        public void RefreshCompanionState()
        {
            RefreshWatchArmed();
            RefreshCompanionLeftBehind();
        }

        public void RefreshSafety()
        {
            RefreshInterruptedBisection();
            RefreshCompanionLeftBehind();
            RefreshWatchArmed();

            Backups.Clear();

            foreach (var backup in ReadBackups())
                Backups.Add(new BackupRowViewModel(backup, RestoreBackupAsync, CompareBackup, DiscardBackupAsync));

            Profiles.Clear();

            foreach (var profile in ReadProfiles())
                Profiles.Add(new ProfileRowViewModel(
                    profile, RestoreProfile, CompareProfile, MarkKnownGood, DeleteProfile, ExportProfileAsync));

            // The rows were rebuilt, so a pick made from the old ones names a profile no row stands for.
            CompareFromProfile = null;
            CompareToProfile = null;
            OnPropertyChanged(nameof(CanPickProfilesToCompare));

            DeletedProfiles.Clear();

            foreach (var deleted in ReadDeletedProfiles())
                DeletedProfiles.Add(new DeletedProfileRowViewModel(deleted, RestoreDeletedProfile, DiscardDeletedProfileAsync));

            KnownGoodName = Profiles.FirstOrDefault(p => p.IsKnownGood)?.Name ?? string.Empty;
        }

        private IReadOnlyList<LoadOrderBackup> ReadBackups()
        {
            try
            {
                return backupStore.List();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to list load order backups");
                return [];
            }
        }

        private IReadOnlyList<LoadOrderProfile> ReadProfiles()
        {
            try
            {
                return profileStore.List();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to list load order profiles");
                return [];
            }
        }

        private IReadOnlyList<DeletedProfile> ReadDeletedProfiles()
        {
            try
            {
                return profileStore.ListDeleted();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to list removed load order profiles");
                return [];
            }
        }

        [RelayCommand(CanExecute = nameof(CanSaveProfile))]
        public void SaveProfile()
        {
            if (!CanSaveProfile)
                return;

            var name = NewProfileName.Trim();

            try
            {
                var saved = profileStore.Save(name, LoadOrderSnapshot.From(BuildEnvironment(), [.. Dividers]));

                var settings = new ProfileSettingsStore(ProfileSettingsStore.DefaultRoot(ActiveDataRoot));

                settings.Capture(saved.Name, ModSettingsScanner.DefaultRootFor(ActiveDataRoot));
                settings.SetActive(saved.Name);

                NewProfileName = string.Empty;
                RefreshSafety();

                StatusMessage = Strings.Current.Plural("Environment.Profile.Saved", saved.Order.Entries.Count,
                    saved.Name, saved.Order.EnabledCount);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save a load order profile");
                StatusMessage = Strings.Current.Format("Environment.Profile.SaveFailed", name, ex.Message);
            }
        }

        [RelayCommand(CanExecute = nameof(CanReturnToKnownGood))]
        public void ReturnToKnownGood()
        {
            var known = Profiles.FirstOrDefault(p => p.IsKnownGood);

            if (known is null)
            {
                StatusMessage = Strings.Current["Environment.Profile.NoKnownGood"];
                return;
            }

            RestoreProfile(known);
        }

        private void MarkKnownGood(ProfileRowViewModel row)
        {
            try
            {
                if (!profileStore.MarkKnownGood(row.Name))
                {
                    StatusMessage = Strings.Current.Format("Environment.Profile.MarkKnownGood.Missing", row.Name);
                    RefreshSafety();
                    return;
                }

                RefreshSafety();
                StatusMessage = Strings.Current.Format("Environment.Profile.MarkKnownGood.Success", row.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to mark a profile known-good");
                StatusMessage = Strings.Current.Format("Environment.Profile.MarkKnownGood.Failed", row.Name, ex.Message);
            }
        }

        private void DeleteProfile(ProfileRowViewModel row)
        {
            try
            {
                if (!profileStore.Delete(row.Name))
                {
                    StatusMessage = Strings.Current.Format("Environment.Profile.Delete.AlreadyGone", row.Name);
                    RefreshSafety();
                    return;
                }

                RefreshSafety();

                StatusMessage = Strings.Current.Format("Environment.Profile.Delete.Success", row.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to delete a load order profile");
                StatusMessage = Strings.Current.Format("Environment.Profile.Delete.Failed", row.Name, ex.Message);
            }
        }

        private void RestoreDeletedProfile(DeletedProfileRowViewModel row)
        {
            try
            {
                if (profileStore.Restore(row.Path) is not { } restored)
                {
                    StatusMessage = Strings.Current.Format("Environment.Profile.Restore.Missing", row.Name);
                    RefreshSafety();
                    return;
                }

                var wasKnownGood = row.Deleted.Profile.IsKnownGood;
                RefreshSafety();

                var renamed = restored.Name == row.Name
                    ? string.Empty
                    : Strings.Current.Format("Environment.Profile.Restore.Renamed", row.Name, restored.Name);

                var mark = wasKnownGood
                    ? Strings.Current.Format("Environment.Profile.Restore.MarkNote", row.KindWord)
                    : string.Empty;

                StatusMessage = Strings.Current.Format("Environment.Profile.Restore.Success", restored.Name, renamed, mark);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to restore a removed load order profile");
                StatusMessage = Strings.Current.Format("Environment.Profile.Restore.Failed", row.Name, ex.Message);
            }
        }

        // Asks first, names the profile, and sends the file to the Recycle Bin rather than unlinking it,
        // so even the deliberately destructive action here has a way back.
        private async Task DiscardDeletedProfileAsync(DeletedProfileRowViewModel row)
        {
            if (!await ConfirmDiscardAsync(row))
            {
                StatusMessage = Strings.Current.Format("Environment.Profile.Discard.Kept", row.Name);
                return;
            }

            try
            {
                if (!profileStore.Discard(row.Path, RecycleFile))
                {
                    StatusMessage = Strings.Current.Format("Environment.Profile.Discard.AlreadyGone", row.Name);
                    RefreshSafety();
                    return;
                }

                RefreshSafety();
                StatusMessage = Strings.Current.Format("Environment.Profile.Discard.Success", row.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, "Failed to discard a removed load order profile");
                StatusMessage = Strings.Current.Format("Environment.Profile.Discard.Failed", row.Name, ex.Message);
            }
        }

        // A backup is a list of module ids, not the modules themselves, so the question names the state
        // it records rather than a size. It goes to the Recycle Bin like everything else.
        private async Task DiscardBackupAsync(BackupRowViewModel row)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.Backup.DiscardDialog.Title"],
                Content = new TextBlock
                {
                    Text = Strings.Current.Format("Environment.Backup.DiscardDialog.Body", row.DisplayName),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Environment.RecycleBinDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current["Environment.Backup.Discard.Kept"];
                return;
            }

            try
            {
                if (!backupStore.Discard(row.Backup.Path, RecycleFile))
                {
                    StatusMessage = Strings.Current["Environment.Backup.DiscardAlreadyGone"];
                    RefreshSafety();
                    return;
                }

                RefreshSafety();
                StatusMessage = Strings.Current.Format("Environment.Backup.DiscardMoved", row.DisplayName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, "Failed to discard a load order backup");
                StatusMessage = Strings.Current.Format("Environment.Backup.Discard.Failed", ex.Message);
            }
        }

        private static void RecycleFile(string path) =>
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        private static async Task<bool> ConfirmDiscardAsync(DeletedProfileRowViewModel row)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.DeletedProfile.DiscardDialog.Title", row.Name),
                Content = new TextBlock
                {
                    Text = Strings.Current.Format("Environment.DeletedProfile.DiscardDialog.Body", row.Name, row.KindWord, row.WhenText, row.ContentText),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Environment.RecycleBinDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private void RestoreProfile(ProfileRowViewModel row)
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Profile.NotLoaded"];
                return;
            }

            var before = BuildEnvironment();
            var restored = row.Profile.Order.ApplyTo(before, ownTrusted: !row.Profile.IsShared);
            var sections = RestoredSections(row.Profile.Order, restored);

            RecordUndo(before, Strings.Current.Format("Environment.UndoDescription.RestoreProfile", row.Name));

            // Ahead of Apply, which is what rebuilds the list on screen: a section adopted after it
            // would not show up until something unrelated refreshed the tab.
            if (sections is not null)
            {
                Dividers.Clear();

                foreach (var divider in sections)
                    Dividers.Add(divider);

                SaveDividersPublic();
            }

            Apply(restored.Environment);
            PersistOrder();
            SwitchProfileSettings(row.Name);
            _ = SwitchProfileSavesAsync(row.Name);

            var sectionsNote = sections is null
                ? Strings.Current["Environment.Profile.RestoreLoaded.NoSections"]
                : Strings.Current.Plural("Environment.Profile.RestoreLoaded.WithSections", sections.Count);

            StatusMessage = Strings.Current.Format("Environment.Profile.RestoreLoaded", row.Name, DescribeRestore(restored), sectionsNote);
        }

        // The settings files follow the profile: the ones live now are saved into the profile being left
        // and the incoming profile's copy goes live, each folder it replaces backed up first. A failure
        // here never undoes the load order that was just restored; it is said instead.
        private void SwitchProfileSettings(string profileName)
        {
            try
            {
                var result = new ProfileSettingsStore(ProfileSettingsStore.DefaultRoot(ActiveDataRoot)).SwitchTo(
                    profileName,
                    ModSettingsScanner.DefaultRootFor(ActiveDataRoot),
                    new ModSettingsArchive(ModSettingsArchive.GetDefaultRoot(ActiveDataRoot)));

                if (result.Applied > 0)
                    StatusMessage = Strings.Current.Format("Environment.Profile.SettingsSwitched", profileName, result.Applied, result.BackedUp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to switch the mod settings with the profile");
            }
        }

        // A profile that recorded no sections says nothing about them, so restoring it leaves the
        // list's own alone rather than clearing them: every profile saved before sections existed
        // would otherwise wipe them. One that did record some replaces them, which is what restoring
        // that load order means. RecognizedDividers are what ApplyTo pulled out of an older profile's
        // entries as a stray divider-* id, adopted alongside anything the profile recorded properly.
        private static IReadOnlyList<LoadOrderDivider>? RestoredSections(
            LoadOrderSnapshot order, LoadOrderRestore restored)
        {
            var sections = order.Dividers.Select(d => d.ToDivider()).ToList();

            foreach (var recognized in restored.RecognizedDividers)
            {
                if (!sections.Any(d => d.Label == recognized.Label && d.AnchorId == recognized.AnchorId))
                    sections.Add(recognized);
            }

            return sections.Count == 0 ? null : sections;
        }

        private static string DescribeRestore(LoadOrderRestore restored)
        {
            var parts = new List<string> { Strings.Current.Plural("Environment.RestoreProfile.Applied", restored.Applied) };

            if (restored.Missing > 0)
                parts.Add(Strings.Current.Plural("Environment.RestoreProfile.Missing", restored.Missing));

            if (restored.Kept > 0)
                parts.Add(Strings.Current.Plural("Environment.RestoreProfile.Kept", restored.Kept));

            // A profile can now come from a stranger, so what it asked for and what it got are not
            // always the same thing. Saying nothing would be the profile silently not being applied.
            if (restored.Refused > 0)
                parts.Add(Strings.Current.Plural("Environment.RestoreProfile.Refused", restored.Refused));

            return string.Join(", ", parts) + ".";
        }

        // Restoring rewrites LauncherData.xml and then reloads from it, so a change still being held
        // for a running launcher would go with it. That is refused rather than reported after the fact.
        private async Task RestoreBackupAsync(BackupRowViewModel row)
        {
            if (HasHeldChanges)
            {
                StatusMessage = Strings.Current["Environment.Backup.Restore.HeldChanges"];
                return;
            }

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Environment.GameRunning.CloseBeforeRestoreBackup"];
                return;
            }

            var before = BuildEnvironment();

            try
            {
                var safety = backupStore.Restore(row.Backup.Path, store.FilePath);

                await Refresh();
                RefreshSafety();

                // Refresh clears the undo stack, so the pre-restore list is recorded after it, not before.
                RecordUndo(before, Strings.Current.Format("Environment.UndoDescription.RestoreBackup", row.TakenText));

                var safetyNote = safety is null
                    ? Strings.Current["Environment.Backup.RestoreSuccess.NoBackup"]
                    : Strings.Current.Format("Environment.Backup.RestoreSuccess.Backup", safety.DisplayName);

                StatusMessage = Strings.Current.Format("Environment.Backup.RestoreSuccess", row.Backup.DisplayName, safetyNote);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                LoggingService.LogException(ex, "Failed to restore a load order backup");
                RefreshSafety();
                StatusMessage = Strings.Current.Format("Environment.Backup.RestoreFailed", ex.Message);
            }
        }

        private void CompareProfile(ProfileRowViewModel row) =>
            ShowComparison(Strings.Current.Format("Environment.CompareWhat.Profile", row.Name), row.Profile.Order);

        private void CompareBackup(BackupRowViewModel row)
        {
            try
            {
                ShowComparison(Strings.Current.Format("Environment.CompareWhat.Backup", row.Backup.DisplayName), backupStore.Read(row.Backup));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read a load order backup");
                StatusMessage = Strings.Current.Format("Environment.Backup.ReadFailed", ex.Message);
            }
        }

        // Two saved profiles side by side, from the first to the second: what changing from one to the
        // other would do. It reads only the saved profiles, so the live list is never touched.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompareSelectedProfilesCommand))]
        public partial ProfileRowViewModel? CompareFromProfile { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompareSelectedProfilesCommand))]
        public partial ProfileRowViewModel? CompareToProfile { get; set; }

        public bool CanPickProfilesToCompare => Profiles.Count >= 2;

        private bool CanCompareSelectedProfiles =>
            CompareFromProfile is not null && CompareToProfile is not null && !ReferenceEquals(CompareFromProfile, CompareToProfile);

        [RelayCommand(CanExecute = nameof(CanCompareSelectedProfiles))]
        private void CompareSelectedProfiles()
        {
            if (CompareFromProfile is not { } from || CompareToProfile is not { } to)
                return;

            ShowComparison(
                Strings.Current.Format("Environment.CompareWhat.Profile", to.Name),
                to.Profile.Order,
                from.Profile.Order,
                Strings.Current.Format("Environment.CompareWhat.Profile", from.Name));
        }

        private void ShowComparison(string what, LoadOrderSnapshot other, LoadOrderSnapshot? baseline = null, string? baselineWhat = null)
        {
            var difference = LoadOrderComparer.Compare(baseline ?? LoadOrderSnapshot.From(BuildEnvironment()), other);

            ComparisonTitle = difference.IsIdentical
                ? baselineWhat is null
                    ? Strings.Current.Format("Environment.Compare.Identical", what)
                    : Strings.Current.Format("Environment.Compare.ProfilesIdentical", baselineWhat, what)
                : baselineWhat is null
                    ? Strings.Current.Format("Environment.Compare.Different", what, difference.Summary)
                    : Strings.Current.Format("Environment.Compare.ProfilesDifferent", baselineWhat, what, difference.Summary);

            ComparisonLines.Clear();

            foreach (var change in difference.Changes)
                ComparisonLines.Add(Strings.Current.Format("Environment.CompareLine", Describe(change.Kind), change.Id));

            StatusMessage = ComparisonTitle;
        }

        private static string Describe(LoadOrderChangeKind kind) => kind switch
        {
            LoadOrderChangeKind.Added => Strings.Current["Environment.ChangeKind.Add"],
            LoadOrderChangeKind.Removed => Strings.Current["Environment.ChangeKind.Remove"],
            LoadOrderChangeKind.Moved => Strings.Current["Environment.ChangeKind.Move"],
            _ => Strings.Current["Environment.ChangeKind.Toggle"]
        };

        partial void OnPreferredTargetChanged(LaunchTargetKind? value)
        {
            _ = value;
            UpdateLaunchTargetText();
            SaveLaunchSettings();
        }

        // Rebuilding the list makes the ComboBox drop its selection and push a null back through the
        // two-way binding, which must not be mistaken for the user choosing nothing.
        partial void OnSelectedTargetChanged(LaunchTarget? value)
        {
            if (value is not null && !restoringSelection)
                PreferredTarget = value.Kind;
        }

        private void RefreshLaunchTargets()
        {
            var found = LaunchTargetResolver.FindAvailable(GameInstallPath);

            // A launcher hands off to the game and exits, so BEM cannot hold this version's junctions
            // for the whole run: the game would end up reading and writing the resting version's
            // saves and settings, which is the one thing versions exist to prevent. LaunchAsync
            // refuses those targets outright, so listing them would be listing controls that cannot
            // work. They come back the moment this version is the resting one again.
            var directOnly = IsPlayingAnotherVersion();

            var available = directOnly
                ? found.Where(target => LaunchRecord.CanSeeTheEnd(target.Kind)).ToList()
                : [.. found];

            AvailableTargets.Clear();
            foreach (var target in available)
                AvailableTargets.Add(target);

            var saved = PreferredTarget;

            var shown = available.FirstOrDefault(t => t.Kind == saved)
                ?? available.FirstOrDefault(t => t.Kind == LaunchTargetKind.BlseStandalone)
                ?? available.FirstOrDefault();

            restoringSelection = true;

            try
            {
                SelectedTarget = shown;
            }
            finally
            {
                restoringSelection = false;
            }

            // Why the list is short is a standing fact about the picker, not news about this refresh, so
            // it sits beside the picker where the question arises. In the status line it was a second
            // and third sentence after the load result, and the three together ran past the end of the
            // line every time an environment loaded.
            LaunchTargetNote = directOnly && found.Count != available.Count
                ? Strings.Current["Environment.LaunchTarget.OnlyDirectNote"]
                : string.Empty;

            // Hidden is not the same as missing, and the difference matters to someone looking for a
            // launcher that is installed: say which it is.
            launchTargetNotice = (saved is { } savedKind && shown?.Kind != savedKind, directOnly) switch
            {
                (true, true) when !LaunchRecord.CanSeeTheEnd(saved!.Value) =>
                    Strings.Current.Format("Environment.LaunchTarget.HiddenWhilePlayingAnother", DescribeTarget(saved.Value),
                        shown?.DisplayName ?? Strings.Current["Environment.LaunchTarget.NoneShown"]),
                (true, _) when shown is null =>
                    Strings.Current.Format("Environment.LaunchTarget.SavedMissingNoOther", DescribeTarget(saved!.Value)),
                (true, _) =>
                    Strings.Current.Format("Environment.LaunchTarget.SavedMissingShowingOther", DescribeTarget(saved!.Value), shown!.DisplayName, DescribeTarget(saved.Value)),
                _ => string.Empty
            };

            if (launchTargetNotice.Length > 0)
                StatusMessage = launchTargetNotice;

            UpdateLaunchTargetText();
        }

        private static string DescribeTarget(LaunchTargetKind kind) => kind switch
        {
            LaunchTargetKind.BlseStandalone => "BLSE (direct)",
            LaunchTargetKind.BlseLauncher => "BLSE Launcher",
            LaunchTargetKind.BlseLauncherEx => "BLSE LauncherEx",
            LaunchTargetKind.GameExecutable => "Bannerlord (direct, no BLSE)",
            LaunchTargetKind.Steam => "Steam",
            _ => "TaleWorlds Launcher"
        };

        partial void OnExtraArgumentsChanged(string value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnMinimizeOnLaunchChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnCheatModeChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnUseVanillaCrashHandlerChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnDisableAutoGeneratedExceptionCatchingChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnKeepCrashHandlerUnderDebuggerChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnPermitManualLoadOrderOverrideChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnPreferInstalledButrStackChanged(bool value)
        {
            _ = value;
            SaveLaunchSettings();
        }

        partial void OnShowNotesChanged(bool value)
        {
            foreach (var row in Modules)
                row.ShowNotes = value;

            SaveLaunchSettings();
        }

        public void LoadLaunchSettings()
        {
            try
            {
                var settings = launchSettingsStore.Load();

                loadingLaunchSettings = true;

                try
                {
                    PreferredTarget = LaunchTargetPreference.Read(settings.TargetKind, settings.TargetIndex);
                    ExtraArguments = settings.ExtraArguments;
                    MinimizeOnLaunch = settings.MinimizeOnLaunch;
                    CheatMode = settings.CheatMode;
                    ShowNotes = settings.ShowNotesOrDefault;
                    UseVanillaCrashHandler = settings.UseVanillaCrashHandler;
                    DisableAutoGeneratedExceptionCatching = settings.DisableAutoGeneratedExceptionCatching;
                    KeepCrashHandlerUnderDebugger = settings.KeepCrashHandlerUnderDebugger;
                    PermitManualLoadOrderOverride = settings.PermitManualLoadOrderOverride;
                    PreferInstalledButrStack = !settings.PreferBundledButrCopies;
                }
                finally
                {
                    loadingLaunchSettings = false;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to load launch settings");
            }
        }

        private void SaveLaunchSettings()
        {
            if (loadingLaunchSettings)
                return;

            try
            {
                launchSettingsStore.Save(new LaunchSettings(
                    null,
                    ExtraArguments,
                    MinimizeOnLaunch,
                    ShowNotes,
                    PreferredTarget?.ToString(),
                    UseVanillaCrashHandler,
                    DisableAutoGeneratedExceptionCatching,
                    KeepCrashHandlerUnderDebugger,
                    PermitManualLoadOrderOverride,
                    !PreferInstalledButrStack,
                    CheatMode,
                    launchSettingsStore.Load().LastLaunchedVersion));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save launch settings");
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            _ = value;
            RefreshVisibleModules();
        }

        partial void OnGameInstallPathChanged(string value)
        {
            GameVersionText = GameVersionReader.Read(value) is { IsEmpty: false } version
                ? version.ToString()
                : Strings.Current["Environment.GameVersionText.Unknown"];

            RefreshLaunchTargets();

            // A version switch is the one moment the notice can be describing a state that is no
            // longer current: it is computed when the page is opened, against whatever install was
            // configured then, and Refresh() resolves the active instance afterwards. Recomputing it
            // here means it can never outlive the install it was read from.
            RefreshCompanionLeftBehind();
        }

        // Every automatic trigger (the Modules folder watcher) and every deliberate one (Play's own
        // menu item, an install completing, an instance switch) funnel through RefreshCoreAsync, so a
        // call arriving while one is already running is coalesced into a single rerun once the first
        // finishes rather than two scans interleaving their writes to the same Modules collection.
        [RelayCommand]
        public async Task Refresh()
        {
            await RefreshCoreAsync(userInitiated: true);
            QueueStylesheetCheck();
        }

        // The watcher's own path: an automatic reload the user did not ask for, so it must not carry
        // the side effects that are only correct when the user deliberately asked to discard something.
        private async Task RefreshFromWatcherAsync() => await RefreshCoreAsync(userInitiated: false);

        private bool refreshRunning;

        // Open while a rescan runs, so Launch stays pressable and waits for it rather than sitting dead.
        private readonly RescanWindow rescanGate = new();
        private bool CanLaunch => IsLoaded || rescanGate.Active;
        private bool refreshRerunRequested;
        private bool refreshRerunUserInitiated;

        private async Task RefreshCoreAsync(bool userInitiated)
        {
            if (refreshRunning)
            {
                refreshRerunRequested = true;
                refreshRerunUserInitiated |= userInitiated;
                return;
            }

            refreshRunning = true;
            rescanGate.RescanStarted();
            LaunchCommand.NotifyCanExecuteChanged();

            try
            {
                await RefreshOnceAsync(userInitiated);
            }
            finally
            {
                refreshRunning = false;
                focusRefresh.Note();
                refreshedOnce = true;

                rescanGate.RescanFinished(refreshRerunRequested);

                LaunchCommand.NotifyCanExecuteChanged();
            }

            // The rows were rebuilt, so which of them hold a settings file is read again: the Settings
            // button shows only on those.
            try
            {
                await ShellViewModels.Instance.ModSettings.MarkSettingsRowsAsync();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not read which mods hold settings files");
            }

            if (refreshRerunRequested)
            {
                refreshRerunRequested = false;
                var rerunUserInitiated = refreshRerunUserInitiated;
                refreshRerunUserInitiated = false;
                await RefreshCoreAsync(rerunUserInitiated);
            }
        }

        private async Task RefreshOnceAsync(bool userInitiated)
        {
            IsBusy = true;

            // Refresh reloads from disk and is the deliberate way to throw list edits away when the
            // user asked for it. A refresh the Modules folder watcher started on its own must not carry
            // that side effect: a user who reordered modules and then had any file touched under
            // Modules - by another tab, another tool, or the game itself - must not lose their undo
            // path over a reload they never asked for.
            //
            // Accepted, not fixed: each undo step is a self-contained snapshot of the whole order, so
            // Undo() after a background refresh replays a pre-refresh snapshot and can discard whatever
            // that refresh just picked up from disk. Clearing the stack here would remove exactly the
            // capability this comment block exists to keep, so the trade stands.
            if (userInitiated)
            {
                undoStack.Clear();
                RefreshUndo();
            }

            // The module set is about to be re-read, so what was detected from the last one no longer
            // describes it.
            undeclaredScan = null;

            try
            {
                // The active instance's folder wins over whatever was found or set before, so the load
                // order, saves and health checks all read the version about to launch rather than
                // whichever install happened to be configured first. With no instance registered at
                // all this falls straight through to the locator, which is what BEM did before instances
                // existed.
                var active = instanceManager.Active();

                if (active is not null)
                {
                    GameInstallPath = active.Record.GameFolder;
                    UseLoadOrderOf(active);
                }
                // Off the UI thread: while no install is configured this runs on every visit to Play,
                // and the locator's Game Pass fallback walks every fixed drive when Steam and GOG
                // turn up nothing.
                else
                {
                    UseLoadOrderOf(null);

                    if (string.IsNullOrWhiteSpace(GameInstallPath))
                        GameInstallPath = await Task.Run(GameInstallLocator.Locate) ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(GameInstallPath))
                {
                    StopWatchingModulesFolder();
                    Unload(Strings.Current["Environment.Unload.NoInstall"]);
                    return;
                }

                // The locator's answer used to live only here: Library and Settings read the path
                // InstallViewModel persists, so on a fresh machine Play showed the game it had just
                // found while both of those pages showed a blank box claiming it had not been. Pushing
                // the found path across at the point of detection persists it (Install saves on the
                // property change) and every page tells one story.
                var install = Views.ShellViewModels.Instance.Install;

                if (string.IsNullOrWhiteSpace(install.GameInstallPath))
                    install.GameInstallPath = GameInstallPath;
                // Mods follow the version being played: Install writes into the active instance's own
                // Modules folder, and stops pointing at it the moment another version is selected.
                else if (active is not null)
                    install.FollowInstance(GameInstallPath);

                var modulesFolder = ModuleScanner.GetModulesFolder(GameInstallPath);

                if (!Directory.Exists(modulesFolder))
                {
                    StopWatchingModulesFolder();
                    Unload(Strings.Current.Format("Environment.Unload.ModulesFolderMissing", modulesFolder));
                    return;
                }

                // Follows whichever install this Refresh() just resolved, including a switch to another
                // instance: a no-op if it is already watching this exact folder, otherwise it tears down
                // the previous watch first so a run of instance switches never leaves more than one alive.
                StartWatchingModulesFolder(modulesFolder);

                RefreshLaunchTargets();

                // The scan reads every SubModule.xml and runs an Authenticode check per module, which
                // at over two hundred modules is seconds of work that must not freeze the UI. Everything
                // it produces is plain data; only Apply below touches the observable collections, and
                // that runs back on the UI thread after the await.
                var scan = await Task.Run(() => ModuleScanner.ScanAll(GameInstallPath));

                if (scan.Failed)
                {
                    LoggingService.Log($"Module scan failed: {scan.Error}", LogLevel.Error);
                    Unload(Strings.Current.Format("Environment.Unload.ModuleScanFailed", scan.Error));
                    return;
                }

                // A version with no LauncherData.xml of its own has never been given a load order by
                // anyone, so its own modules are switched on before the file is read rather than shown
                // as a version with the game itself turned off. It writes only when the file is absent,
                // so this is a no-op on every load after the first.
                var seed = await Task.Run(() => OfficialModuleSeed.Seed(store, scan));

                if (seed.Seeded)
                    LoggingService.Log($"Seeded a new load order: {seed.Reason}");

                var stored = await Task.Run(() => store.Read());

                await KeepWorkshopModulesAsync(scan.Modules, modulesFolder);
                await ShowManagerDeploymentAsync(scan.Modules);

                if (scan.WorkshopFailed)
                    LoggingService.Log($"Workshop module scan failed: {scan.WorkshopError}", LogLevel.Warn);

                // BEM's own companion is not one of the user's mods and is never theirs to manage. It is
                // put on one run's command line by BEM and taken off again, so listing it here only ever
                // invited the one action that is wrong: enabling it. That happened, which wrote
                // it into the saved load order, where it outlives the folder and points at nothing.
                //
                // Keeping it out of the list is also what makes that impossible to repeat, because the
                // list is what Save writes. The folder and any entry already in the file stay visible
                // and removable on the banner above, which reads the disk rather than this list.
                var merged = ModuleEnvironment.Merge(scan, stored);
                var loaded = merged.WithEntries(
                    [.. merged.Entries.Where(e => !CompanionManifest.IsCompanionId(e.Id.Value))]);

                // A real divider-* module folder (shipped inside a shared modlist) is recognized here
                // and pulled out of Entries so nothing downstream ever treats it as a mod. Dedup is by
                // SourceModuleId so a folder already adopted on an earlier scan is never added twice.
                var (withoutDividers, recognized) = loaded.ExtractDividers();
                loaded = withoutDividers;

                var existingSourceIds = Dividers.Where(d => d.SourceModuleId is not null)
                    .Select(d => d.SourceModuleId!.Value).ToHashSet();

                foreach (var divider in recognized)
                {
                    if (divider.SourceModuleId is { } source && !existingSourceIds.Contains(source))
                        Dividers.Add(divider);
                }

                if (recognized.Count > 0)
                    SaveDividersPublic();

                var applyReads = await Task.Run(() => ReadApplyReads(loaded));
                lastApplyReads = applyReads;

                Apply(loaded, applyReads);
                MarkSaved(loaded);
                IsLoaded = true;

                // The install path is set and the load order is read, which is what arming needs: it
                // checks the install and wants Bannerlord.Harmony among the enabled modules. Earlier
                // callers on startup ran before either was true.
                RefreshWatchArmed();

                var loadedMessage = seed.Seeded
                    ? Strings.Current.Plural("Environment.LoadedMessage.Seeded", seed.Enabled.Count)
                    : store.Exists
                        ? Strings.Current.Plural("Environment.LoadedMessage.Loaded", environment.Entries.Count, GameInstallPath)
                        : Strings.Current.Plural("Environment.LoadedMessage.NoFile", scan.Modules.Count);

                // The load result only. Anything else worth saying has its own place on the page.
                StatusMessage = loadedMessage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, "Failed to load the environment");
                Unload(Strings.Current.Format("Environment.Unload.EnvironmentLoadFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;

                // Re-read here rather than once at construction: the module set has just changed, and
                // signing in to Nexus mid-session should bring the chip with it on the next
                // refresh rather than on the next start. The verdict computation reads module folders,
                // so it runs on a worker and only its result is applied back on this thread.
                await RefreshNexusAvailabilityAsync();
            }
        }

        public ObservableCollection<PinRowViewModel> PinnedModules { get; } = [];

        public bool HasPins => pins.Count > 0;

        public string PinsLabel => pins.Count == 0
            ? Strings.Current["Environment.PinsLabel.Empty"]
            : Strings.Current.Format("Environment.PinsLabel.WithCount", pins.Count);

        public string PinsSummary
        {
            get
            {
                if (pins.Count == 0)
                    return Strings.Current["Environment.PinsSummary.Empty"];

                var inactive = pins.InactiveIn([.. Modules.Select(row => row.Entry)]).Count;

                return inactive == 0
                    ? Strings.Current.Plural("Environment.PinsSummary.AllActive", pins.Count)
                    : Strings.Current.Plural("Environment.PinsSummary.SomeInactive", pins.Count, inactive);
            }
        }

        private void LoadPins()
        {
            try
            {
                pins = pinStore.Read();
                loadOrderChoices = loadOrderChoiceStore.Read();
                orderRules = orderRuleStore.Read();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read the pinned modules");

                StatusMessage = Strings.Current.Format("Environment.Pins.ReadFailed", pinStore.FilePath, ex.Message);
            }
        }

        private void SavePins()
        {
            try
            {
                pinStore.Write(pins);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the pinned modules");
                StatusMessage = Strings.Current.Format("Environment.Pins.SaveFailed", pinStore.FilePath, ex.Message);
            }
        }

        // Rebuilds the row flags and the Pins list from the one set of ids, so the indicator on a row
        // and the list in the flyout can never disagree about what is pinned.
        private void RefreshPins(ApplyReads? reads = null)
        {
            foreach (var row in Modules)
                row.IsPinned = pins.Contains(row.Entry.Id);

            var positionOf = new Dictionary<ModuleId, int>();

            for (var i = 0; i < Modules.Count; i++)
                positionOf[Modules[i].Entry.Id] = i + 1;

            RefreshUserNotes(reads?.Notes);

            PinnedModules.Clear();

            foreach (var id in pins.Ids)
            {
                PinnedModules.Add(positionOf.TryGetValue(id, out var position)
                    ? new PinRowViewModel(id, position, Modules[position - 1].DisplayName, Unpin)
                    : new PinRowViewModel(id, 0, id.Value, Unpin));
            }

            OnPropertyChanged(nameof(HasPins));
            OnPropertyChanged(nameof(PinsLabel));
            OnPropertyChanged(nameof(PinsSummary));
            OnPropertyChanged(nameof(ContextPinLabel));
            ClearPinsCommand.NotifyCanExecuteChanged();
        }

        private void Unpin(PinRowViewModel row)
        {
            pins = pins.Without(row.Id);
            SavePins();
            RefreshPins();

            StatusMessage = row.IsInstalled
                ? Strings.Current.Format("Environment.Pins.UnpinnedInstalled", row.DisplayName)
                : Strings.Current.Format("Environment.Pins.UnpinnedNotInstalled", row.Id);
        }

        public string ContextPinLabel => ContextModule?.IsPinned == true
            ? Strings.Current["Environment.ContextPinLabel.Unpin"]
            : Strings.Current["Environment.ContextPinLabel.Pin"];

        [RelayCommand(CanExecute = nameof(CanToggleContextPin))]
        public void ToggleContextPin()
        {
            if (ContextModule is not { } row)
                return;

            if (pins.Contains(row.Entry.Id))
            {
                pins = pins.Without(row.Entry.Id);
                StatusMessage = Strings.Current.Format("Environment.Pins.UnpinnedInstalled", row.DisplayName);
            }
            else
            {
                pins = pins.With(row.Entry.Id);

                StatusMessage = Strings.Current.Format("Environment.Pins.PinnedAt", row.DisplayName, row.Position);
            }

            SavePins();
            RefreshPins();
            ContextModule = null;
        }

        private bool CanToggleContextPin => ContextModule is not null;

        // The answer to the 169-of-195 problem: freeze a hand-tuned order before ever pressing
        // Auto-Sort. It pins what the list holds now, so what it pins is exactly what is on screen.
        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void PinEverythingInPlace()
        {
            var ids = Modules.Select(row => row.Entry.Id).ToList();
            var added = ids.Count(id => !pins.Contains(id));

            if (added == 0)
            {
                StatusMessage = ids.Count == 0
                    ? Strings.Current["Environment.PinEverything.Empty"]
                    : Strings.Current.Plural("Environment.PinEverything.AlreadyPinned", ids.Count);
                return;
            }

            pins = pins.WithAll(ids);
            SavePins();
            RefreshPins();

            var pinnedCount = Strings.Current.Plural("Environment.PinEverything.PinnedCount", added);
            var alreadyPinnedCount = Strings.Current.Plural("Environment.PinEverything.AlreadyPinnedCount", ids.Count - added);
            var totalPins = Strings.Current.Plural("Environment.PinEverything.TotalPins", pins.Count);

            StatusMessage = Strings.Current.Format("Environment.PinEverything.Summary", pinnedCount, alreadyPinnedCount, totalPins);
        }

        [RelayCommand(CanExecute = nameof(HasPins))]
        public void ClearPins()
        {
            if (pins.Count == 0)
            {
                StatusMessage = Strings.Current["Environment.ClearPins.Empty"];
                return;
            }

            var removed = pins.Count;
            var inactive = pins.InactiveIn([.. Modules.Select(row => row.Entry)]).Count;

            pins = pins.Cleared();
            SavePins();
            RefreshPins();

            StatusMessage = inactive == 0
                ? Strings.Current.Plural("Environment.ClearPins.AllActive", removed)
                : Strings.Current.Plural("Environment.ClearPins.SomeInactive", removed, inactive);
        }

        // Auto-Sort runs the default plan without adopting it. The Sort menu is the manual sort and its
        // keys are the user's, so pressing Auto-Sort must not relabel it or tick its entries: the two
        // controls share a sorter, not a plan.
        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void AutoSort()
        {
            // A failed sort (a declared rule the sorter could not satisfy) or a sort that found nothing
            // to move both leave the load order exactly as it was, so neither should have the visible
            // side effect of moving dividers: ApplySort's return says whether an order change actually
            // reached Modules, and the divider policy only runs when it did.
            if (!ApplySort("Auto-Sort", ModuleSortPlan.Default))
                return;

            var dividerOptions = dividerOptionsStore.Load();

            // A copy, not the live collection: keeping the sections anchored hands the same list
            // straight back, so clearing Dividers below would empty what is about to be read from
            // and leave every section gone.
            var reordered = LoadOrderDividerAutoSortPolicy.Apply([.. Dividers], dividerOptions);

            Dividers.Clear();

            foreach (var divider in reordered)
                Dividers.Add(divider);

            SaveDividersPublic();
            RefreshVisibleModules();
        }

        // A sort satisfies every declared load-before, load-after and dependency and then orders what is
        // left by the chosen keys, so it writes the real load order and can never write a broken one.
        // Returns whether the load order actually changed, so a caller like AutoSort can gate a further
        // step (the divider policy) on a real reorder having happened rather than a failure or a no-op.
        private bool ApplySort(string undoDescription, ModuleSortPlan? plan = null)
        {
            var sortedBy = plan ?? sortPlan;
            var before = BuildEnvironment();
            var result = LoadOrderSorter.Sort(before, sortedBy, pins, loadOrderChoices, orderRules);

            if (result.Failed)
            {
                var broken = string.Join(", ", result.Violations.Take(3).Select(v => v.Describe()));

                LoggingService.Log(
                    $"Sort by {sortedBy.Describe()} produced an order breaking {result.Violations.Count} constraint(s): {broken}",
                    LogLevel.Error);

                StatusMessage = Strings.Current.Plural("Environment.Sort.Failed", result.Violations.Count,
                    sortedBy.Describe(), result.Violations[0].Describe());
                return false;
            }

            var moved = before.Entries.Where((entry, i) => entry.Id != result.Entries[i].Id).Count();

            if (moved > 0)
                RecordUndo(before, undoDescription);

            Apply(environment.WithEntries(result.Entries));
            PersistOrder();

            var outcome = moved > 0
                ? Strings.Current.Plural("Environment.Sort.MovedOutcome", before.Entries.Count, sortedBy.Describe(), moved,
                    DescribeMoveCauses(result), DescribeBiggestMove(result))
                : Strings.Current.Format("Environment.Sort.NoMoveOutcome", sortedBy.Describe());

            if (result.Cycles.Count > 0)
                outcome = Strings.Current.Plural("Environment.Sort.CyclesNote", result.Cycles.Count, outcome);

            StatusMessage = DescribeOverriddenPins(result.UnhonoredPins) is { Length: > 0 } overridden
                ? Strings.Current.Format("Environment.Sort.WithOverridden", outcome, overridden)
                : outcome;

            if (result.UnkeptChoices.Count > 0)
                StatusMessage = $"{StatusMessage} {string.Join(" ", result.UnkeptChoices.Select(unkept => unkept.Describe()))}";

            if (result.UnkeptRules.Count > 0)
                StatusMessage = $"{StatusMessage} {string.Join(" ", result.UnkeptRules.Select(unkept => unkept.Describe()))}";

            // No order answers a pair the player marked incompatible, so the sort says so instead.
            if (orderRules.OnTogether(result.Entries) is { Count: > 0 } together)
                StatusMessage = $"{StatusMessage} {string.Join(" ", together.Select(pair => pair.DescribeOnTogether()))}";

            ShowSortExplanation(result);

            return moved > 0;
        }

        // The scale of a sort is the thing the user most needs before they start scrolling: on a
        // real install the default sort moves 169 of 195 modules, and the old line said only how
        // many. The split says which of the two causes did it, and neither is claimed of the other.
        private static string DescribeMoveCauses(SortResult result)
        {
            var causes = result.Reasons
                .Where(reason => reason.Moved)
                .GroupBy(reason => reason.Kind)
                .OrderByDescending(group => group.Count())
                .Select(group => DescribeCause(group.Key, group.Count()))
                .ToList();

            return causes.Count == 0 ? string.Empty : Strings.Current.Format("Environment.SortCauses.Summary", string.Join(", ", causes));
        }

        private static string DescribeCause(SortReasonKind kind, int count) => kind switch
        {
            SortReasonKind.Constraint => Strings.Current.Plural("Environment.SortCause.Constraint", count),
            SortReasonKind.Pinned => Strings.Current.Plural("Environment.SortCause.Pinned", count),
            SortReasonKind.PlayerChoice => Strings.Current.Plural("Environment.SortCause.PlayerChoice", count),
            SortReasonKind.Unopposed => Strings.Current.Plural("Environment.SortCause.Unopposed", count),
            _ => Strings.Current.Plural("Environment.SortCause.Default", count)
        };

        private static string DescribeBiggestMove(SortResult result)
        {
            var biggest = result.Reasons
                .Where(reason => reason.Moved)
                .OrderByDescending(reason => reason.Distance)
                .FirstOrDefault();

            return biggest is null
                ? string.Empty
                : Strings.Current.Format("Environment.Sort.BiggestMove", biggest.Id, biggest.From + 1, biggest.To + 1);
        }

        // The per-module detail, biggest move first, behind an expander so the summary stays one line.
        // A pin the sort could not keep is listed above the moves: it is the one thing here the user did
        // not ask for and would otherwise have to notice on their own.
        private void ShowSortExplanation(SortResult result)
        {
            SortExplanationLines.Clear();

            foreach (var pin in result.UnhonoredPins)
                SortExplanationLines.Add(Strings.Current.Format("Environment.SortExplanation.PinOverridden", pin.Describe()));

            var moves = result.Reasons
                .Where(reason => reason.Moved)
                .OrderByDescending(reason => reason.Distance)
                .ToList();

            foreach (var reason in moves)
                SortExplanationLines.Add(reason.Describe());

            SortExplanationHeader = (moves.Count, result.UnhonoredPins.Count) switch
            {
                (0, 0) => string.Empty,
                (_, 0) => Strings.Current.Plural("Environment.SortExplanation.MovedOnly", moves.Count),
                (0, var pins) => Strings.Current.Plural("Environment.SortExplanation.PinsOnly", pins),
                var (movedCount, pinCount) => Strings.Current.Format("Environment.SortExplanation.MovedAndPins",
                    Strings.Current.Plural("Environment.SortExplanation.MovedOnly", movedCount),
                    Strings.Current.Plural("Environment.SortExplanation.PinsHeaderClause", pinCount))
            };

            HasSortExplanation = SortExplanationLines.Count > 0;
        }

        // Everything below is about the sort that has just run, so anything that replaces the list
        // clears it rather than leaving an explanation of an order that is no longer on screen.
        private void ClearSortExplanation()
        {
            SortExplanationLines.Clear();
            SortExplanationHeader = string.Empty;
            HasSortExplanation = false;
        }

        // Absent rather than inert. With update checking switched off, or with no key stored, there is
        // no chip, no panel and no section in the detail pane: Settings already says what is
        // unavailable and why, and repeating it here would be a dead control taking space.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        [NotifyCanExecuteChangedFor(nameof(FindModIdsCommand))]
        [NotifyPropertyChangedFor(nameof(ShowNexusModuleFacts))]
        public partial bool ShowNexusUpdates { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        [NotifyCanExecuteChangedFor(nameof(FindModIdsCommand))]
        public partial bool IsCheckingUpdates { get; set; }

        [ObservableProperty]
        public partial string UpdateCheckStatus { get; set; } = string.Empty;

        // Everything the status line no longer says out loud. It is a tooltip rather than a deletion:
        // the coverage sentence in particular is the one that stops a short line reading as a clean
        // bill of health, and it is still one hover away.
        [ObservableProperty]
        public partial string UpdateCheckDetail { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string UpdateCheckRanAt { get; set; } = Strings.Current["Environment.UpdateCheckRanAt.Never"];

        private bool CanCheckForUpdates => ShowNexusUpdates && !IsCheckingUpdates;

        private static string NexusDirectory => NexusApiKeyStore.DefaultDirectory;

        private static NexusOptionsStore NexusOptions() =>
            new(Path.Combine(NexusDirectory, NexusOptionsStore.FileName));

        private static NexusApiKeyStore NexusKeys() =>
            new(NexusDirectory, new WindowsSecretProtector());

        private static NexusUpdateCache NexusCache() =>
            new(Path.Combine(NexusDirectory, NexusUpdateCache.FileName));

        // Two small file reads and no request. The transport is not constructed here, so a session
        // that never presses the button never creates an HttpClient.
        private async Task RefreshNexusAvailabilityAsync()
        {
            ShowNexusUpdates = NexusKeys().Status().State == NexusApiKeyState.Available;
            RefreshFileVersions();

            var nexusGroup = nexusFilterGroup;
            var present = nexusGroup.Options.Contains(nexusVerdictFilters[0]);

            if (ShowNexusUpdates)
            {
                if (!present)
                {
                    // In front of the identity options, because a verdict is what the user came to
                    // this menu for and which page a module is on is why they stayed.
                    for (var i = 0; i < nexusVerdictFilters.Count; i++)
                        nexusGroup.Options.Insert(i, nexusVerdictFilters[i]);
                }

                nexusGroup.Refresh();
                await ReadLastNexusAnswerAsync();
                return;
            }

            if (!present)
                return;

            // Untick before removing, or a group goes on narrowing the list by an option that is no
            // longer in it and nothing on screen says why rows are missing.
            foreach (var verdict in nexusVerdictFilters)
            {
                verdict.SetSelected(false);
                nexusGroup.Options.Remove(verdict);
            }

            attentionFilterGroup.Options
                .First(option => option.Kind == ModuleFilter.Updates).SetSelected(false);

            nexusGroup.Refresh();

            nexusUpdates.Clear();
            nexusVerdicts.Clear();
            nexusLinks = [];
            hasNexusAnswer = false;
        }

        // The answer BEM already paid for, dated. Opening a page is not consent to spend a request, so
        // nothing here asks Nexus anything. Working the verdicts out again reads a module's whole
        // folder for every module about to be named out of date, which is seconds of disk I/O on a
        // large load order, so that runs on a worker and only the applying comes back to the UI thread.
        private async Task ReadLastNexusAnswerAsync()
        {
            var options = NexusOptions().Load();

            // The module set is snapshotted here, on the UI thread, so the worker never reads the
            // observable collection while this thread might be mutating it.
            var modules = BuildEnvironment().Entries
                .Select(entry => entry.Manifest)
                .OfType<ModuleManifest>()
                .ToList();

            // Modules that were not here at the previous load are what an install (Library, nxm://, the
            // BUTR stack or a folder dropped in) just added. The first load only records the set, so
            // opening the page never spends a request on modules that were already installed.
            var installedNow = modules.Select(module => module.Id.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var justInstalled = seenModuleIds is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : installedNow.Where(id => !seenModuleIds.Contains(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            seenModuleIds = installedNow;

            var (links, report) = await Task.Run(async () =>
            {
                var computed = NexusLinks(modules);
                var request = new NexusUpdateRequest(computed, null, true, options.Period,
                    options.CacheLifetime, DateTimeOffset.UtcNow);

                if (justInstalled.Count > 0 && NexusKeys().Load() is { } key)
                {
                    try
                    {
                        var scoped = await new NexusUpdateCheck(new NexusClient(new NexusHttpTransport()), NexusCache())
                            .RunForModulesAsync(request with { ApiKey = key }, justInstalled, CancellationToken.None);

                        if (scoped is not null)
                            return (computed, scoped);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogException(ex, "The update check for newly installed modules failed");
                    }
                }

                return (computed, NexusUpdateCheck.FromCache(NexusCache(), request));
            });

            nexusLinks = links;
            RefreshFileVersions();

            if (report is null)
            {
                nexusUpdates.Clear();
                nexusVerdicts.Clear();
                hasNexusAnswer = false;

                // Nothing has been asked, so coverage is the only thing there is to say and it says
                // itself rather than hiding in the tooltip of a line that is not there. "Judged" is a
                // word for an answer BEM holds, and on this path it holds none: this line sits directly
                // under "Nexus has never been asked", so the two contradicted each other on screen.
                UpdateCheckStatus = DescribeUnasked(links);
                UpdateCheckDetail = Strings.Current["Environment.UpdateCheck.PressCheck"];
                UpdateCheckRanAt = Strings.Current["Environment.UpdateCheck.NeverAsked"];
                return;
            }

            ApplyNexusReport(report);
        }

        // Check for Updates always asks Nexus afresh. A cached answer behind a button with that name
        // read as a check that had not happened, and a box to choose between the two was one more
        // thing to decide. What a fresh check spends is said once, first.
        [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
        public async Task CheckForUpdatesAsync()
        {
            if (!await ConfirmNexusRefreshAsync(NexusLinks().Count(link => link.IsCheckable)))
            {
                UpdateCheckStatus = Strings.Current["Environment.Nexus.NothingAsked"];
                return;
            }

            await RunUpdateCheckAsync(force: true);
        }

        // Ticked, Find Mod IDs stops filling gaps and rebuilds every mod id BEM has concluded, from
        // nothing. The file is copied first and Undo Rebuild puts it back.
        [ObservableProperty]
        public partial bool RebuildEveryModId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUndoModIdRebuild))]
        public partial string? LastModIdBackupPath { get; set; }

        public bool CanUndoModIdRebuild => LastModIdBackupPath is { Length: > 0 };

        // Library owns the archive list and the stores the archive steps read. Set by the shell.
        internal InstallViewModel? Library { get; set; }

        // How many modules that should have a Nexus page have one, said in the Updates popup.
        [ObservableProperty]
        public partial string ModIdCoverage { get; set; } = string.Empty;

        // The other question entirely: not "is anything out of date" but "which mod page is this".
        // Check for Updates can only speak for modules BEM holds an id for, so a module with no id is
        // invisible to it until this finds one.
        //
        // One chain, free steps first, each step handed only the modules the earlier steps left
        // without an id (Core\Nexus\ModIdChain.cs). The steps that spend Nexus requests are confirmed
        // once, together, with their cost. Nothing a resemblance suggests is recorded unseen.
        [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
        public async Task FindModIdsAsync()
        {
            if (RebuildEveryModId && !await RebuildEveryModIdAsync())
                return;

            nexusCancellation?.Cancel();
            nexusCancellation?.Dispose();
            nexusCancellation = new CancellationTokenSource();
            var token = nexusCancellation.Token;

            IsCheckingUpdates = true;

            try
            {
                readInstallLogThisSession = false;
                nexusLinks = NexusLinks();

                var targets = ModIdChain.Targets(nexusLinks, RebuildEveryModId);

                if (targets.Count == 0)
                {
                    UpdateCheckStatus = Strings.Current.Plural("Environment.Nexus.AllHavePage", nexusLinks.Count(link => !link.IsExcluded));
                    return;
                }

                var key = NexusKeys().Load();
                var result = await ModIdChain.RunAsync(
                    targets, ModIdSteps(key), StillWithoutId, ConfirmModIdLookupAsync, token);

                RedrawNexusIds();
                NexusIdsChanged.Raise(this);

                var still = result.StillUnresolved.Count;

                UpdateCheckStatus = result.SpendingDeclined
                    ? $"{Strings.Current.Plural("Environment.Nexus.RecordedCount", result.Solved)} {Strings.Current["Environment.Nexus.NothingAsked"]}"
                    : key is null && still > 0
                        ? $"{Strings.Current.Plural("Environment.Nexus.RecordedCount", result.Solved)} {Strings.Current["Environment.Nexus.NoApiKey"]}"
                        : result.Solved == 0
                            ? Strings.Current.Plural("Environment.Nexus.NoneSuggested", still)
                            : Strings.Current.Format("Environment.Nexus.RecordedSummary",
                                Strings.Current.Plural("Environment.Nexus.RecordedCount", result.Solved),
                                Strings.Current.Plural("Environment.Nexus.StillNoPage", still));

                foreach (var step in result.Steps.Where(step => step.Message is { Length: > 0 }))
                    LoggingService.Log($"Find Mod IDs, {step.Kind}: {step.Solved} of {step.Given} solved. {step.Message}");
            }
            catch (OperationCanceledException)
            {
                RedrawNexusIds();
                NexusIdsChanged.Raise(this);
                UpdateCheckStatus = Strings.Current["Environment.Canceled.NothingChanged"];
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to look up Nexus mod ids from the Play tab");
                UpdateCheckStatus = Strings.Current.Format("Environment.Nexus.LookupFailed", ex.Message);
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        // The six steps, in the order ModIdChain runs them whatever order they are listed in. With no
        // API key only the free steps exist, and the status says why the rest did not run.
        private List<ModIdStep> ModIdSteps(NexusApiKey? key)
        {
            var gamePath = GameInstallPath;
            var library = Library;
            var steps = new List<ModIdStep>
            {
                new(ModIdStepKind.OtherManagers, false,
                    (ids, _) => Task.FromResult<string?>(RecordOtherManagersIds(ids))),
            };

            if (library is not null)
            {
                steps.Add(new ModIdStep(ModIdStepKind.ArchiveNames, false,
                    (ids, _) => Step(Strings.Current["Environment.FindModIds.Step.ArchiveNames"], () => library.LinkArchivesByNameAsync(gamePath, ids))));
                steps.Add(new ModIdStep(ModIdStepKind.DeletedArchiveNames, false,
                    (ids, _) => Step(Strings.Current["Environment.FindModIds.Step.DeletedArchiveNames"], () => library.ReadDeletedArchiveNamesAsync(gamePath, ids))));
            }

            if (key is null)
                return steps;

            // BUTR's index costs one read, and naming each page it proposes costs at most one
            // request per module it is asked about.
            steps.Add(new ModIdStep(ModIdStepKind.ButrIndex, true,
                (ids, token) => Step(Strings.Current["Environment.FindModIds.Step.ButrIndex"], () => FindByButrIndexAsync(key, ids, token)),
                ids => 1 + ids.Count));

            if (library is null)
                return steps;

            var limit = InstallViewModel.FileIdentificationLimit;

            steps.Add(new ModIdStep(ModIdStepKind.UnlinkedArchives, true,
                (ids, token) => Step(Strings.Current["Environment.FindModIds.Step.UnlinkedArchives"], () => library.IdentifyUnlinkedArchivesAsync(key, ids, token)),
                ids => Math.Min(library.UnlinkedArchiveCandidates(ids).Count, limit)));
            steps.Add(new ModIdStep(ModIdStepKind.FileHash, true,
                (ids, token) => Step(Strings.Current["Environment.FindModIds.Step.FileHash"], () => library.IdentifyByFileHashAsync(key, ids, token)),
                ids => Math.Min(library.FileHashCandidates(ids).Askable.Count, limit)));
            steps.Add(new ModIdStep(ModIdStepKind.UnrecognizedAgain, true,
                (ids, token) => Step(Strings.Current["Environment.FindModIds.Step.UnrecognizedAgain"], () => library.AskAgainAboutUnrecognizedAsync(key, ids, token)),
                ids => Math.Min(library.FileHashCandidates(ids).Unrecognized.Count, limit)));

            return steps;
        }

        // Says which step is running while it runs.
        private async Task<string?> Step(string working, Func<Task<string?>> run)
        {
            UpdateCheckStatus = working;
            return await run();
        }

        private IReadOnlyList<string> StillWithoutId(IReadOnlyList<string> modules)
        {
            nexusLinks = NexusLinks();

            var withId = nexusLinks
                .Where(link => link.NexusModId is not null)
                .Select(link => link.ModuleId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return [.. modules.Where(module => !withId.Contains(module))];
        }

        // A mod MO2 installed from Nexus carries its page id in its meta.ini, read when the rows were
        // labelled. That is the manager's own record of where it came from, so it is recorded as found.
        private string? RecordOtherManagersIds(IReadOnlyList<string> modules)
        {
            var wanted = modules.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = Modules
                .Where(row => row.ManagerNexusModId is > 0 && wanted.Contains(row.ModuleId))
                .GroupBy(row => row.ModuleId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new LearnedNexusId(group.Key, group.First().ManagerNexusModId!.Value,
                    Strings.Current.Format("Environment.FindModIds.OtherManagerBasis", group.First().Manager.ToString()),
                    DateTimeOffset.UtcNow))
                .ToList();

            if (found.Count == 0)
                return null;

            var record = confirmedNexusIds.Record(found);
            return Strings.Current.Plural("Environment.Nexus.RecordedCount", record.Added + record.Replaced);
        }

        // BUTR's index is a resemblance, so what it proposes goes in front of the owner and only what
        // he ticks is recorded.
        private async Task<string?> FindByButrIndexAsync(NexusApiKey key, IReadOnlyList<string> modules, CancellationToken token)
        {
            var read = await new ButrModuleIndex(new ButrModuleIndexClient(new ButrAuthenticatedHttpTransport()))
                .ReadAsync(new ButrModuleIndexRequest(key.Reveal()), token);

            if (read.Outcome != ButrIndexOutcome.Ok)
                return read.Message;

            var wanted = modules.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = NexusLinks().Where(link => wanted.Contains(link.ModuleId)).ToList();
            var downloaded = Library?.DownloadedModIdsWithNoModule() ?? [];

            var set = NexusIdProposals.From(missing, read.NexusModIdsByModuleId, read.ModuleIdsByNexusModId, downloaded);

            var names = await InstallViewModel.LearnCandidateModNamesAsync(
                key, [.. set.Proposals.Select(proposal => proposal.NexusModId)], token);

            if (names.Count > 0)
                set = NexusIdProposals.From(missing, read.NexusModIdsByModuleId, read.ModuleIdsByNexusModId, downloaded, names);

            var recorded = await ReviewProposedNexusIdsAsync(set.Proposals);

            return $"{read.Message} {Strings.Current.Plural("Environment.Nexus.RecordedCount", recorded)}";
        }

        // Everything BEM concluded for itself, thrown away so it can be worked out again. The user's
        // own hand-set ids go with it, because they said every id and narrowing that on their behalf
        // would leave a rebuild that quietly kept the answers they most wanted to redo. The count of
        // those is named before it happens, and the file is copied first.
        private async Task<bool> RebuildEveryModIdAsync()
        {
            var held = confirmedNexusIds.Load();

            if (held.Count == 0)
            {
                UpdateCheckStatus = Strings.Current["Environment.RebuildModIds.None"];
                return false;
            }

            var byHand = held.Count(entry => entry.SetByOwner);

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.RebuildModIds.Dialog.Title"],
                Content = Strings.Current.Format("Environment.RebuildModIds.Dialog.Body",
                    Strings.Current.Plural("Environment.RebuildModIds.Dialog.Held", held.Count),
                    byHand > 0 ? Strings.Current.Format("Environment.RebuildModIds.Dialog.ByHand", byHand) : string.Empty,
                    Environment.NewLine + Environment.NewLine),
                PrimaryButtonText = Strings.Current["Environment.RebuildModIds.Dialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RebuildModIds.Dialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                UpdateCheckStatus = Strings.Current["Environment.RebuildModIds.Canceled"];
                return false;
            }

            if (confirmedNexusIds.Backup(DateTimeOffset.UtcNow) is not { } backup)
            {
                UpdateCheckStatus = Strings.Current["Environment.RebuildModIds.BackupFailed"];
                return false;
            }

            LastModIdBackupPath = backup;
            confirmedNexusIds.Clear();
            NexusIdsChanged.Raise(this);

            StatusMessage = Strings.Current.Plural("Environment.RebuildModIds.ThrownAway", held.Count, backup);

            return true;
        }

        private static async Task<bool> ConfirmModIdLookupAsync(int requests)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.FindModPages.Dialog.Title"],
                Content = Strings.Current.Plural("Environment.FindModIds.Dialog.Body", requests, Environment.NewLine + Environment.NewLine),
                PrimaryButtonText = Strings.Current["Environment.FindModPages.Dialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.FindModPages.Dialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // Every candidate in front of the user with its reason beside it, and nothing ticked that more
        // than one page could account for. BEM has already reported two of this install's modules
        // against the wrong mod page, and it did that by believing one source without asking.
        private async Task<int> ReviewProposedNexusIdsAsync(IReadOnlyList<NexusIdProposal> proposals)
        {
            // A page already turned down for this module is not offered again. The rejection is about
            // the pair, so BEM stays free to propose a different page for the same module.
            var rows = proposals
                .Where(proposal => !rejectedNexusIds.IsRejected(proposal.ModuleId, proposal.NexusModId))
                .Select(proposal => new NexusIdMatchRow(proposal))
                .ToList();

            if (rows.Count == 0)
                return 0;

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.ReviewModPages.Dialog.Title"],
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = Strings.Current.Plural("Environment.ReviewModPages.Dialog.Body", rows.Count)
                        },
                        new ScrollViewer
                        {
                            MaxHeight = 400,
                            Content = new ItemsControl
                            {
                                ItemsSource = rows,
                                ItemTemplate = (DataTemplate)Application.Current.Resources["NexusIdProposalRow"]
                            }
                        }
                    }
                },
                PrimaryButtonText = Strings.Current["Environment.ReviewModPages.Dialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.ReviewModPages.Dialog.CloseButton"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return 0;

            var ticked = rows.Where(row => row.IsSelected).ToList();
            var turnedDown = rows.Where(row => row.IsRejected).ToList();

            if (turnedDown.Count > 0)
            {
                var rejection = rejectedNexusIds.Record(turnedDown.Select(row =>
                    new RejectedNexusId(row.ModuleId, row.NexusModId, DateTimeOffset.UtcNow)));

                StatusMessage = rejection.Describe();
            }

            if (ticked.Count == 0)
                return 0;

            var record = confirmedNexusIds.Record(ticked.Select(row => new LearnedNexusId(
                row.ModuleId, row.NexusModId, row.Explanation, DateTimeOffset.UtcNow)));

            if (record.Failed > 0)
            {
                StatusMessage = Strings.Current["Environment.ReviewModPages.WriteFailed"];
                return 0;
            }

            if (record.Refused > 0)
                StatusMessage = Strings.Current.Plural("Environment.ReviewModPages.Refused", record.Refused);

            return record.Written;
        }

        [RelayCommand(CanExecute = nameof(CanUndoModIdRebuild))]
        public void UndoModIdRebuild()
        {
            if (LastModIdBackupPath is not { Length: > 0 } backup)
                return;

            if (!confirmedNexusIds.Restore(backup))
            {
                StatusMessage = Strings.Current.Format("Environment.UndoModIdRebuild.Failed", backup);
                return;
            }

            LastModIdBackupPath = null;

            RedrawNexusIds();
            NexusIdsChanged.Raise(this);

            StatusMessage = Strings.Current["Environment.UndoModIdRebuild.Success"];
        }

        private static async Task<bool> ConfirmNexusRefreshAsync(int checkable)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.FetchFresh.Dialog.Title"],
                Content = Strings.Current.Plural("Environment.FetchFresh.Dialog.Body", checkable, Environment.NewLine + Environment.NewLine),
                PrimaryButtonText = Strings.Current["Environment.FetchFresh.Dialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.FetchFresh.Dialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private async Task RunUpdateCheckAsync(bool force)
        {
            nexusCancellation?.Cancel();
            nexusCancellation?.Dispose();
            nexusCancellation = new CancellationTokenSource();

            IsCheckingUpdates = true;

            try
            {
                var options = NexusOptions().Load();

                nexusLinks = NexusLinks();

                var check = new NexusUpdateCheck(new NexusClient(new NexusHttpTransport()), NexusCache());

                ApplyNexusReport(await check.RunAsync(
                    new NexusUpdateRequest(
                        nexusLinks,
                        NexusKeys().Load(),
                        true,
                        options.Period,
                        options.CacheLifetime,
                        DateTimeOffset.UtcNow,
                        ForceRefresh: force),
                    nexusCancellation.Token));
            }
            catch (OperationCanceledException)
            {
                UpdateCheckStatus = Strings.Current["Environment.Canceled.NothingChanged"];
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        [RelayCommand]
        public void CancelUpdateCheck() => nexusCancellation?.Cancel();

        private bool readInstallLogThisSession;

        // The module ids present at the last load, so the next one can tell what an install added.
        private HashSet<string>? seenModuleIds;

        private HashSet<string> workshopModuleIds = new(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<NexusModuleLink> NexusLinks() =>
            NexusLinks([.. BuildEnvironment().Entries.Select(entry => entry.Manifest).OfType<ModuleManifest>()]);

        // The module set is handed in rather than read from BuildEnvironment, so a background call
        // can snapshot it on the UI thread first and never read the observable collection while the
        // UI thread might be mutating it.
        private IReadOnlyList<NexusModuleLink> NexusLinks(IReadOnlyList<ModuleManifest> modules)
        {
            workshopModuleIds = [.. modules.Where(module => module.Source == ModuleSource.Workshop)
                .Select(module => module.Id.Value)];

            RecoverModIdsFromInstallLog(modules);

            // Passed rather than left to default, so the check reads exactly the two files this tab
            // writes instead of two separate instances that happen to point at the same paths.
            return NexusModuleMatching.Link(modules, recorded: ModuleArchiveLinkStore.For(ActiveDataRoot),
                confirmed: confirmedNexusIds, notOnNexus: notOnNexus, builtLocally: builtLocally);
        }

        // The count of modules BEM has no mod id for is reported on this tab, so the one recovery that
        // costs nothing and asks nobody has to be able to run from this tab. It reads BEM's own log of
        // its own installs, and once per session is enough because that log only grows when BEM
        // installs something.
        private void RecoverModIdsFromInstallLog(IReadOnlyList<ModuleManifest> modules)
        {
            if (readInstallLogThisSession || string.IsNullOrWhiteSpace(GameInstallPath))
                return;

            readInstallLogThisSession = true;

            try
            {
                var recovery = InstallLogArchiveLinks.Recover(
                    LoggingService.GetLogDirectory(),
                    ModuleScanner.GetModulesFolder(GameInstallPath),
                    [.. modules.Where(module => !module.IsOfficial).Select(module => module.Id.Value)],
                    ModuleArchiveLinkStore.For(ActiveDataRoot).Load(),
                    DateTimeOffset.UtcNow);

                ModuleArchiveLinkStore.For(ActiveDataRoot).Record(recovery.Recovered);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read mod ids back out of BEM's own install log");
            }
        }

        private void ApplyNexusReport(NexusUpdateReport report)
        {
            nexusUpdates = report.Updates.ToDictionary(update => update.ModuleId, StringComparer.OrdinalIgnoreCase);

            nexusVerdicts = (report.Verdicts ?? [])
                .DistinctBy(verdict => verdict.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(verdict => verdict.ModuleId, StringComparer.OrdinalIgnoreCase);
            nexusVerdicts = ModuleUpdateVerdicts.ApplyHolds(nexusVerdicts, UpdateHolds.Load());

            // Only an answer that actually came back counts. A refusal, an unreachable host or a
            // rejected key leaves the per-module wording at "not checked" rather than at "nothing
            // found", because those say opposite things.
            hasNexusAnswer = report.Status is NexusUpdateStatus.NoNewFiles or NexusUpdateStatus.UpdatesFound;

            // A failure has to keep saying why it failed. Only an answer that came back is reduced to
            // the tally.
            lastNexusReport = report;

            if (!hasNexusAnswer)
            {
                UpdateCheckStatus = report.Message;
                UpdateCheckDetail = DescribeDetail(report);
            }

            UpdateCheckRanAt = report.DataFetchedUtc is { } fetched
                ? report.FromCache
                    ? Strings.Current.Format("Environment.UpdateCheckRanAt.FromCache", fetched.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                    : fetched.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : Strings.Current["Environment.UpdateCheckRanAt.NoAnswer"];

            RefreshUpdatePips();
            RefreshVisibleModules();
            ShowDetails(SelectedModule);
            InvalidateAvailableUpdates();
        }

        // The verdict, on the row it is about. A tally at the top of the tab says how many are behind
        // and never which, so the answer was on a different screen from the thing it is an answer about.
        // Whether BEM holds an id, which is true or false whether or not Nexus has ever been asked
        // anything. Kept apart from the verdicts for that reason: before the first check every row would
        // otherwise claim to have no id, which is the opposite of what BEM knows.
        private void RefreshNexusIdMarks(ApplyReads? reads = null)
        {
            var links = reads?.Links ?? NexusLinks();

            // One line for the Updates popup: of the modules that should have a Nexus page, how many
            // BEM holds an id for.
            var eligible = links.Where(link => !link.IsExcluded).ToList();
            ModIdCoverage = Strings.Current.Format("Environment.ModIdCoverage",
                eligible.Count(link => link.NexusModId is not null), eligible.Count);

            var linked = links
                .Where(link => link.NexusModId is not null)
                .Select(link => link.ModuleId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var noPublishedFile = reads?.NoPublishedFile ?? NexusAbsence.ModulesWithUnpublishedArchive(
                links, ModuleArchiveLinkStore.For(ActiveDataRoot).ByModuleId(),
                UnrecognizedArchiveHashStore.Default.Remembered(DateTimeOffset.UtcNow));

            foreach (var row in Modules)
            {
                row.HasNexusId = linked.Contains(row.ModuleId);
                row.NexusHasNoMatchingFile = noPublishedFile.Contains(row.ModuleId);
            }

            // The Nexus category each linked page is filed under, from what the update check already
            // read and kept. Display only: the tier the sort reads is untouched.
            var categories = reads?.Categories ?? NexusVersionStore.Default.Load().CategoryNamesByModId();
            var pageIds = NexusModPages.IdsByModuleId(links);

            foreach (var row in Modules)
                row.NexusCategory = pageIds.TryGetValue(row.ModuleId, out var pageId)
                    && categories.TryGetValue(pageId, out var category)
                        ? category
                        : null;

            // A newly learned id can turn a module into one with an installable update, so the cached
            // plan has to stand down whenever the id set changes. The same goes for the page ids.
            nexusModIds = null;
            InvalidateAvailableUpdates();
        }

        // Needs no answer from Nexus: it reads only what BEM recorded when it installed the mod.
        private void RefreshFileVersions()
        {
            var archives = ModuleArchiveLinkStore.For(ActiveDataRoot).ArchiveFileNamesByModuleId();

            foreach (var row in Modules)
                row.FileVersion = archives.TryGetValue(row.ModuleId, out var archive)
                    ? NexusArchiveName.VersionDifferingFrom(archive, row.VersionText)
                    : null;
        }

        private void RefreshUpdatePips()
        {
            RefreshNexusIdMarks();

            var conflicted = nexusLinks
                .Where(link => link.SourcesDisagree)
                .Select(link => link.ModuleId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            RefreshFileVersions();

            foreach (var row in Modules)
            {
                // No verdict at all is not the CannotTell verdict. The game's own modules are never
                // asked about, and neither is an orphan with no manifest to link, so treating a missing
                // entry as "cannot tell" marked all nine official modules Unknown: the base game,
                // reported as something BEM had failed to judge.
                var state = ModuleUpdateVerdicts.RowState(nexusVerdicts, row.ModuleId, hasNexusAnswer);

                row.IsOutOfDate = state == ModuleUpdateState.OutOfDate;
                row.IsProbablyOutOfDate = state == ModuleUpdateState.ProbablyOutOfDate;

                // The count at the top of the tab said "5 unknown" and no row said which five, so the
                // only way to find them was to read 232 rows and guess what the word meant. A verdict
                // BEM cannot reach is a fact about that module and belongs on it.
                row.IsUpdateUnknown = state == ModuleUpdateState.CannotTell;
                row.IsUpdateHeld = state == ModuleUpdateState.Held;
                row.HeldReason = row.IsUpdateHeld ? nexusVerdicts[row.ModuleId].Reason : null;
                row.HasIdConflict = conflicted.Contains(row.ModuleId);
            }

            RefreshUpdateSummary();
        }

        // The line at the top of the tab, counted from the rows below it. It is rebuilt here, from the
        // same state the rows were just given, every time that state changes, so it cannot describe a
        // different set of modules than the list does.
        private void RefreshUpdateSummary()
        {
            if (!hasNexusAnswer || lastNexusReport is not { } report)
                return;

            UpdateCheckStatus = DescribeVerdicts(ModuleUpdateVerdicts.TallyRows(
                nexusVerdicts, Modules.Select(row => row.ModuleId), hasNexusAnswer));
            UpdateCheckDetail = DescribeDetail(report);
        }

        // Three counts and nothing else, because this sits high and center on the tab the user works
        // in. Unknown is named on the line rather than left to the tooltip: a line that showed only
        // what is wrong would read as a clean bill of health for a load order most of which was never
        // judged. Everything else moved into the tooltip rather than out of the product.
        private static string DescribeVerdicts(ModuleUpdateTally tally)
        {

            if (tally.Total == 0)
                return Strings.Current["Environment.UpdateSummary.None"];

            var parts = new List<string>();

            if (tally.OutOfDate > 0)
                parts.Add(Strings.Current.Plural("Environment.UpdateSummary.OutOfDate", tally.OutOfDate));

            if (tally.ProbablyOutOfDate > 0)
                parts.Add(Strings.Current.Plural("Environment.UpdateSummary.ProbablyOutOfDate", tally.ProbablyOutOfDate));

            if (tally.CannotTell > 0)
                parts.Add(Strings.Current.Plural("Environment.UpdateSummary.Unknown", tally.CannotTell));

            // A held module is the owner's own choice, so it counts as neither wrong nor up to date.
            if (tally.Held > 0)
                parts.Add(Strings.Current.Plural("Environment.UpdateSummary.Held", tally.Held));

            // Nothing wrong and nothing unknown is the one case where saying how many are current is
            // the whole answer rather than a boast.
            return parts.Count == 0
                ? Strings.Current.Plural("Environment.UpdateSummary.AllUpToDate", tally.UpToDate)
                : $"{string.Join(", ", parts)}.";
        }

        // The rest of what the check knows, one hover away. What the counts mean, not why any of them
        // is large: how much BEM can find out changes as the check improves, and a tooltip explaining
        // today's limits would be wrong by next week.
        private string DescribeDetail(NexusUpdateReport report)
        {
            var parts = new List<string>();

            var tally = ModuleUpdateVerdicts.TallyRows(nexusVerdicts, Modules.Select(row => row.ModuleId), hasNexusAnswer);

            if (tally.UpToDate > 0)
                parts.Add(Strings.Current.Plural("Environment.UpdateSummary.AllUpToDate", tally.UpToDate));

            // The count used to sit here with no way to reach the modules behind it, so the only way to
            // find out which ones they were was to read every row and guess what the word meant. It
            // means a verdict BEM could not reach, and it has never meant a missing mod id.
            if (tally.CannotTell > 0)
                parts.Add(Strings.Current["Environment.UpdateDetail.UnknownExplanation"]);

            if (nexusLinks.Count(link => link.SourcesDisagree) is > 0 and var conflicts)
                parts.Add(Strings.Current.Plural("Environment.UpdateDetail.IdConflict", conflicts));

            // "Nexus recorded a new file for 107" used to sit here, with a clause explaining that it
            // does not mean 107 modules are behind. A number that large, followed by a sentence saying
            // to ignore it, is the framing that made the Updates chip wrong in the first place: a new
            // file on a page says nothing about the copy on disk. The verdict counts already answer
            // the question, so the observation is gone rather than restated with a disclaimer.
            parts.Add(DescribeCoverage(nexusLinks));

            // Straight after the coverage line, because that line says how many modules were not
            // checked and this one says which of those nobody needs to worry about. Settings has had
            // this sentence all along inside report.Message; the tab the user actually works in built
            // its own text and never picked it up, so a Workshop module read here as a gap.
            if (report.DescribeExcluded() is { } excluded)
                parts.Add(excluded);

            // A sweep cut short by the Nexus quota leaves modules unknown for a reason that has nothing
            // to do with the modules. Without this the only sign on this tab is a stubbornly high
            // unknown count, which reads as BEM being useless rather than BEM being unfinished.
            if (report.Sweep?.Describe() is { } coverage)
                parts.Add(coverage);

            return string.Join(" ", parts);
        }

        // How much of the load order the tally above actually speaks for. It used to count what BEM
        // could ask Nexus about, and reported "218 of 233 community modules can be checked" on an
        // install where every one of the other 15 had a definite answer: 13 kept current by Steam and
        // 2 the user had marked as not published on Nexus. Every module carries a verdict now, so the
        // only shortfall worth a sentence is a module with no Nexus mod page found for it, which is the
        // one case nothing here can answer.
        private string DescribeCoverage(IReadOnlyList<NexusModuleLink> links)
        {
            if (links.Count == 0)
                return Strings.Current["Environment.Community.NoneToCheck"];

            var unidentified = links.Count(link => link.NexusModId is null && !link.IsExcluded);

            if (unidentified == 0)
                return Strings.Current.Plural("Environment.Community.AllJudged", links.Count);

            // Nearly all of these do have a mod page; what is missing is BEM's record of which one, so
            // the sentence says that rather than claiming the mods are not on Nexus.
            return Strings.Current.Plural("Environment.Community.PartiallyJudged", links.Count, links.Count - unidentified, unidentified);
        }

        // The same coverage before anything has been asked, in the tense that is true then. Nothing has
        // been judged yet, so this counts what a check would cover instead of what it decided.
        private static string DescribeUnasked(IReadOnlyList<NexusModuleLink> links)
        {
            if (links.Count == 0)
                return Strings.Current["Environment.Community.NoneToCheck"];

            var unidentified = links.Count(link => link.NexusModId is null && !link.IsExcluded);

            return unidentified == 0
                ? Strings.Current.Plural("Environment.Community.AllReady", links.Count)
                : Strings.Current.Plural("Environment.Community.PartiallyReady", links.Count, links.Count - unidentified, unidentified);
        }

        // The row the list has selected, and the only trigger for the detail pane. It is two-way bound,
        // so the Issues panel scrolling to a module fills the pane exactly as clicking the row does.
        [ObservableProperty]
        public partial ModuleRowViewModel? SelectedModule { get; set; }

        public ObservableCollection<ModuleFact> SelectedModuleFacts { get; } = [];

        public ObservableCollection<string> SelectedModuleDeclares { get; } = [];

        public ObservableCollection<string> SelectedModuleNeededBy { get; } = [];

        public ObservableCollection<string> SelectedModuleDiagnoses { get; } = [];

        public ObservableCollection<string> SelectedModuleUndeclared { get; } = [];

        public ObservableCollection<string> SelectedModuleNexus { get; } = [];

        // The same region of the pane for a subscribed module, which is a different section rather than
        // the Nexus one with its words changed: Steam publishes the item, keeps it current and ends it,
        // so nothing Nexus reports is about it and nothing here is framed in Nexus terms.
        public ObservableCollection<string> SelectedModuleWorkshop { get; } = [];

        // Not gated on ShowNexusUpdates the way the Nexus region is. Whether a Nexus key is stored has
        // nothing to do with a Steam subscription, and the page this region links is the only route to
        // Unsubscribe, which is the only way a Workshop module is removed.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNexusModuleFacts))]
        public partial bool ShowWorkshopFacts { get; set; }

        // The Nexus region and the Workshop one are exclusive: a module belongs to one site or the
        // other, and a subscribed item has no Nexus verdict to put beside its subscription.
        public bool ShowNexusModuleFacts => ShowNexusUpdates && !ShowWorkshopFacts;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenSelectedModuleWorkshopPageCommand))]
        public partial bool CanOpenSelectedModuleWorkshopPage { get; set; }

        private string? selectedModuleWorkshopPage;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenSelectedModuleOnNexusCommand))]
        public partial bool CanOpenSelectedModuleOnNexus { get; set; }

        private string? selectedModuleNexusPage;

        [ObservableProperty]
        public partial bool HasSelectedModule { get; set; }

        [ObservableProperty]
        public partial string SelectedModuleTitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SelectedModuleId { get; set; } = string.Empty;

        partial void OnSelectedModuleChanged(ModuleRowViewModel? value) => ShowDetails(value);

        [RelayCommand]
        public void CloseDetails() => SelectedModule = null;

        // Everything BEM already knew about the module and had nowhere to say. Both directions of the
        // dependencies are always shown, including when there are none: an empty section is an answer,
        // and a missing one reads as a question that was never asked.
        private void ShowDetails(ModuleRowViewModel? row)
        {
            SelectedModuleFacts.Clear();
            SelectedModuleDeclares.Clear();
            SelectedModuleNeededBy.Clear();
            SelectedModuleDiagnoses.Clear();
            SelectedModuleUndeclared.Clear();
            SelectedModuleNexus.Clear();
            SelectedModuleWorkshop.Clear();

            if (row is null || ModuleDetails.For(BuildEnvironment(), row.Entry.Id, pins.Contains(row.Entry.Id), ModulePageFor(row)) is not { } details)
            {
                HasSelectedModule = false;
                SelectedModuleTitle = string.Empty;
                SelectedModuleId = string.Empty;
                ShowWorkshopFacts = false;
                CanOpenSelectedModuleWorkshopPage = false;
                selectedModuleWorkshopPage = null;
                return;
            }

            SelectedModuleTitle = details.DisplayName;
            SelectedModuleId = details.Id.Value;

            foreach (var fact in details.Facts)
                SelectedModuleFacts.Add(fact);

            Fill(SelectedModuleDeclares, details.Declares, Strings.Current["Environment.ModuleDetail.NoDeclares"]);
            Fill(SelectedModuleNeededBy, details.NeededBy, Strings.Current["Environment.ModuleDetail.NoNeededBy"]);
            Fill(SelectedModuleDiagnoses, details.Diagnoses, Strings.Current["Environment.ModuleDetail.NoDiagnoses"]);
            ShowUndeclared(details.Id);
            ShowWorkshopSubscription(row);
            ShowNexusFacts(row);

            HasSelectedModule = true;
        }

        // Four different things a reader must be able to tell apart: a new file was recorded, no new
        // file was recorded, this module cannot be asked about at all, and nobody has asked yet.
        // Nothing here says "out of date": both version strings are typed by the mod author and
        // neither is ordered, so the two are put side by side and the reader decides.
        private void ShowNexusFacts(ModuleRowViewModel row)
        {
            SelectedModuleNexus.Clear();
            selectedModuleNexusPage = null;
            CanOpenSelectedModuleOnNexus = false;

            if (!ShowNexusModuleFacts)
                return;

            var link = nexusLinks.FirstOrDefault(candidate =>
                string.Equals(candidate.ModuleId, row.ModuleId, StringComparison.OrdinalIgnoreCase));

            if (link is null)
            {
                SelectedModuleNexus.Add(row.Entry.IsOfficial
                    ? Strings.Current["Environment.ModuleNexus.Official"]
                    : Strings.Current["Environment.ModuleNexus.NotScanned"]);

                return;
            }

            selectedModuleNexusPage = link.ModPageUrl;
            CanOpenSelectedModuleOnNexus = selectedModuleNexusPage is not null;

            if (!link.IsCheckable)
            {
                SelectedModuleNexus.Add(Strings.Current["Environment.ModuleNexus.NoId"]);

                return;
            }

            SelectedModuleNexus.Add(
                Strings.Current.Format("Environment.ModuleNexus.IdSource", link.NexusModId, NexusIdSources.Describe(link.Source)));

            if (NexusIdSources.Caveat(link.Source) is { Length: > 0 } caveat)
                SelectedModuleNexus.Add(caveat);

            SelectedModuleNexus.Add(Strings.Current.Format("Environment.ModuleNexus.InstalledVersion", row.VersionText));

            if (row.FileVersion is { } fileVersion)
                SelectedModuleNexus.Add(Strings.Current.Format("Environment.ModuleNexus.FileVersion", fileVersion, row.VersionText));

            if (nexusUpdates.TryGetValue(row.ModuleId, out var update))
            {
                SelectedModuleNexus.Add(update.NexusVersion is { } version
                    ? Strings.Current.Format("Environment.ModuleNexus.NexusVersion", version)
                    : Strings.Current["Environment.ModuleNexus.NexusVersionNotRead"]);

                SelectedModuleNexus.Add(Strings.Current.Format("Environment.ModuleNexus.NewFileOn",
                    update.LatestFileUpdate.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));

                return;
            }

            SelectedModuleNexus.Add(hasNexusAnswer
                ? Strings.Current["Environment.ModuleNexus.NoNewFile"]
                : Strings.Current["Environment.ModuleNexus.NotCheckedYet"]);
        }

        [RelayCommand(CanExecute = nameof(CanOpenSelectedModuleOnNexus))]
        public void OpenSelectedModuleOnNexus()
        {
            if (selectedModuleNexusPage is not { } page)
                return;

            Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", page));
        }

        // The subscribed module's own region, which stands in place of the Nexus one rather than beside
        // it. Steam is the only site involved, so the facts name Steam, and the link is the item's page
        // in the default browser: it is where Unsubscribe lives, and unsubscribing is the only way a
        // Workshop module is removed, so the region is never the empty answer to a hidden Nexus panel.
        private void ShowWorkshopSubscription(ModuleRowViewModel row)
        {
            SelectedModuleWorkshop.Clear();
            selectedModuleWorkshopPage = null;
            CanOpenSelectedModuleWorkshopPage = false;
            ShowWorkshopFacts = IsWorkshopSubscription(row);

            if (!ShowWorkshopFacts)
                return;

            foreach (var fact in WorkshopModuleFacts.For(row.Entry.Manifest, row.VersionText, GameInstallPath))
                SelectedModuleWorkshop.Add(fact);

            selectedModuleWorkshopPage = WorkshopModuleFacts.Page(row.Entry.Manifest, GameInstallPath);
            CanOpenSelectedModuleWorkshopPage = selectedModuleWorkshopPage is not null;
        }

        [RelayCommand(CanExecute = nameof(CanOpenSelectedModuleWorkshopPage))]
        public void OpenSelectedModuleWorkshopPage()
        {
            if (selectedModuleWorkshopPage is not { } page)
                return;

            Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", page));
        }

        // One reading of "this module is Steam's" for the whole page, asked the way the batch plan asks
        // it: the manifest first, then the library folder by name, so a module reached through a
        // junction is still recognized.
        private bool IsWorkshopSubscription(ModuleRowViewModel? row) =>
            row is not null && !WorkshopContent.NexusSurfaceApplies(row.Entry.Manifest, GameInstallPath);

        // Evidence, never a constraint. What a manifest does not declare is not constrained, so these
        // edges reach the user and nothing else: not the sorter, not the validator, not Auto-Sort.
        private void ShowUndeclared(ModuleId id)
        {
            SelectedModuleUndeclared.Clear();

            if (undeclaredScan is { } scan)
            {
                if (scan.Failed)
                {
                    SelectedModuleUndeclared.Add(
                        Strings.Current.Format("Environment.UndeclaredScan.Failed", scan.Error));
                    return;
                }

                Fill(
                    SelectedModuleUndeclared,
                    [
                        .. UndeclaredDependencies.From(scan, id).Select(edge => edge.Describe()),
                        .. UndeclaredDependencies.To(scan, id).Select(edge => edge.DescribeFromTheOtherSide())
                    ],
                    Strings.Current["Environment.UndeclaredScan.NoneDetected"]);

                return;
            }

            SelectedModuleUndeclared.Add(Strings.Current["Environment.UndeclaredScan.CheckingNow"]);

            _ = ScanUndeclaredAsync();
        }

        // Reading 500 assemblies takes seconds, so it happens once per refresh, off the UI thread, and
        // only when a detail pane has actually asked for it.
        private async Task ScanUndeclaredAsync()
        {
            if (isScanningUndeclared || string.IsNullOrWhiteSpace(GameInstallPath))
                return;

            isScanningUndeclared = true;

            var install = GameInstallPath;
            var entries = BuildEnvironment().Entries;

            try
            {
                undeclaredScan = await Task.Run(
                    () => UndeclaredDependencies.Find(AssemblyIndex.Build(install), entries));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to scan for undeclared dependencies");
                undeclaredScan = new UndeclaredDependencyScan([], ex.Message);
            }
            finally
            {
                isScanningUndeclared = false;
            }

            if (SelectedModule is { } row)
                ShowUndeclared(row.Entry.Id);
        }

        private static void Fill(ObservableCollection<string> target, IReadOnlyList<string> lines, string whenEmpty)
        {
            if (lines.Count == 0)
            {
                target.Add(whenEmpty);
                return;
            }

            foreach (var line in lines)
                target.Add(line);
        }

        public ObservableCollection<string> SortExplanationLines { get; } = [];

        [ObservableProperty]
        public partial string SortExplanationHeader { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasSortExplanation { get; set; }

        // A pin that lost to a constraint is named here rather than left to be discovered by scrolling.
        // Compliance is hard-locked and outranks a pin, so this is the sort working as designed; a pin
        // that vanished without a word would not be.
        private static string DescribeOverriddenPins(IReadOnlyList<UnhonoredPin> overridden)
        {
            if (overridden.Count == 0)
                return string.Empty;

            var first = overridden[0].Describe();

            return Strings.Current.Plural("Environment.OverriddenPins.Summary", overridden.Count, first);
        }

        [RelayCommand]
        public void Validate()
        {
            Apply(BuildEnvironment());

            var found = new List<string>();

            if (ErrorCount > 0)
                found.Add(Strings.Current.Plural("Environment.Validate.ErrorCount", ErrorCount));

            if (WarningCount > 0)
                found.Add(Strings.Current.Plural("Environment.Validate.WarningCount", WarningCount));

            if (InformationCount > 0)
                found.Add(Strings.Current.Plural("Environment.Validate.NoteCount", InformationCount));

            StatusMessage = found.Count == 0
                ? Strings.Current.Plural("Environment.Validate.NoProblems", Modules.Count)
                : Strings.Current.Plural("Environment.Validate.WithProblems", Modules.Count, string.Join(", ", found));
        }

        private enum BulkAction
        {
            Enable,
            Disable,
            Invert
        }

        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void EnableAll() => Bulk(BulkAction.Enable);

        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void DisableAll() => Bulk(BulkAction.Disable);

        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void InvertEnabled() => Bulk(BulkAction.Invert);

        // The search box and the filter chip together decide what the list is showing, so a bulk action
        // that ignored either would touch modules the user cannot see. Scope is exactly the visible
        // rows, and the status line names it. Core refuses official modules whatever is asked here.
        private void Bulk(BulkAction action)
        {
            var scope = VisibleModules.Select(row => row.Entry.Id).ToHashSet();

            bool InScope(ModuleEntry entry) => scope.Contains(entry.Id);

            var before = BuildEnvironment();

            var after = action switch
            {
                BulkAction.Enable => before.WithEnabled(true, InScope),
                BulkAction.Disable => before.WithEnabled(false, InScope),
                _ => before.WithInvertedEnabled(InScope)
            };

            var changed = before.Entries.Where((entry, i) => entry.IsEnabled != after.Entries[i].IsEnabled).Count();
            var scoped = before.Entries.Count(InScope);
            var official = before.Entries.Count(e => InScope(e) && e.IsOfficial && WouldChange(action, e));
            var wouldChange = before.Entries.Count(e => InScope(e) && WouldChange(action, e));

            var orphaned = action == BulkAction.Disable
                ? 0
                : before.Entries.Count(e => InScope(e) && e.IsOrphan && !e.IsEnabled);

            var scopeText = ScopeDescription();

            RecordUndo(before, action switch
            {
                BulkAction.Enable => Strings.Current["Environment.Bulk.EnableAll"],
                BulkAction.Disable => Strings.Current["Environment.Bulk.DisableAll"],
                _ => Strings.Current["Environment.Bulk.InvertEnabled"]
            });

            Apply(after);
            PersistOrder();

            StatusMessage = BulkMessage(action, changed, scoped, official, orphaned, wouldChange, scopeText);
        }

        private static bool WouldChange(BulkAction action, ModuleEntry entry) => action switch
        {
            BulkAction.Enable => !entry.IsEnabled,
            BulkAction.Disable => entry.IsEnabled,
            _ => true
        };

        private string ScopeDescription()
        {
            var query = SearchText?.Trim() ?? string.Empty;
            // Every group that is narrowing, named, because a bulk action's scope is the one sentence
            // the user reads before agreeing to it and "in a filter" would not say which.
            var narrowing = FilterGroups
                .Where(group => group.IsNarrowing)
                .Select(group => string.Join(" or ", group.Selected.Select(option => option.Name)))
                .ToList();

            var chip = narrowing.Count == 0 ? string.Empty : Strings.Current.Format("Environment.ScopeDescription.Filtered", string.Join(" and ", narrowing));
            var search = query.Length == 0 ? string.Empty : Strings.Current.Format("Environment.ScopeDescription.Matching", query);

            return (chip.Length, search.Length) switch
            {
                (0, 0) => string.Empty,
                (0, _) => search,
                (_, 0) => chip,
                _ => Strings.Current.Format("Environment.ScopeDescription.FilteredAndMatching", chip, search)
            };
        }

        private static string BulkMessage(
            BulkAction action, int changed, int scoped, int official, int orphaned, int wouldChange, string scopeText)
        {
            var suffix = scopeText.Length == 0 ? string.Empty : $" {scopeText}";

            var settled = action switch
            {
                BulkAction.Enable => Strings.Current["Environment.Bulk.Settled.Enable"],
                BulkAction.Disable => Strings.Current["Environment.Bulk.Settled.Disable"],
                _ => Strings.Current["Environment.Bulk.Settled.Invert"]
            };

            var verb = action switch
            {
                BulkAction.Enable => Strings.Current["Environment.Bulk.Verb.Enable"],
                BulkAction.Disable => Strings.Current["Environment.Bulk.Verb.Disable"],
                _ => Strings.Current["Environment.Bulk.Verb.Invert"]
            };

            // Saying "already enabled" of a module that was skipped for being official or orphaned would
            // claim the list is in a state it is not, so a run that changed nothing only calls the rest
            // settled once nothing was refused.
            var headline = (changed, scoped) switch
            {
                (0, 0) => Strings.Current.Format("Environment.Bulk.NoneMatched", suffix),
                (0, _) when wouldChange > 0 =>
                    Strings.Current.Plural("Environment.Bulk.NothingChangedWouldHave", wouldChange, suffix),
                (0, _) => Strings.Current.Plural("Environment.Bulk.NothingToChange", scoped, suffix, settled),
                _ => Strings.Current.Plural("Environment.Bulk.Headline", scoped, verb, changed, suffix)
            };

            var notes = new List<string>();

            if (orphaned > 0)
                notes.Add(Strings.Current.Plural("Environment.Bulk.OrphanedNote", orphaned));

            if (official > 0)
                notes.Add(Strings.Current.Plural("Environment.Bulk.OfficialNote", official));

            return notes.Count == 0 ? headline : $"{headline} {string.Join(" ", notes)}";
        }

        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void PruneOrphans()
        {
            var before = BuildEnvironment();
            var pruned = before.WithoutOrphans();
            var removed = before.Entries.Count - pruned.Entries.Count;

            RecordUndo(before, Strings.Current["Environment.UndoDescription.Prune"]);
            Apply(pruned);
            PersistOrder();

            StatusMessage = removed == 0
                ? Strings.Current["Environment.Prune.None"]
                : Strings.Current.Plural("Environment.Prune.Removed", removed);
        }

        [RelayCommand(CanExecute = nameof(CanFixAll))]
        public void FixAll()
        {
            var before = ErrorCount + WarningCount;
            var beforeFix = BuildEnvironment();
            var result = LoadOrderValidator.FixAll(beforeFix);

            RecordUndo(beforeFix, Strings.Current["Environment.UndoDescription.FixAll"]);
            Apply(result.Environment);
            PersistOrder();

            var remaining = ErrorCount + WarningCount;
            var repaired = before - remaining;

            // Enabling a required dependency can surface that module's own problems, so the count
            // can rise; claiming a negative number of fixes would be worse than claiming none.
            var fixedPart = repaired > 0
                ? Strings.Current.Plural("Environment.FixAll.FixedCount", repaired)
                : Strings.Current["Environment.FixAll.FixedNothing"];

            // Stalled and "ran out of passes" are different findings. A stall means the fixes ran and
            // changed nothing at all, which no number of further passes would alter, and blaming a
            // dependency loop for it would name a cause nothing established.
            if (result.Stalled)
            {
                StatusMessage = Strings.Current.Plural("Environment.FixAll.Stalled", remaining, fixedPart, UnresolvedSummary());
                return;
            }

            if (!result.Converged)
            {
                StatusMessage = Strings.Current.Plural("Environment.FixAll.GaveUp", remaining, fixedPart, UnresolvedSummary());
                return;
            }

            StatusMessage = remaining == 0
                ? Strings.Current.Format("Environment.FixAll.NoneRemain", fixedPart)
                : Strings.Current.Plural("Environment.FixAll.SomeRemain", remaining, fixedPart);
        }

        private string UnresolvedSummary()
        {
            var ids = Issues
                .Where(i => i.Issue.IsAutoFixable)
                .Select(i => i.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0)
                return Strings.Current["Environment.FixAll.UnresolvedFallback"];

            return ids.Count <= 3
                ? string.Join(", ", ids)
                : Strings.Current.Plural("Environment.FixAll.AndMore", ids.Count - 3, string.Join(", ", ids.Take(3)));
        }

        // A Fix that reorders the load order and says nothing has changed the user's work silently. What
        // it did is counted from the two lists rather than assumed from the issue's kind, so a fix that
        // turned out to change nothing says that instead of implying it worked.
        // Called once a Health remedy has moved Mover below Anchor, so the next sort keeps that order.
        public void RememberLoadOrderChoice(ModuleId mover, ModuleId anchor) =>
            SaveLoadOrderChoices(loadOrderChoices.With(mover, anchor));

        public LoadOrderChoices ChosenLoadOrder => loadOrderChoices;

        // The player's own before and after rules. Every sort keeps them, which is the ground between
        // ordering by hand and leaving the whole order to Auto-Sort.
        public IReadOnlyList<LoadOrderRule> RulesNaming(ModuleId id) => orderRules.Naming(id);

        // Null when the rule was added, otherwise the reason it was refused. A rule the current order
        // breaks is applied at once by the smallest move that keeps it: the empty plan leaves every
        // module the rule does not reach exactly where it was, so an order arranged by hand survives.
        public string? AddLoadOrderRule(ModuleId earlier, ModuleId later)
        {
            var entries = BuildEnvironment().Entries;

            if (orderRules.RefusalFor(entries, earlier, later) is { } refusal)
                return LoadOrderRules.Describe(refusal, earlier, later);

            SaveOrderRules(orderRules.With(earlier, later));

            if (orderRules.BrokenBy(entries).Count == 0)
                StatusMessage = Strings.Current["Environment.OrderRules.AlreadyKept"];
            else
                ApplySort(Strings.Current["Environment.UndoDescription.OrderRule"], ModuleSortPlan.Empty);

            return null;
        }

        public void RemoveLoadOrderRule(LoadOrderRule rule) => SaveOrderRules(orderRules.Without(rule));

        public IReadOnlyList<IncompatiblePair> IncompatibleNaming(ModuleId id) => orderRules.IncompatibleNaming(id);

        // Null when the pair was saved, otherwise why not. Saving turns nothing off: which of the two
        // goes is the player's call, so the status names the pair when both are on right now.
        public string? AddIncompatible(ModuleId one, ModuleId other)
        {
            if (one == other)
                return LoadOrderRules.Describe(LoadOrderRuleRefusal.SameModule, one, other);

            SaveOrderRules(orderRules.WithIncompatible(one, other));

            var pair = IncompatiblePair.Of(one, other);

            StatusMessage = orderRules.OnTogether(BuildEnvironment().Entries).Contains(pair)
                ? pair.DescribeOnTogether()
                : Strings.Current["Environment.OrderRules.IncompatibleSaved"];

            return null;
        }

        public void RemoveIncompatible(IncompatiblePair pair) => SaveOrderRules(orderRules.WithoutIncompatible(pair));

        private void SaveOrderRules(LoadOrderRules rules)
        {
            orderRules = rules;

            try
            {
                orderRuleStore.Write(rules);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the load order rules");
            }
        }

        private void SaveLoadOrderChoices(LoadOrderChoices choices)
        {
            if (choices.Items.SequenceEqual(loadOrderChoices.Items))
                return;

            loadOrderChoices = choices;

            try
            {
                loadOrderChoiceStore.Write(choices);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the load order choices");
            }
        }

        public void ApplyFix(LoadOrderIssue issue)
        {
            ArgumentNullException.ThrowIfNull(issue);

            if (issue.Fix is null)
                return;

            var before = BuildEnvironment();
            var after = issue.Fix(before);
            var outcome = DescribeFixOutcome(before, after);

            RecordUndo(before, Strings.Current["Environment.UndoDescription.ThatFix"]);
            Apply(after);
            PersistOrder();

            StatusMessage = outcome;
        }

        private static string DescribeFixOutcome(ModuleEnvironment before, ModuleEnvironment after)
        {
            var removed = before.Entries.Count - after.Entries.Count;

            if (removed > 0)
                return Strings.Current.Plural("Environment.FixOutcome.Removed", removed);

            var wasAt = new Dictionary<ModuleId, int>();
            var enabledWas = new Dictionary<ModuleId, bool>();

            for (var i = 0; i < before.Entries.Count; i++)
            {
                wasAt[before.Entries[i].Id] = i;
                enabledWas[before.Entries[i].Id] = before.Entries[i].IsEnabled;
            }

            var moved = 0;
            var toggled = 0;

            for (var i = 0; i < after.Entries.Count; i++)
            {
                var entry = after.Entries[i];

                if (wasAt.TryGetValue(entry.Id, out var was) && was != i)
                    moved++;

                if (enabledWas.TryGetValue(entry.Id, out var enabled) && enabled != entry.IsEnabled)
                    toggled++;
            }

            var parts = new List<string>();

            if (moved > 0)
                parts.Add(Strings.Current.Plural("Environment.FixOutcome.Moved", after.Entries.Count, moved));

            if (toggled > 0)
                parts.Add(Strings.Current.Plural("Environment.FixOutcome.Toggled", toggled));

            return parts.Count == 0
                ? Strings.Current["Environment.FixOutcome.NoChange"]
                : Strings.Current.Format("Environment.FixOutcome.Fixed", string.Join(" and ", parts));
        }

        // Nothing to save, so nothing to press. What remains is the one case a write can be held: a
        // launcher is running and would overwrite the file when it closes. Everything that launches or
        // leaves the app calls this to flush that, and it reports whether the file is now current.
        internal bool TryFlush()
        {
            PersistOrder();

            return !HasHeldChanges;
        }


        private readonly SingleFlight launchFlight = new();

        // Concurrent presses are allowed so the button never sits dead with no reason: a press while a
        // launch is still running says so instead of doing nothing.
        [RelayCommand(CanExecute = nameof(CanLaunch), AllowConcurrentExecutions = true)]
        public async Task LaunchAsync()
        {
            if (!await launchFlight.TryRunAsync(LaunchOnceAsync))
                StatusMessage = Strings.Current["Environment.Launch.StillRunning"];
        }

        private async Task LaunchOnceAsync()
        {
            // Each stage is logged: a press that ends with nothing on screen has to say where it stopped.
            LoggingService.Log($"Launch pressed (rescan window open: {rescanGate.Active}).");

            // A press during the rescan that follows a load-order write queues behind it.
            // Capped, so a window that never closes (a change whose rescan never came) cannot leave the press dead.
            using (var cap = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    await rescanGate.WaitIdleAsync(cap.Token);
                    LoggingService.Log("Launch: no rescan pending.");
                }
                catch (OperationCanceledException)
                {
                    LoggingService.Log("Launch: the rescan window stayed open 30 s; rescanning now and launching after it.");
                    await RefreshCoreAsync(userInitiated: false);
                }
            }

            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Launch.NotLoaded"];
                return;
            }

            // The activation lock belongs to the process; Phase belongs to this page. A dry run, a
            // bisection or a teardown running past its budget holds the lock while this button reads
            // Idle, and the press used to reach the lock and come back saying another BEM window was
            // busy, which was false and left the user nothing to do.
            if (instanceManager.ActivationHeldHere)
            {
                StatusMessage = Strings.Current["Environment.Launch.AlreadyHeld"];
                return;
            }

            if (!TryFlush())
            {
                LoggingService.Log("Launch stopped: the load order could not be written first.");
                return;
            }

            var report = (await RunPreflightAsync()) with { IgnoredOnce = new HashSet<string>(ignoredOnce) };

            ShowPreflight(report);

            // Never a gate. With no dialog wired up, which is every host but the app itself, a preflight
            // that found something is shown in the panel and the launch goes ahead: the user decides,
            // and a launcher that refuses to launch is worse than a crash. Gated on Prominent rather
            // than IsClear so an accepted risk stops interrupting the launch it was accepted for, while
            // Header and Summary still say plainly that it is there.
            LoggingService.Log($"Launch: preflight found {report.Prominent.Count} prominent finding(s).");

            if (report.Prominent.Count > 0 && AskAboutPreflight is { } ask)
            {
                var choice = await ask(report);
                LoggingService.Log($"Launch: preflight answered {choice}.");

                if (choice == PreflightChoice.Cancel)
                {
                    StatusMessage = Strings.Current.Format("Environment.Launch.Canceled", report.Summary);
                    return;
                }

                if (choice == PreflightChoice.FixAndLaunch)
                {
                    ApplyPreflightFixes(report);

                    if (!TryFlush())
                        return;

                    // A remedy rewrote manifests on disk, and the rows still hold the ones read before it.
                    if (report.Fixable.Any(finding => finding.Remedy is not null))
                        await Refresh();

                    ShowPreflight(await RunPreflightAsync());
                }
            }

            var plan = PlanLaunch(EnabledForLaunch());

            if (plan.Target is not { } target)
            {
                StatusMessage = Strings.Current["Environment.Launch.NoTarget"];
                return;
            }

            ignoredOnce.Clear();
            LoggingService.Log($"Launch: starting {target.DisplayName}.");

            // Unblock every module's bin files, set aside mod shader folders, set aside the other
            // platform's binaries and copy across a module that ships only those. The tray watcher runs
            // the very same steps for a game started anywhere else. When it changed anything, one note
            // says so in the launch line.
            var prepared = await PreLaunchRunner.RunAsync(GameInstallPath);

            try
            {
                Phase = LaunchPhase.Starting;
                LastRunEnd = LaunchWaitOutcome.RunEnded;
                LastRunCrashEvidence = null;

                var capturing = StartCapturingArtifacts(
                    DryRunPaths.NewRunId(), Strings.Current.Format("Environment.Launch.CaptureLabel", target.DisplayName)) is not null;

                // Both effects of a successful press hang on LaunchSequence rather than on the statement
                // after the await: that await spans the whole play session on a version that is not the
                // resting one, so the window used to minimize and the success line used to appear when
                // the player came back rather than when the game started.
                var sequence = new LaunchSequence(message => StatusMessage = message, MinimizeIfAsked);

                var started = LaunchSequence.StartedMessage(
                    target.DisplayName,
                    DescribeCrashHandling(target),
                    capturing,
                    // Only carried into the status line when the user launched past something. A clear
                    // preflight already says so in the disclosure below and does not need a second line.
                    report.IsClear ? string.Empty : report.Summary,
                    string.Join(" ", new[] { plan.Note, prepared.Note }.Where(note => !string.IsNullOrEmpty(note))));

                var activation = await LaunchThroughActiveInstanceAsync(target, sequence, started);

                // Junctions still standing is the one outcome that changes which version's saves the
                // game would use next, so it is the whole status line rather than a clause on the end
                // of one.
                sequence.RunFinished(activation is { JunctionsReleased: false }
                    ? activation.Message
                    : LaunchSequence.FinishedMessage(activation is { JunctionsUsed: true }, LastRunEnd, LastRunCrashEvidence));

                ReportAnyGameSettingsNotice();

                if (activation is { } result)
                    await AwaitTheTeardownAsync(result);
            }
            // InvalidOperationException here is InstanceManager.LaunchAsync's refusal (an isolation
            // hazard, or another version's junctions already standing): the message already says why
            // and what to do, and it is not a permissions problem, so "run as administrator" would be
            // wrong advice.
            catch (InvalidOperationException ex)
            {
                LoggingService.LogException(ex, "Launch was refused to protect version isolation");
                StatusMessage = ex.Message;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                LoggingService.LogException(ex, "Failed to launch the game");
                StatusMessage = Strings.Current.Format("Environment.Launch.Failed", target.DisplayName, ex.Message)
                    + " " + Strings.Current["Environment.Launch.FailedAdvice"];
            }
            finally
            {
                // The button comes back whatever happened. Every path into here now completes, and this
                // is the one place that guarantees the control does not read as broken if one does not.
                Phase = LaunchPhase.Idle;
            }
        }

        // The teardown outran its budget and still holds the activation lock. Returning here with the
        // button re-enabled is what produced the refusal about another BEM window, so the phase is held
        // until the lock is really free. There is no way out of the wait to offer: the teardown is
        // filesystem work that ends either way, and releasing the lock under it would repoint the
        // canonical folders while they were still being moved, which is the one thing that could bury
        // a campaign.
        // The other half of the activation contract, and the half two of the three entry points
        // dropped: the result carries the teardown that outran its budget, and a caller that returns
        // without awaiting it tells the user the run finished cleanly while the canonical folders are
        // still moving. Every entry point goes through here so all three say the same thing.
        private async Task AwaitTheTeardownAsync(ActivationResult activation)
        {
            if (activation.Releasing is not { } releasing)
                return;

            try
            {
                await WaitForTheFoldersToGoBackAsync(releasing);
            }
            finally
            {
                Phase = LaunchPhase.Idle;
            }
        }

        private async Task WaitForTheFoldersToGoBackAsync(Task releasing)
        {
            Phase = LaunchPhase.Releasing;

            try
            {
                await releasing;

                StatusMessage = Strings.Current["Environment.Launch.ReleaseFinished"];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "The canonical folders could not all be put back after the run");
                StatusMessage = Strings.Current.Format("Environment.Launch.ReleaseFailed", ex.Message);
            }
        }

        // Runs the launch under the active instance's junctions when one is registered, exactly as
        // before when it is not. RecordLaunch keeps its own timing: it is called the moment the
        // process starts, not after this method's await, because the load order snapshot it takes has
        // to describe the run that is starting, not the run that already ended.
        //
        // The junctions can only be held open for as long as BEM can see the run: WaitForExitAsync on
        // the handle GameLauncher.Launch returns. For a launcher-mediated target (Steam, the TaleWorlds
        // launcher, BLSE's launcher) that handle is the launcher, not the game it hands off to, and
        // RecordLaunch already treats those as unwatchable for the same reason (LaunchRecord.CanSeeTheEnd).
        // InstanceManager.LaunchAsync refuses those targets outright when the active instance is not
        // the resting one, rather than junctioning and then tearing down while the game is still
        // starting: that is the isolation break the whole feature exists to prevent, and it would look
        // to the user like their saves had been mixed. The refusal surfaces as an InvalidOperationException,
        // caught by the same handler LaunchAsync already has for one.
        // Which LauncherData.xml this page reads and writes. The resting instance's load order IS the
        // canonical one in Documents, because that is where its data lives between launches; every
        // other instance keeps its own under UserData, which is what a launch junctions into place.
        // Writing one version's list into another version's file is the corruption this prevents.
        private void UseLoadOrderOf(InstalledInstance? active)
        {
            var dataRoot = DataRootOf(active);
            var path = LauncherDataStore.GetDefaultPath(dataRoot);

            if (string.Equals(path, store.FilePath, StringComparison.OrdinalIgnoreCase))
                return;

            // Before the store, because the store keeps the ring it was handed. One shared ring meant
            // Restore could put a backup of one version's load order onto another version's file.
            backupStore = new LoadOrderBackupStore(LoadOrderBackupStore.GetDefaultRoot(dataRoot));

            store = new LauncherDataStore(path, backupStore);

            // BEM's own opinions about a load order move with it. The pins, the sections, the saved
            // profiles, the risks already accepted and what has been launched all describe one version's
            // list, and one machine-wide copy of each is how a risk accepted on the Steam install a
            // fortnight ago turned up on a version that has none of those mods. The two that are held in
            // memory are re-read here as well, or a switch would leave the previous version's sections
            // and pins on screen over the new version's modules.
            profileStore = new LoadOrderProfileStore(LoadOrderProfileStore.GetDefaultRoot(dataRoot));
            dividerStore = new LoadOrderDividerStore(LoadOrderDividerStore.GetDefaultPath(dataRoot));
            pinStore = new ModulePinStore(ModulePinStore.GetDefaultPath(dataRoot));
            loadOrderChoiceStore = new LoadOrderChoiceStore(LoadOrderChoiceStore.GetDefaultPath(dataRoot));
            orderRuleStore = new LoadOrderRuleStore(LoadOrderRuleStore.GetDefaultPath(dataRoot));
            launchHistory = new LaunchHistoryStore(LaunchHistoryStore.GetDefaultPath(dataRoot));
            acceptedRisks = new AcceptedRiskStore(AcceptedRiskStore.DefaultPath(dataRoot));
            setAsideStore = new BundledStackSetAsideStore(BundledStackSetAsideStore.GetDefaultRoot(dataRoot));

            // The launch target, the extra arguments and the crash-handler switches describe one
            // install, so they are re-read here too. LoadLaunchSettings holds loadingLaunchSettings
            // across every assignment, so this reload does not write back the file it just read, and a
            // version with no file of its own lands on the defaults rather than on the previous
            // version's arguments.
            launchSettingsStore = new LaunchSettingsStore(LaunchSettingsStore.GetDefaultPath(dataRoot));
            LoadLaunchSettings();

            Dividers.Clear();

            foreach (var divider in dividerStore.Load())
                Dividers.Add(divider);

            LoadPins();
            RefreshPins();
            RefreshAcceptedRiskRows();
            RefreshSetAsideRows();

            // The Profiles and Backups lists were read from the stores just replaced. Left alone they
            // kept showing the previous version's rows, or the resting install's when the startup read
            // ran before the first Refresh, and a Restore from one of them wrote another version's
            // load order into this one.
            RefreshSafety();
        }

        // The user data of the version the user picked, or null while that version is the resting one,
        // whose data really does sit at the canonical machine paths. Every page that reads game state -
        // saves, logs, crash evidence, MCM settings, the load order - passes this to Core, or it reads
        // whatever version happens to be resting instead of the one on screen.
        //
        // Resolved on every read rather than cached: a page can be opened before Play has ever
        // refreshed, and the version dropdown can change the answer under it, and a stale root here
        // shows another version's saves and logs as if they were this one's.
        public InstanceDataRoot? ActiveDataRoot => DataRootOf(instanceManager.Active());

        // What Refresh() would resolve GameInstallPath to, read directly rather than waiting for a
        // Refresh() to have already run this session. Install reads this so its own write target
        // cannot go stale between an instance switch and the next time Play happens to refresh; null
        // means no instance is active, in which case Install keeps whatever it already has.
        public string? ActiveInstanceGameFolder => instanceManager.Active()?.Record.GameFolder;

        // The data root of the instance that owns a given game folder. A check scans the folders of the
        // install it was pointed at, so it has to read that install's load order rather than the active
        // instance's: the Install Checks passes skip a module that is not enabled, and a load order from
        // another instance leaves every module on disk looking exactly that way.
        public InstanceDataRoot? DataRootForGameFolder(string? gameFolder) =>
            InstanceDataRoot.ForGameFolder(
                instanceManager.List(), gameFolder, instanceManager.RestingInstanceId);

        // Null from the call above means the canonical paths for the resting install and means nothing
        // found for a folder no instance owns, so a caller that wants to fall back asks this first.
        public bool KnowsGameFolder(string? gameFolder) =>
            InstanceDataRoot.Owns(instanceManager.List(), gameFolder);

        private InstanceDataRoot? DataRootOf(InstalledInstance? active) =>
            active is null
            || string.Equals(active.Record.Id, instanceManager.RestingInstanceId, StringComparison.Ordinal)
                ? null
                : InstanceDataRoot.ForInstance(active.Folder);

        // Whether the next launch runs a version that is not the one sitting at the canonical paths.
        // That is the case junctions are created for, and the case a launcher cannot be trusted with.
        private bool IsPlayingAnotherVersion()
        {
            var active = instanceManager.ActiveInstanceId;

            return active is not null
                && !string.Equals(active, instanceManager.RestingInstanceId, StringComparison.Ordinal);
        }

        // Nothing restores the window when the run ends. Minimizing is what the user asked for and
        // restoring is not; a window that raises itself an hour later would land on top of whatever they
        // moved on to.
        private void MinimizeIfAsked()
        {
            if (MinimizeOnLaunch
                && App.AppWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }

        private async Task<ActivationResult?> LaunchThroughActiveInstanceAsync(
            LaunchTarget target, LaunchSequence sequence, string startedMessage)
        {
            var activeId = instanceManager.ActiveInstanceId;

            if (activeId is null)
            {
                LoggingService.Log($"Launch starting: {target.Kind} at '{target.Path}', no managed instance active.");
                RestorePhysicsFrameRate(InstanceDataRoot.ForMachine().Configs);
                // Deliberately detached: with no instance active there are no junctions to hold and
                // nothing to release, so the launch hands off rather than waiting the run out. What
                // is dropped is the watcher that writes down how the run ended, and dropping it took
                // any failure inside it with it, so its fault is logged now.
                DetachedWork.Start(
                    RecordLaunch(target, GameLauncher.Launch(target)),
                    $"Failed to record how the {target.Kind} run of '{target.Path}' ended",
                    LoggingService.LogException);

                sequence.GameStarted(startedMessage, watchingTheRun: false);
                LoggingService.Log("Launch command finished: nothing to wait on.");
                return null;
            }

            LoggingService.Log($"Launch starting: {target.Kind} at '{target.Path}' under instance '{activeId}'.");

            var result = await instanceManager.LaunchAsync(activeId, target.Kind, async ct =>
            {
                // Junctions are up for the whole of this delegate, so this is the window in which an
                // instance's Configs folder is the one the game is about to read. A failure here is
                // reported and never stops the launch: starting with the wrong volume beats not
                // starting at all.
                //
                // Only for a target whose end BEM will see. Apply is half a transaction: it rewrites
                // the live files from the frozen copy, discarding the drift the player has not asked
                // to keep, and records what it wrote so the capture after the run can tell BEM's own
                // values from the player's. A hand-off target starts the game and exits, this
                // delegate returns with it while the game is still coming up, and no later point
                // exists at which the files could be read back, so the other half never runs: the
                // settings changed during that run are discarded by the next launch and the record
                // is left describing a run nobody observed. Applying is therefore wrong here too,
                // and the run keeps the instance's own settings instead. Only the resting instance
                // reaches this, because LaunchAsync refuses a hand-off target for every other one.
                var willSeeTheEnd = LaunchRecord.CanSeeTheEnd(target.Kind);

                if (willSeeTheEnd)
                    ApplySharedGameSettings(activeId);
                else
                    LoggingService.Log(
                        "Hand-off target: the shared game settings hooks are skipped, because nothing would "
                        + "read the files back once this delegate returns.");

                RestorePhysicsFrameRate(
                    instanceManager.List().FirstOrDefault(candidate =>
                        string.Equals(candidate.Record.Id, activeId, StringComparison.Ordinal)) is { } launching
                        ? GameSettingsTarget.For(launching, instanceManager.RestingInstanceId).Configs
                        : InstanceDataRoot.ForMachine().Configs);

                var process = GameLauncher.Launch(target);
                var runEnded = RecordLaunch(target, process);
                var watching = process is not null && willSeeTheEnd;

                // The game is up. This delegate runs on the UI thread, because nothing between the
                // command and here awaits with ConfigureAwait(false), so the minimize and the status
                // line are on the thread that owns the window.
                sequence.GameStarted(startedMessage, watching);

                if (!watching)
                {
                    // Whether a process was started is not knowable until the launch has been tried,
                    // so the one way to apply and still land here is a watchable target that started
                    // no process of its own. No run happened, but the files were rewritten and the
                    // applied record is open, so this closes it rather than leaving the next run's
                    // capture to be mediated against a launch that never occurred.
                    if (willSeeTheEnd)
                        CaptureSharedGameSettings(activeId);

                    LoggingService.Log("Launch handed off to a process BEM cannot watch; not waiting on the run.");
                    return;
                }

                Phase = LaunchPhase.Playing;

                var trackedId = TrackedIdOf(process);

                try
                {
                    LastRunEnd = await LaunchCompletion.WaitForRunAsync(
                        runEnded,
                        () => RunLiveness.IsAlive(RunLiveness.Snapshot(), RunningGame.InstallRootOf(target.Path), trackedId),
                        log: message => LoggingService.Log(message));

                    LoggingService.Log($"The run under instance '{activeId}' ended ({LastRunEnd}).");
                }
                finally
                {
                    // Still inside the delegate, so the junctions have not come down yet and the
                    // files the player just changed are still where this can read them.
                    CaptureSharedGameSettings(activeId);
                    Phase = LaunchPhase.Releasing;
                }
            }, CancellationToken.None);

            LoggingService.Log(
                $"Launch command finished under instance '{activeId}'; junctions released: {result.JunctionsReleased}.");

            return result;
        }

        // Both hooks run inside the launch delegate, where the junctions stand and an instance's
        // Configs folder is the one the game reads. Neither can stop a launch or fail one: settings
        // are a convenience, and refusing to start the game over one would be the worse outcome.
        // Runs after the shared settings are applied so the player's recorded value wins. A failure is
        // logged and never stops the launch.
        private static void RestorePhysicsFrameRate(string configsFolder)
        {
            try
            {
                var result = PhysicsFrameRateGuard.RestoreIfReset(
                    PhysicsFrameRateGuard.EngineConfigPath(configsFolder),
                    PhysicsFrameRateStore.Beside(InstanceSettingsStore.DefaultPath));

                if (result.Outcome is not (PhysicsFrameRateOutcome.NotRecording or PhysicsFrameRateOutcome.Unchanged))
                    LoggingService.Log($"Physics frame rate guard: {result.Outcome} {result.Value} {result.Error}".TrimEnd());
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Restoring the physics frame rate before launch");
            }
        }

        private void ApplySharedGameSettings(string instanceId) =>
            RunSharedGameSettings(instanceId, applying: true);

        private void CaptureSharedGameSettings(string instanceId) =>
            RunSharedGameSettings(instanceId, applying: false);

        private void RunSharedGameSettings(string instanceId, bool applying)
        {
            try
            {
                if (!instanceSettingsStore.Read().ShareGameSettings)
                    return;

                var instance = instanceManager.List()
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Record.Id, instanceId, StringComparison.Ordinal));

                if (instance is null)
                    return;

                var target = GameSettingsTarget.For(instance, instanceManager.RestingInstanceId);
                var result = applying ? gameSettingsSync.Apply(target) : gameSettingsSync.Capture(target);

                LoggingService.Log(
                    $"Shared game settings {(applying ? "applied to" : "captured from")} instance "
                    + $"'{instanceId}': {result.Changed} changed, {result.Added} added, "
                    + $"{result.Failures.Count} failed.");

                if (result.Failures.FirstOrDefault() is not { } failure)
                    return;

                pendingGameSettingsNotice = applying
                    ? Strings.Current.Format(
                        "Core.GameSettings.ApplyFailed",
                        instance.Record.DisplayName,
                        GameSettingsFiles.NameOf(failure.Kind),
                        failure.Error ?? string.Empty)
                    : Strings.Current.Format(
                        "Core.GameSettings.CaptureFailed",
                        GameSettingsFiles.NameOf(failure.Kind),
                        failure.Error ?? string.Empty);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Sharing the base game settings");
                pendingGameSettingsNotice = ex.Message;
            }
        }

        private void ReportAnyGameSettingsNotice()
        {
            if (pendingGameSettingsNotice.Length == 0)
                return;

            StatusMessage = StatusMessage.Length == 0
                ? pendingGameSettingsNotice
                : $"{StatusMessage} {pendingGameSettingsNotice}";

            pendingGameSettingsNotice = string.Empty;
        }

        // A launch started on another page still has to run the version the user picked. The Saves page
        // builds its own command line out of one save's recorded module list and used to start it
        // directly, which on a non-resting version ran the game against whichever version rests at the
        // canonical paths: a different save folder, a different config, and the save that was clicked
        // nowhere in it.
        //
        // Nothing is written to the launch history here. That command line is not this page's load
        // order, and recording it under this list's fingerprint would have a later preflight answer
        // "this exact order has run before" about a run that never happened.
        public async Task LaunchUnderActiveVersionAsync(LaunchTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);

            var activeId = instanceManager.ActiveInstanceId;

            if (activeId is null)
            {
                GameLauncher.Launch(target)?.Dispose();
                return;
            }

            var activation = await instanceManager.LaunchAsync(activeId, target.Kind, async ct =>
            {
                using var process = GameLauncher.Launch(target);

                if (process is null || !LaunchRecord.CanSeeTheEnd(target.Kind))
                    return;

                var trackedId = TrackedIdOf(process);

                await LaunchCompletion.WaitForRunAsync(
                    process.WaitForExitAsync(ct),
                    () => RunLiveness.IsAlive(RunLiveness.Snapshot(), RunningGame.InstallRootOf(target.Path), trackedId),
                    log: message => LoggingService.Log(message));
            }, CancellationToken.None);

            await AwaitTheTeardownAsync(activation);
        }

        // Work that starts the game without being a launch: the dry run on this page and both bisection
        // modes on Diagnostics. They went through DryRunOrchestrator and GameLauncher directly, which on
        // a version that is not the resting one meant no junctions existed and the game read and wrote
        // the resting version's saves, settings, logs and shader cache while the result was reported
        // against the version the user had picked.
        //
        // The scope is the caller's whole operation rather than one game start: a bisection is many
        // starts in a row, and ActivationLock refuses a second activation inside a live one, so
        // activating per step is not merely wasteful but impossible. Nothing is written to the launch
        // history here, for the same reason LaunchUnderActiveVersionAsync writes none: a dry run and a
        // bisection experiment are not runs of this page's load order, and recording them under its
        // fingerprint would have a later preflight answer for runs that never happened.
        public async Task<T> RunUnderActiveVersionAsync<T>(
            Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (instanceManager.ActiveInstanceId is not { } activeId)
                return await body(cancellationToken);

            var produced = default(T)!;

            var activation = await instanceManager.RunUnderInstanceAsync(
                activeId, async ct => produced = await body(ct), cancellationToken);

            await AwaitTheTeardownAsync(activation);

            return produced;
        }

        // What this press of Launch starts. The preflight and the watch are the two things that happen
        // on the way to the game, so the watch is decided here rather than on a launch path of its own:
        // it adds the companion to this one command line and changes nothing that is saved.
        //
        // Every part of it is wrapped. An armed watch that cannot be put in place is reported in the
        // status line and the game still starts, because the watcher may never be the reason a launch
        // does not happen.
        private LaunchPlan PlanLaunch(IReadOnlyList<ModuleEntry> enabled)
        {
            var armed = false;
            var session = string.Empty;
            var couldNotArm = string.Empty;

            try
            {
                if (watch.IsArmed)
                {
                    var payload = CompanionPayload.LocateForInstall(GameInstallPath);

                    // Re-armed on every watched launch rather than trusted: the folder can be deleted
                    // from outside BEM between one launch and the next, and a state file on its own
                    // watches nothing.
                    var result = payload.Found
                        ? watch.Arm(new WatchRequest(
                            GameInstallPath, enabled, payload.Path!, LaunchArguments, PreferredTarget, CrashHandling))
                        : new WatchStartResult(WatchStartStatus.NoPayload, payload.Reason);

                    armed = result.Status is WatchStartStatus.Armed or WatchStartStatus.AlreadyWatching;
                    session = armed ? result.RunId : string.Empty;
                    couldNotArm = armed ? string.Empty : Strings.Current.Format("Environment.Watch.RunNotWatched", result.Message);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to arm the watch for this launch");
                couldNotArm = Strings.Current.Format("Environment.Watch.RunNotWatchedFailed", ex.Message);
            }

            var guardArmed = ArmTheExceptionManager();

            var plan = WatchedLaunch.Plan(new LaunchPlanRequest(
                GameInstallPath,
                enabled,
                PreferredTarget,
                LaunchArguments,
                CrashHandling,
                armed,
                session,
                guardArmed));

            LoggingService.Log(
                $"Launch plan: {plan.Watch} (watch armed: {armed}, exception manager armed: {guardArmed}); "
                + (plan.Target is { } planned
                    ? $"{planned.Kind} '{planned.Path}' with final arguments: {planned.Arguments}"
                    : "no target")
                + (plan.Detail.Length == 0 ? string.Empty : $"; {plan.Detail}"));

            return couldNotArm.Length == 0 ? plan : plan with { Note = couldNotArm };
        }

        // The Bannerlord Exception Manager, on unless its one setting says otherwise. Written for every
        // launch, a watched one included, because the file is also where the Companion learns which exe
        // shows the window. Anything that stops it leaves an ordinary launch and is only logged: nobody
        // asked for this run to carry it, so there is nothing to report in the status line.
        private bool ArmTheExceptionManager()
        {
            try
            {
                if (!new CrashGuardSwitch(CrashGuardSwitch.DefaultStateFilePath).IsOn)
                    return false;

                var payload = CompanionPayload.LocateForInstall(GameInstallPath);

                var result = new ExceptionSession(ExceptionSession.DefaultStateFilePath).Arm(
                    GameInstallPath,
                    payload.Found ? payload.Path! : string.Empty,
                    Environment.ProcessPath ?? string.Empty,
                    DateTime.UtcNow);

                if (!result.Armed)
                    LoggingService.Log($"The Bannerlord Exception Manager is not on this launch: {result.Reason}");

                return result.Armed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LoggingService.LogException(ex, "Failed to arm the Bannerlord Exception Manager for this launch");
                return false;
            }
        }

        // Set by the page, which is the only place a dialog can be shown from. Null everywhere else,
        // and a null hook launches: the preflight informs the decision, it never makes it.
        public Func<PreflightReport, Task<PreflightChoice>>? AskAboutPreflight { get; set; }

        public ObservableCollection<PreflightRowViewModel> PreflightFindings { get; } = [];

        // Every risk on record, independent of the current load order: a mod that was turned off after
        // its risk was accepted still has that acceptance sitting in the store, and this is where the
        // owner finds it to take back.
        public ObservableCollection<AcceptedRiskRowViewModel> AcceptedRiskRows { get; } = [];

        [ObservableProperty]
        public partial bool HasAcceptedRisks { get; set; }

        [ObservableProperty]
        public partial string AcceptedRiskCountText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string PreflightHeader { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasPreflight { get; set; }

        // The whole check, on the way to the game rather than after it. Reading the load order has to
        // happen here, on the thread that owns the module rows; everything after it is file and
        // metadata work and belongs off it. Measured at roughly half a second on a real
        // 242-module install, of which the assembly index is about three quarters.
        private async Task<PreflightReport> RunPreflightAsync()
        {
            var environment = BuildEnvironment();
            var installPath = GameInstallPath;
            var safety = ShellViewModels.Instance.ModSafety.LastResults;
            var enabled = EnabledForLaunch();
            var preferred = PreferredTarget;
            var extraArguments = LaunchArguments;
            var crashHandling = CrashHandling;
            var preferInstalledStack = PreferInstalledButrStack;
            var setAside = setAsideStore;
            var acceptedOnHealth = new AcceptedFindingStore(AcceptedFindingStore.DefaultPath(ActiveDataRoot)).LoadKeys();

            // The game folders of every other version on this computer, so a module built for another
            // line can be named as such; the active one is the build being checked.
            var otherGameFolders = OtherInstanceGameFolders(installPath);
            var xmlRepairBackups = XmlRepairBackupFolder;
            var clearedFiles = new ClearedFileStore(ClearedFileStore.GetDefaultRoot(ActiveDataRoot));

            // When each acceptance was recorded, from both stores, so a finding accepted before a crash
            // it predicts can come back once.
            var acceptedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach (var (key, at) in acceptedRisks.LoadRecordedTimes())
                acceptedAt[key] = at;

            foreach (var (key, at) in new AcceptedFindingStore(AcceptedFindingStore.DefaultPath(ActiveDataRoot)).LoadRecordedTimes())
                acceptedAt.TryAdd(key, at);

            // Read here rather than inside the Task: it is a file read, but IsArmed is cheap and the
            // wording of one finding is all that depends on it.
            var watchArmed = watch.IsArmed;
            var archivesFolder = ShellViewModels.Instance.Install.ArchivesFolderPath;

            return await Task.Run(() =>
            {
                var inspectClock = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // The same call the launch itself makes, so the length measured is the length sent.
                    var target = LaunchTargetResolver.Resolve(
                        installPath, preferred, enabled, extraArguments, crashHandling);

                    var report = LaunchPreflight.Inspect(new PreflightRequest(
                        environment,
                        GameVersionReader.Read(installPath),
                        AssemblyIndex.Build(installPath),
                        GameInstallLocator.GetBinaryFolder(installPath),
                        launchHistory.Read(),
                        safety,
                        target,
                        watchArmed,
                        // Reused, never recomputed. Running every registered stylesheet takes about
                        // thirteen seconds on this install, and no launch is going to wait for that.
                        // Diagnostics stores its result when Mod Overlaps runs; if it has not, the
                        // preflight says so rather than reporting a clean result it never established.
                        XmlTransformCache.For(XmlTransformCache.SignatureFor(
                            environment.Entries.Where(e => e.IsEnabled).Select(e => e.Id))),
                        installPath,
                        acceptedRisks.LoadKeys(),
                        preferInstalledStack ? setAside : null,
                        // Only the binding-failure lines of the newest logs, so a call the game already
                        // failed on in a recent run can be named rather than guessed from metadata.
                        RecentRunErrors.Read(LogLocator.Find(installPath, ActiveDataRoot).Logs, DateTimeOffset.Now),
                        RenderRequestBurstScanner.ReadRecent(LogLocator.Find(installPath, ActiveDataRoot).Logs, DateTimeOffset.Now),
                        acceptedOnHealth,
                        OtherGameBuilds(otherGameFolders),
                        xmlRepairBackups,
                        clearedFiles,
                        acceptedAt,
                        static folder => FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin),
                        RecordedArchivesStillThere(archivesFolder),
                        ShowArchiveInLibrary,
                        OtherArchivesOfTheSameMod(archivesFolder),
                        ParallelTickCorruption.ReadRecent(FirstChancePaths.GetDefaultRoot(), DateTimeOffset.Now),
                        SmartAppControl.Read(DateTimeOffset.Now, AppDataRoot.Folder),
                        ExceptionManagerOn: new CrashGuardSwitch(CrashGuardSwitch.DefaultStateFilePath).IsOn));

                    // A preflight that takes minutes has no other trace to say which part held it.
                    LoggingService.Log($"Launch: preflight inspected in {inspectClock.ElapsedMilliseconds} ms.");

                    // An acceptance recorded under the old (check, headline) key is rewritten under the
                    // finding's scoped key the first time the finding is seen again.
                    acceptedRisks.UpgradeLegacy(report.Findings);

                    return report;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, "The launch preflight could not be completed");

                    // Not an empty report, which would read as a clear one. Nothing was checked, and
                    // that is the opposite statement.
                    return PreflightReport.Empty with
                    {
                        NotChecked =
                        [
                            new PreflightNote(PreflightCheck.LoadOrder, Strings.Current.Format("Environment.Preflight.CouldNotRun", ex.Message))
                        ]
                    };
                }
            });
        }

        // Every other instance's game folder that is on disk, never the one being checked.
        private IReadOnlyList<string> OtherInstanceGameFolders(string installPath)
        {
            try
            {
                var active = string.IsNullOrWhiteSpace(installPath) ? string.Empty : Path.GetFullPath(installPath);

                return [.. instanceManager.List()
                    .Select(instance => instance.Record.GameFolder)
                    .Where(folder => !string.IsNullOrWhiteSpace(folder)
                        && !string.Equals(Path.GetFullPath(folder), active, StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(folder))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LoggingService.LogException(ex, "Could not list the other game versions for the launch check");
                return [];
            }
        }

        // Read off the UI thread with the rest of the preflight. A folder that cannot be read is left
        // out rather than failing the whole check.
        private static IReadOnlyList<GameBuildOnDisk> OtherGameBuilds(IReadOnlyList<string> folders)
        {
            var builds = new List<GameBuildOnDisk>();

            foreach (var folder in folders)
            {
                try
                {
                    builds.Add(new GameBuildOnDisk(GameVersionReader.Read(folder), AssemblyIndex.Build(folder)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, $"Could not read the game build in {folder}");
                }
            }

            return builds;
        }

        // Where an XML repair keeps the original bytes: beside this version's other BEM state.
        private string XmlRepairBackupFolder =>
            Path.Combine(
                Path.GetDirectoryName(AcceptedRiskStore.DefaultPath(ActiveDataRoot)) ?? Path.GetTempPath(),
                "xml-repair-backups");

        private CancellationTokenSource? transformCheck;

        // What every registered stylesheet does to the merged datasets, worked out in the background
        // after the load order changes, so the launch check never depends on Mod Overlaps having been
        // opened. A newer order cancels the run for the older one.
        public void QueueStylesheetCheck()
        {
            transformCheck?.Cancel();
            transformCheck = new CancellationTokenSource();

            var token = transformCheck.Token;
            var entries = BuildEnvironment().Entries;

            _ = Task.Run(() =>
            {
                try
                {
                    XmlTransformCache.Ensure(entries, token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
                {
                    LoggingService.LogException(ex, "The background stylesheet check failed");
                }
            }, token);
        }

        // The last report shown, so Show Hidden can redraw the list without running the check again.
        private PreflightReport? lastPreflight;

        // Off by default: a hidden finding fired on clean sessions and matched no crash in the deep dive,
        // so it is computed, counted and acceptable, and shown only when asked for.
        [ObservableProperty]
        public partial bool ShowHiddenPreflightFindings { get; set; }

        [ObservableProperty]
        public partial int HiddenPreflightCount { get; set; }

        // Findings he chose to ignore still count (accepted risks keep counting), said as
        // "N ignored" beside the list so an empty-looking preflight never reads as a clean one.
        [ObservableProperty]
        public partial int IgnoredPreflightCount { get; set; }

        partial void OnShowHiddenPreflightFindingsChanged(bool value)
        {
            _ = value;

            if (lastPreflight is { } report)
                ShowPreflight(report);
        }

        private static IEnumerable<string> KeysOf(PreflightFinding finding) =>
            finding.IgnoreKey is { } scoped
                ? [PreflightFinding.KeyFor(finding.Check, finding.Headline), scoped]
                : [PreflightFinding.KeyFor(finding.Check, finding.Headline)];

        // An acceptance changed: the report in hand is corrected, so the finding moves, the counts follow
        // and the launch reads the same answer. Re-running the check here would race a launch in progress.
        // Enqueued because the press came from a row the redraw rebuilds.
        private void MarkAccepted(IEnumerable<string> keys, bool accepted)
        {
            if (lastPreflight is not { } report)
                return;

            var changed = keys.ToHashSet(StringComparer.Ordinal);
            var corrected = report with
            {
                Findings = [.. report.Findings.Select(f => KeysOf(f).Any(changed.Contains) ? f with { Accepted = accepted } : f)],
            };

            lastPreflight = corrected;
            App.AppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(lastPreflight, corrected))
                    ShowPreflight(corrected);
            });
        }

        private void ShowPreflight(PreflightReport report)
        {
            lastPreflight = report;
            PreflightFindings.Clear();

            PreflightGrade? previousGrade = null;

            foreach (var finding in report.Findings)
            {
                // An accepted finding lives in the Accepted Risks section, not among the open ones.
                if (finding.Accepted || report.IgnoredOnce.Contains(finding.Key) || (finding.IsHiddenByDefault && !ShowHiddenPreflightFindings))
                    continue;

                var row = new PreflightRowViewModel(finding, ApplyPreflightFix, AcceptPreflightRisk, TurnOffPreflightModule);
                row.SetUndo(UndoPreflightAcceptance);

                if (finding.Grade != previousGrade)
                {
                    row.GroupHeading = finding.Grade switch
                    {
                        PreflightGrade.Critical => Strings.Current["Environment.Preflight.Group.Critical"],
                        PreflightGrade.Warning => Strings.Current["Environment.Preflight.Group.Warning"],
                        _ => string.Empty,
                    };
                    previousGrade = finding.Grade;
                }

                PreflightFindings.Add(row);
            }

            HiddenPreflightCount = report.Findings.Count(f => f.IsHiddenByDefault && !f.Accepted) + (report.NotChecked.Count > 0 ? 1 : 0);
            IgnoredPreflightCount = report.Findings.Count(f => f.Accepted || report.IgnoredOnce.Contains(f.Key));

            // Every check that could not run, as one row among the hidden ones (catalog K32); each check's reasons are its detail.
            if (report.NotChecked.Count > 0 && ShowHiddenPreflightFindings)
            {
                PreflightFindings.Add(PreflightRowViewModel.ForNotCheckedSummary(
                    report.NotCheckedSummary,
                    string.Join(
                        Environment.NewLine,
                        report.NotCheckedByCheck.Select(group => $"{group.Key}: {string.Join(" ", group.Select(note => note.Reason))}"))));
            }

            if (!string.IsNullOrWhiteSpace(report.HistoryLine))
                PreflightFindings.Add(PreflightRowViewModel.ForHistory(report.HistoryLine));

            RefreshAcceptedRiskRows();
            PreflightHeader = report.Header;
            HasPreflight = PreflightFindings.Count > 0;
        }

        // The moment the load order changes, the last verdict is about an order that is no longer on
        // screen, and leaving it up would have BEM vouching for something it never checked.
        private void ClearPreflight()
        {
            lastPreflight = null;
            ignoredOnce.Clear();
            HiddenPreflightCount = 0;
            IgnoredPreflightCount = 0;
            PreflightFindings.Clear();
            PreflightHeader = string.Empty;
            HasPreflight = false;
        }

        private Dictionary<string, string> RecordedArchivesStillThere(string archivesFolder)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(archivesFolder))
                return found;

            foreach (var link in ModuleArchiveLinkStore.For(ActiveDataRoot).Load())
            {
                if (File.Exists(Path.Combine(archivesFolder, link.ArchiveFileName)))
                    found[link.ModuleId] = link.ArchiveFileName;
            }

            return found;
        }

        private Dictionary<string, IReadOnlyList<string>> OtherArchivesOfTheSameMod(string archivesFolder)
        {
            var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(archivesFolder) || !Directory.Exists(archivesFolder))
                return found;

            var files = Directory.EnumerateFiles(archivesFolder).Select(Path.GetFileName).OfType<string>().ToList();

            foreach (var link in ModuleArchiveLinkStore.For(ActiveDataRoot).Load())
            {
                if (link.NexusModId is not { } modId)
                    continue;

                var others = files.Where(f => NexusArchiveName.TryGetModId(f) == modId
                    && !string.Equals(f, link.ArchiveFileName, StringComparison.OrdinalIgnoreCase)).ToList();

                if (others.Count > 0)
                    found[link.ModuleId] = others;
            }

            return found;
        }

        private static void ShowArchiveInLibrary(string archiveFileName)
        {
            ShellViewModels.Instance.Install.ArchiveSearchText = archiveFileName;
            Views.ShellPage.Open(typeof(Views.InstallPage));
        }

        public void ApplyPreflightFix(PreflightFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);

            // An XML repair is its own button with its own undo, never part of Fix and Launch.
            if (finding.Check == PreflightCheck.MalformedXml)
            {
                ApplyXmlRepair(finding);
                return;
            }

            // Only a trip to Library, so none of the set-aside wording or the refresh below applies.
            if (finding.Check == PreflightCheck.InvalidDeclaredAssembly)
            {
                finding.Remedy?.Invoke();
                return;
            }

            if (finding.Remedy is { } remedy)
            {
                RunRemedy(finding, remedy);
                _ = Refresh();
                return;
            }

            if (finding.Fix is null)
                return;

            var before = BuildEnvironment();
            var after = finding.Fix(before);

            RecordUndo(before, Strings.Current["Environment.UndoDescription.ThatPreflightFix"]);
            Apply(after);
            PersistOrder();

            StatusMessage = DescribeFixOutcome(before, after);
        }

        // The Health page's keep-on answer to a duplicated SubModule class: the module stops declaring what an
        // earlier module already declares. Undone from the Set-Aside Bundled Copies list.
        public bool DeclareSubModuleClassesOnce(string moduleId)
        {
            var plan = DuplicateSubModuleClasses.DeclareOncePlan(BuildEnvironment().Entries, new ModuleId(moduleId));

            if (plan is null)
                return false;

            try
            {
                setAsideStore.Apply(plan);
                StatusMessage = Strings.Current.Format("Environment.SetAside.Applied", moduleId, setAsideStore.RootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, $"Could not stop {moduleId} declaring a duplicated class");
                StatusMessage = Strings.Current.Format("Environment.SetAside.Failed", moduleId, ex.Message);
                return false;
            }

            _ = Refresh();
            return true;
        }

        // The last choice beside a keep-on fix: switches the module off through the same undoable
        // path as any preflight fix. Reached only from its own button, never from a batch.
        public void TurnOffPreflightModule(PreflightFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);

            if (finding.TurnOff is null)
                return;

            var before = BuildEnvironment();
            var after = finding.TurnOff(before);

            RecordUndo(before, Strings.Current["Environment.UndoDescription.ThatPreflightFix"]);
            Apply(after);
            PersistOrder();

            StatusMessage = DescribeFixOutcome(before, after);
        }

        // The last XML repair applied from the launch check, so it can be taken back.
        private XmlRepairResult? lastXmlRepair;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UndoXmlRepairCommand))]
        public partial bool CanUndoXmlRepair { get; set; }

        // Proposed afresh from the file as it is now, so the edit applied is the one the file needs
        // today, and the original bytes go to this version's backup folder first.
        private void ApplyXmlRepair(PreflightFinding finding)
        {
            try
            {
                var match = ModuleXmlWellFormedness.Inspect(BuildEnvironment().Entries)
                    .FirstOrDefault(bad => bad.ModuleId == finding.ModuleId && bad.Repair is not null
                        && (finding.Subject is null
                            || finding.Subject == $"{bad.RelativePath.Replace(Path.DirectorySeparatorChar, '/')}:{bad.Line}"));

                if (match?.Repair is not { } repair)
                {
                    StatusMessage = Strings.Current["Environment.XmlRepair.NothingToRepair"];
                    return;
                }

                lastXmlRepair = repair.Apply(XmlRepairBackupFolder);
                CanUndoXmlRepair = true;
                StatusMessage = Strings.Current.Format(
                    "Environment.XmlRepair.Applied", Path.GetFileName(repair.FilePath), lastXmlRepair.BackupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Could not apply the XML repair");
                StatusMessage = Strings.Current.Format("Environment.XmlRepair.Failed", ex.Message);
            }
        }

        // The same repair and undo for a finding Health shows. Returns what happened, for its status line.
        public string ApplyXmlRepair(XmlRepair? repair)
        {
            if (repair is null)
                return Strings.Current["Environment.XmlRepair.NothingToRepair"];

            try
            {
                lastXmlRepair = repair.Apply(XmlRepairBackupFolder);
                CanUndoXmlRepair = true;

                return Strings.Current.Format("Environment.XmlRepair.Applied", Path.GetFileName(repair.FilePath), lastXmlRepair.BackupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Could not apply the XML repair");

                return Strings.Current.Format("Environment.XmlRepair.Failed", ex.Message);
            }
        }

        [RelayCommand(CanExecute = nameof(CanUndoXmlRepair))]
        private void UndoXmlRepair()
        {
            if (lastXmlRepair is not { } result)
                return;

            try
            {
                XmlRepair.Undo(result);
                StatusMessage = Strings.Current.Format("Environment.XmlRepair.Undone", Path.GetFileName(result.FilePath));
                lastXmlRepair = null;
                CanUndoXmlRepair = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Could not undo the XML repair");
                StatusMessage = Strings.Current.Format("Environment.XmlRepair.Failed", ex.Message);
            }
        }

        // The user reading a finding and choosing to launch past it anyway. Recorded by the exact check
        // and headline, so a mod that later trades this problem for a different one is not silently
        // covered by an acceptance that was only ever about the one already read. Re-runs the preflight
        // immediately so the panel and the next launch both see it without a second click.
        public void AcceptPreflightRisk(PreflightFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);

            var record = acceptedRisks.Accept([AcceptedRisk.For(finding, DateTimeOffset.UtcNow)]);

            StatusMessage = record.Describe();

            RefreshAcceptedRiskRows();
            MarkAccepted(KeysOf(finding), accepted: true);

            // Deliberately not re-run here: the panel and the dialog that may still be open both read
            // from the preflight already in hand, and rebuilding it now would race whatever launch flow
            // is in progress. The acceptance takes effect the next time a preflight runs, same as a fix
            // already only takes effect once the order it changed is checked again.
        }

        // Ignore This Time: passes the finding for the next launch and stores nothing. A changed load
        // order or that launch ends it, so the same finding asks again after either.
        public void IgnorePreflightOnce(PreflightFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);

            ignoredOnce.Add(finding.Key);

            if (lastPreflight is { } report)
                ShowPreflight(report with { IgnoredOnce = new HashSet<string>(ignoredOnce) });
        }

        private readonly HashSet<string> ignoredOnce = [];

        // Accept the Risk taken back from the finding's own row: forgotten under its scoped key and the
        // old (check, headline) key, so the next preflight reads it again.
        public void UndoPreflightAcceptance(PreflightFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);

            List<string> keys = [PreflightFinding.KeyFor(finding.Check, finding.Headline)];

            if (finding.IgnoreKey is { } scoped)
                keys.Add(scoped);

            StatusMessage = acceptedRisks.Forget(keys).Describe();
            RefreshAcceptedRiskRows();
            MarkAccepted(keys, accepted: false);
        }

        private void ForgetRisks(IReadOnlyList<AcceptedRisk> risks)
        {
            var keys = risks.SelectMany(r => new[] { r.Key, r.LegacyKey }).Distinct(StringComparer.Ordinal).ToList();

            // A finding accepted on Health shows here too, so taking it back has to reach that store as well.
            var healthStore = new AcceptedFindingStore(AcceptedFindingStore.DefaultPath(ActiveDataRoot));
            var healthKeys = healthStore.LoadKeys();
            var onHealth = (lastPreflight?.Findings ?? [])
                .Where(finding => KeysOf(finding).Any(keys.Contains))
                .SelectMany(finding => healthKeys.Where(key => KeysOf(finding).Contains(key)
                    || finding.AlternateKey == key
                    || finding.AlternateKeys.Contains(key)
                    || finding.AlternatePattern?.IsMatch(key) == true))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (onHealth.Count > 0)
                healthStore.Forget(onHealth);

            StatusMessage = acceptedRisks.Forget(keys).Describe();
            MarkAccepted(keys, accepted: false);
            RefreshAcceptedRiskRows();
            RefreshIssues(BuildEnvironment());
        }

        // Every acceptance on this version's launch check at once, asked first by the page.
        public void UnacceptAllRisks() => ForgetRisks([.. AcceptedRiskRows.Select(r => r.Risk)]);

        // The same for a load order issue accepted on the issue panel; the panel is rebuilt so the row,
        // the counts and the module's flag come back at once.
        public void UndoIssueAcceptance(IssueRowViewModel row)
        {
            ArgumentNullException.ThrowIfNull(row);

            StatusMessage = acceptedRisks.Forget([PreflightFinding.KeyFor(PreflightCheck.LoadOrder, row.Issue.Message)]).Describe();
            RefreshAcceptedRiskRows();
            RefreshIssues(BuildEnvironment());
        }

        // The load order panel's own Accept the Risk: recorded under the launch check's key for the same
        // finding, then the panel is rebuilt so the row, the counts and the module's flag go at once.
        private void AcceptIssueRisk(IssueRowViewModel row)
        {
            var record = acceptedRisks.Accept([new AcceptedRisk(PreflightCheck.LoadOrder, row.Issue.Message, DateTimeOffset.UtcNow)]);

            StatusMessage = record.Describe();

            RefreshAcceptedRiskRows();
            RefreshIssues(BuildEnvironment());
        }

        // The other half: taking an acceptance back, so its finding returns to its list at once.
        private void ForgetAcceptedRisk(AcceptedRiskRowViewModel row) => ForgetRisks([row.Risk]);

        internal void RefreshAcceptedRiskRows()
        {
            AcceptedRiskRows.Clear();

            foreach (var risk in acceptedRisks.Load())
                AcceptedRiskRows.Add(new AcceptedRiskRowViewModel(risk, ForgetAcceptedRisk));

            // Findings the check counts as accepted because Health accepted them have no record in this
            // store, yet they left the open list, so without this they could not be found or undone here.
            var listed = AcceptedRiskRows.SelectMany(row => new[] { row.Risk.Key, row.Risk.LegacyKey }).ToHashSet(StringComparer.Ordinal);

            foreach (var finding in (lastPreflight?.Findings ?? []).Where(finding => finding.Accepted && !KeysOf(finding).Any(listed.Contains)))
                AcceptedRiskRows.Add(new AcceptedRiskRowViewModel(
                    new AcceptedRisk(finding.Check, finding.Headline, DateTimeOffset.UtcNow, finding.IgnoreKey), ForgetAcceptedRisk));

            HasAcceptedRisks = AcceptedRiskRows.Count > 0;
            AcceptedRiskCountText = AcceptedRiskRows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }

        // Only the fixes that put something back where it belongs or turn something back on. A
        // destructive one deletes recorded data and stays a separate, deliberate press, exactly as
        // Fix all already leaves pruning to the Prune button.
        private void ApplyPreflightFixes(PreflightReport report)
        {
            var current = BuildEnvironment();
            var before = current;
            var remedies = new List<string>();

            foreach (var finding in report.Fixable)
            {
                if (finding.Remedy is { } remedy)
                    remedies.Add(RunRemedy(finding, remedy));
                else
                    current = finding.Fix!(current);
            }

            RecordUndo(before, Strings.Current["Environment.UndoDescription.PreflightFixes"]);
            Apply(current);
            PersistOrder();

            StatusMessage = string.Join(" ", [DescribeFixOutcome(before, current), .. remedies]);
        }

        // Undone from the Set-Aside Bundled Copies list rather than from Undo, which only knows load
        // orders and would put the list back while leaving the files where the remedy moved them.
        private string RunRemedy(PreflightFinding finding, Action remedy)
        {
            var name = finding.ModuleId?.Value ?? finding.Headline;

            try
            {
                remedy();
                StatusMessage = Strings.Current.Format("Environment.SetAside.Applied", name, setAsideStore.RootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, $"Could not set aside the bundled copies in {name}");
                StatusMessage = Strings.Current.Format("Environment.SetAside.Failed", name, ex.Message);
            }

            RefreshSetAsideRows();

            return StatusMessage;
        }

        public ObservableCollection<SetAsideRowViewModel> SetAsideRows { get; } = [];

        [ObservableProperty]
        public partial bool HasSetAsideCopies { get; set; }

        private void RefreshSetAsideRows()
        {
            SetAsideRows.Clear();

            try
            {
                foreach (var record in setAsideStore.List())
                    SetAsideRows.Add(new SetAsideRowViewModel(record, PutBackSetAside));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Could not list the set-aside bundled copies");
            }

            HasSetAsideCopies = SetAsideRows.Count > 0;
        }

        private void PutBackSetAside(SetAsideRowViewModel row)
        {
            try
            {
                StatusMessage = setAsideStore.Undo(row.Record) switch
                {
                    UndoOutcome.Restored => Strings.Current.Format("Environment.SetAside.Restored", row.Record.Bundler),
                    UndoOutcome.ModChangedSince => Strings.Current.Format("Environment.SetAside.ModChangedSince", row.Record.Bundler),
                    _ => Strings.Current.Format("Environment.SetAside.BackupMissing", row.Record.Bundler, row.Record.BackupFolder)
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Could not put back the bundled copies of {row.Record.Bundler}");
                StatusMessage = Strings.Current.Format("Environment.SetAside.Failed", row.Record.Bundler, ex.Message);
            }

            RefreshSetAsideRows();
            _ = Refresh();
        }

        // What the run was, written before the game has a chance to end. A launcher target starts a
        // launcher and hands off, so the process BEM holds is not the game and its exit code would be
        // a lie about the run; those are recorded as unknown, with the reason, rather than guessed at.
        // Returns the task that completes when this run ends, already completed when BEM holds no
        // handle on the run or the target hands off to a process it never sees.
        private Task RecordLaunch(LaunchTarget target, System.Diagnostics.Process? process)
        {
            var order = LoadOrderSnapshot.From(BuildEnvironment());

            // What the launch check said of this exact order, kept with the run: a run that plays through a
            // finding refutes it, and a crash that names one proves it (LaunchEvidence).
            var carried = lastPreflight?.Findings ?? [];
            var installPath = GameInstallPath;

            try
            {
                if (GameVersionReader.Read(installPath) is { IsEmpty: false } launchedVersion)
                    launchSettingsStore.RecordLaunchedVersion(launchedVersion.ToString());
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to record the launched game version");
            }

            var history = launchHistory;
            var record = history.Start(new LaunchRecord(
                Guid.NewGuid().ToString("N")[..12],
                DateTime.UtcNow,
                target.Kind,
                target.DisplayName,
                LoadOrderFingerprint.Of(order),
                order,
                FindingsPresent: LaunchEvidence.Present(carried)));

            if (process is null)
            {
                history.CouldNotSeeTheEnd(
                    record.Id,
                    Strings.Current.Format("Environment.LaunchRecord.NoHandle", target.DisplayName));
                return Task.CompletedTask;
            }

            if (!LaunchRecord.CanSeeTheEnd(target.Kind))
            {
                history.CouldNotSeeTheEnd(
                    record.Id,
                    Strings.Current.Format("Environment.LaunchRecord.LauncherOnly", target.DisplayName));
                process.Dispose();
                return Task.CompletedTask;
            }

            return WatchToTheEnd(history, record.Id, process, carried, installPath, record.StartedUtc);
        }

        // The returned task is the one wait on this run, and this method is the only owner of the
        // process: it disposes it when the run ends. A second WaitForExitAsync on the same object from
        // the launch itself was a use-after-dispose race, and a wait that never returned held the
        // junctions and the Launch command for the rest of the session.
        // Said on every page, in the context bar, while the game is running and the engine log shows a
        // burst of item render requests: the player can still close the screen that causes it.
        [ObservableProperty]
        public partial string LiveRunWarning { get; set; } = string.Empty;

        // Accept the risk: the same warning, once accepted, is never raised again.
        partial void OnLiveRunWarningChanged(string value)
        {
            if (AcceptedNotices.IsAccepted("LiveRun", value))
                LiveRunWarning = string.Empty;
        }

        [RelayCommand]
        private void AcceptLiveRunWarning()
        {
            AcceptedNotices.Accept("LiveRun", LiveRunWarning);
            LiveRunWarning = string.Empty;
        }

        private static int? TrackedIdOf(System.Diagnostics.Process? process)
        {
            try
            {
                return process?.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // Polls the running game's own engine log every few seconds and raises the warning the first
        // time the burst threshold is crossed. Ends with the run.
        private void WatchForRenderBurst(System.Diagnostics.Process process)
        {
            int processId;

            try
            {
                processId = process.Id;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // The canonical shared logs folder: during a BEM launch it is junctioned to the instance's.
            var logs = LogLocator.DefaultFolders(GameInstallPath)
                .FirstOrDefault(folder => folder.Source == LogSource.Game && folder.MaxDepth == 0
                    && string.Equals(Path.GetFileName(folder.Path), "logs", StringComparison.OrdinalIgnoreCase));

            if (logs is null)
                return;

            var watch = new LiveRenderBurstWatch(LiveRenderBurstWatch.LogPathFor(logs.Path, processId));
            var gameFolder = GameInstallPath;
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            LiveRunWarning = string.Empty;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Not process.HasExited: the run watcher disposes the Process when the launcher exits,
                    // and the next read threw "No process is associated with this object" while the game
                    // itself was still running. The machine's process list is asked instead, as the
                    // launch wait asks it.
                    while (RunLiveness.Probe(() => RunLiveness.IsAlive(RunLiveness.Snapshot(), gameFolder, processId), null))
                    {
                        if (watch.Poll() is { } burst)
                        {
                            var text = Strings.Current.Format("Environment.LiveRenderBurst", burst.Count, burst.MostRequestedItem);
                            dispatcher?.TryEnqueue(() => LiveRunWarning = text);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, "The live render burst watch stopped");
                }
                finally
                {
                    dispatcher?.TryEnqueue(() => LiveRunWarning = string.Empty);
                }
            });
        }

        // The Bannerlord Exception Manager can switch suspect mods off in the load order while the game
        // runs, from a process of its own. The list on Play is then older than the file, and its next
        // edit would write the old selection back over the change. Reloaded once the game is gone, by
        // the same path and under the same held-change guard as a change in the Modules folder.
        private void ReloadAnOrderChangedDuringTheRun(DateTime startedUtc)
        {
            try
            {
                if (!store.Exists || File.GetLastWriteTimeUtc(store.FilePath) <= startedUtc)
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            LoggingService.Log("The load order changed while the game ran; reloading Play.");
            OnModulesFolderSettled();
        }

        private Task WatchToTheEnd(
            LaunchHistoryStore history, string id, System.Diagnostics.Process process, IReadOnlyList<PreflightFinding> carried, string? installPath,
            DateTime startedUtc)
        {
            WatchForRenderBurst(process);

            var watching = Task.Run(async () =>
            {
                try
                {
                    var processId = process.Id;

                    await process.WaitForExitAsync();

                    // Read the moment the process is gone, before anything else can fail: the exit is the
                    // one fact the record must get, and it was lost whenever a step after it (the reload,
                    // the log scan) threw something the catch below did not name.
                    var exitCode = process.ExitCode;
                    var endedUtc = DateTime.UtcNow;

                    LoggingService.Log($"The run of process {processId} ended with exit code {exitCode}.");

                    var unhandled = default(UnhandledException);
                    IReadOnlyList<string> crashedOn = [];

                    try
                    {
                        ReloadAnOrderChangedDuringTheRun(startedUtc);

                        // The engine's own error log for this process, matched by id. A crash whose dump the
                        // player cancels leaves no crash folder and an exit code that says nothing, so this
                        // line is then the only sign the run did not end normally.
                        unhandled = UnhandledExceptionLog.FindForRun(
                            Path.Combine((ActiveDataRoot ?? InstanceDataRoot.ForMachine()).ProgramData, "logs"), processId);

                        if (unhandled is not null)
                        {
                            LastRunCrashEvidence = LaunchHistoryStore.DescribeEvidence(unhandled);
                            LoggingService.Log(
                                $"The game crashed: {Path.GetFileName(unhandled.LogPath)} records an unhandled exception "
                                + $"{LastRunCrashEvidence}; exit code {exitCode}.");
                        }

                        // Only a run that crashed is asked what it named, and only its own logs answer.
                        if (!string.IsNullOrEmpty(installPath)
                            && (unhandled is not null || LaunchRecord.Classify(exitCode) is LaunchOutcome.Crashed
                            || LaunchEvidence.EndedOnModuleLoadError(ReadRglLogTail(installPath, processId))))
                        {
                            var now = DateTimeOffset.Now;

                            // A mod that throws while modules load leaves no message in any game log; only
                            // the Companion's startup file names the exception, so both are read.
                            var startup = StartupExceptions.FindForRun(AppDataRoot.Folder, processId, startedUtc);

                            if (startup.Count > 0)
                            {
                                var culprit = StartupExceptions.Culprit(startup)!;
                                var where = culprit.ModuleFolder.Length > 0 ? $" in module '{culprit.ModuleFolder}'" : string.Empty;
                                LoggingService.Log($"The Companion recorded {startup.Count} exception(s) before the first tick; the one that ended the start{where}: {culprit.Line}");
                            }

                            crashedOn = LaunchEvidence.NamedBy(
                                string.Join("\n",
                                    RecentRunErrors.Read(
                                        LogLocator.Find(installPath).Logs, now, now.UtcDateTime - startedUtc + TimeSpan.FromSeconds(30)),
                                    StartupExceptions.CrashText(startup)),
                                carried);

                            // A startup death that wrote no exception text: the last module the engine
                            // loaded is where it died.
                            if (crashedOn.Count == 0)
                                crashedOn = LaunchEvidence.NamedByLastModule(ReadRglLogTail(installPath, processId), carried);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Evidence is best effort; the end of the run is recorded regardless.
                        LoggingService.LogException(ex, "Reading the evidence of the run failed; recording its end without it");
                    }

                    var finished = history.Finish(id, exitCode, endedUtc, unhandled, crashedOn);

                    LoggingService.Log(
                        finished
                            ? $"Launch record {id} closed: exit code {exitCode}."
                            : $"Launch record {id} was not found in '{history.FilePath}' when the run ended.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LoggingService.LogException(ex, "Could not record how the run ended");
                    history.CouldNotSeeTheEnd(id, Strings.Current["Environment.LaunchRecord.LostTrack"]);
                }
                finally
                {
                    process.Dispose();
                }
            });

            // Nobody awaits this task. LaunchCompletion.WaitForRunAsync reads IsCompleted and returns
            // the moment it is done without ever awaiting it, so a fault inside would reach the log
            // only if the finalizer happened to run. The fallback write above is to the same file that
            // just refused the first one, so it can throw for the same reason and escape the catch.
            return DetachedWork.Observe(
                watching, "Failed to record how the run ended", LoggingService.LogException);
        }

        // The game's own uploader deletes the whole crash folder five seconds after it finishes
        // sending it, so the only moment a copy can be taken is while the run is still going. This
        // never waits on the run: it watches in the background and stops on its own once the game
        // and the uploader are both gone.
        private CrashArtifactWatcher? StartCapturingArtifacts(
            string runId, string reason, CancellationToken runEnded = default)
        {
            try
            {
                // The selected version's store, matching the sources on the next line. Taken from the
                // machine root, a non-resting version's crash evidence was copied into the resting
                // version's store and listed there as that version's.
                var watcher = new CrashArtifactWatcher(
                    CrashArtifactPaths.GetDefaultRoot(ActiveDataRoot),
                    runId,
                    reason,
                    CrashArtifactPaths.DefaultSources(GameInstallPath, ActiveDataRoot));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await watcher.WatchAsync(runEnded);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogException(ex, "Capturing the crash artifacts failed");
                    }
                });

                return watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Could not start capturing the crash artifacts");
                return null;
            }
        }

        // A dry run is a means, never a destination: one secondary button beside Launch, a progress
        // line and a Cancel. Everything it finds is read on the Diagnostics tab, which is where the
        // crash reports it explains already live.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckLoadOrderBootsCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelDryRunCommand))]
        public partial bool IsDryRunning { get; set; }

        [ObservableProperty]
        public partial string DryRunProgressText { get; set; } = string.Empty;

        private CancellationTokenSource? dryRunCancellation;

        private readonly DryRunOptionsStore dryRunOptionsStore = new(DryRunOptionsStore.DefaultPath);

        // A double because NumberBox binds to one; the store keeps whole minutes.
        public double BootCheckTimeLimitMinutes
        {
            get => dryRunOptionsStore.Load().TimeLimit.TotalMinutes;
            set
            {
                if (double.IsNaN(value))
                    return;

                dryRunOptionsStore.Save(new DryRunOptions((int)Math.Round(value)));
                OnPropertyChanged();
            }
        }

        private bool CanCheckLoadOrderBoots() => IsLoaded && !IsDryRunning;

        private bool CanCancelDryRun() => IsDryRunning;

        [RelayCommand(CanExecute = nameof(CanCancelDryRun))]
        private void CancelDryRun()
        {
            DryRunProgressText = Strings.Current["Environment.DryRun.Stopping"];
            dryRunCancellation?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanCheckLoadOrderBoots))]
        public Task CheckLoadOrderBootsAsync() => RunDryRunAsync();

        // Public so the Diagnostics tab can offer this where the need for it arises: a crash analyzed
        // against a stale or missing patch registry is exactly where refreshing it belongs.
        public async Task<DryRunVerdict?> RunDryRunAsync()
        {
            if (IsDryRunning)
                return null;

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Environment.GameRunning.CloseBeforeDryRun"];
                return null;
            }

            var payload = CompanionPayload.LocateForInstall(GameInstallPath);

            if (!payload.Found)
            {
                StatusMessage = Strings.Current.Format("Environment.DryRun.NotStarted", payload.Reason);
                return null;
            }

            var enabled = BuildEnvironment().Entries.Where(e => e.IsEnabled && !e.IsOrphan).ToList();

            // The same file this page reads and writes, which is the active version's rather than
            // always the machine's: a dry run of one version must not be handed another one's list.
            var launcherDataPath = store.FilePath;

            var request = new DryRunRequest(
                GameInstallPath,
                enabled,
                payload.Path!,
                launcherDataPath,
                Path.GetDirectoryName(launcherDataPath) ?? string.Empty,
                LaunchArguments,
                PreferredTarget,
                CrashHandling);

            var orchestrator = new DryRunOrchestrator(
                DryRunPaths.GetDefaultRoot(),
                DryRunPaths.GetDefaultSnapshotRoot(),
                DryRunProcessLauncher.Create(),
                timeout: new DryRunOptionsStore(DryRunOptionsStore.DefaultPath).Load().TimeLimit);

            using var cancellation = new CancellationTokenSource();

            dryRunCancellation = cancellation;
            IsDryRunning = true;
            DryRunProgressText = Strings.Current["Environment.DryRun.Starting"];
            // The caveat leads, and it is said before the run rather than with the verdict. A dry run
            // whose companion never loaded ends looking exactly like a clean one, so the reason to
            // doubt an empty result has to be on screen while the result is being read.
            StatusMessage = (payload.MatchesInstallPlatform ? string.Empty : payload.Reason + " ")
                + Strings.Current["Environment.DryRun.Going"];

            var progress = new Progress<DryRunProgress>(p => DryRunProgressText = Describe(p));

            // Deliberately not disposed here: the watcher outlives this method by design, and pulling
            // the source out from under it while it is still waiting would be the one way to lose the
            // evidence it exists to keep.
            var runEnded = new CancellationTokenSource();

            var capturing = StartCapturingArtifacts(
                DryRunPaths.NewRunId(), Strings.Current["Environment.DryRun.BootCheckReason"], runEnded.Token);

            DryRunVerdict verdict;

            try
            {
                // Under the selected version's junctions, not the machine's bare paths. A dry run of
                // one version that boots against another version's Configs, shader cache and logs is
                // testing the wrong install and says so about the right one.
                verdict = await RunUnderActiveVersionAsync(
                    ct => orchestrator.RunAsync(request, progress, ct), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                DryRunProgressText = string.Empty;
                StatusMessage = Strings.Current["Environment.DryRun.Canceled"];
                return null;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "The dry run failed");
                DryRunProgressText = string.Empty;
                StatusMessage = Strings.Current.Format("Environment.DryRun.Failed", ex.Message);
                return null;
            }
            finally
            {
                dryRunCancellation = null;
                IsDryRunning = false;

                // The game is gone, so the capture moves to its grace phase: the uploader is still
                // holding the crash folder open and has not deleted it yet.
                await runEnded.CancelAsync();
            }

            if (capturing is not null)
                capturing.Reason = Strings.Current.Format("Environment.DryRun.BootCheckRunLabel", verdict.RunId);

            DryRunProgressText = string.Empty;

            var saved = DryRunRegistryCapture.Save(
                verdict,
                [.. enabled.Select(e => e.Id)],
                new PatchRegistryStore(PatchRegistryStore.GetDefaultRoot(ActiveDataRoot)));

            ShellViewModels.Instance.Diagnostics.ShowDryRun(verdict, saved, enabled);

            StatusMessage = Strings.Current.Format("Environment.DryRun.ResultSummary", verdict.Summary, saved.Message)
                + (payload.MatchesInstallPlatform ? string.Empty : " " + payload.Reason);

            return verdict;
        }

        // A guided bisection needs the game started on a set BEM chose rather than the one on screen.
        // It never touches the list the user is editing: the caller has already snapshotted
        // LauncherData.xml and Configs and puts them back when the experiment ends.
        //
        // This deliberately takes no activation of its own. It returns the moment the game is started
        // and the user then plays for as long as the experiment needs, so a scope taken here would be
        // released while the game was still running. The whole guided search runs inside one scope the
        // caller holds (DiagnosticsViewModel, through RunUnderActiveVersionAsync), which is also why a
        // launcher-mediated target is not refused here: the junctions outlive the hand-off.
        public string LaunchModules(IReadOnlyList<ModuleEntry> enabled)
        {
            ArgumentNullException.ThrowIfNull(enabled);

            if (RunningGame.AnyGameProcessRunning())
                return Strings.Current["Environment.GameRunning.CloseBeforeBisectionRun"];

            // Filtered on IsEnabled as well as IsOrphan, exactly as a normal launch is: this list goes
            // on the command line, where every id present loads. A caller that hands over the whole
            // load order with its flags set would otherwise launch all of it.
            var launching = enabled.Where(e => e.IsEnabled && !e.IsOrphan).ToList();

            var target = LaunchTargetResolver.Resolve(
                GameInstallPath, PreferredTarget, launching, LaunchArguments, CrashHandling);

            if (target is null)
                return Strings.Current["Environment.Launch.NoTarget"];

            try
            {
                // Nothing watches this one and nothing records it: a bisection deliberately launches a
                // set BEM chose, so calling it a run of the user's load order would put a fabricated
                // outcome in the history the preflight reads.
                GameLauncher.Launch(target)?.Dispose();

                MinimizeIfAsked();

                // Both numbers, so an experiment that quietly launches the whole load order reads as
                // one rather than looking like any other run.
                return Strings.Current.Plural("Environment.Bisection.Launched", Modules.Count, target.DisplayName, launching.Count);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to launch a bisection experiment");
                return Strings.Current.Format("Environment.Bisection.LaunchFailed", target.DisplayName, ex.Message);
            }
        }

        private static string Describe(DryRunProgress progress)
        {
            var elapsed = progress.Elapsed.ToString(@"mm\:ss");

            return progress.Total is { } total and > 0
                ? Strings.Current.Plural("Environment.DryRunProgress.WithTotal", total, progress.Message, progress.Completed, elapsed)
                : Strings.Current.Plural("Environment.DryRunProgress.NoTotal", progress.Completed, progress.Message, elapsed);
        }

        // Ticking a crash handling box and then launching a target that ignores it would otherwise look
        // like the setting was honored, so the status line says which way it went.
        private string DescribeCrashHandling(LaunchTarget target)
        {
            if (CrashHandling.ToArguments() is not { Length: > 0 } flags)
                return string.Empty;

            return target.Kind == LaunchTargetKind.BlseStandalone
                ? Strings.Current.Format("Environment.CrashHandling.Flags", flags)
                : Strings.Current.Format("Environment.CrashHandling.NotUsed", target.DisplayName);
        }

        // The row the context menu was opened on. Every menu item reads it, and its CanExecute is what
        // grays an item out, so an action whose target is missing cannot be clicked at all.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ContextToggleLabel))]
        [NotifyPropertyChangedFor(nameof(ContextPinLabel))]
        [NotifyPropertyChangedFor(nameof(ContextHoldLabel))]
        [NotifyPropertyChangedFor(nameof(ContextNotOnNexusLabel))]
        [NotifyPropertyChangedFor(nameof(DeselectContextModuleTooltip))]
        [NotifyCanExecuteChangedFor(nameof(DeselectContextModuleCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleContextNotOnNexusCommand))]
        [NotifyCanExecuteChangedFor(nameof(ForgetContextModuleNexusIdCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleContextModuleCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleContextPinCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveContextModuleUpCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveContextModuleDownCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveContextModuleToTopCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveContextModuleToBottomCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenContextModuleFolderCommand))]
#if DEV_BEM
        [NotifyCanExecuteChangedFor(nameof(OpenContextModuleManifestCommand))]
#endif
        [NotifyCanExecuteChangedFor(nameof(CopyContextModuleIdCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenContextModulePageCommand))]
        [NotifyCanExecuteChangedFor(nameof(PruneContextModuleCommand))]
        [NotifyCanExecuteChangedFor(nameof(UninstallContextModuleCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableWithDependenciesCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableWithDependentsCommand))]
        public partial ModuleRowViewModel? ContextModule { get; set; }

        // The divider row a right-click landed on, mirroring ContextModule above. A divider's own
        // menu (Rename, Collapse/Expand, Remove) reads this rather than ContextModule, since the two
        // row kinds share one list but never share a context menu.
        [ObservableProperty]
        public partial DividerRowViewModel? ContextDivider { get; set; }

        [RelayCommand]
        private void InsertDividerAboveContextModule()
        {
            if (ContextModule is not { } target)
                return;

            InsertDivider(target.Entry.Id);
        }

        [RelayCommand]
        private void InsertDividerBelowContextModule()
        {
            if (ContextModule is not { } target)
                return;

            var index = Modules.IndexOf(target);
            var below = index >= 0 && index + 1 < Modules.Count ? Modules[index + 1].Entry.Id : (ModuleId?)null;

            InsertDivider(below);
        }

        private void InsertDivider(ModuleId? anchor)
        {
            // Left as a literal rather than Strings.Current: this label is user data from here on (it
            // flows straight into DividerRowViewModel.Label, which several LoggingService.Log diagnostics
            // below print verbatim while chasing a reported "menu items do nothing" bug), and a log line
            // must never carry a translated value. The user can rename it immediately; only the moment
            // before that rename would show English regardless of the app's language.
            Dividers.Add(new LoadOrderDivider("New Section", anchor));
            RefreshVisibleModules();
            SaveDividersPublic();
        }

        // Which section the right-clicked row is, by position rather than by value. Two sections can
        // hold exactly the same label, anchor and state - inserting two above the same module gives
        // two called "New Section" - and looking one up by value would rename or remove whichever
        // came first instead of the one under the pointer. Nothing changes Dividers between the menu
        // opening and the item being clicked, so the position the row carries is the right one; the
        // value check falls back to a search for the case where something did.
        private int ResolveContextDividerIndex(DividerRowViewModel target)
        {
            var index = target.DividerIndex;

            return index >= 0 && index < Dividers.Count && Dividers[index] == target.Divider
                ? index
                : Dividers.IndexOf(target.Divider);
        }

        [RelayCommand]
        private void RenameContextDivider(string newLabel)
        {
            // Temporary diagnostics for the reported "menu items do nothing": logs which branch this
            // command actually took, so a real failure shows up as evidence instead of another guess.
            if (ContextDivider is not { } target || string.IsNullOrWhiteSpace(newLabel))
            {
                LoggingService.Log($"RenameContextDivider: no-op (ContextDivider null={ContextDivider is null}, newLabel blank={string.IsNullOrWhiteSpace(newLabel)})");
                return;
            }

            var index = ResolveContextDividerIndex(target);

            if (index < 0)
            {
                LoggingService.Log($"RenameContextDivider: ResolveContextDividerIndex returned -1 for '{target.Label}'");
                return;
            }

            Dividers[index] = Dividers[index] with { Label = LoadOrderDivider.FitLabel(newLabel) };
            RefreshVisibleModules();
            SaveDividersPublic();
            LoggingService.Log($"RenameContextDivider: renamed index {index} to '{newLabel.Trim()}'");
        }

        [RelayCommand]
        private void RemoveContextDivider()
        {
            if (ContextDivider is not { } target)
            {
                LoggingService.Log("RemoveContextDivider: no-op (ContextDivider is null)");
                return;
            }

            var index = ResolveContextDividerIndex(target);

            if (index < 0)
            {
                LoggingService.Log($"RemoveContextDivider: ResolveContextDividerIndex returned -1 for '{target.Label}'");
                return;
            }

            Dividers.RemoveAt(index);
            ContextDivider = null;
            RefreshVisibleModules();
            SaveDividersPublic();
            LoggingService.Log($"RemoveContextDivider: removed index {index} ('{target.Label}')");
        }

        [RelayCommand]
        private void ToggleContextDividerCollapsed()
        {
            if (ContextDivider is not { } target)
            {
                LoggingService.Log("ToggleContextDividerCollapsed: no-op (ContextDivider is null)");
                return;
            }

            var index = ResolveContextDividerIndex(target);

            if (index < 0)
            {
                LoggingService.Log($"ToggleContextDividerCollapsed: ResolveContextDividerIndex returned -1 for '{target.Label}'");
                return;
            }

            Dividers[index] = Dividers[index] with { Collapsed = !Dividers[index].Collapsed };
            RefreshVisibleModules();
            SaveDividersPublic();
            LoggingService.Log($"ToggleContextDividerCollapsed: index {index} now Collapsed={Dividers[index].Collapsed}");
        }

        // internal rather than private because EnvironmentPage.xaml.cs's drag-completion handler is a
        // caller too. Writes only to LoadOrderDividerStore's own file, never to LauncherData.xml.
        internal void SaveDividersPublic() => dividerStore.Save([.. Dividers]);

        // For the page's drop handler, after it has moved a whole selection.
        internal void RefreshVisibleModulesPublic() => RefreshVisibleModules();

        public string ContextToggleLabel => ContextModule?.IsEnabled == true
            ? Strings.Current["Environment.ContextToggleLabel.Disable"]
            : Strings.Current["Environment.ContextToggleLabel.Enable"];

        // Whether the right-clicked row is Steam's, which the page asks about because the outer ring
        // inserts its own identity item into that menu and the map cannot name it in every build.
        public bool ContextIsWorkshopSubscription => IsWorkshopSubscription(ContextModule);

        // Whether the ring's own statement about where this copy came from means anything for the row.
        // Steam published a subscribed item and nobody compiled it here, so it does not, unless the
        // statement is already recorded: the same item is how it is taken back, and hiding it then
        // would leave a mark nothing can clear.
        public bool ContextOffersBuiltLocally =>
            !ContextIsWorkshopSubscription || ContextModule?.IsBuiltLocally == true;

        // The item's own label stays "Open Mod Page", which is true of every row. The sentence under it
        // named Nexus, which is false for a subscribed item: what opens is the item's Workshop page in
        // the default browser, and saying Nexus there describes a destination the click does not go to.
        public string ContextOpenModPageTooltip => ContextIsWorkshopSubscription
            ? Strings.Current["Environment.OpenModPageMenuItem.Tooltip.Workshop"]
            : Strings.Current["Environment.OpenModPageMenuItem.Tooltip"];

        private string? ContextFolderPath => ContextModule?.Entry.Manifest?.FolderPath;

        private string? ContextManifestPath => ContextModule?.Entry.Manifest?.ManifestPath;

        // Recording the row is the view's job and cannot be unit tested; deciding what that row is
        // allowed to do is not, so the decision lives in Core behind this one description of the row.
        private ModuleContextTarget? ContextTarget =>
            ContextModule is not { } row
                ? null
                : new ModuleContextTarget(
                    row.IsOrphan,
                    Modules.IndexOf(row),
                    Modules.Count,
                    Directory.Exists(ContextFolderPath),
                    File.Exists(ContextManifestPath),
                    !string.IsNullOrWhiteSpace(ContextModulePage));

        private bool CanToggleContextModule => ModuleContextActions.CanToggle(ContextTarget);

        private bool CanMoveContextModuleUp => CanMoveContextModuleToTop;

        private bool CanMoveContextModuleDown => CanMoveContextModuleToBottom;

        private bool CanMoveContextModuleToTop => ModuleContextActions.CanMoveUp(ContextTarget);

        private bool CanMoveContextModuleToBottom => ModuleContextActions.CanMoveDown(ContextTarget);

        private bool CanOpenContextModuleFolder => ModuleContextActions.CanOpenFolder(ContextTarget);

#if DEV_BEM
        private bool CanOpenContextModuleManifest => ModuleContextActions.CanOpenManifest(ContextTarget);
#endif

        private string? ContextModulePage => ContextModule is { } row ? ModulePageFor(row) : null;

        // A subscription first, and with no fallback past its own page: that page is the one place
        // Unsubscribe lives, so a Workshop module has one destination from this screen whichever
        // control reaches for it, and a Url its author typed into the manifest must not send the reader
        // somewhere that button is absent.
        //
        // Otherwise the author's own Url, because it may point somewhere other than Nexus and that is
        // their choice, and failing that the page rebuilt from the mod id BEM already knows, which is
        // the difference between this item working for 68 of the modules on the reference install and
        // working for 165 of them. Nothing is written back into anyone's SubModule.xml to make this
        // work. Shared by the single-row "Open mod page" and the multi-select "open them all".
        private string? ModulePageFor(ModuleRowViewModel row)
        {
            if (IsWorkshopSubscription(row))
                return WorkshopModuleFacts.Page(row.Entry.Manifest, GameInstallPath);

            if (row.Entry.Manifest?.Url is { Length: > 0 } declared)
                return declared;

            return NexusModIds().TryGetValue(row.ModuleId, out var modId)
                ? NexusArchiveName.PageUrl(modId)
                : null;
        }

        // The same links the Nexus column and the "has a Nexus id" mark read: this instance's own
        // archive records, the confirmed ids and the install log. Linking with defaults read the
        // resting version's record instead, so a module whose id was recorded in any other instance
        // showed an id on its row and still had no Open Mod Page.
        private IReadOnlyDictionary<string, int> NexusModIds() =>
            nexusModIds ??= NexusModPages.IdsByModuleId(
                NexusLinks([.. Modules.Select(row => row.Entry.Manifest).OfType<ModuleManifest>()]));

        private bool CanCopyContextModuleId => ModuleContextActions.CanCopyId(ContextTarget);

        private bool CanOpenContextModulePage => ModuleContextActions.CanOpenModPage(ContextTarget);

        private bool CanPruneContextModule => ModuleContextActions.CanPrune(ContextTarget, IsLoaded);

        private bool CanUninstallContextModule =>
            IsLoaded && ContextModule?.Entry.Manifest is not null && Directory.Exists(ContextFolderPath);

        private bool CanRunDependencyClosure => ModuleContextActions.CanRunDependencyClosure(ContextTarget, IsLoaded);

        // What the right-clicked row is, in the terms the menu decides what to offer by. Read as the
        // flyout opens rather than held: an update, a confirmed mod id and a dependency closure are
        // all facts about this moment, and each is cheap beside the right-click that asked.
        private ModuleMenuRow? ContextMenuRow
        {
            get
            {
                if (ContextModule is not { } row)
                    return null;

                var (needsDependencies, hasDependents) = DependencyReach(row);

                return new ModuleMenuRow(
                    row.IsOrphan,
                    PlannedUpdateFor(row) is not null,
                    confirmedNexusIds.NexusModIdsByModuleId().ContainsKey(row.ModuleId),
                    row.ShowNoNexusId,
                    row.IsNotOnNexus,
                    needsDependencies,
                    hasDependents,
                    IsWorkshopSubscription(row),
                    row.IsUpdateHeld,
                    CanEndorseModule(row));
            }
        }

        // Whether either closure item would reach past the row itself. With no load order scanned
        // there is no graph to walk and neither of them can.
        private (bool NeedsDependencies, bool HasDependents) DependencyReach(ModuleRowViewModel row)
        {
            if (!IsLoaded)
                return (false, false);

            var scanned = BuildEnvironment();

            return (Reaches(DependencyClosureKind.EnableWhatItNeeds), Reaches(DependencyClosureKind.DisableWhatNeedsIt));

            bool Reaches(DependencyClosureKind kind) =>
                DependencyClosure.For(scanned, row.Entry.Id, kind).ChangesAnythingBeyondRoots;
        }

        // Which items the menu offers this row, grouped the way its separators are. Whether Shift was
        // held is the view's fact, so it arrives as an argument rather than living here.
        public IReadOnlyList<IReadOnlyList<ModuleMenuItem>> ContextMenuGroups(bool extended) =>
            ModuleContextMenu.Groups(ContextMenuRow, HasMultiContextSelection, extended);

        // The row a right-click landed on, forced through a null so the generated setter cannot drop
        // an assignment equal to the current value: right-clicking the same row twice must still
        // re-read its checkbox and its position.
        public void SetContextModule(ModuleRowViewModel? row)
        {
            ContextModule = null;
            ContextModule = row;
            RefreshContextCommands();
        }

        // Called again as the flyout opens. Every item's enabled state is then computed from the row
        // that was just recorded, whatever order the right-click and the flyout arrived in.
        public void RefreshContextCommands()
        {
            OnPropertyChanged(nameof(ContextToggleLabel));
            OnPropertyChanged(nameof(ContextPinLabel));
            OnPropertyChanged(nameof(ContextNotOnNexusLabel));
            OnPropertyChanged(nameof(ContextOpenModPageTooltip));
            OnPropertyChanged(nameof(DeselectContextModuleTooltip));

            DeselectContextModuleCommand.NotifyCanExecuteChanged();
            ToggleContextNotOnNexusCommand.NotifyCanExecuteChanged();
            ForgetContextModuleNexusIdCommand.NotifyCanExecuteChanged();
            ToggleContextModuleCommand.NotifyCanExecuteChanged();
            ToggleContextPinCommand.NotifyCanExecuteChanged();
            MoveContextModuleUpCommand.NotifyCanExecuteChanged();
            MoveContextModuleDownCommand.NotifyCanExecuteChanged();
            MoveContextModuleToTopCommand.NotifyCanExecuteChanged();
            MoveContextModuleToBottomCommand.NotifyCanExecuteChanged();
            OpenContextModuleFolderCommand.NotifyCanExecuteChanged();
#if DEV_BEM
            OpenContextModuleManifestCommand.NotifyCanExecuteChanged();
#endif
            CopyContextModuleIdCommand.NotifyCanExecuteChanged();
            OpenContextModulePageCommand.NotifyCanExecuteChanged();
            PruneContextModuleCommand.NotifyCanExecuteChanged();
            UninstallContextModuleCommand.NotifyCanExecuteChanged();
            EnableWithDependenciesCommand.NotifyCanExecuteChanged();
            DisableWithDependentsCommand.NotifyCanExecuteChanged();
#if DEV_BEM
            ToggleContextBuiltLocallyCommand.NotifyCanExecuteChanged();
#endif
            SetContextModuleNexusIdCommand.NotifyCanExecuteChanged();
            UpdateContextModuleCommand.NotifyCanExecuteChanged();
#if DEV_BEM
            OnPropertyChanged(nameof(ContextBuiltLocallyLabel));
#endif
        }

        [RelayCommand(CanExecute = nameof(CanRunDependencyClosure))]
        public Task EnableWithDependenciesAsync() =>
            RunDependencyClosureAsync(DependencyClosureKind.EnableWhatItNeeds);

        [RelayCommand(CanExecute = nameof(CanRunDependencyClosure))]
        public Task DisableWithDependentsAsync() =>
            RunDependencyClosureAsync(DependencyClosureKind.DisableWhatNeedsIt);

        // Closure is a batch action, so the official-module guard in Core applies and every refusal
        // is named rather than swallowed. Nothing is applied until the preview has been accepted, and
        // the message afterwards is built from the same lists the preview was.
        private async Task RunDependencyClosureAsync(DependencyClosureKind kind)
        {
            if (ContextModule is not { } row)
                return;

            var before = BuildEnvironment();
            var closure = DependencyClosure.For(before, row.Entry.Id, kind);

            ContextModule = null;

            if (!closure.ChangesAnything)
            {
                StatusMessage = closure.DescribeOutcome();
                return;
            }

            if (!await ConfirmDependencyClosureAsync(closure))
            {
                StatusMessage = Strings.Current["Environment.Canceled.NothingChanged"];
                return;
            }

            RecordUndo(before, kind == DependencyClosureKind.EnableWhatItNeeds
                ? Strings.Current.Format("Environment.UndoDescription.EnableDependencies", row.DisplayName)
                : Strings.Current.Format("Environment.UndoDescription.DisableDependents", row.DisplayName));

            Apply(closure.ApplyTo(before));
            PersistOrder();

            StatusMessage = closure.DescribeOutcome();
        }

        private static async Task<bool> ConfirmDependencyClosureAsync(DependencyClosure closure)
        {
            var dialog = new ContentDialog
            {
                Title = closure.Enable
                    ? Strings.Current["Environment.DependencyClosure.EnableTitle"]
                    : Strings.Current["Environment.DependencyClosure.DisableTitle"],
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { Text = closure.Describe(), TextWrapping = TextWrapping.Wrap }
                },
                PrimaryButtonText = closure.Enable ? Strings.Current["Environment.DependencyClosure.EnableButton"] : Strings.Current["Environment.DependencyClosure.DisableButton"],
                CloseButtonText = Strings.Current["Environment.DependencyClosure.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        [RelayCommand(CanExecute = nameof(CanToggleContextModule))]
        public void ToggleContextModule()
        {
            if (ContextModule is not { IsOrphan: false } row)
                return;

            row.IsEnabled = !row.IsEnabled;
            StatusMessage = row.IsEnabled
                ? Strings.Current.Format("Environment.ToggleModule.Enabled", row.DisplayName)
                : Strings.Current.Format("Environment.ToggleModule.Disabled", row.DisplayName);
            ContextModule = null;
        }

        [RelayCommand(CanExecute = nameof(CanMoveContextModuleUp))]
        public void MoveContextModuleUp() => MoveByOne(ContextModule, -1);

        [RelayCommand(CanExecute = nameof(CanMoveContextModuleDown))]
        public void MoveContextModuleDown() => MoveByOne(ContextModule, 1);

        [RelayCommand(CanExecute = nameof(CanMoveContextModuleToTop))]
        public void MoveContextModuleToTop() => MoveTo(ContextModule, 0);

        [RelayCommand(CanExecute = nameof(CanMoveContextModuleToBottom))]
        public void MoveContextModuleToBottom() => MoveTo(ContextModule, Modules.Count - 1);

        [RelayCommand(CanExecute = nameof(CanOpenContextModuleFolder))]
        public void OpenContextModuleFolder()
        {
            if (ContextFolderPath is not { } folder || !Directory.Exists(folder))
                return;

            Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", folder));
        }

#if DEV_BEM
        // The outer ring. A player never edits somebody else's SubModule.xml: reading the raw manifest
        // is the author checking a declaration, and everything a player needs off that file BEM already
        // states in words on Play and in the Validator.
        [RelayCommand(CanExecute = nameof(CanOpenContextModuleManifest))]
        public void OpenContextModuleManifest()
        {
            if (ContextManifestPath is not { } manifest || !File.Exists(manifest))
                return;

            var editor = TextEditors.FirstOrDefault(File.Exists);

            var startInfo = editor is null
                ? new System.Diagnostics.ProcessStartInfo(manifest) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(editor, [manifest]);

            Start(startInfo, Strings.Current.Format("Environment.Opened", manifest));
        }
#endif

        [RelayCommand(CanExecute = nameof(CanCopyContextModuleId))]
        public void CopyContextModuleId()
        {
            if (ContextModule is not { } row)
                return;

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(row.ModuleId);

            // Whatever else has the clipboard open (a clipboard manager, an RDP session) makes this
            // throw, and an unhandled throw here would close the app in the middle of writing the load order.
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current.Format("Environment.Clipboard.Copied", row.ModuleId);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy a module id to the clipboard");
                StatusMessage = Strings.Current.Format("Environment.Clipboard.CopyFailed", row.ModuleId);
            }
        }

        // The user's two statements about a module's place on Nexus, both of which BEM could record and
        // could not take back until now. Neither is ever inferred: "no install record and no mod id"
        // describes a mod somebody extracted by hand exactly as well as one they wrote themselves.
        public string ContextNotOnNexusLabel => ContextModule?.IsNotOnNexus == true
            ? Strings.Current["Environment.ContextNotOnNexusLabel.Marked"]
            : Strings.Current["Environment.ContextNotOnNexusLabel.Unmarked"];

        private bool CanToggleContextNotOnNexus => ContextModule is not null;

        private bool CanForgetContextModuleNexusId =>
            ContextModule is { } row && confirmedNexusIds.NexusModIdsByModuleId().ContainsKey(row.ModuleId);

        [RelayCommand(CanExecute = nameof(CanToggleContextNotOnNexus))]
        public void ToggleContextNotOnNexus()
        {
            if (ContextModule is not { } row)
                return;

            var marked = notOnNexus.ByModuleId().ContainsKey(row.ModuleId);

            // The reason stays the user's own words or stays empty. Making somebody justify themselves
            // before they can silence a false worry is a tax, not a safeguard, and BEM writing a reason
            // of its own into a field that exists to hold theirs would misrepresent who said it.
            var record = marked
                ? notOnNexus.Forget([row.ModuleId])
                : notOnNexus.Record([new ModuleNotOnNexus(row.ModuleId, string.Empty, DateTimeOffset.UtcNow)]);

            StatusMessage = record.Describe(row.ModuleId);

            RefreshNotOnNexusMarks();
            RefreshContextCommands();
        }

        [RelayCommand(CanExecute = nameof(CanForgetContextModuleNexusId))]
        public void ForgetContextModuleNexusId()
        {
            if (ContextModule is not { } row)
                return;

            StatusMessage = confirmedNexusIds.Forget([row.ModuleId]).DescribeForget(row.ModuleId);

            RefreshContextCommands();
        }

        // Rebuilds the badge on every row from the one file, so a row and the update check can never
        // disagree about what the user marked.
        // Which manager put each module on disk. Display only, and the sources are all optional hints, so a
        // locked or missing file leaves a row on Hand rather than failing the refresh.
        // The community modules in, mapped to what manages each folder; null when a source was
        // unreadable, which leaves the marks as they were.
        private IReadOnlyDictionary<string, ModuleManagement>? DetectManagers(IEnumerable<(string FolderPath, string ModuleId)> modules)
        {
            try
            {
                return ModManagerDetector.Detect(
                    GameInstallPath,
                    modules.Select(module => new ModuleToLabel(Path.GetFileName(module.FolderPath.TrimEnd('\\', '/')), module.ModuleId)),
                    ModuleArchiveLinkStore.For(ActiveDataRoot),
                    ModManagerDetector.DefaultMo2Roots());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        private void RefreshManagerMarks(ApplyReads? reads = null)
        {
            var targets = Modules
                .Where(row => !row.IsOfficial && row.Entry.Manifest is not null)
                .ToList();

            var labelled = reads is not null
                ? reads.Managed
                : DetectManagers(targets.Select(row => (row.Entry.Manifest!.FolderPath, row.ModuleId)));

            if (labelled is null)
                return;

            foreach (var row in targets)
            {
                var folder = Path.GetFileName(row.Entry.Manifest!.FolderPath.TrimEnd('\\', '/'));
                var found = labelled.TryGetValue(folder, out var management) ? management : ModuleManagement.ByHand;

                // Only a module sitting under the game's own Modules folder can be one a manager
                // deployed; a Workshop copy is Steam's, whatever shares its folder name.
                var inGame = Path.GetDirectoryName(row.Entry.Manifest.FolderPath.TrimEnd('\\', '/'))
                    ?.EndsWith("Modules", StringComparison.OrdinalIgnoreCase) == true && !row.IsWorkshop;

                row.Manager = inGame ? found.Manager : ModManager.Hand;
                row.ManagerNexusModId = inGame ? found.NexusModId : null;
            }
        }

        private void RefreshNotOnNexusMarks(ApplyReads? reads = null)
        {
            var marked = reads?.NotOnNexusIds ?? notOnNexus.ByModuleId().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var compiled = reads?.BuiltLocallyIds ?? builtLocally.ByModuleId().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in Modules)
            {
                row.IsNotOnNexus = marked.Contains(row.ModuleId);
                row.IsBuiltLocally = compiled.Contains(row.ModuleId);
            }
        }

#if DEV_BEM
        // The outer ring. "I compiled this one myself" is a statement only someone holding the source
        // tree can make; a player has an archive. What the mark means is still read in every build.
        public string ContextBuiltLocallyLabel => ContextModule?.IsBuiltLocally == true
            ? Strings.Current["Environment.ContextBuiltLocallyLabel.Marked"]
            : Strings.Current["Environment.ContextBuiltLocallyLabel.Unmarked"];

        private bool CanToggleContextBuiltLocally => ContextModule is not null;

        // The third thing the user can say about a module's place on Nexus, and the one BEM had no way
        // to be told. A module compiled here declares whatever its source tree declares, which is
        // routinely ahead of the page it will eventually be published to, and BEM could only report that
        // as two version strings that disagree for no stated reason. Three of five real unknowns
        // were exactly this.
        [RelayCommand(CanExecute = nameof(CanToggleContextBuiltLocally))]
        public void ToggleContextBuiltLocally()
        {
            if (ContextModule is not { } row)
                return;

            var marked = builtLocally.ByModuleId().ContainsKey(row.ModuleId);

            var record = marked
                ? builtLocally.Forget([row.ModuleId])
                : builtLocally.Record([new ModuleBuiltLocally(row.ModuleId, string.Empty, DateTimeOffset.UtcNow)]);

            StatusMessage = record.Describe(row.ModuleId);

            RefreshNotOnNexusMarks();
            RefreshContextCommands();
            RefreshUpdatePips();
        }
#endif

        private bool CanSetContextModuleNexusId => ContextModule is not null;

        // The user's answer to "which mod page is this", which nothing else in BEM can supply. Both
        // sources BEM derives an id from have now been observed naming the wrong page on this very
        // install, and they were wrong in opposite directions, so there is no rule BEM can apply that
        // would have got both right. This is that rule: ask.
        [RelayCommand(CanExecute = nameof(CanSetContextModuleNexusId))]
        public async Task SetContextModuleNexusIdAsync()
        {
            if (ContextModule is not { } row)
                return;

            var link = nexusLinks.FirstOrDefault(candidate =>
                string.Equals(candidate.ModuleId, row.ModuleId, StringComparison.OrdinalIgnoreCase));

            var input = new TextBox
            {
                PlaceholderText = Strings.Current["Environment.SetNexusId.Placeholder"],
                Text = link?.NexusModId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            };

            var known = link is null
                ? string.Empty
                : Strings.Current.Format("Environment.SetNexusId.Known",
                    link.NexusModId?.ToString(CultureInfo.InvariantCulture) ?? Strings.Current["Environment.SetNexusId.NoneFallback"],
                    NexusIdSources.Describe(link.Source))
                  + (link.SourcesDisagree
                      ? Strings.Current.Format("Environment.SetNexusId.SourcesDisagree", link.ArchiveNexusModId)
                      : string.Empty)
                  + Environment.NewLine + Environment.NewLine;

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.SetNexusId.DialogTitle", row.DisplayName),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = known + Strings.Current["Environment.SetNexusId.Body"]
                        },
                        input
                    }
                },
                PrimaryButtonText = Strings.Current["Environment.SetNexusId.SetButton"],
                CloseButtonText = Strings.Current["Environment.SetNexusId.CancelButton"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return;

            var typed = input.Text?.Trim() ?? string.Empty;

            if (typed.Length == 0)
            {
                StatusMessage = confirmedNexusIds.Forget([row.ModuleId]).DescribeForget(row.ModuleId);
                await ReloadNexusAnswersAsync();
                return;
            }

            var modId = NexusArchiveName.TryGetModIdFromUrl(typed)
                        ?? (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                            && parsed > 0
                                ? parsed
                                : null);

            if (modId is not { } chosen)
            {
                StatusMessage = Strings.Current.Format("Environment.SetNexusId.Invalid", typed);
                return;
            }

            var record = confirmedNexusIds.Record([new LearnedNexusId(row.ModuleId, chosen,
                Strings.Current["Environment.SetNexusId.ManualReason"], DateTimeOffset.UtcNow, SetByOwner: true)]);

            StatusMessage = record.Failed > 0
                ? Strings.Current.Format("Environment.SetNexusId.WriteFailed", chosen)
                : Strings.Current.Format("Environment.SetNexusId.Success", row.ModuleId, chosen);

            await ReloadNexusAnswersAsync();
        }

        // Re-links every module and redraws every mark, without asking Nexus anything. Setting an id
        // changes which page a module is checked against, and a row still showing the old verdict would
        // be reporting a page the user has just replaced.
        private async Task ReloadNexusAnswersAsync()
        {
            RedrawNexusIds();
            NexusIdsChanged.Raise(this);

            await Task.CompletedTask;
        }

        // Every mark that follows from which ids BEM holds, drawn again. Play's rows and Library's
        // Installed Mods rows are the same row objects, so both lists show a saved id at once.
        private void RedrawNexusIds()
        {
            nexusLinks = NexusLinks();

            RefreshUpdatePips();
            RefreshNotOnNexusMarks();
            RefreshContextCommands();
            RefreshVisibleModules();
            ShowDetails(SelectedModule);
        }

        private void OnNexusIdsChanged(object? sender, EventArgs e)
        {
            _ = e;

            if (ReferenceEquals(sender, this))
                return;

            try
            {
                RedrawNexusIds();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to redraw the mod ids after one was saved");
            }
        }

        [RelayCommand(CanExecute = nameof(CanOpenContextModulePage))]
        public void OpenContextModulePage()
        {
            if (ContextModulePage is not { } page)
                return;

            Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", page));
        }

        // ---- Autonomous updates ----

        // The plan is cached: its count feeds a button label that is read whenever the context menu
        // opens and when a check lands, and recomputing the whole ledger on every read is exactly the
        // delay that made right-clicks and the Play page feel slow. It is invalidated when the
        // verdicts or the module set change.
        private IReadOnlyList<ModUpdateTarget>? cachedAvailableUpdates;

        private IReadOnlyList<ModUpdateTarget> AvailableUpdates()
        {
            if (cachedAvailableUpdates is not null)
                return cachedAvailableUpdates;

            // Before the first check there are no verdicts, so there is nothing to plan and no reason to
            // read the whole version ledger on a page that just loaded.
            if (nexusVerdicts.Count == 0)
            {
                cachedAvailableUpdates = [];
                return cachedAvailableUpdates;
            }

            // Only modules the last check actually flagged are planned. The planner on its own would
            // name every module whose installed file Nexus moved out of its current files, which also
            // catches modules the verdict considers current or healed - that is why the offered count
            // used to disagree with the out-of-date tally.
            var links = NexusModuleMatching.Link(
                    Modules.Select(row => row.Entry.Manifest).OfType<ModuleManifest>().ToList(),
                    recorded: ModuleArchiveLinkStore.For(ActiveDataRoot))
                .Where(link => nexusVerdicts.TryGetValue(link.ModuleId, out var verdict)
                               && verdict.State is ModuleUpdateState.OutOfDate or ModuleUpdateState.ProbablyOutOfDate)
                .ToList();

            var filesByModId = NexusVersionStore.Default.Load().FilesByModId();
            cachedAvailableUpdates = ModUpdatePlanner.Plan(links, filesByModId);
            return cachedAvailableUpdates;
        }

        private void InvalidateAvailableUpdates()
        {
            cachedAvailableUpdates = null;
            OnPropertyChanged(nameof(AvailableUpdatesLabel));
            OnPropertyChanged(nameof(HasAvailableUpdates));
            InstallAvailableUpdatesCommand.NotifyCanExecuteChanged();
        }

        public IReadOnlyList<ModUpdateTarget> PlanAvailableUpdates() => AvailableUpdates();

        public int AvailableUpdateCount => AvailableUpdates().Count;

        // Basic shows updates as one "N updates" button and nothing while there are none.
        public bool HasAvailableUpdates => AvailableUpdateCount > 0;

        [RelayCommand(CanExecute = nameof(CanInstallAvailableUpdates))]
        public async Task InstallAvailableUpdatesAsync()
        {
            var targets = AvailableUpdates();

            if (targets.Count == 0)
            {
                StatusMessage = Strings.Current["Environment.AvailableUpdates.None"];
                return;
            }

            await InstallTargetsAsync(targets, Strings.Current.Plural("Environment.AvailableUpdates.Installing", targets.Count));
        }

        private bool CanInstallAvailableUpdates => !IsBusy;

        public string AvailableUpdatesLabel
        {
            get
            {
                var count = AvailableUpdateCount;
                return count == 0 ? Strings.Current["Environment.AvailableUpdatesLabel.None"] : Strings.Current.Format("Environment.AvailableUpdatesLabel.WithCount", count);
            }
        }

        private ModNotesStore UserNotes => new(ModNotesStore.DefaultPath(ActiveDataRoot));

        // Puts the player's notes and tags on every row from the one file, so the row line and the
        // search can never disagree about what was written.
        private void RefreshUserNotes(IReadOnlyDictionary<string, ModNote>? loaded = null)
        {
            var notes = loaded ?? UserNotes.Load();

            foreach (var row in Modules)
            {
                var note = notes.TryGetValue(row.ModuleId, out var found) ? found : null;

                row.UserNote = note?.Text ?? string.Empty;
                row.UserTags = note?.Tags ?? [];
                row.UserSource = note?.Source ?? string.Empty;
            }
        }

        // Notes and tags are the player's own words about a mod, so the dialog is a plain pair of text
        // boxes and saving both empty removes the entry.
        [RelayCommand]
        public async Task EditContextModuleNotesAsync()
        {
            if (ContextModule is not { } row)
                return;

            var note = new TextBox
            {
                Header = Strings.Current["Library.Notes.NoteLabel"],
                Text = row.UserNote,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
                MaxLength = ModNotesStore.MaxTextLength
            };

            var tags = new TextBox
            {
                Header = Strings.Current["Library.Notes.TagsLabel"],
                Text = ModNotesStore.JoinTags(row.UserTags)
            };

            var source = new TextBox
            {
                Header = Strings.Current["Library.Notes.SourceLabel"],
                Text = row.UserSource,
                MaxLength = ModNotesStore.MaxSourceLength
            };

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Library.Notes.DialogTitle", row.DisplayName),
                Content = new StackPanel { Spacing = 8, Children = { note, tags, source } },
                PrimaryButtonText = Strings.Current["Library.Notes.Save"],
                CloseButtonText = Strings.Current["Environment.SetNexusId.CancelButton"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return;

            if (!UserNotes.Set(row.ModuleId, note.Text, ModNotesStore.ParseTags(tags.Text), source.Text))
            {
                StatusMessage = Strings.Current["Library.Notes.SaveFailed"];
                return;
            }

            RefreshUserNotes();
            RefreshVisibleModules();
        }

        private bool CanEndorseModule(ModuleRowViewModel? row) =>
            ShowNexusUpdates && row is not null && !row.IsOfficial && !row.IsWorkshop && NexusModIdOf(row) is > 0;

        private int? NexusModIdOf(ModuleRowViewModel row) =>
            nexusLinks.FirstOrDefault(link =>
                string.Equals(link.ModuleId, row.ModuleId, StringComparison.OrdinalIgnoreCase))?.NexusModId;

        [RelayCommand]
        public async Task EndorseContextModuleAsync()
        {
            if (ContextModule is not { } row || !CanEndorseModule(row) || NexusModIdOf(row) is not { } modId)
                return;

            var key = NexusKeys().Load();

            if (key is null)
            {
                StatusMessage = Strings.Current["Core.Nexus.Client.Endorse.Unauthorized"];
                return;
            }

            var version = row.FileVersion ?? row.Entry.Version.ToString();
            var result = await new NexusClient(new NexusHttpTransport()).EndorseAsync(key, modId, version, CancellationToken.None);

            StatusMessage = result.Message;
        }

        private ModuleUpdateHoldStore UpdateHolds => new(ModuleUpdateHoldStore.DefaultPath(ActiveDataRoot));

        // The store decides, not the row: a hold on a mod with nothing newer shows no Held state yet must still be undoable.
        public string ContextHoldLabel => ContextModule is { } held && UpdateHolds.Load().Contains(held.ModuleId)
            ? Strings.Current["Environment.HoldUpdate.Undo"]
            : Strings.Current["Environment.HoldUpdate.Hold"];

        [RelayCommand]
        public void ToggleContextModuleHold()
        {
            if (ContextModule is not { } row)
                return;

            if (!UpdateHolds.Set(row.ModuleId, !UpdateHolds.Load().Contains(row.ModuleId)))
                return;

            nexusVerdicts = ModuleUpdateVerdicts.ApplyHolds(nexusVerdicts, UpdateHolds.Load());
            InvalidateAvailableUpdates();
            RefreshUpdatePips();
            OnPropertyChanged(nameof(ContextHoldLabel));
        }

        private bool CanUpdateContextModule => PlannedUpdateFor(ContextModule) is not null;

        // Update from a row outside Play (the Library's installed mods table): the same update the row
        // menu runs, for that row.
        public async Task UpdateModuleAsync(ModuleRowViewModel row)
        {
            ArgumentNullException.ThrowIfNull(row);

            ContextModule = row;

            if (UpdateContextModuleCommand.CanExecute(null))
                await UpdateContextModuleCommand.ExecuteAsync(null);
        }

        [RelayCommand(CanExecute = nameof(CanUpdateContextModule))]
        public async Task UpdateContextModuleAsync()
        {
            if (PlannedUpdateFor(ContextModule) is not { } target)
            {
                StatusMessage = Strings.Current["Environment.ContextUpdate.None"];
                return;
            }

            await InstallTargetsAsync([target], Strings.Current.Format("Environment.ContextUpdate.Installing", target.DisplayName));
        }

        private ModUpdateTarget? PlannedUpdateFor(ModuleRowViewModel? row) =>
            row is null ? null : AvailableUpdates().FirstOrDefault(t => t.ModuleId == row.ModuleId);

        private async Task InstallTargetsAsync(IReadOnlyList<ModUpdateTarget> targets, string starting)
        {
            var install = ShellViewModels.Instance.Install;
            IsBusy = true;

            try
            {
                var results = await install.InstallModUpdatesAsync(
                    targets, new Progress<string>(message => StatusMessage = message), CancellationToken.None);

                var installed = results.Count(result => result.Installed);
                var failed = results.Where(result => !result.Installed).ToList();

                // An install just answered the exact question a Nexus check exists to answer: this
                // module is current now. Without this, nexusVerdicts kept saying OutOfDate for a module
                // BEM itself just updated, so the available-update count, the Updates filter and this
                // row's own badge all kept disagreeing with what was actually installed until the user
                // ran Check for Updates again by hand - the update finished, but nothing that reads it
                // was told.
                foreach (var result in results.Where(r => r.Installed))
                {
                    nexusVerdicts[result.ModuleId] = nexusVerdicts.TryGetValue(result.ModuleId, out var previous)
                        ? previous with { State = ModuleUpdateState.UpToDate, Reason = Strings.Current["Environment.ModUpdate.Reason"] }
                        : new ModuleUpdateVerdict(result.ModuleId, result.ModuleName, ModuleUpdateState.UpToDate, Strings.Current["Environment.ModUpdate.Reason"]);
                }

                if (installed > 0)
                {
                    RefreshUpdatePips();
                    RefreshVisibleModules();
                }

                // A count alone said something failed without saying what or why, so the only way to
                // learn which module was still out of date was to notice the tally was short and go
                // read the log by hand. Naming the module and its own reason here is what the log
                // already had; leaving it out of the one place the user actually looks was the gap.
                StatusMessage = failed.Count == 0
                    ? Strings.Current.Plural("Environment.InstallUpdates.AllInstalled", installed)
                    : Strings.Current.Format("Environment.InstallUpdates.SomeFailed", failed.Count, installed,
                        string.Join("; ", failed.Select(result => $"{result.ModuleName} ({result.Message})")));
            }
            finally
            {
                IsBusy = false;
                RefreshContextCommands();
                InvalidateAvailableUpdates();
            }
        }

        // Multi-select. The set is recorded as the flyout opens, from the rows the view has
        // highlighted, so a command never has to guess what "selected" meant at the moment it ran.
        // Which of the selection an action may legally touch, and why not the rest, is decided in
        // Core by ModuleBatchActions; everything here is the doing and the reporting.
        private IReadOnlyList<ModuleRowViewModel> selectedContextModules = [];

        // The install the current highlight was made against, so a rebuild can tell a rescan from an
        // instance switch.
        private string? selectionScope;

        public bool HasMultiContextSelection => selectedContextModules.Count > 1;

        public IReadOnlyList<ModuleBatchMenuEntry> MultiContextMenuEntries { get; private set; } = [];

        public void SetContextSelection(ModuleRowViewModel? context, IEnumerable<ModuleRowViewModel> selected)
        {
            // Right-clicking a row outside the current selection collapses to that row, which is what
            // every Windows list does: the batch acts on the set the user built and then right-clicked
            // one of, never on a selection the clicked row is not part of. The highlight is cleared
            // with it, so what is on screen is what the menu is about to act on.
            var set = selected.Distinct().ToList();
            var inside = context is not null && set.Contains(context) && set.Count > 1;

            selectedContextModules = context is null ? [] : inside ? set : [context];

            if (context is not null && !inside)
            {
                ClearUiSelection();

                // Collapsing to the clicked row makes it the row a later Shift+click measures from,
                // the same way a left-click on it would have.
                SelectionAnchor = context;
            }

            OnPropertyChanged(nameof(HasMultiContextSelection));

            RebuildMultiContextEntries();
        }

        private void ClearUiSelection()
        {
            foreach (var row in Modules.Where(row => row.IsUiSelected))
                row.IsUiSelected = false;

            SelectionAnchor = null;
            RefreshSelectedForActions();
        }

        // What the list is actually showing, in the order it is showing it. VisibleModules is what the
        // search box and the filter chips leave; a collapsed section then hides its modules from
        // DisplayRows entirely, and a row nobody can see is not one a range or a Select All may reach.
        internal IReadOnlyList<ModuleRowViewModel> ShownModules => [.. DisplayRows.OfType<ModuleRowViewModel>()];

        // The row a Shift+click measures from, which is not the row the ListView is drawing as its own
        // selection: Explorer moves the anchor on a plain click and on a Ctrl+click, and leaves it
        // alone while a range is being stretched.
        internal ModuleRowViewModel? SelectionAnchor { get; private set; }

        // The Explorer gestures, decided in Core and written to the rows here. The view's whole share
        // is turning a click or a keystroke into one of these.
        internal void ApplySelectionGesture(ListSelectionGesture gesture, ModuleRowViewModel? target)
        {
            var shown = ShownModules;
            var result = ListSelection.Apply(
                gesture, shown, new ListSelectionState<ModuleRowViewModel>(CurrentSelection(shown), SelectionAnchor), target);

            SelectionAnchor = result.Anchor;

            // A selection of exactly the row the list is about to draw as its own needs no highlight of
            // its own: lit twice, one plain-clicked row reads as a multi-selection of one.
            SetUiSelection(result.Selected.Count == 1 && ReferenceEquals(result.Selected[0], target) ? [] : result.Selected);
        }

        // Read back from the rows rather than held in a field, because a rebuild replaces every row and
        // the highlight is what survives it. Nothing highlighted means at most one row is selected and
        // the list is drawing it, so the anchor is where that row is recorded.
        private IReadOnlyList<ModuleRowViewModel> CurrentSelection(IReadOnlyList<ModuleRowViewModel> shown)
        {
            var marked = shown.Where(row => row.IsUiSelected).ToList();

            if (marked.Count > 0)
                return marked;

            return SelectionAnchor is { } anchor && shown.Contains(anchor) ? [anchor] : [];
        }

        private void SetUiSelection(IReadOnlyCollection<ModuleRowViewModel> selected)
        {
            var mark = selected.ToHashSet();

            foreach (var row in Modules)
                row.IsUiSelected = mark.Contains(row);

            selectionScope = GameInstallPath;
            RefreshSelectedForActions();
        }

        // How many rows are highlighted for a batch, kept as a property so Clear Selection is never a
        // control that does nothing and the count can be stated back to the user.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUiSelection))]
        [NotifyCanExecuteChangedFor(nameof(ClearModuleSelectionCommand))]
        public partial int SelectedForActionsCount { get; set; }

        public bool HasUiSelection => SelectedForActionsCount > 0;

        internal void RefreshSelectedForActions() =>
            SelectedForActionsCount = Modules.Count(row => row.IsUiSelected);

        // Select All and Select Matching are the same command over the rows the list is showing, and
        // the label says which of the two it is doing. Anything narrowing the list makes it Matching,
        // a collapsed section included, because that is what the command would then be selecting.
        public string SelectAllLabel =>
            ShownModules.Count < Modules.Count
                ? Strings.Current["Environment.SelectMatchingMenuItem"]
                : Strings.Current["Environment.SelectAllMenuItem"];

        [RelayCommand(CanExecute = nameof(IsLoaded))]
        public void SelectAllShown()
        {
            ApplySelectionGesture(ListSelectionGesture.All, null);

            StatusMessage = Strings.Current.Plural("Environment.Multi.Selected", SelectedForActionsCount);
        }

        [RelayCommand(CanExecute = nameof(HasUiSelection))]
        public void ClearModuleSelection()
        {
            var cleared = SelectedForActionsCount;

            ClearUiSelection();

            StatusMessage = Strings.Current.Plural("Environment.Multi.ClearedSelection", cleared);
        }

        // Names the module rather than repeating the label, so the item that drops one row and Clear
        // the Selection right under it can never be read for one another.
        private bool HasContextModule => ContextModule is not null;

        public string DeselectContextModuleTooltip =>
            Strings.Current.Format("Environment.Multi.DeselectMenuItem.Tooltip", ContextModule?.DisplayName ?? string.Empty);

        // The one way to correct a selection without a keyboard. Built with the mouse alone a selection
        // could be made and thrown away but never adjusted, so one wrong row meant starting over.
        [RelayCommand(CanExecute = nameof(HasContextModule))]
        public void DeselectContextModule()
        {
            if (ContextModule is not { } row)
                return;

            ApplySelectionGesture(ListSelectionGesture.Toggle, row);

            StatusMessage = Strings.Current.Plural(
                "Environment.Multi.Deselected", SelectedForActionsCount, row.DisplayName);
        }

        // Everything Core needs to rule on one row, read once per selection rather than once per row
        // per action: each store call rebuilds a dictionary from its own file, and doing that inside
        // the loop is what would make the menu slow enough to notice on the reference install.
        private IReadOnlyList<ModuleBatchCandidate> SelectionCandidates()
        {
            if (selectedContextModules.Count == 0)
                return [];

            var marked = notOnNexus.ByModuleId();
            var compiled = builtLocally.ByModuleId();
            var confirmed = confirmedNexusIds.NexusModIdsByModuleId();
            var updates = AvailableUpdates().Select(target => target.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return
            [
                .. selectedContextModules.Select(row => new ModuleBatchCandidate(
                    row.Entry.Id,
                    row.DisplayName,
                    row.IsOfficial,
                    row.IsOrphan,
                    row.IsEnabled,
                    Directory.Exists(row.Entry.Manifest?.FolderPath),
                    !string.IsNullOrWhiteSpace(ModulePageFor(row)),
                    updates.Contains(row.ModuleId),
                    confirmed.ContainsKey(row.ModuleId),
                    marked.ContainsKey(row.ModuleId),
                    compiled.ContainsKey(row.ModuleId),
                    pins.Contains(row.Entry.Id),
                    IsWorkshopSubscription(row)))
            ];
        }

        private ModuleBatchPlan PlanFor(ModuleBatchAction action) =>
            ModuleBatchActions.Plan(action, SelectionCandidates());

        private void RebuildMultiContextEntries()
        {
            if (!IsLoaded || selectedContextModules.Count < 2)
            {
                MultiContextMenuEntries = [];
                OnPropertyChanged(nameof(MultiContextMenuEntries));
                return;
            }

            var candidates = SelectionCandidates();
            var tooltip = Strings.Current["Environment.Multi.MenuItem.Tooltip"];
            var entries = new List<ModuleBatchMenuEntry>();

            // The label counts what the action will actually touch, not what is highlighted: an item
            // reading "Disable 7 Modules" over a selection of nine is the promise the action keeps,
            // and the two official modules it left out are named in the status line afterwards.
            void Offer(ModuleBatchAction action, string labelKey, System.Windows.Input.ICommand command)
            {
                var plan = ModuleBatchActions.Plan(action, candidates);

                // Uninstall is offered for Workshop modules too, because what it does for them is offer the
                // pages they are actually removed on, and the count is everything the press reaches.
                var reached = plan.Eligible.Count
                    + (action == ModuleBatchAction.Uninstall ? SubscribedIn(plan).Count : 0);

                if (reached > 0)
                    entries.Add(new ModuleBatchMenuEntry(Strings.Current.Plural(labelKey, reached), tooltip, command));
            }

            Offer(ModuleBatchAction.Disable, "Environment.Multi.DisableMenuItem", DisableSelectedModulesCommand);
            Offer(ModuleBatchAction.EnableWithDependencies, "Environment.Multi.EnableWithDependenciesMenuItem", EnableSelectedWithDependenciesCommand);
            Offer(ModuleBatchAction.DisableWithDependents, "Environment.Multi.DisableWithDependentsMenuItem", DisableSelectedWithDependentsCommand);
            Offer(ModuleBatchAction.MoveToTop, "Environment.Multi.MoveToTopMenuItem", MoveSelectedToTopCommand);
            Offer(ModuleBatchAction.MoveToBottom, "Environment.Multi.MoveToBottomMenuItem", MoveSelectedToBottomCommand);
            Offer(ModuleBatchAction.Pin, "Environment.Multi.PinMenuItem", PinSelectedModulesCommand);
            Offer(ModuleBatchAction.OpenModPage, "Environment.Multi.OpenModPageMenuItem", OpenSelectedModulePagesCommand);
            Offer(ModuleBatchAction.Update, "Environment.Multi.UpdateMenuItem", UpdateSelectedModulesCommand);
            Offer(ModuleBatchAction.MarkNotOnNexus, "Environment.Multi.NotOnNexusMenuItem", MarkSelectedNotOnNexusCommand);
            Offer(ModuleBatchAction.MarkBuiltOnThisMachine, "Environment.Multi.BuiltLocallyMenuItem", MarkSelectedBuiltLocallyCommand);
            Offer(ModuleBatchAction.ForgetModId, "Environment.Multi.ForgetModIdMenuItem", ForgetSelectedModIdsCommand);
            Offer(ModuleBatchAction.Prune, "Environment.Multi.PruneMenuItem", PruneSelectedEntriesCommand);
            Offer(ModuleBatchAction.Uninstall, "Environment.Multi.UninstallMenuItem", UninstallSelectedModulesCommand);

            MultiContextMenuEntries = entries;
            OnPropertyChanged(nameof(MultiContextMenuEntries));
        }

        private IReadOnlyList<ModuleRowViewModel> RowsFor(ModuleBatchPlan plan)
        {
            var wanted = plan.Eligible.Select(module => module.Id).ToHashSet();

            return [.. selectedContextModules.Where(row => wanted.Contains(row.Entry.Id))];
        }

        private static IReadOnlyList<ModuleBatchCandidate> SubscribedIn(ModuleBatchPlan plan) =>
            [.. plan.Skipped.Where(skip => skip.Reason == ModuleBatchSkipReason.Subscribed).Select(skip => skip.Module)];

        private IReadOnlyList<(string Name, string? Page)> WorkshopPagesFor(IReadOnlyList<ModuleBatchCandidate> modules)
        {
            var wanted = modules.Select(module => module.Id).ToHashSet();

            return [.. selectedContextModules
                .Where(row => wanted.Contains(row.Entry.Id))
                .Select(row => (row.DisplayName, ModulePageFor(row)))];
        }

        [RelayCommand]
        public void DisableSelectedModules()
        {
            var plan = PlanFor(ModuleBatchAction.Disable);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var scope = plan.Eligible.Select(module => module.Id).ToHashSet();
            var before = BuildEnvironment();

            // One undo step for one press. Nine steps to take back one click is not undo, it is a
            // chore, and the snapshot the stack holds already restores the whole batch at once.
            RecordUndo(before, Strings.Current.Plural("Environment.UndoDescription.MultiDisable", scope.Count));

            Apply(before.WithEnabled(false, entry => scope.Contains(entry.Id)));
            PersistOrder();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.Disabled", scope.Count));
        }

        [RelayCommand]
        public Task EnableSelectedWithDependenciesAsync() =>
            RunSelectedClosureAsync(DependencyClosureKind.EnableWhatItNeeds);

        [RelayCommand]
        public Task DisableSelectedWithDependentsAsync() =>
            RunSelectedClosureAsync(DependencyClosureKind.DisableWhatNeedsIt);

        private async Task RunSelectedClosureAsync(DependencyClosureKind kind)
        {
            var plan = PlanFor(kind == DependencyClosureKind.EnableWhatItNeeds
                ? ModuleBatchAction.EnableWithDependencies
                : ModuleBatchAction.DisableWithDependents);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var roots = plan.Eligible.Select(module => module.Id).ToList();
            var before = BuildEnvironment();
            var closure = DependencyClosure.For(before, roots, kind);

            if (!closure.ChangesAnything)
            {
                StatusMessage = plan.Describe(closure.DescribeOutcome());
                return;
            }

            if (!await ConfirmDependencyClosureAsync(closure))
            {
                StatusMessage = Strings.Current["Environment.Canceled.NothingChanged"];
                return;
            }

            RecordUndo(before, Strings.Current.Plural(
                kind == DependencyClosureKind.EnableWhatItNeeds
                    ? "Environment.UndoDescription.MultiEnableDependencies"
                    : "Environment.UndoDescription.MultiDisableDependents",
                roots.Count));

            Apply(closure.ApplyTo(before));
            PersistOrder();

            StatusMessage = plan.Describe(closure.DescribeOutcome());
        }

        [RelayCommand]
        public void MoveSelectedToTop() => MoveSelected(toTop: true);

        [RelayCommand]
        public void MoveSelectedToBottom() => MoveSelected(toTop: false);

        // The selection travels as a block. Sending nine rows to the top one at a time arrives in
        // reverse and destroys whatever order the user had inside them, so the target order is worked
        // out in Core first and the list is then walked into it.
        private void MoveSelected(bool toTop)
        {
            var plan = PlanFor(toTop ? ModuleBatchAction.MoveToTop : ModuleBatchAction.MoveToBottom);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var moving = plan.Eligible.Select(module => module.Id).ToList();
            var order = Modules.Select(row => row.Entry.Id).ToList();

            var target = toTop
                ? ModuleBatchOrder.ToTop(order, moving)
                : ModuleBatchOrder.ToBottom(order, moving);

            if (target.SequenceEqual(order))
            {
                StatusMessage = plan.Describe(Strings.Current["Environment.Multi.OrderUnchanged"]);
                return;
            }

            var before = BuildEnvironment();
            var byId = Modules.ToDictionary(row => row.Entry.Id);

            for (var i = 0; i < target.Count; i++)
            {
                var current = Modules.IndexOf(byId[target[i]]);

                if (current != i)
                    Modules.Move(current, i);
            }

            OnOrderChangedByUser();
            RefreshVisibleModules();

            // OnOrderChangedByUser refuses a move that introduces a dependency violation and puts the
            // order back, writing its own reason into the status line. Nothing happened, so nothing is
            // recorded and its reason is left standing rather than overwritten with a success.
            if (Modules.Select(row => row.Entry.Id).SequenceEqual(order))
                return;

            RecordUndo(before, Strings.Current.Plural(
                toTop ? "Environment.UndoDescription.MultiMoveToTop" : "Environment.UndoDescription.MultiMoveToBottom",
                moving.Count));

            StatusMessage = plan.Describe(Strings.Current.Plural(
                toTop ? "Environment.Multi.MovedToTop" : "Environment.Multi.MovedToBottom", moving.Count));
        }

        [RelayCommand]
        public void PinSelectedModules()
        {
            var plan = PlanFor(ModuleBatchAction.Pin);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            pins = plan.Eligible.Aggregate(pins, (current, module) => current.With(module.Id));

            SavePins();
            RefreshPins();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.Pinned", plan.Eligible.Count));
        }

        [RelayCommand]
        public void OpenSelectedModulePages()
        {
            var plan = PlanFor(ModuleBatchAction.OpenModPage);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            foreach (var row in RowsFor(plan))
            {
                if (ModulePageFor(row) is { } page)
                    Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", page));
            }

            // Start writes the status line on every call it makes, so whatever it wrote last named only
            // the final page. The batch's own sentence replaces it, and the cap and anything without a
            // page are named rather than silently dropped.
            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.OpenedPages", plan.Eligible.Count));
        }

        [RelayCommand]
        public async Task UpdateSelectedModulesAsync()
        {
            var plan = PlanFor(ModuleBatchAction.Update);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var wanted = plan.Eligible.Select(module => module.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = AvailableUpdates().Where(target => wanted.Contains(target.ModuleId)).ToList();

            await InstallTargetsAsync(targets, Strings.Current.Plural("Environment.AvailableUpdates.Installing", targets.Count));

            StatusMessage = plan.Describe(StatusMessage);
        }

        [RelayCommand]
        public void MarkSelectedNotOnNexus()
        {
            var plan = PlanFor(ModuleBatchAction.MarkNotOnNexus);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var now = DateTimeOffset.UtcNow;

            notOnNexus.Record([.. plan.Eligible.Select(module => new ModuleNotOnNexus(module.Id.Value, string.Empty, now))]);

            RefreshNotOnNexusMarks();
            RefreshContextCommands();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.MarkedNotOnNexus", plan.Eligible.Count));
        }

        [RelayCommand]
        public void MarkSelectedBuiltLocally()
        {
            var plan = PlanFor(ModuleBatchAction.MarkBuiltOnThisMachine);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var now = DateTimeOffset.UtcNow;

            builtLocally.Record([.. plan.Eligible.Select(module => new ModuleBuiltLocally(module.Id.Value, string.Empty, now))]);

            RefreshNotOnNexusMarks();
            RefreshContextCommands();
            RefreshUpdatePips();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.MarkedBuiltLocally", plan.Eligible.Count));
        }

        [RelayCommand]
        public void ForgetSelectedModIds()
        {
            var plan = PlanFor(ModuleBatchAction.ForgetModId);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            confirmedNexusIds.Forget([.. plan.Eligible.Select(module => module.Id.Value)]);

            RefreshContextCommands();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.ForgotModIds", plan.Eligible.Count));
        }

        [RelayCommand]
        public void PruneSelectedEntries()
        {
            var plan = PlanFor(ModuleBatchAction.Prune);

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                return;
            }

            var before = BuildEnvironment();
            var after = plan.Eligible.Aggregate(before, (current, module) => current.WithoutOrphan(module.Id));
            var removed = before.Entries.Count - after.Entries.Count;

            if (removed == 0)
            {
                StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.Pruned", 0));
                return;
            }

            RecordUndo(before, Strings.Current.Plural("Environment.UndoDescription.MultiPrune", removed));

            Apply(after);
            PersistOrder();

            StatusMessage = plan.Describe(Strings.Current.Plural("Environment.Multi.Pruned", removed));
        }

        // Uninstalling nine modules is not nine times as reversible as uninstalling one, so it is
        // confirmed once, and the one dialog names every folder, its file count and its size before
        // anything moves. Official modules never reach it: the plan refuses them, so the typed-name
        // guard the single-row uninstall carries has nothing to guard here.
        [RelayCommand]
        public async Task UninstallSelectedModulesAsync()
        {
            var plan = PlanFor(ModuleBatchAction.Uninstall);
            var subscribed = WorkshopPagesFor(SubscribedIn(plan));

            if (!plan.CanRun)
            {
                StatusMessage = plan.DescribeSkips();
                await AppendOpenedPagesAsync(subscribed);
                return;
            }

            var plans = RowsFor(plan)
                .Select(row => row.Entry.Manifest)
                .OfType<ModuleManifest>()
                .Select(manifest => ModuleUninstaller.Plan(manifest, FindModuleResidue(manifest)))
                .ToList();

            if (plans.Count == 0 || !await ConfirmBatchUninstallAsync(plans, HasHeldChanges))
            {
                StatusMessage = Strings.Current["Environment.Canceled.NothingChanged"];
                return;
            }

            var removed = new List<string>();
            var failed = new List<string>();
            var residue = new List<ModuleResidue>();

            foreach (var one in plans)
            {
                var versionBefore = InstallHistoryLog.VersionBefore(one);
                var report = ModuleUninstaller.Uninstall(one, one.Name, RecycleFolder);

                if (report.Removed)
                {
                    InstallHistoryLog.Removed(one, versionBefore);
                    LoggingService.Log($"Uninstalled {one.Id}: {report.Message}");
                    removed.Add(one.Name);
                    residue.AddRange(report.Residue);
                    ForgetLoadOrderEntry(one.Id, report.Residue);
                }
                else
                {
                    LoggingService.Log($"Uninstall refused for {one.Id}: {report.Message}", LogLevel.Warn);
                    failed.Add($"{one.Name} ({report.Message})");
                }
            }

            pendingResidue = residue;
            LeftoverSummary = residue.Count == 0
                ? string.Empty
                : Strings.Current.Plural("Environment.Multi.Uninstall.LeftBehind", residue.Count);

            await Refresh();

            var outcome = Strings.Current.Plural("Environment.Multi.Uninstalled", removed.Count, string.Join(", ", removed));

            if (failed.Count > 0)
                outcome += " " + Strings.Current.Plural("Environment.Multi.Uninstall.Failed", failed.Count, string.Join("; ", failed));

            StatusMessage = plan.Describe(outcome);

            await AppendOpenedPagesAsync(subscribed);
        }

        // The status line already says which Workshop modules were left alone, so opening their pages adds
        // how many opened rather than replacing that sentence. A page that failed to open leaves the failure
        // Start wrote in its place.
        private async Task AppendOpenedPagesAsync(IReadOnlyList<(string Name, string? Page)> modules)
        {
            var said = StatusMessage;
            var (opened, failed) = await OfferWorkshopPagesAsync(modules);

            if (opened > 0 && failed == 0)
                StatusMessage = $"{said} {Strings.Current.Plural("Environment.Multi.OpenedPages", opened)}";
        }

        private static async Task<bool> ConfirmBatchUninstallAsync(IReadOnlyList<ModuleUninstallPlan> plans, bool hasHeldChanges)
        {
            var body = new StackPanel { Spacing = 8 };

            body.Children.Add(new TextBlock
            {
                Text = Strings.Current.Plural(
                    "Environment.Multi.Uninstall.Dialog.Body",
                    plans.Count,
                    string.Join(Environment.NewLine, plans.Select(plan => plan.Describe()))),
                TextWrapping = TextWrapping.Wrap
            });

            if (hasHeldChanges)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Strings.Current["Environment.HeldChanges.WillBeLost"],
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var residue = plans.SelectMany(plan => plan.Residue).ToList();

            body.Children.Add(new TextBlock
            {
                Text = residue.Count == 0
                    ? Strings.Current["Environment.Uninstall.Dialog.NoResidue"]
                    : Strings.Current.Format("Environment.Uninstall.Dialog.ResidueList",
                        string.Join("\n", residue.Select(item => $"{item.Path} - {item.Detail}"))),
                TextWrapping = TextWrapping.Wrap
            });

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Plural("Environment.Multi.Uninstall.Dialog.Title", plans.Count),
                Content = new ScrollViewer { MaxHeight = 420, Content = body },
                PrimaryButtonText = Strings.Current["Environment.RecycleBinDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        public enum ModuleHandoff
        {
            NotLoaded,
            NotListed,
            Orphan,
            AlreadyDisabled,
            Disabled
        }

        // How the Mod Safety tab switches a mod off. It goes through the list exactly as ticking the
        // box here does, so it lands as a pending edit the user saves, undoes or throws away like any
        // other, and nothing held back for a running launcher is written over or dropped.
        //
        // Refresh is only reached when nothing is loaded yet, where there is nothing of theirs to lose.
        public async Task<ModuleHandoff> DisableModuleAsync(string moduleId, string source)
        {
            if (!IsLoaded)
            {
                await Refresh();

                if (!IsLoaded)
                    return ModuleHandoff.NotLoaded;
            }

            var row = Modules.FirstOrDefault(module =>
                module.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase));

            if (row is null)
                return ModuleHandoff.NotListed;

            if (row.IsOrphan)
                return ModuleHandoff.Orphan;

            if (!row.IsEnabled)
                return ModuleHandoff.AlreadyDisabled;

            RecordUndo(BuildEnvironment(), Strings.Current.Format("Environment.UndoDescription.DisableFromSource", row.DisplayName, source));
            row.IsEnabled = false;

            StatusMessage = Strings.Current.Format("Environment.DisableModule.SwitchedOff", row.DisplayName, source);

            return ModuleHandoff.Disabled;
        }

        [RelayCommand(CanExecute = nameof(CanPruneContextModule))]
        public void PruneContextModule()
        {
            if (ContextModule is not { IsOrphan: true } row)
                return;

            var before = BuildEnvironment();
            var pruned = before.WithoutOrphan(row.Entry.Id);

            if (pruned.Entries.Count == before.Entries.Count)
            {
                StatusMessage = Strings.Current.Format("Environment.PruneContext.StillListed", row.ModuleId);
                return;
            }

            RecordUndo(before, Strings.Current.Format("Environment.UndoDescription.RemoveOrphan", row.ModuleId));
            Apply(pruned);
            PersistOrder();
            StatusMessage = Strings.Current.Format("Environment.PruneContext.Removed", row.ModuleId);
        }

        // The module folder goes to the Recycle Bin, so an uninstall the user regrets is undone from
        // Explorer. Everything of the module's that lives elsewhere is named with its full path rather
        // than quietly left behind, and an official module is refused until its name has been typed.
        [RelayCommand(CanExecute = nameof(CanUninstallContextModule))]
        public async Task UninstallContextModuleAsync()
        {
            if (ContextModule?.Entry.Manifest is not { } manifest)
                return;

            var row = ContextModule;

            ContextModule = null;

            var plan = ModuleUninstaller.Plan(manifest, FindModuleResidue(manifest));

            // Asked before the uninstall dialog rather than refused after it: the answer is a different
            // action in a different program, so offering to delete the folder first would be offering
            // something that cannot work.
            if (plan.IsWorkshopSubscription)
            {
                StatusMessage = Strings.Current.Format("Environment.Uninstall.Subscribed", plan.Name);
                await AppendOpenedPagesAsync([(plan.Name, plan.UnsubscribePage)]);
                return;
            }

            var confirmation = await AskToUninstallAsync(plan, HasHeldChanges);

            if (confirmation is null)
            {
                StatusMessage = Strings.Current.Format("Environment.Uninstall.Kept", plan.Name);
                return;
            }

            var versionBefore = InstallHistoryLog.VersionBefore(plan);
            var report = ModuleUninstaller.Uninstall(plan, confirmation, RecycleFolder);

            if (!report.Removed)
            {
                LoggingService.Log($"Uninstall refused for {plan.Id}: {report.Message}", LogLevel.Warn);
                StatusMessage = report.Message;
                return;
            }

            InstallHistoryLog.Removed(plan, versionBefore);

            LoggingService.Log($"Uninstalled {plan.Id}: {report.Message}");

            ForgetLoadOrderEntry(plan.Id, report.Residue);

            // Offered, not applied. The files belong to settings the user may want to keep, so the
            // uninstall names them and the clearing is a separate deliberate press.
            pendingResidue = report.Residue;
            LeftoverSummary = report.Residue.Count == 0
                ? string.Empty
                : Strings.Current.Plural("Environment.Uninstall.LeftBehind", report.Residue.Count, plan.Name);

            await Refresh();

            StatusMessage = Strings.Current.Plural("Environment.Uninstall.Moved", plan.FileCount, plan.Name,
                ModuleUninstaller.DescribeSize(plan.SizeBytes), DescribeResidue(report.Residue), row.ModuleId);
        }

        // What the last uninstall left behind, kept so it can be acted on rather than only described.
        // Cleared the moment it is dealt with, so the button never outlives the thing it clears.
        private IReadOnlyList<ModuleResidue> pendingResidue = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLeftovers))]
        [NotifyCanExecuteChangedFor(nameof(ClearLeftoversCommand))]
        public partial string LeftoverSummary { get; set; } = string.Empty;

        public bool HasLeftovers => LeftoverSummary.Length > 0;

        // Accept the risk on what an uninstall left: the files stay, and the same summary is not shown again.
        private const string LeftoversCategory = "Leftovers";

        partial void OnLeftoverSummaryChanged(string value)
        {
            if (AcceptedNotices.IsAccepted(LeftoversCategory, value))
                LeftoverSummary = string.Empty;
        }

        [RelayCommand(CanExecute = nameof(HasLeftovers))]
        private void AcceptLeftovers()
        {
            StatusMessage = AcceptedNotices.Accept(LeftoversCategory, LeftoverSummary);
            pendingResidue = [];
            LeftoverSummary = string.Empty;
        }

        [RelayCommand(CanExecute = nameof(HasLeftovers))]
        public async Task ClearLeftoversAsync()
        {
            var report = ModuleUninstaller.RemoveResidue(pendingResidue, RecycleFolder);

            LoggingService.Log($"Cleared uninstall leftovers: {report.Describe()}");

            pendingResidue = [.. report.Failed.Count > 0 ? pendingResidue : []];
            LeftoverSummary = report.Failed.Count > 0 ? LeftoverSummary : string.Empty;
            StatusMessage = report.Describe();

            // A shadowed copy of a mod installed under Modules is very often the Workshop copy, so this is
            // where clearing leftovers would have reached Steam's folders without the user ever uninstalling
            // a Workshop module. Every copy Steam still holds is offered by its page, not just the first.
            await AppendOpenedPagesAsync([.. report.Subscribed.Select(item =>
                (System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(item.Path)),
                    ModulePageUrl.WorkshopUnsubscribePage(item.Path)))]);

            await Refresh();
        }

        // The launcher's entry for the module is never a leftover: nearly every installed mod has one,
        // so counting it made every uninstall report something left behind, which Clear Leftovers then
        // could not clear. The uninstall takes the entry out itself (ForgetLoadOrderEntry).
        private IReadOnlyList<ModuleResidue> FindModuleResidue(ModuleManifest manifest) =>
            ModuleUninstaller.FindResidue(manifest, ReviewSettingsFolders(), environment.Duplicates, loadOrderFilePath: null);

        // Unless another copy of the module is still installed: then the game still loads it, and the
        // entry is what keeps its place and its On or Off. A failure costs only a stale line in the
        // file, which the next save of the load order rewrites, so it is logged and never stops the
        // uninstall that already happened.
        private void ForgetLoadOrderEntry(ModuleId id, IReadOnlyList<ModuleResidue> residue)
        {
            if (residue.Any(item => item.Kind == ModuleResidueKind.AnotherCopy))
                return;

            try
            {
                if (store.Forget([id]) > 0)
                    LoggingService.Log($"Took {id}'s entry out of {store.FilePath}: the module is uninstalled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LoggingService.LogException(ex, $"Failed to take {id}'s entry out of the load order after its uninstall");
            }
        }

        // Attribution is worth an on-demand scan here: it is the only way the uninstall can name the
        // mod's settings folder, and a settings folder BEM cannot attribute is never claimed for it.
        private IReadOnlyList<SettingsFolderReview> ReviewSettingsFolders()
        {
            try
            {
                var folders = ModSettingsScanner.Scan(ModSettingsScanner.DefaultRootFor(ActiveDataRoot));
                var modules = Modules
                    .Select(module => module.Entry.Manifest)
                    .OfType<ModuleManifest>()
                    .ToList();

                return OrphanSettingsFinder.Review(folders, modules);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to review mod settings folders before an uninstall");
                return [];
            }
        }

        private static string DescribeResidue(IReadOnlyList<ModuleResidue> residue) =>
            residue.Count == 0
                ? Strings.Current["Environment.Residue.None"]
                : Strings.Current.Plural("Environment.Residue.Some", residue.Count, string.Join("; ", residue.Select(item => item.Path)));

        // Null means the user said no. An official module also has to be named back, so a mis-click on
        // Native cannot take the base game out.
        // The refusal and the way out of it in one place, for one Workshop module or several: what happened,
        // why deleting the folders will not do it, and every page whose Unsubscribe button will, opened
        // together in the default browser on one press. A module with no page address is still named, and
        // the button is dropped only when none of them has one.
        private async Task<(int Opened, int Failed)> OfferWorkshopPagesAsync(IReadOnlyList<(string Name, string? Page)> modules)
        {
            if (modules.Count == 0)
                return (0, 0);

            var pages = modules
                .Select(module => module.Page)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.Uninstall.Workshop.Title"],
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock
                    {
                        Text = Strings.Current.Plural(
                            "Environment.Uninstall.Workshop.PagesBody",
                            modules.Count,
                            string.Join(Environment.NewLine, modules.Select(module => module.Name))),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                },
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (pages.Count > 0)
            {
                dialog.PrimaryButtonText = Strings.Current.Plural("Environment.Uninstall.Workshop.OpenPages", pages.Count);
                dialog.DefaultButton = ContentDialogButton.Primary;
            }

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary || pages.Count == 0)
                return (0, 0);

            var opened = 0;

            foreach (var page in pages)
            {
                if (Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }, Strings.Current.Format("Environment.Opened", page)))
                    opened++;
            }

            return (opened, pages.Count - opened);
        }

        private static async Task<string?> AskToUninstallAsync(ModuleUninstallPlan plan, bool hasHeldChanges)
        {
            var body = new StackPanel { Spacing = 8 };

            body.Children.Add(new TextBlock
            {
                Text = Strings.Current.Format("Environment.Uninstall.Dialog.DescribeAndRecycle", plan.Describe()),
                TextWrapping = TextWrapping.Wrap
            });

            if (hasHeldChanges)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Strings.Current["Environment.HeldChanges.WillBeLost"],
                    TextWrapping = TextWrapping.Wrap
                });
            }

            body.Children.Add(new TextBlock
            {
                Text = plan.Residue.Count == 0
                    ? Strings.Current["Environment.Uninstall.Dialog.NoResidue"]
                    : Strings.Current.Format("Environment.Uninstall.Dialog.ResidueList",
                        string.Join("\n", plan.Residue.Select(item => $"{item.Path} - {item.Detail}"))),
                TextWrapping = TextWrapping.Wrap
            });

            var typed = new TextBox { PlaceholderText = plan.Name };

            if (plan.IsOfficial)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Strings.Current.Format("Environment.Uninstall.Dialog.OfficialWarning", plan.Name),
                    TextWrapping = TextWrapping.Wrap
                });

                body.Children.Add(typed);
            }

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.Uninstall.Dialog.Title", plan.Name),
                Content = new ScrollViewer { MaxHeight = 420, Content = body },
                PrimaryButtonText = Strings.Current["Environment.RecycleBinDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return null;

            return plan.IsOfficial ? typed.Text : plan.Name;
        }

        // A folder under Modules with no SubModule.xml in it. It goes to the Recycle Bin and never
        // anywhere less recoverable, because a folder holding only a log file may still be the remains
        // of a mod the user wants back, and BEM cannot know which.
        //
        // The load order is deliberately untouched. Where the module is installed elsewhere the entry is
        // correct and clearing it would turn off a working mod; where it is not, the entry is already
        // reported separately with its own Fix, and doing both from one press would take two decisions
        // on one click.
        public async Task RemoveFolderWithoutManifestAsync(ModuleFolderWithoutManifest folder)
        {
            ArgumentNullException.ThrowIfNull(folder);

            // A Workshop item still downloading has no SubModule.xml yet and lands in this list, and so does
            // what an unsubscribed item left behind. Only the first is Steam's; the second is removed like
            // any other leftovers.
            if (WorkshopContent.Owns(folder.FolderPath))
            {
                StatusMessage = Strings.Current.Format("Core.Modules.Uninstall.SubscribedFolder", folder.FolderPath);

                await AppendOpenedPagesAsync([(
                    System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder.FolderPath)),
                    ModulePageUrl.WorkshopUnsubscribePage(folder.FolderPath))]);

                return;
            }

            if (!await ConfirmRemoveFolderAsync(folder))
            {
                StatusMessage = Strings.Current.Format("Environment.RemoveFolder.Kept", folder.FolderPath);
                return;
            }

            ModuleUninstallReport report;

            try
            {
                report = ModuleUninstaller.RemoveFolderWithoutManifest(folder, RecycleFolder);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = Strings.Current.Format("Environment.RemoveFolder.Kept", folder.FolderPath);
                return;
            }

            if (!report.Removed)
            {
                LoggingService.Log($"Folder removal refused for {folder.FolderPath}: {report.Message}", LogLevel.Warn);
                StatusMessage = report.Message;
                return;
            }

            LoggingService.Log($"Removed folder with no manifest: {report.Message}");

            await Refresh();

            StatusMessage = Strings.Current.Format("Environment.RemoveFolder.Moved", folder.FolderPath);
        }

        private async Task<bool> ConfirmRemoveFolderAsync(ModuleFolderWithoutManifest folder)
        {
            var body = new StackPanel { Spacing = 8 };

            var named = folder.FileNames.Count == 0
                ? string.Empty
                : Strings.Current.Format("Environment.RemoveFolder.Holds", string.Join(", ", folder.FileNames))
                    + (folder.FileCount > folder.FileNames.Count ? Strings.Current["Environment.RemoveFolder.AndMore"] : ".");

            body.Children.Add(new TextBlock
            {
                Text = Strings.Current.Format("Environment.RemoveFolder.Dialog.Body", folder.FolderPath, named),
                TextWrapping = TextWrapping.Wrap
            });

            body.Children.Add(new TextBlock
            {
                Text = Strings.Current["Environment.RemoveFolder.Dialog.Reinstall"],
                TextWrapping = TextWrapping.Wrap
            });

            if (HasHeldChanges)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Strings.Current["Environment.HeldChanges.WillBeLost"],
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.RemoveFolder.Dialog.Title", folder.FolderName),
                Content = new ScrollViewer { MaxHeight = 420, Content = body },
                PrimaryButtonText = Strings.Current["Environment.RecycleBinDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.RecycleBinDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // The last net. Every route above refuses a Workshop folder by name; this is the one call that
        // would carry out the removal if a new one ever forgot to ask.
        private static void RecycleFolder(string path)
        {
            WorkshopContent.Refuse(path);
            RecycleFolderCore(path);
        }

        private static void RecycleFolderCore(string path) =>
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

#if DEV_BEM
        // Notepad++ is what a modder actually reads SubModule.xml in, but it is not always installed,
        // so the shell default (usually Notepad) takes over when neither path is there. Here rather
        // than beside Start because the manifest item is its only reader.
        private static readonly string[] TextEditors =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe")
        ];
#endif

        private bool Start(System.Diagnostics.ProcessStartInfo startInfo, string success)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(startInfo);
                StatusMessage = success;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LoggingService.LogException(ex, "Failed to open a module path");
                StatusMessage = Strings.Current.Format("Environment.OpenPath.Failed", startInfo.FileName, ex.Message);
                return false;
            }
        }

        // One step is a position in the whole list, not in the filtered view, so the row can step past
        // a hidden neighbor and appear not to have moved. The status line names its new position for
        // that reason.
        private void MoveByOne(ModuleRowViewModel? row, int offset)
        {
            if (row is null)
                return;

            var before = Modules.IndexOf(row);

            Move(row, offset);

            if (Modules.IndexOf(row) == before)
                return;

            StatusMessage = Strings.Current.Format(
                offset < 0 ? "Environment.MoveModule.Up" : "Environment.MoveModule.Down",
                row.DisplayName, row.Position);
            ContextModule = null;
        }

        private void MoveTo(ModuleRowViewModel? row, int target)
        {
            if (row is null)
                return;

            var index = Modules.IndexOf(row);

            if (index < 0 || target < 0 || target >= Modules.Count || index == target)
                return;

            Modules.Move(index, target);
            OnOrderChangedByUser();
            RefreshVisibleModules();

            StatusMessage = Strings.Current.Format(
                target == 0 ? "Environment.MoveModule.ToTop" : "Environment.MoveModule.ToBottom", row.DisplayName);
            ContextModule = null;
        }

        public void OnOrderChangedByUser()
        {
            var current = BuildEnvironment();

            // environment still holds the order as it was before the move, because only Apply replaces
            // it, so it is what the proposed order is measured against.
            if (!PermitManualLoadOrderOverride
                && LoadOrderDrag.Introduced(environment.Entries, current.Entries) is { Count: > 0 } introduced)
            {
                RestoreOrder(current);

                StatusMessage = Strings.Current.Format("Environment.MoveRefused", LoadOrderDrag.Describe(introduced));
                return;
            }

            RecordUndo(standing, Strings.Current["Environment.UndoDescription.ThatMove"]);
            standing = current;

            Renumber();
            RefreshPins();
            ClearSortExplanation();
            ClearPreflight();

            // The list no longer follows the last sort, so the keys stop claiming it does.
            if (!sortPlan.IsEmpty)
            {
                sortPlan = sortPlan.Cleared();
                RefreshSort();
            }

            PersistOrder(current);
            RefreshIssues(current);
        }

        // Puts the order back without discarding anything else the user has changed: the enabled states
        // come from the list as it stands and only the positions are taken from before the move.
        private void RestoreOrder(ModuleEnvironment current)
        {
            var wasAt = new Dictionary<ModuleId, int>();

            for (var i = 0; i < environment.Entries.Count; i++)
                wasAt[environment.Entries[i].Id] = i;

            Apply(current.WithEntries(
                [.. current.Entries.OrderBy(e => wasAt.TryGetValue(e.Id, out var at) ? at : int.MaxValue)]));

            PersistOrder();
        }

        public ModuleEnvironment BuildEnvironment() =>
            environment.WithEntries([.. Modules.Select(m => m.ToEntry())]);

        private static List<(string Id, bool IsEnabled)> Signature(IReadOnlyList<ModuleEntry> entries) =>
            [.. entries.Select(e => (e.Id.Value, e.IsEnabled))];

        private void MarkSaved(ModuleEnvironment saved)
        {
            savedOrder = Signature(saved.Entries);
            HasHeldChanges = false;
        }

        public event EventHandler? LoadOrderChanged;

        private void PersistOrder() => PersistOrder(BuildEnvironment());

        // LauncherData.xml is the load order, so BEM keeps it current instead of holding a copy and
        // asking to be told when to commit it. There is no pending state and nothing to press.
        //
        // One thing holds a write back, and it is a real one: the vanilla launcher and LauncherEx write
        // this file themselves when they close, so writing underneath a running launcher would be
        // silently overwritten. The change is held and goes out as soon as they are gone.
        //
        // An order carrying errors is written like any other. A toggle that quietly did not persist
        // would be a control that does nothing, and a file that disagrees with the screen is worse than
        // one that matches a state the screen already flags.
        private void PersistOrder(ModuleEnvironment current)
        {
            if (!IsLoaded)
                return;

            SaveLoadOrderChoices(loadOrderChoices.WithoutContradictedBy(current.Entries));

            if (Signature(current.Entries).SequenceEqual(savedOrder))
            {
                HasHeldChanges = false;
                return;
            }

            // Health reruns its checks shortly after any change to the order.
            LoadOrderChanged?.Invoke(this, EventArgs.Empty);

            QueueStylesheetCheck();

            if (RunningGame.AnyGameProcessRunning())
            {
                HasHeldChanges = true;
                StatusMessage = Strings.Current["Environment.PersistOrder.Held"];
                return;
            }

            try
            {
                // A dated backup once per burst of editing, not once per change. The copy worth keeping
                // is the order as it was before this run of edits started.
                store.Write(current.ToLauncherEntries(), hasBackedUpThisBurst ? null : Strings.Current["Environment.Backup.BeforeEditsReason"]);

                hasBackedUpThisBurst = true;
                savedOrder = Signature(current.Entries);
                HasHeldChanges = false;

                // Writing silently is not the same as writing. With no Save button to press there is
                // nothing for the user to have done, so the confirmation has to come from BEM: the file,
                // the count and the time it went out, plus a line in the log for afterwards.
                var enabled = current.Entries.Count(entry => entry.IsEnabled);

                LastWrittenText = Strings.Current.Plural("Environment.LastWrittenText", current.Entries.Count,
                    DateTime.Now.ToString("HH:mm:ss"), enabled);

                LoggingService.Log(
                    $"Wrote the load order to {store.FilePath}: {enabled} of {current.Entries.Count} enabled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidDataException or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, "Failed to write LauncherData.xml");
                HasHeldChanges = true;
                StatusMessage = Strings.Current.Format("Environment.WriteLoadOrder.Failed", ex.Message);
            }
        }

        private void Move(ModuleRowViewModel? row, int offset)
        {
            if (row is null)
                return;

            var index = Modules.IndexOf(row);
            var target = index + offset;

            if (index < 0 || target < 0 || target >= Modules.Count)
                return;

            Modules.Move(index, target);
            OnOrderChangedByUser();
            RefreshVisibleModules();
        }

        // The live-watching half of Play's refresh: without this, a module installed or removed by
        // hand, by another tool, or by BEM before this Refresh() ever ran once against the active
        // instance stayed invisible until the app restarted. StartWatchingModulesFolder is called from
        // Refresh() itself, so it always follows whatever install Refresh() just resolved.
        private void StartWatchingModulesFolder(string modulesFolder)
        {
            // EnableRaisingEvents is checked, not just the path: a watcher can go quiet without ever
            // raising Error (the folder it watches is deleted and recreated faster than anything here
            // observes), and matching only on the path would leave that dead watcher in place with live
            // refresh silently off for the rest of the session.
            if (modulesFolderWatcher is not null
                && modulesFolderWatcher.EnableRaisingEvents
                && string.Equals(watchedModulesFolder, modulesFolder, StringComparison.OrdinalIgnoreCase))
                return;

            StopWatchingModulesFolder();

            try
            {
                modulesFolderChangeDebouncer = new ModulesFolderChangeDebouncer(ModulesFolderQuietPeriod, OnModulesFolderSettled);

                modulesFolderWatcher = new FileSystemWatcher
                {
                    Path = modulesFolder,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                modulesFolderWatcher.Created += OnModulesFolderChanged;
                modulesFolderWatcher.Deleted += OnModulesFolderChanged;
                modulesFolderWatcher.Renamed += OnModulesFolderChanged;
                modulesFolderWatcher.Changed += OnModulesFolderChanged;
                modulesFolderWatcher.Error += OnModulesFolderWatcherError;

                watchedModulesFolder = modulesFolder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A folder that exists at the Directory.Exists check above but cannot be watched (the
                // instance was deactivated between the two, or the drive dropped) leaves Play exactly
                // where it was before this fix: refreshed once, just not watched continuously.
                LoggingService.LogException(ex, $"Could not watch the Modules folder for live changes: {modulesFolder}");
                StopWatchingModulesFolder();
            }
        }

        private void StopWatchingModulesFolder()
        {
            if (modulesFolderWatcher is not null)
            {
                modulesFolderWatcher.EnableRaisingEvents = false;
                modulesFolderWatcher.Created -= OnModulesFolderChanged;
                modulesFolderWatcher.Deleted -= OnModulesFolderChanged;
                modulesFolderWatcher.Renamed -= OnModulesFolderChanged;
                modulesFolderWatcher.Changed -= OnModulesFolderChanged;
                modulesFolderWatcher.Error -= OnModulesFolderWatcherError;
                modulesFolderWatcher.Dispose();
                modulesFolderWatcher = null;
            }

            modulesFolderChangeDebouncer?.Dispose();
            modulesFolderChangeDebouncer = null;
            rescanGate.ChangeDropped();
            watchedModulesFolder = null;
        }

        private void OnModulesFolderChanged(object sender, FileSystemEventArgs e) => NoteModulesFolderChange();

        // The window opens at the first event, not when the rescan starts, so a Launch pressed during the
        // debouncer's quiet period waits for the rescan instead of reading a list the change has outdated.
        private void NoteModulesFolderChange()
        {
            if (modulesFolderChangeDebouncer is not { } debouncer)
                return;

            rescanGate.ChangeSeen();
            debouncer.Notify();
        }

        // The watcher's own buffer can overflow when many files land at once (an archive extracting
        // hundreds of files, or several installs finishing together), and the watch dies outright if the
        // folder itself is renamed, deleted, or its drive disappears out from under it - an instance can
        // be deactivated while BEM keeps running. Either way, nothing here can tell what was missed, so
        // the only correct response is to treat it exactly like a real change and rescan once.
        private void OnModulesFolderWatcherError(object sender, ErrorEventArgs e)
        {
            LoggingService.LogException(
                e.GetException(), "The Modules folder watcher lost events or stopped; rescanning to reconcile");
            NoteModulesFolderChange();
        }

        // Runs on the debouncer's own timer thread, never the UI thread that owns the Modules
        // collection Refresh() writes to; marshaled through the dispatcher the same way
        // InstallViewModel's own archive-folder watcher already does, so this is the one place a WinUI
        // thread rule from that pattern had to be repeated rather than invented.
        private void OnModulesFolderSettled()
        {
            App.AppWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // A held change (PersistOrder refused to write because the game or its launcher is
                    // running) is exactly the state RestoreBackup already refuses to reload over: the
                    // reload this triggers would silently throw away a toggle the user made and has not
                    // yet been able to save, and clear the flag that would have told them so. Skipped
                    // rather than deferred by a timer: there is no signal here for when the hold clears
                    // short of polling. Instead, the miss is recorded and OnHasHeldChangesChanged below
                    // catches up the moment the flag itself goes false - the earliest honest point,
                    // whether that comes from the user's own next edit, Refresh, or a discard.
                    if (HasHeldChanges)
                    {
                        rescanGate.ChangeDropped();
                        heldChangeCatchUp.RecordMissedWhileHeld();
                        LoggingService.Log(
                            "Skipped an automatic Play refresh: a load-order change is still held for a "
                            + "running launcher.");
                        return;
                    }

                    await RefreshFromWatcherAsync();
                }
                catch (Exception ex)
                {
                    rescanGate.ChangeDropped();
                    LoggingService.LogException(ex, "Failed to refresh Play after a Modules folder change");
                }
            });
        }

        private void Unload(string message)
        {
            IsLoaded = false;
            undoStack.Clear();
            RefreshUndo();
            Apply(ModuleEnvironment.Empty);
            MarkSaved(ModuleEnvironment.Empty);
            StatusMessage = message;
        }

        // Everything Apply reads from disk, gathered on a worker so the UI thread only assigns it. A scan
        // and the rows it builds are cheap; the stores, the manager detection and the game version read
        // are not, and one Apply spent 15.8 s on the UI thread on 2026-11-12.
        private sealed record ApplyReads(
            string Key,
            long TakenAt,
            IReadOnlyDictionary<string, ModNote> Notes,
            IReadOnlySet<string> NotOnNexusIds,
            IReadOnlySet<string> BuiltLocallyIds,
            IReadOnlyDictionary<string, ModuleManagement>? Managed,
            IReadOnlyList<NexusModuleLink> Links,
            IReadOnlySet<string> NoPublishedFile,
            IReadOnlyDictionary<int, string> Categories,
            IReadOnlySet<string> AcceptedKeys,
            string? LastLaunchedVersion,
            ModuleVersion CurrentGameVersion);

        private ApplyReads? lastApplyReads;

        private const long ApplyReadsLifetimeMs = 60_000;

        private static string ApplyReadsKey(ModuleEnvironment updated) =>
            string.Join('|', updated.Entries.Select(entry => entry.Id.Value + "@" + entry.Manifest?.FolderPath));

        // Worker-thread safe: touches no observable collection and no row.
        private ApplyReads ReadApplyReads(ModuleEnvironment updated)
        {
            var manifests = updated.Entries.Select(entry => entry.Manifest).OfType<ModuleManifest>().ToList();
            var links = NexusLinks(manifests);

            var noPublishedFile = NexusAbsence.ModulesWithUnpublishedArchive(
                links, ModuleArchiveLinkStore.For(ActiveDataRoot).ByModuleId(),
                UnrecognizedArchiveHashStore.Default.Remembered(DateTimeOffset.UtcNow));

            string? lastLaunched = null;
            var currentVersion = default(ModuleVersion);

            try
            {
                if (!string.IsNullOrWhiteSpace(GameInstallPath))
                {
                    lastLaunched = launchSettingsStore.Load().LastLaunchedVersion;
                    currentVersion = GameVersionReader.Read(GameInstallPath);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to check for a game update");
            }

            return new ApplyReads(
                ApplyReadsKey(updated),
                Environment.TickCount64,
                UserNotes.Load(),
                notOnNexus.ByModuleId().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                builtLocally.ByModuleId().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                DetectManagers(updated.Entries.Where(entry => !entry.IsOfficial && entry.Manifest is not null)
                    .Select(entry => (entry.Manifest!.FolderPath, entry.Id.Value))),
                links,
                noPublishedFile,
                NexusVersionStore.Default.Load().CategoryNamesByModId(),
                acceptedRisks.LoadKeys(),
                lastLaunched,
                currentVersion);
        }

        // What a worker gathered for exactly this module set a moment ago stands; anything else is read now.
        private ApplyReads ApplyReadsFor(ModuleEnvironment updated)
        {
            if (lastApplyReads is { } held
                && Environment.TickCount64 - held.TakenAt < ApplyReadsLifetimeMs
                && held.Key == ApplyReadsKey(updated))
                return held;

            return lastApplyReads = ReadApplyReads(updated);
        }

        private void Apply(ModuleEnvironment updated, ApplyReads? reads = null)
        {
            using var stallTag = Core.Diagnostics.UiStallMonitor.Op("Play: apply the module list");
            reads ??= ApplyReadsFor(updated);
            var cursor = System.Diagnostics.Stopwatch.StartNew();
            environment = updated;
            standing = updated;

            // A selection belongs to the install it was made in, and this method serves both a rescan
            // of the same one and a switch to another instance. The ids it carries across repeat in
            // every instance, so left alone a switch would land in the new version with the previous
            // one's Harmony, ButterLib and UIExtenderEx already selected (see SelectionScope).
            var sameInstall = string.Equals(selectionScope, GameInstallPath, StringComparison.OrdinalIgnoreCase);

            if (!SelectionScope.Carries(selectionScope, GameInstallPath))
            {
                foreach (var row in Modules.Where(row => row.IsUiSelected))
                    row.IsUiSelected = false;

                SelectionAnchor = null;
                selectionScope = GameInstallPath;
            }

            // The rows carry immutable Entry records, so record equality says exactly whether the
            // list on screen still describes this environment. When nothing changed (a plain reload or
            // fresh read of the same disk state), tearing down and re-adding every row to the bound
            // collection is the whole cost - hundreds of collection notifications for identical rows -
            // and none of it is necessary. Skip that, and only refresh the derived state that can have
            // moved (tiers, issues, pips, the command line).
            if (!RowsMatchCurrent(updated))
            {
                ContextModule = null;
                nexusModIds = null;
                ClearSortExplanation();

                if (!sameInstall || !updated.HasSameLoadOrder([.. Modules.Select(row => row.ToEntry())]))
                    ClearPreflight();

                // The rows are replaced, so the multi-select highlight would be replaced with them and
                // a batch action would silently stand its own selection down as it finished. The ids
                // carry across instead, which is what lets one selection take a second action.
                var selected = Modules.Where(row => row.IsUiSelected).Select(row => row.Entry.Id).ToHashSet();
                var anchorId = SelectionAnchor?.Entry.Id;

                foreach (var row in Modules)
                    row.PropertyChanged -= OnModuleRowPropertyChanged;

                Modules.Clear();
                SelectionAnchor = null;

                foreach (var entry in updated.Entries)
                {
                    var row = new ModuleRowViewModel(entry)
                    {
                        ShowNotes = ShowNotes,
                        IsUiSelected = selected.Contains(entry.Id)
                    };

                    if (anchorId is { } held && entry.Id == held)
                        SelectionAnchor = row;

                    row.PropertyChanged += OnModuleRowPropertyChanged;
                    Modules.Add(row);
                }
            }

            Renumber();
            RefreshPins(reads);
            RefreshNotOnNexusMarks(reads);
            RefreshNexusIdMarks(reads);
            RefreshManagerMarks(reads);
            RefreshTiers(updated);
            RefreshIssues(updated, reads);
            RefreshVisibleModules();
            UpdateLaunchTargetText(updated);
            RefreshGameUpdateNotice(updated, reads);

            LogPerf("Apply", cursor.ElapsedMilliseconds, updated.Entries.Count);
        }

        // Steam updated the game since the last launch BEM saw: shown on Play until the player turns the
        // listed mods off or goes on. Dismissing remembers the version, so it reappears only for the next update.
        [ObservableProperty]
        public partial bool HasGameUpdateNotice { get; set; }

        [ObservableProperty]
        public partial string GameUpdateHeading { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GameUpdateModList { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasGameUpdateMods { get; set; }

        private GameUpdateNotice? gameUpdateNotice;

        // Re-read the game folder when the window comes to the front, so what Vortex, MO2 or Novus did
        // while BEM sat in the background is on Play at once. The scan itself runs off the UI thread
        // (RefreshOnceAsync), a refresh within five seconds of the last one is skipped, and nothing
        // runs while a launch, a dry run or a held load-order change owns the state.
        private readonly RefreshThrottle focusRefresh = new(RefreshThrottle.DefaultInterval);

        // The window is activated once while the shell is still starting; the first load is its own.
        private bool refreshedOnce;

        public async Task RefreshOnFocusAsync()
        {
            if (!refreshedOnce || Phase != LaunchPhase.Idle || IsDryRunning || instanceManager.ActivationHeldHere)
                return;

            if (HasHeldChanges)
            {
                heldChangeCatchUp.RecordMissedWhileHeld();
                return;
            }

            if (!focusRefresh.TryBegin())
                return;

            try
            {
                await RefreshFromWatcherAsync();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to refresh Play when the window was activated");
            }
            finally
            {
                focusRefresh.Finish();
            }
        }

        // "Keep BEM Watching": off until the player turns it on. On, closing the window leaves a tray
        // icon, and every game start, whoever made it, gets the same preparation BEM's own Launch does.
        private readonly TrayWatcherSettingsStore trayWatcherStore = new();

        [ObservableProperty]
        public partial bool KeepWatchingEnabled { get; set; } = new TrayWatcherSettingsStore().Read().Enabled;

        public event EventHandler? KeepWatchingChanged;

        partial void OnKeepWatchingEnabledChanged(bool value)
        {
            try
            {
                var settings = trayWatcherStore.Read();

                // Choosing either way settles the one-time offer.
                trayWatcherStore.Write(settings with { Enabled = value, OfferMade = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the Keep BEM Watching setting");
            }

            KeepWatchingChanged?.Invoke(this, EventArgs.Empty);
        }

        public TrayWatcherSettings TrayWatcher => trayWatcherStore.Read();

        // The player's answer to the one-time offer: yes turns the watcher on, no only records that it was asked.
        public void SettleWatcherOffer(bool turnOn)
        {
            if (turnOn)
            {
                KeepWatchingEnabled = true;
                return;
            }

            try
            {
                trayWatcherStore.Write(trayWatcherStore.Read() with { OfferMade = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the Keep BEM Watching offer");
            }
        }

        // A game BEM itself started (its Launch, a dry run, a bisection) holds the activation lock or
        // the launch phase; anything else running is somebody else's launch.
        public bool BemOwnsTheRunningGame =>
            Phase != LaunchPhase.Idle || IsDryRunning || instanceManager.ActivationHeldHere;

        // What the tray watcher does for a game started somewhere else: the steps Launch runs, then the
        // same crash-artifact capture, through the same StartCapturingArtifacts.
        // One click registers this game folder with Mod Organizer 2, as an instance in the folder its
        // own instance manager lists. MO2 is never launched.
        public ManagerRegistrationResult RegisterWithMo2()
        {
            if (string.IsNullOrWhiteSpace(GameInstallPath))
                return new ManagerRegistrationResult(false, Strings.Current["Core.Modules.Managers.NoGame"]);

            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");

            return ManagerRegistration.RegisterWithMo2(root, "Bannerlord (BEM)", GameInstallPath);
        }

        // One click writes the enabled load order as a Novus Launcher preset, in the Novus folder found
        // beside the game or running now. Novus is never launched.
        public ManagerRegistrationResult RegisterWithNovus()
        {
            if (string.IsNullOrWhiteSpace(GameInstallPath))
                return new ManagerRegistrationResult(false, Strings.Current["Core.Modules.Managers.NoGame"]);

            var candidates = new List<string>();

            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("NovusLauncher"))
                {
                    using (process)
                    {
                        try
                        {
                            if (System.IO.Path.GetDirectoryName(process.MainModule?.FileName) is { } running)
                                candidates.Add(running);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            candidates.Add(GameInstallPath);
            candidates.Add(System.IO.Path.Combine(GameInstallPath, "bin", "Win64_Shipping_Client"));
            candidates.Add(System.IO.Path.Combine(GameInstallPath, "NovusLauncher"));

            var modules = EnabledForLaunch()
                .Where(e => e.Manifest is not null)
                .Select(e => (e.Id.Value, e.Manifest!.VersionText ?? e.Manifest.Version.ToString()));

            return ManagerRegistration.RegisterWithNovus(
                ManagerRegistration.FindNovus(candidates), "Bannerlord (BEM)", modules);
        }

        // Set by the app shell to raise a tray notification; null when there is no tray.
        public Action<string, string>? ForeignLaunchAdvice { get; set; }

        public async Task PrepareForeignLaunchAsync(string processName)
        {
            if (string.IsNullOrWhiteSpace(GameInstallPath))
                return;

            await PreLaunchRunner.RunAsync(GameInstallPath);

            // The same preflight BEM's own Launch runs, for a game Vortex, MO2, Novus or Steam started.
            // The game is already starting, so this never blocks it: findings show on Play and, when any
            // would gate a launch, one tray notification says so.
            try
            {
                var report = await RunPreflightAsync();
                ShowPreflight(report);

                if (ForeignLaunchNotice.For(report) is { } notice)
                    ForeignLaunchAdvice?.Invoke(Strings.Current["Tray.Foreign.Title"], notice);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "The preflight for a game started outside BEM failed");
            }

            StartCapturingArtifacts(
                DryRunPaths.NewRunId(), Strings.Current.Format("Environment.Launch.CaptureLabel", processName));
        }

        [ObservableProperty]
        public partial bool HasManagerDeployment { get; set; }

        [ObservableProperty]
        public partial string ManagerDeploymentText { get; set; } = string.Empty;

        // Another manager's deployed files are active in this game folder: said on Play so a purge or
        // deploy there is never a surprise. Read-only; BEM never touches the other manager's records.
        private async Task ShowManagerDeploymentAsync(IReadOnlyList<ModuleManifest> modules)
        {
            try
            {
                var gameRoot = GameInstallPath;
                var found = await Task.Run(() => ActiveManagerDeployment.Find(
                    gameRoot,
                    modules.Select(m => new ModuleToLabel(System.IO.Path.GetFileName(m.FolderPath.TrimEnd('\\', '/')), m.Id.Value)),
                    ModManagerDetector.DefaultMo2Roots()));

                var lines = new List<string>();

                if (found.VortexModules > 0)
                    lines.Add(Strings.Current.Plural("Environment.ManagerActive.Vortex", found.VortexModules));

                if (found.Mo2Modules > 0)
                    lines.Add(Strings.Current.Plural("Environment.ManagerActive.Mo2", found.Mo2Modules));

                ManagerDeploymentText = string.Join(" ", lines);
                HasManagerDeployment = found.IsActive;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                HasManagerDeployment = false;
            }
        }

        // The restorable copies of Steam Workshop modules, kept only while the setting is on.
        public WorkshopKeep WorkshopKeep { get; } = new(
            System.IO.Path.Combine(AppDataRoot.Folder, "workshop-keep"),
            () => new WorkshopKeepSettingsStore().Read().Enabled);

        [ObservableProperty]
        public partial bool WorkshopKeepEnabled { get; set; } = new WorkshopKeepSettingsStore().Read().Enabled;

        partial void OnWorkshopKeepEnabledChanged(bool value)
        {
            try
            {
                new WorkshopKeepSettingsStore().Write(new WorkshopKeepSettings(value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the Workshop keep setting");
            }
        }

        [ObservableProperty]
        public partial bool HasMissingWorkshopCopies { get; set; }

        [ObservableProperty]
        public partial string MissingWorkshopCopiesText { get; set; } = string.Empty;

        private IReadOnlyList<KeptWorkshopModule> missingWorkshopCopies = [];

        // After each scan: copy what changed (off the UI thread, only with the setting on), then see
        // which kept modules have lost their Workshop folder.
        private async Task KeepWorkshopModulesAsync(IReadOnlyList<ModuleManifest> modules, string modulesFolder)
        {
            try
            {
                var missing = await Task.Run(() =>
                {
                    if (WorkshopKeep.IsEnabled)
                    {
                        foreach (var module in modules.Where(WorkshopContent.CameFromWorkshop))
                            WorkshopKeep.Keep(module);
                    }

                    return WorkshopKeep.Missing(modulesFolder);
                });

                missingWorkshopCopies = missing;
                HasMissingWorkshopCopies = missing.Count > 0;
                MissingWorkshopCopiesText = string.Join(", ", missing.Select(k => k.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to keep Workshop modules");
            }
        }

        [RelayCommand]
        public async Task RestoreWorkshopCopiesAsync()
        {
            var modulesFolder = ModuleScanner.GetModulesFolder(GameInstallPath);

            try
            {
                await Task.Run(() =>
                {
                    foreach (var kept in missingWorkshopCopies)
                        WorkshopKeep.Restore(kept, modulesFolder);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to restore a kept Workshop module");
                StatusMessage = ex.Message;
            }

            await Refresh();
        }

        private void RefreshGameUpdateNotice(ModuleEnvironment source, ApplyReads? reads = null)
        {
            GameUpdateNotice? notice = null;

            try
            {
                if (reads is not null)
                    notice = string.IsNullOrWhiteSpace(GameInstallPath)
                        ? null
                        : GameUpdateNotice.Evaluate(reads.LastLaunchedVersion, reads.CurrentGameVersion, source.Entries);
                else if (!string.IsNullOrWhiteSpace(GameInstallPath))
                    notice = GameUpdateNotice.Evaluate(
                        launchSettingsStore.Load().LastLaunchedVersion,
                        GameVersionReader.Read(GameInstallPath),
                        source.Entries);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to check for a game update");
            }

            gameUpdateNotice = notice;
            HasGameUpdateNotice = notice is not null;
            HasGameUpdateMods = notice is { OutdatedMods.Count: > 0 };
            GameUpdateHeading = notice is null
                ? string.Empty
                : Strings.Current.Format("Core.Launcher.GameUpdate.Heading", notice.PreviousVersion, notice.CurrentVersion);
            GameUpdateModList = notice is null
                ? string.Empty
                : string.Join(", ", notice.OutdatedMods.Select(m => m.DisplayName));
        }

        [RelayCommand]
        public void TurnOffGameUpdateMods()
        {
            if (gameUpdateNotice is null)
                return;

            var ids = gameUpdateNotice.OutdatedMods.Select(m => m.Id).ToHashSet();

            foreach (var row in Modules.Where(r => !r.IsOrphan && ids.Contains(r.Entry.Id)))
                row.IsEnabled = false;

            DismissGameUpdate();
        }

        [RelayCommand]
        public void GoOnAfterGameUpdate() => DismissGameUpdate();

        private void DismissGameUpdate()
        {
            try
            {
                if (gameUpdateNotice is { } notice)
                    launchSettingsStore.RecordLaunchedVersion(notice.CurrentVersion.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to record the game version");
            }

            gameUpdateNotice = null;
            HasGameUpdateNotice = false;
        }

        private static void LogPerf(string label, long elapsedMilliseconds, int entries)
        {
            if (elapsedMilliseconds < 20)
                return;

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bem_perf.log"),
                    $"[P] {DateTime.Now:HH:mm:ss.fff} {label} elapsed={elapsedMilliseconds}ms thread=UI entries={entries}\r\n");
            }
            catch
            {
            }
        }

        // What counts as a change lives in Core, on the environment itself, so it can be stated once
        // and tested: see ModuleEnvironment.DescribesSameRows. Each row's Entry is the record it was
        // built from, so a tick since then is only in ToEntry; comparing Entry made an Undo, or a
        // profile or backup restoring the order as it was loaded, look like no change and leave the
        // ticked boxes on screen and in the file.
        private bool RowsMatchCurrent(ModuleEnvironment updated) =>
            updated.DescribesSameRows([.. Modules.Select(row => row.ToEntry())]);

        private void OnModuleRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ModuleRowViewModel.IsEnabled))
                return;

            var current = BuildEnvironment();

            if (sender is ModuleRowViewModel ticked)
            {
                RecordUndo(standing, ticked.IsEnabled
                    ? Strings.Current.Format("Environment.UndoDescription.EnableModule", ticked.DisplayName)
                    : Strings.Current.Format("Environment.UndoDescription.DisableModule", ticked.DisplayName));
            }

            standing = current;
            PersistOrder(current);

            // The pane states whether the module is enabled, so ticking the box it is open on has to
            // rewrite it rather than leave a fact that is now wrong on screen.
            if (SelectedModule is { } selected && ReferenceEquals(sender, selected))
                ShowDetails(selected);

            // RefreshIssues moves the chip counts but the visible list is deliberately left alone:
            // under an Enabled or Disabled chip the row would otherwise vanish from under the pointer
            // that just ticked it. The next search, chip or refresh re-narrows.
            RefreshIssues(current);

            // Ticking a box is exactly what moves the command line, so the budget is re-measured here
            // rather than only on save. This is the tick that can take the order over the limit.
            UpdateLaunchTargetText(current);
        }

        // The tier is what Auto-Sort leads with, and until now it was computed on every scan and shown
        // nowhere. It reads off the whole list, since a library is only infrastructure relative to the
        // modules that depend on it, so it is worked out once per list rather than per row.
        private void RefreshTiers(ModuleEnvironment source)
        {
            var tiers = ModuleTierMap.For(source.Entries);

            for (var i = 0; i < Modules.Count && i < tiers.Length; i++)
                Modules[i].Tier = Modules[i].Entry.Manifest is null ? null : tiers[i];
        }

        private void Renumber()
        {
            for (var i = 0; i < Modules.Count; i++)
                Modules[i].Position = i + 1;
        }

        // A Health finding about one module, paired with the action its row offers.
        public sealed record ModuleFindingSource(ModuleFinding Finding, KnownIssueActionViewModel? Action);

        private void RefreshIssues(ModuleEnvironment source, ApplyReads? reads = null)
        {
            // The picked enabled set's diff is measured against the list as it stands, so it follows
            // every change to the list, not only the pick.
            ShowEnabledSetDiff();

            var acceptedKeys = reads?.AcceptedKeys ?? acceptedRisks.LoadKeys();

            // An error or warning the owner has accepted leaves the list, the totals and its module's
            // flag; the Accepted Risks section is where it is seen and taken back.
            var allIssues = LoadOrderValidator.Validate(source).Concat(orderRules.IssuesIn(source)).ToList();

            bool IsAccepted(LoadOrderIssue i) => i.Severity != IssueSeverity.Information
                && acceptedKeys.Contains(PreflightFinding.KeyFor(PreflightCheck.LoadOrder, i.Message));

            var issues = allIssues.Where(i => !IsAccepted(i)).ToList();

            var foldersByPath = source.FoldersWithoutManifest
                .ToDictionary(folder => folder.FolderPath, StringComparer.OrdinalIgnoreCase);

            Issues.Clear();
            foreach (var issue in issues.OrderBy(i => SeverityDisplayRank(i.Severity)))
            {
                var folder = issue.Kind == IssueKind.FolderWithoutManifest && issue.Path is not null
                    ? foldersByPath.GetValueOrDefault(issue.Path)
                    : null;

                Issues.Add(new IssueRowViewModel(issue, folder, AcceptIssueRisk, accepted: false));
            }

            ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
            WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
            InformationCount = issues.Count(i => i.Severity == IssueSeverity.Information);
            AcceptedIssueCount = allIssues.Count(IsAccepted);
            OrphanCount = source.Entries.Count(e => e.IsOrphan);
            HasAutoFixableIssues = issues.Any(i => i.IsAutoFixable);

            var byModule = issues
                .GroupBy(i => i.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var row in Modules)
            {
                var mine = byModule.GetValueOrDefault(row.Entry.Id) ?? [];

                row.IssueSummary = string.Join(" ", mine
                    .Where(i => i.Severity != IssueSeverity.Information)
                    .Select(i => i.Message));

                row.Notes = [.. mine
                    .Where(i => i.Severity == IssueSeverity.Information)
                    .Select(i => i.Message)];
            }

            // A note is only reachable on the row it names, so one aimed at a module with no row
            // would vanish entirely; those stay in the panel rather than being dropped.
            //
            // A note carrying a folder removal stays too, whatever row it names. The row can show its
            // text but has no button for it, and the module the row is for is installed and working, so
            // dropping the note here would leave the only way to act on the folder unreachable.
            var rowIds = Modules.Select(m => m.Entry.Id).ToHashSet();

            PanelIssues.Clear();
            foreach (var issue in Issues)
            {
                if (issue.Issue.Severity != IssueSeverity.Information
                    || issue.CanRemoveFolder
                    || !rowIds.Contains(issue.Issue.ModuleId))
                {
                    PanelIssues.Add(issue);
                }
            }

            openIssues = issues;
            RefreshModuleProblems();

            RefreshFilterCounts();
        }

        // The open load-order issues of the last read, and the open Health findings that name one module.
        // Both feed Core's ModuleProblems, which decides what each row says; the fix a button runs is the
        // issue's own Fix or the finding's own first action, so nothing is repaired a second way.
        private IReadOnlyList<LoadOrderIssue> openIssues = [];
        private IReadOnlyList<ModuleFindingSource> moduleFindings = [];

        public void SetModuleFindings(IReadOnlyList<ModuleFindingSource> findings)
        {
            moduleFindings = findings;
            RefreshModuleProblems();
        }

        private void RefreshModuleProblems()
        {
            var map = ModuleProblems.Group(openIssues, moduleFindings.Select(f => f.Finding));

            var actions = moduleFindings
                .GroupBy(f => f.Finding.Key)
                .ToDictionary(g => g.Key, g => g.First().Action);

            foreach (var row in Modules)
            {
                if (!map.TryGetValue(row.Entry.Id, out var problems))
                {
                    if (row.HasProblems)
                        row.Problems = [];

                    continue;
                }

                row.Problems = [.. problems.Select(p =>
                {
                    var isError = p.Severity == IssueSeverity.Error;

                    if (p.Issue is { IsFixable: true } issue)
                    {
                        return new ModuleRowProblem(
                            p.Reason,
                            isError,
                            Strings.Current["Environment.IssueRow.FixButton"],
                            Strings.Current["Environment.IssueRow.FixButton.Tooltip"],
                            new RelayCommand(() => ApplyFix(issue)));
                    }

                    if (p.Finding is { } finding && actions.GetValueOrDefault(finding.Key) is { } action)
                    {
                        return new ModuleRowProblem(
                            p.Reason,
                            isError,
                            action.Label,
                            action.Tooltip,
                            new RelayCommand(() => action.Command.Execute(action.Parameter)));
                    }

                    return new ModuleRowProblem(p.Reason, isError, string.Empty, string.Empty, null);
                })];
            }
        }

        private bool suspendFilterRefresh;

        private void OnFilterToggled()
        {
            if (suspendFilterRefresh)
                return;

            RefreshVisibleModules();
        }

        // Each option counts what the list would hold if it alone were added to whatever else is
        // ticked, so a zero means ticking it shows nothing rather than that none exist. Counting each
        // one against no filters at all would put a number beside an option that leads to an empty
        // list, which is the wrong answer to the question a number beside a tick box asks.
        private void RefreshFilterCounts()
        {
            foreach (var group in FilterGroups)
                group.Refresh();

            foreach (var option in Filters.Concat(categoryFilterGroup.Options))
            {
                var owner = FilterGroups.FirstOrDefault(group => group.Options.Contains(option));

                option.Count = Modules.Count(row =>
                    Matches(row, SearchText)
                    && FilterGroups.Where(group => group != owner).All(group => GroupAdmits(group, row))
                    && MatchesFilter(row, option));
            }

            OnPropertyChanged(nameof(IsFiltering));
            OnPropertyChanged(nameof(FilterSummary));
        }

        // Within a group the ticks are alternatives; nothing ticked is not a condition at all.
        private bool GroupAdmits(ModuleFilterGroupViewModel group, ModuleRowViewModel row) =>
            !group.IsNarrowing || group.Selected.Any(option => MatchesFilter(row, option));

        private bool MatchesFilters(ModuleRowViewModel row) =>
            FilterGroups.All(group => GroupAdmits(group, row));

        private bool MatchesFilter(ModuleRowViewModel row, ModuleFilterViewModel option) =>
            option.Kind == ModuleFilter.Category
                ? row.HasTier && string.Equals(row.TierText, option.Name, StringComparison.CurrentCultureIgnoreCase)
                : MatchesFilter(row, option.Kind);

        // The Category options are the labels the rows show now. A ticked label that no row carries any
        // more is unticked and dropped, so nothing narrows the list by an option that is not on screen.
        private void RefreshCategoryFilter()
        {
            var labels = Modules.Where(row => row.HasTier)
                .Select(row => row.TierText)
                .Where(label => label.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var options = categoryFilterGroup.Options;

            if (options.Select(option => option.Name).SequenceEqual(labels))
                return;

            var ticked = options.Where(option => option.IsSelected)
                .Select(option => option.Name)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

            var wasSuspended = suspendFilterRefresh;
            suspendFilterRefresh = true;

            try
            {
                options.Clear();

                foreach (var label in labels)
                {
                    var option = new ModuleFilterViewModel(ModuleFilter.Category, label,
                        Strings.Current.Format("Environment.Filter.Category.Option", label), OnFilterToggled);
                    option.SetSelected(ticked.Contains(label));
                    options.Add(option);
                }
            }
            finally
            {
                suspendFilterRefresh = wasSuspended;
            }

            categoryFilterGroup.Refresh();
        }

        private bool MatchesFilter(ModuleRowViewModel row, ModuleFilter filter) => filter switch
        {
            ModuleFilter.Enabled => row.IsEnabled,
            ModuleFilter.Disabled => !row.IsEnabled,
            ModuleFilter.Pinned => row.IsPinned,
            ModuleFilter.OutOfDate => row.IsOutOfDate,
            ModuleFilter.ProbablyOutOfDate => row.IsProbablyOutOfDate,
            ModuleFilter.UpdateUnknown => row.IsUpdateUnknown,
            ModuleFilter.IdConflict => row.HasIdConflict,
            ModuleFilter.NoNexusId => row.ShowNoNexusId,
            ModuleFilter.NotOnNexus => row.IsNotOnNexus,
            ModuleFilter.BuiltHere => row.IsBuiltLocally,
            ModuleFilter.Official => row.Entry.Origin == ModuleOrigin.Official,
            ModuleFilter.Manual => row.Entry.Origin == ModuleOrigin.Manual,
            ModuleFilter.Workshop => row.Entry.Origin == ModuleOrigin.Workshop,
            ModuleFilter.Issues => row.IssueSummary.Length > 0,
            ModuleFilter.Orphans => row.IsOrphan,
            ModuleFilter.Unreadable => row.IsUnreadable,
            // The same notes the row folds away and the issue panel header counts, so the chip can
            // never disagree with the number beside it. Show notes only hides them, it does not
            // change which modules have them.
            ModuleFilter.Notes => row.HasNotes,
            // Exactly the rows the last answer judged to need a look, so the chip and the count beside
            // it and the line above the list can never disagree. Before any check has run it selects
            // nothing, which is the truth: nothing has been asked.
            ModuleFilter.Updates => nexusVerdicts.TryGetValue(row.ModuleId, out var verdict)
                && verdict.State is ModuleUpdateState.OutOfDate or ModuleUpdateState.ProbablyOutOfDate,
            _ => true
        };

        // The list always shows the real load order, narrowed by the search box and the filter chip.
        // Sorting is not applied here: a sort reorders Modules itself, which is what BuildEnvironment
        // reads and Save writes.
        private void RefreshVisibleModules()
        {
            RefreshCategoryFilter();
            RefreshFilterCounts();

            var visible = new List<ModuleRowViewModel>();

            foreach (var row in Modules)
            {
                if (Matches(row, SearchText) && MatchesFilters(row))
                    visible.Add(row);
            }

            // Worked out from the whole load order rather than from the visible rows, because that is
            // what a sort now runs over: a filter narrows what is shown, never what is sorted.
            UpdateSortOptions([.. Modules.Select(row => row.ToEntry())]);

            // When nothing narrowed the list, the rebuilt collection is the same rows in the same
            // order. Re-adding them raises one notification per row to the bound list for no change;
            // the rows are the same instances, so a plain sequence check is enough to keep the UI
            // collection idle.
            if (!VisibleModules.SequenceEqual(visible))
            {
                VisibleModules.Clear();
                foreach (var row in visible)
                    VisibleModules.Add(row);
            }

            // A collapsed divider hides its modules from DisplayRows entirely (LoadOrderDisplay.Merge
            // never emits them, only the divider's HiddenCount), so a drag over a collapsed section
            // could never be written back correctly: the modules underneath were never on screen to
            // grab, and reconstructing them from nothing is not possible. Blocking the drag here, the
            // same way a search filter already does, is the only safe option.
            CanReorder = VisibleModules.Count == Modules.Count && !Dividers.Any(d => d.Collapsed);
            RefreshReorderReason();

            // After the list, not with the counts: the summary says how many rows are showing, and
            // raised before they were added it would say how many were showing a moment ago.
            OnPropertyChanged(nameof(FilterSummary));
            RefreshSelectedForActions();

            RebuildDisplayRows();

            // After the rebuild, not before it: the label says whether anything is narrowing the list,
            // and a collapsed section only narrows it once DisplayRows has been rebuilt without it.
            OnPropertyChanged(nameof(SelectAllLabel));
        }

        private void RebuildDisplayRows()
        {
            var rows = LoadOrderDisplay.Merge(
                [.. VisibleModules.Select(m => m.Entry)],
                Dividers,
                Modules.Select(m => m.Entry.Id).ToHashSet());

            var rowsByAnchorKey = Modules.ToDictionary(m => m.Entry.Id, m => m);

            // Which entry of Dividers each merged divider row came from. Equal-valued sections are
            // indistinguishable by value, so the queue hands them out in the order Merge emits them,
            // which is the order they sit in Dividers for any two that could be confused.
            var unusedIndexes = new Dictionary<LoadOrderDivider, Queue<int>>();

            for (var i = 0; i < Dividers.Count; i++)
            {
                if (!unusedIndexes.TryGetValue(Dividers[i], out var queue))
                    unusedIndexes[Dividers[i]] = queue = new Queue<int>();

                queue.Enqueue(i);
            }

            // Module rows are reused from Modules, so an unchanged list rebuilds to the same
            // instances and the sequence check below finds nothing to do. Divider rows are built
            // here rather than held anywhere, so they are reused the same way from the rows already
            // on screen: without this every rebuild produces fresh instances, the check can never
            // match, and the bound collection is torn down and refilled on every keystroke.
            var reusable = DisplayRows.OfType<DividerRowViewModel>().ToList();
            var dividerOrdinal = 0;
            var built = new List<ILoadOrderRowViewModel>(rows.Count);

            foreach (var row in rows)
            {
                built.Add(row switch
                {
                    LoadOrderDisplayRow.Module module => rowsByAnchorKey[module.Entry.Id],
                    LoadOrderDisplayRow.Divider divider => DividerRow(divider, dividerOrdinal++),
                    _ => throw new InvalidOperationException("Unknown load order display row.")
                });
            }

            if (DisplayRows.SequenceEqual(built))
                return;

            // A rebuild is not a drag. EnvironmentPage watches DisplayRows to hear a drop, and
            // reads the dividers' new anchors back out of the rows it finds; running that over a
            // list this method is in the middle of replacing would rewrite every anchor from a
            // half-built view, which at startup (sections loaded, modules not scanned yet) means
            // anchoring them all to nothing and saving that over the real file.
            isRebuildingDisplayRows = true;

            try
            {
                // Only what differs, never a clear and refill. A build tool deploying into the Modules
                // folder really does take a module away and put it back, so the list really must
                // change; emptied first it would flash blank and drop the scroll position, while a
                // removal and an insertion leave the surrounding rows in place for the list to hold
                // its position against.
                RowListSync.Apply(DisplayRows, built);
            }
            finally
            {
                isRebuildingDisplayRows = false;
            }

            DividerRowViewModel DividerRow(LoadOrderDisplayRow.Divider divider, int ordinal)
            {
                var index = unusedIndexes.TryGetValue(divider.LoadOrderDivider, out var queue) && queue.Count > 0
                    ? queue.Dequeue()
                    : -1;

                return ordinal < reusable.Count
                    && reusable[ordinal] is { } existing
                    && existing.Divider == divider.LoadOrderDivider
                    && existing.HiddenCount == divider.HiddenCount
                    && existing.DividerIndex == index
                        ? existing
                        : new DividerRowViewModel(divider.LoadOrderDivider, divider.HiddenCount, index);
            }
        }

        // A grayed-out drag target explains nothing on its own, so the reason is written out above the
        // list. Sorting is no longer one of the reasons: it writes the load order, so the list a sort
        // leaves behind is the real one and dragging it is meaningful. CanReorder can be false for two
        // independent reasons at once (a search filter active and a section collapsed); the filter
        // reason is named first when both apply, since clearing the filter is always the smaller ask.
        private void RefreshReorderReason()
        {
            if (CanReorder)
            {
                ReorderBlockedReason = string.Empty;
                return;
            }

            ReorderBlockedReason = VisibleModules.Count != Modules.Count
                ? Strings.Current["Environment.ReorderBlocked.Filtered"]
                : Strings.Current["Environment.ReorderBlocked.Collapsed"];
        }

        public bool HasSortKeys => !sortPlan.IsEmpty;

        public string SortLabel => sortPlan.IsEmpty
            ? Strings.Current["Environment.SortLabel.Empty"]
            : Strings.Current.Format("Environment.SortLabel.WithPlan", sortPlan.Describe());

        public string SortLimitNote => sortPlan.IsFull
            ? AcceptedNotices.Unless(SortNoteCategory, Strings.Current.Plural("Environment.SortLimitNote", ModuleSortPlan.MaxLevels))
            : string.Empty;

        // The sort menu's two cautions, accepted by their own words like every other notice.
        internal const string SortNoteCategory = "SortNote";

        [RelayCommand]
        private void AcceptSortLimitNote()
        {
            StatusMessage = AcceptedNotices.Accept(SortNoteCategory, SortLimitNote);
            OnPropertyChanged(nameof(SortLimitNote));
            OnPropertyChanged(nameof(HasSortLimitNote));
        }

        public bool HasSortLimitNote => SortLimitNote.Length > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasReorderBlockedReason))]
        public partial string ReorderBlockedReason { get; set; } = string.Empty;

        public bool HasReorderBlockedReason => ReorderBlockedReason.Length > 0;

        private static string SortName(ModuleSortKey kind) => kind switch
        {
            ModuleSortKey.Name => Strings.Current["Environment.SortKey.Name"],
            ModuleSortKey.Enabled => Strings.Current["Environment.SortKey.EnabledState"],
            ModuleSortKey.Tier => Strings.Current["Environment.SortKey.Tier"],
            _ => Strings.Current["Environment.SortKey.ModuleId"]
        };

        // Choosing a key sorts the load order there and then. It is the same topological pass Auto-Sort
        // runs, so every declared load-before, load-after and dependency is satisfied first and the keys
        // only decide between modules that are free of each other.
        private void ToggleSort(ModuleSortKey kind)
        {
            var option = SortOptions.First(o => o.Kind == kind);

            // The menu item is disabled at the limit, so this is the belt to that braces: it says why
            // rather than looking like a click that worked.
            if (!sortPlan.CanAdd(kind))
            {
                option.Refresh();
                StatusMessage = Strings.Current.Plural("Environment.SortKey.AtLimit", ModuleSortPlan.MaxLevels, ModuleSortPlan.Describe(kind));
                return;
            }

            var adding = sortPlan.PrecedenceOf(kind) == 0;

            sortPlan = sortPlan.Toggle(kind);
            RefreshSort();

            if (sortPlan.IsEmpty)
            {
                StatusMessage = Strings.Current.Format("Environment.SortKey.RemovedLast", ModuleSortPlan.Describe(kind));
                return;
            }

            ApplySort(Strings.Current.Format("Environment.UndoDescription.SortBy", sortPlan.Describe()));

            if (adding && UnreachableCaveat(option) is { Length: > 0 } caveat)
                StatusMessage = $"{StatusMessage} {caveat}";
        }

        // A key that can never engage is worth saying out loud the moment it is chosen, not only in
        // the menu the user is about to close.
        private static string UnreachableCaveat(ModuleSortViewModel option) =>
            option.UnreachableNote.Length == 0
                ? string.Empty
                : Strings.Current.Format("Environment.SortKey.Unreachable", option.Precedence, ModuleSortPlan.Describe(option.Kind), option.UnreachableNote);

        private void ToggleSortDirection(ModuleSortKey kind)
        {
            if (sortPlan.PrecedenceOf(kind) == 0)
                return;

            sortPlan = sortPlan.ToggleDirection(kind);
            RefreshSort();
            ApplySort(Strings.Current.Format("Environment.UndoDescription.SortBy", sortPlan.Describe()));
        }

        // Clears the keys only. Re-running a sort to undo one would be a second reorder, not an undo,
        // so the order stays where the last sort left it and Undo is the way back.
        [RelayCommand(CanExecute = nameof(HasSortKeys))]
        public void ClearSort()
        {
            if (sortPlan.IsEmpty)
            {
                StatusMessage = Strings.Current["Environment.ClearSort.None"];
                return;
            }

            sortPlan = sortPlan.Cleared();
            RefreshSort();

            StatusMessage = Strings.Current["Environment.ClearSort.Success"];
        }

        private void RefreshSort()
        {
            OnPropertyChanged(nameof(HasSortKeys));
            OnPropertyChanged(nameof(SortLabel));
            OnPropertyChanged(nameof(SortLimitNote));
            OnPropertyChanged(nameof(HasSortLimitNote));
            ClearSortCommand.NotifyCanExecuteChanged();

            RefreshVisibleModules();
        }

        private void UpdateSortOptions(IReadOnlyList<ModuleEntry> entries)
        {
            var notes = LoadOrderTies.UnreachableNotes(entries, sortPlan);

            foreach (var option in SortOptions)
            {
                var precedence = sortPlan.PrecedenceOf(option.Kind);

                option.Update(
                    precedence,
                    sortPlan.DescendingFor(option.Kind),
                    sortPlan.CanAdd(option.Kind),
                    precedence == 0 ? string.Empty : notes[precedence - 1]);
            }
        }

        // Explicit rather than a cast of the enum's ordinal: IssueSeverity's declaration order does
        // not match display order, and a future severity added between existing values must not
        // silently reshuffle this list.
        private static int SeverityDisplayRank(IssueSeverity severity) => severity switch
        {
            IssueSeverity.Error => 0,
            IssueSeverity.Warning => 1,
            IssueSeverity.Information => 2,
            _ => 3
        };

        // The row answers this, not the entry behind it. Half of what the search box now has to find is
        // a mark the row carries and the entry has never heard of: Out of Date, No Nexus ID, Built Here.
        private static bool Matches(ModuleRowViewModel row, string query) => row.MatchesSearch(query);

        private static bool Matches(ModuleEntry entry, string query) =>
            string.IsNullOrWhiteSpace(query)
            || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase);

        private void UpdateLaunchTargetText() => UpdateLaunchTargetText(BuildEnvironment());

        // Resolved with the real enabled list rather than an empty one, because the same target now
        // answers two questions: which launcher runs, and how long the command line it would send is.
        private void UpdateLaunchTargetText(ModuleEnvironment source)
        {
            var target = LaunchTargetResolver.Resolve(
                GameInstallPath, PreferredTarget, EnabledForLaunch(source), LaunchArguments, CrashHandling);

            LaunchTargetText = target is null
                ? Strings.Current["Environment.LaunchTargetText.None"]
                : Strings.Current.Format("Environment.LaunchTargetText.Via", target.DisplayName);
            CommandLine = target is null ? null : GameCommandLine.Measure(target);
        }

    }

    public partial class BackupRowViewModel : ObservableObject
    {
        private readonly Func<BackupRowViewModel, Task> restore;
        private readonly Action<BackupRowViewModel> compare;
        private readonly Func<BackupRowViewModel, Task> discard;

        public BackupRowViewModel(
            LoadOrderBackup backup,
            Func<BackupRowViewModel, Task> restore,
            Action<BackupRowViewModel> compare,
            Func<BackupRowViewModel, Task> discard)
        {
            ArgumentNullException.ThrowIfNull(backup);
            ArgumentNullException.ThrowIfNull(restore);
            ArgumentNullException.ThrowIfNull(compare);
            ArgumentNullException.ThrowIfNull(discard);

            Backup = backup;
            this.restore = restore;
            this.compare = compare;
            this.discard = discard;
        }

        public LoadOrderBackup Backup { get; }

        public string DisplayName => Backup.DisplayName;

        public string TakenText => Backup.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public string ContentText => Backup.ContentSummary;

        public string LabelText => Backup.Label;

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud for every row.
        public override string ToString() => DisplayName;

        [RelayCommand]
        private Task Restore() => restore(this);

        [RelayCommand]
        private void Compare() => compare(this);

        [RelayCommand]
        private Task Discard() => discard(this);
    }

    public partial class ProfileRowViewModel : ObservableObject
    {
        private readonly Action<ProfileRowViewModel> restore;
        private readonly Action<ProfileRowViewModel> compare;
        private readonly Action<ProfileRowViewModel> markKnownGood;
        private readonly Action<ProfileRowViewModel> delete;
        private readonly Func<ProfileRowViewModel, Task> export;

        public ProfileRowViewModel(
            LoadOrderProfile profile,
            Action<ProfileRowViewModel> restore,
            Action<ProfileRowViewModel> compare,
            Action<ProfileRowViewModel> markKnownGood,
            Action<ProfileRowViewModel> delete,
            Func<ProfileRowViewModel, Task> export)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(restore);
            ArgumentNullException.ThrowIfNull(compare);
            ArgumentNullException.ThrowIfNull(markKnownGood);
            ArgumentNullException.ThrowIfNull(delete);
            ArgumentNullException.ThrowIfNull(export);

            Profile = profile;
            this.restore = restore;
            this.compare = compare;
            this.markKnownGood = markKnownGood;
            this.delete = delete;
            this.export = export;
        }

        public LoadOrderProfile Profile { get; }

        public string Name => Profile.Name;

        public bool IsKnownGood => Profile.IsKnownGood;

        public string SavedText => Profile.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string ContentText =>
            Strings.Current.Plural("Environment.ProfileRow.ContentText", Profile.Order.Entries.Count, Profile.Order.EnabledCount);

        public string KnownGoodText => Profile.IsKnownGood ? Strings.Current["Environment.ProfileRow.KnownGoodText"] : string.Empty;

        public override string ToString() => Name;

        [RelayCommand]
        private void Restore() => restore(this);

        [RelayCommand]
        private void Compare() => compare(this);

        [RelayCommand]
        private void MarkKnownGood() => markKnownGood(this);

        [RelayCommand]
        private void Delete() => delete(this);

        [RelayCommand]
        private Task Export() => export(this);
    }

    public partial class DeletedProfileRowViewModel : ObservableObject
    {
        private readonly Action<DeletedProfileRowViewModel> restore;
        private readonly Func<DeletedProfileRowViewModel, Task> discard;

        public DeletedProfileRowViewModel(
            DeletedProfile deleted,
            Action<DeletedProfileRowViewModel> restore,
            Func<DeletedProfileRowViewModel, Task> discard)
        {
            ArgumentNullException.ThrowIfNull(deleted);
            ArgumentNullException.ThrowIfNull(restore);
            ArgumentNullException.ThrowIfNull(discard);

            Deleted = deleted;
            this.restore = restore;
            this.discard = discard;
        }

        public DeletedProfile Deleted { get; }

        public string Name => Deleted.Name;

        public string Path => Deleted.Path;

        // Removed is a deliberate delete; replaced is the previous version of a profile the user saved
        // over. Both land in the same folder, so the row is the only place the two can be told apart.
        public string KindWord => Deleted.Replaced ? Strings.Current["Environment.DeletedProfileRow.Replaced"] : Strings.Current["Environment.DeletedProfileRow.Removed"];

        public string WhenText => Deleted.DeletedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string RemovedText => Strings.Current.Format("Environment.DeletedProfileRow.RemovedText", KindWord, WhenText);

        public string ContentText =>
            Strings.Current.Plural("Environment.ProfileRow.ContentText", Deleted.Profile.Order.Entries.Count, Deleted.Profile.Order.EnabledCount);

        public override string ToString() => Strings.Current.Format("Environment.DeletedProfileRow.ToString", Name, RemovedText);

        [RelayCommand]
        private void Restore() => restore(this);

        [RelayCommand]
        private async Task Discard() => await discard(this);
    }

    public partial class PinRowViewModel : ObservableObject
    {
        private readonly Action<PinRowViewModel> unpin;

        public PinRowViewModel(ModuleId id, int position, string displayName, Action<PinRowViewModel> unpin)
        {
            ArgumentNullException.ThrowIfNull(unpin);

            Id = id;
            Position = position;
            DisplayName = displayName;
            this.unpin = unpin;
        }

        public ModuleId Id { get; }

        // 0 for a pin whose module is not installed right now. The pin is kept and shown, because the
        // module can come back: a Workshop module is invisible to a folder scan while Steam is offline.
        public int Position { get; }

        public bool IsInstalled => Position > 0;

        public string DisplayName { get; }

        public string PositionText => IsInstalled
            ? Strings.Current.Format("Environment.PinRow.PositionText", Position)
            : Strings.Current["Environment.PinRow.NotInstalled"];

        public override string ToString() => DisplayName;

        [RelayCommand]
        private void Unpin() => unpin(this);
    }

    // Every mark a row can carry, so a mark the user can see is one they can narrow the list to. All
    // is gone: it was the single-select escape hatch, and with nothing ticked meaning nothing narrowed
    // it was a button for a state that is already the default.
    public enum ModuleFilter
    {
        Enabled,
        Disabled,
        Pinned,
        Issues,
        Orphans,
        Workshop,
        Official,
        Manual,
        Unreadable,
        Notes,
        Updates,
        OutOfDate,
        ProbablyOutOfDate,
        UpdateUnknown,
        IdConflict,
        NoNexusId,
        NotOnNexus,
        BuiltHere,
        Category
    }

    public partial class ModuleSortViewModel : ObservableObject
    {
        private readonly Action<ModuleSortKey> select;
        private readonly Action<ModuleSortKey> toggleDirection;

        public ModuleSortViewModel(
            ModuleSortKey kind,
            string name,
            Action<ModuleSortKey> select,
            Action<ModuleSortKey> toggleDirection)
        {
            ArgumentNullException.ThrowIfNull(select);
            ArgumentNullException.ThrowIfNull(toggleDirection);

            Kind = kind;
            Name = name;
            this.select = select;
            this.toggleDirection = toggleDirection;
        }

        public ModuleSortKey Kind { get; }

        public string Name { get; }

        // 1 for the key that decides the order, 2 for the one that breaks its ties, and so on. 0 for
        // a key that is not part of the sort at all.
        public int Precedence { get; private set; }

        public bool Descending { get; private set; }

        public bool IsAvailable { get; private set; } = true;

        // Why this key cannot move a row given the keys above it. Empty when it does have a say.
        public string UnreachableNote { get; private set; } = string.Empty;

        public bool IsSelected => Precedence > 0;

        public string Label => Precedence == 0 ? Name : Strings.Current.Format("Environment.ModuleSortViewModel.Label", Precedence, Name);

        public string DirectionText => Descending ? Strings.Current["Environment.SortDirection.Descending"] : Strings.Current["Environment.SortDirection.Ascending"];

        public bool HasUnreachableNote => UnreachableNote.Length > 0;

        public void Update(int precedence, bool descending, bool available, string note)
        {
            Precedence = precedence;
            Descending = descending;
            IsAvailable = available;
            UnreachableNote = AcceptedNotices.Unless(EnvironmentViewModel.SortNoteCategory, note);

            Refresh();
        }

        [RelayCommand]
        private void AcceptUnreachableNote()
        {
            AcceptedNotices.Accept(EnvironmentViewModel.SortNoteCategory, UnreachableNote);
            UnreachableNote = string.Empty;
            Refresh();
        }

        // Clicking an option unchecks or checks the button locally before the plan has had its say,
        // and an assignment equal to the current value would raise nothing to put it back. This
        // notifies regardless.
        public void Refresh()
        {
            OnPropertyChanged(nameof(Precedence));
            OnPropertyChanged(nameof(Descending));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(UnreachableNote));
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(DirectionText));
            OnPropertyChanged(nameof(HasUnreachableNote));
        }

        [RelayCommand]
        private void Select() => select(Kind);

        [RelayCommand]
        private void ToggleDirection() => toggleDirection(Kind);
    }

    public partial class ModuleFilterViewModel : ObservableObject
    {
        private readonly Action changed;

        public ModuleFilterViewModel(ModuleFilter kind, string name, string description, Action changed)
        {
            ArgumentNullException.ThrowIfNull(changed);

            Kind = kind;
            Name = name;
            Description = description;
            this.changed = changed;
        }

        public ModuleFilter Kind { get; }

        public string Name { get; }

        // One short sentence saying what the option selects. A tick box labelled "Unknown" is a word,
        // not a question anybody can answer.
        public string Description { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        public partial int Count { get; set; }

        // The count is what the list would hold with this one added to whatever else is ticked, so a
        // zero says "ticking this shows nothing" rather than "there are none of these anywhere".
        public string Label => $"{Name} ({Count})";

        partial void OnIsSelectedChanged(bool value)
        {
            _ = value;
            changed();
        }

        public void SetSelected(bool selected) => IsSelected = selected;
    }

    // One button, one closed question, several answers at once. Within a group the ticks are alternatives
    // and a row needs any one of them; across groups they are conditions and a row needs all of them. So
    // Workshop plus Manual with Out of Date means "the Workshop and manual mods that are behind", which
    // is the question a single-select chip row could not ask at all.
    public partial class ModuleFilterGroupViewModel : ObservableObject
    {
        public ModuleFilterGroupViewModel(string name, string description, IEnumerable<ModuleFilterViewModel> options)
        {
            Name = name;
            Description = description;
            Options = [.. options];
        }

        public string Name { get; }

        public string Description { get; }

        public ObservableCollection<ModuleFilterViewModel> Options { get; }

        public IEnumerable<ModuleFilterViewModel> Selected => Options.Where(option => option.IsSelected);

        public bool HasAny => Options.Count > 0;

        public bool IsNarrowing => Options.Any(option => option.IsSelected);

        // The button says what it is doing without being opened. A group with nothing ticked reads as
        // its own name, which is the honest description of a condition that is not being applied.
        public string Label => Selected.Count() switch
        {
            0 => Name,
            1 => Strings.Current.Format("Environment.Filter.GroupLabelOneSelected", Name, Selected.First().Name),
            var many => Strings.Current.Plural("Environment.Filter.GroupLabelManySelected", many, Name)
        };

        public string Tooltip => Strings.Current.Format("Environment.Filter.GroupTooltip", Description);

        public void Refresh()
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(IsNarrowing));
            OnPropertyChanged(nameof(HasAny));
        }

        [RelayCommand]
        private void Clear()
        {
            foreach (var option in Options)
                option.SetSelected(false);
        }
    }
}
