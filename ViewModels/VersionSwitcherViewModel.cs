using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The one place a game version is chosen and the one place a version is downloaded. It builds a
    // single list and cuts it in two: Play's dropdown gets the versions already on this machine, so
    // picking one only ever switches, and the Versions page gets the rest, so a download is started
    // deliberately from the page that owns it. One list means the two pages can never disagree about
    // what is installed or what is playing. Nothing here downloads anything on its own: the only way
    // in is DownloadAsync or AddDlcAsync, and both are reached from a Versions page button.
    public sealed partial class VersionSwitcherViewModel : BaseViewModel
    {
        private readonly InstanceSettingsStore settingsStore = new();
        private readonly InstanceManager instanceManager;

        // Steam's app info as fetched, kept because the same document carries each branch's depots and
        // the sizes the download dialog quotes.
        private string appInfoJson = string.Empty;

        // Held between the sign-in dialog and the prompt DepotDownloader answers it with, and cleared
        // the moment it is used. It is never written to disk.
        private string? pendingPassword;

        // Where each Steam Guard prompt in this download gets its answer. Built before
        // DepotDownloader is started, so a code the user has already read off their authenticator is
        // written to the process the instant it asks rather than typed while it waits. It is never
        // written to disk, never logged, and dropped in the finally block that ends the download.
        private SteamGuardPlan? guardPlan;

        // What AcquireAsync has already said that a plain "downloaded" line must not paper over: a
        // base game that landed while its DLC was refused is not the same result as a clean run, and
        // the user has to be able to read which one happened.
        private string? acquireWarning;

        // Set when DepotDownloader reported that Steam publishes no such branch for one of the
        // depots it fetched, which means the version that landed is a mixture of two branches.
        private string? branchWarning;

        // Set while this view model is writing SelectedVersion itself. Every rebuild of the list
        // replaces the row objects, which moves the ComboBox's selection; without this the rebuild
        // that follows a switch would read as a second switch and loop.
        private bool isSelecting;

        public VersionSwitcherViewModel()
        {
            instanceManager = new InstanceManager(CanonicalPathSet.ForMachine(), settingsStore);
            StatusMessage = string.Empty;

            // True until a refresh has actually looked. The first refresh enumerates instance
            // folders and fetches Steam's branch list, which takes seconds, and telling a machine
            // full of versions that it has none for those seconds is claiming a fact BEM has not
            // established yet.
            HasInstalledVersions = true;
        }

        // Raised after the active instance changes, so the pages showing that instance's load order,
        // saves and health reload rather than describing the version that was playing a moment ago.
        public event EventHandler? ActiveInstanceChanged;

        // Play's dropdown: only what this machine already has, so the one gesture it offers is a
        // switch.
        public ObservableCollection<GameVersionRowViewModel> InstalledVersions { get; } = [];

        // The Versions page's download picker: only what would have to be fetched from Steam.
        public ObservableCollection<GameVersionRowViewModel> DownloadableVersions { get; } = [];

        // Whether Steam's branch list has actually been read. False until it has, and false again
        // after a refresh that could not reach Steam, so nothing reads an empty DownloadableVersions
        // as "Steam publishes none of these": OfferFor answers null either way, and only this tells
        // the two apart.
        [ObservableProperty]
        public partial bool HasSteamBranchList { get; set; }

        // A dropdown with nothing in it is a control that cannot be used, so Play shows an empty
        // state pointing at Versions instead of an empty list.
        [ObservableProperty]
        public partial bool HasInstalledVersions { get; set; }

        [ObservableProperty]
        public partial GameVersionRowViewModel? SelectedVersion { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        public partial bool IsBusy { get; set; }

        // Both dropdowns are disabled while a switch or a download is running: a switch under a
        // running download would move the game out from under it, and a second download picked
        // mid-download would start a second one.
        public bool IsIdle => !IsBusy;

        // True from the moment a download or a DLC addition starts until it has ended, however it
        // ended. Narrower than IsBusy on purpose: IsBusy also covers a version switch, and a switch
        // has no child process to stop.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StopDownloadCommand))]
        public partial bool IsDownloading { get; set; }

        [ObservableProperty]
        public partial bool IsProgressVisible { get; set; }

        [ObservableProperty]
        public partial int ProgressValue { get; set; }

        [ObservableProperty]
        public partial string QrBlock { get; set; } = string.Empty;

        // The same code as a picture a camera can actually read. Null until DepotDownloader prints
        // one, and null again the moment sign-in is done.
        [ObservableProperty]
        public partial Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? QrImage { get; set; }

        // Built here rather than in the page, because the block arrives a line at a time and only the
        // last, complete one is a code: parsing every partial block and painting nothing is how the
        // page stays still while it fills in.
        partial void OnQrBlockChanged(string value)
        {
            QrImage = Services.QrCodeImage.From(value);
        }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        public string GamesRoot => settingsStore.Read().GamesRoot;

        // Re-read from outside while a switch or a download is running would rebuild the list under
        // the gesture that is running it, so the pages that just want a fresh look ask for one only
        // when nothing is in flight. A download the Steam client finished in the meantime turns up
        // here, which is what makes the Import row appear without restarting BEM.
        public Task RefreshIfIdleAsync() => IsBusy ? Task.CompletedTask : RefreshAsync();

        [RelayCommand]
        public async Task RefreshAsync()
        {
            var installed = instanceManager.List();
            var activeId = instanceManager.ActiveInstanceId;
            // Read here rather than per row: which instance rests is one setting, and the marker it
            // puts on a row is what tells two installs of the same version and variant apart.
            var restingId = instanceManager.RestingInstanceId;

            // Off the UI thread: GameDlcDetector.TryDetect reads each instance's Modules folder, and
            // this is called directly from RefreshAsync (a RelayCommand, and awaited straight from
            // ApplyAsync and from VersionsViewModel) with no Task.Run of its own upstream, so without
            // this the disk walk for every installed instance would run on the UI thread on every
            // refresh, and hang it outright for a referenced instance on a slow or disconnected drive.
            var known = await Task.Run(() => BuildKnownEntries(installed, activeId, restingId));

            // Before anything has been adopted there are no instances, but the machine is still
            // running a version: listing it keeps the dropdown honest on first run and stops the
            // version already installed from being offered as a download.
            if (known.Count == 0 && GameInstallLocator.Locate() is { } currentInstall)
            {
                var (currentBranch, currentBuildId) = SteamBranchInfo(currentInstall);
                var currentVersion = GameVersionReader.Read(currentInstall);

                if (!currentVersion.IsEmpty)
                    known.Add(new GameVersionEntry(currentVersion, currentBranch, currentBuildId, null, true));
            }

            var publicVersion = PublicVersion(installed);

            // Not a download, so no token: a refresh is what rebuilds both version lists, it runs on
            // navigation and after a download rather than from a button, and there is no control
            // anywhere that could stop one. Abandoning it half way would leave the page describing a
            // machine that no longer matches it.
            var json = await SteamBranchCatalog.FetchJsonAsync(CancellationToken.None);

            if (json.Length > 0)
                appInfoJson = json;

            HasSteamBranchList = appInfoJson.Length > 0;

            var offered = GameVersionCatalog.From(SteamBranchCatalog.Parse(appInfoJson), publicVersion);
            // A version downloaded by a build that wrote the game to the top of its folder is put right
            // here rather than listed as installed and refusing to launch, and a download that
            // finished while BEM was closed is taken in under the name its version earns. Both need
            // the offered list first: a folder is only adopted when it holds every byte Steam
            // publishes for that version.
            if (await Task.Run(() => Sweep(offered)) > 0)
            {
                installed = instanceManager.List();
                known = await Task.Run(() => BuildKnownEntries(installed, activeId, restingId));
            }

            // Every offered version is offered once per DLC BEM knows how to build, alongside the
            // base game, so a user who already owns War Sails can pick "v1.5.2 + War Sails" as a
            // single download - but only for a base branch the DLC actually publishes a build for.
            // A DLC is its own Steam app with its own branch list, so its own catalog is fetched
            // here too, and DlcCatalog.Read is asked the same question DlcDownloadStep asks before a
            // download runs: a row this offers is always a row that download check would accept. A
            // DLC whose branch list this refresh could not read offers nothing at all rather than
            // everything, since a missing answer is not a yes.
            var dlcCatalogJson = new Dictionary<int, string>();
            var dlcCatalogUnavailable = false;

            foreach (var dlc in GameDlc.Known)
            {
                var dlcJson = await SteamBranchCatalog.FetchJsonAsync(dlc.AppId.ToString(), CancellationToken.None);

                if (dlcJson.Length > 0)
                    dlcCatalogJson[dlc.AppId] = dlcJson;
                else
                    dlcCatalogUnavailable = true;
            }

            var dlcCatalogUnavailableMessage = Strings.Current["Versions.DlcCatalogUnavailable"];

            if (dlcCatalogUnavailable)
                StatusMessage = dlcCatalogUnavailableMessage;
            else if (string.Equals(StatusMessage, dlcCatalogUnavailableMessage, StringComparison.Ordinal))
                StatusMessage = string.Empty;

            bool DlcOffersBranch(GameDlcInfo dlc, string branch) =>
                dlcCatalogJson.TryGetValue(dlc.AppId, out var dlcJson)
                    && DlcCatalog.Read(dlcJson, dlc.AppId, branch).BranchPublished;

            // One list, cut in two by Core: Play switches between what is here, Versions downloads
            // what is not. Cutting one Build result rather than building twice is what keeps the two
            // pages agreeing about what is installed.
            var all = GameVersionList.Build(known, offered, GameDlc.Known, DlcOffersBranch);
            var onMachine = Rows(GameVersionSplit.OnThisMachine(all));
            var downloadable = Rows(GameVersionSplit.Downloadable(all));

            isSelecting = true;

            try
            {
                InstalledVersions.Clear();

                foreach (var row in onMachine)
                    InstalledVersions.Add(row);

                DownloadableVersions.Clear();

                foreach (var row in downloadable)
                    DownloadableVersions.Add(row);

                HasInstalledVersions = InstalledVersions.Count > 0;

                SelectedVersion = InstalledVersions.FirstOrDefault(row => row.IsActive)
                    ?? InstalledVersions.FirstOrDefault();
            }
            finally
            {
                isSelecting = false;
            }
        }

        private static List<GameVersionRowViewModel> Rows(IReadOnlyList<GameVersionEntry> entries) =>
            [.. entries.Select(entry => new GameVersionRowViewModel(entry, DisplayNameOf(entry)))];

        // The download row for one exact version and variant, which is what the Versions row menu's
        // Install Another Copy needs: the menu acts on an instance, and a download is started from a
        // row in this list, so one has to be found for the other. Null when Steam no longer publishes
        // that pair, which is the state the menu item disables itself for.
        public GameVersionRowViewModel? OfferFor(ModuleVersion version, GameDlcSet variant) =>
            DownloadableVersions.FirstOrDefault(row => row.Version == version && row.Entry.Variant == variant);

        // Every name the versions already on this machine are listed under, without the resting
        // marker: the marker is not part of any name, and a copy that took it into account would be
        // numbered against a decoration rather than against a name.
        private string SuggestedCopyName() => InstanceCopyName.Next(
            InstalledVersions.Select(installed =>
                InstanceLabel.NameOf(installed.Instance?.Record, installed.Label)));

        // The download body, the sentence a second copy gets, then the two things the folder is
        // composed from before a byte is fetched: the name and what the version is for. The panel
        // asks for DialogText.ContentWidth so a one-line dialog does not collapse toward the
        // ContentDialog minimum with a text box floating in it.
        private static StackPanel AcquirePanel(string body, string? copySentence, TextBox nameBox)
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = DialogText.ContentWidth };

            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = body });

            if (copySentence is not null)
                panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = copySentence });

            panel.Children.Add(nameBox);

            return panel;
        }

#if DEV_BEM
        // The outer ring, for the same reason the Versions row menu's Purpose submenu is in it:
        // declaring what an install is for serves a mod's build tool, which is a thing an author has
        // and a player does not. Only the declaring is gated. Every tier still composes the purpose
        // into the folder name and still reads it off the record, so a public build opening a
        // developer's games root reads and honors what is written there.
        //
        // The purposes are offered in the order this one array lists them and read back by position
        // from it: a second list mapping the selection to an enum is how the two come to disagree
        // about which line means Testing.
        //
        // Undeclared leads and is what the box starts on, exactly as the record's own field defaults.
        // A dialog starting on Testing would sanction mod builds into an install nobody said that
        // about, and one starting on Playing would claim something the user did not say either.
        private static readonly InstancePurpose[] PurposeChoices =
            [InstancePurpose.Unspecified, InstancePurpose.Playing, InstancePurpose.Testing];

        private static string PurposeText(InstancePurpose purpose) => purpose switch
        {
            InstancePurpose.Playing => Strings.Current["Versions.Menu.Purpose.Playing"],
            InstancePurpose.Testing => Strings.Current["Versions.Menu.Purpose.Testing"],
            _ => Strings.Current["Versions.Menu.Purpose.Undeclared"]
        };

        private static ComboBox PurposeBox()
        {
            var box = new ComboBox
            {
                Header = Strings.Current["Versions.Menu.Purpose"],
                ItemsSource = PurposeChoices.Select(PurposeText).ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            ToolTipService.SetToolTip(box, Strings.Current["Versions.Menu.Purpose.Tooltip"]);

            return box;
        }
#endif

        // The name a folder full of partial bytes already carries, so resuming into it offers that
        // name rather than a fresh disambiguator: the folder is picked from the name in this box, and
        // a resume that composed a different one would fetch the whole version again beside the bytes
        // it was meant to pick up.
        private static string? ResumedName(GameVersionRowViewModel row, string? resumeFolder)
        {
            if (resumeFolder is null)
                return null;

            var named = InstanceFolderName.ChosenNameIn(
                InstanceDraft.ForDownload(row.Version, row.Branch, row.BuildId, row.Entry.Variant),
                Path.GetFileName(resumeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

            return string.IsNullOrEmpty(named) ? null : named;
        }

        // What every installed instance is known by: its version, branch, build id and the variant it
        // was found in. The variant comes from the disk, not from what BEM happened to write down, so
        // GameVersionList.Build's de-duplication and the DLC rows it offers agree with what is actually
        // installed; a referenced instance's own record started this whole bug by carrying no DLC at
        // all. Meant to run inside Task.Run: GameDlcDetector.TryDetect reads a Modules folder per row.
        private static List<GameVersionEntry> BuildKnownEntries(
            IReadOnlyList<InstalledInstance> installed, string? activeId, string? restingId) =>
            [.. installed.Select(instance => new GameVersionEntry(
                VersionOf(instance),
                instance.Record.Branch,
                instance.Record.BuildId,
                instance,
                string.Equals(instance.Record.Id, activeId, StringComparison.Ordinal),
                DlcVariantOf(instance),
                InstanceLabel.IsResting(instance.Record.Id, restingId)))];

        // The disk is the authority on which DLC an instance carries, everywhere that answer decides
        // identity rather than only display: TryDetect fails only when the instance's own game folder
        // cannot currently be resolved at all (a referenced install on a drive that is disconnected or
        // renamed), and the record, however stale, is a better answer than an empty set in that one
        // case, since an empty set would offer a redownload of a DLC that most likely is still there.
        private static GameDlcSet DlcVariantOf(InstalledInstance instance) =>
            GameDlcDetector.TryDetect(instance.Record.GameFolder, out var detected) ? detected : instance.Record.Dlc;

        // Queued rather than run inside the setter: switching rebuilds this list, and doing that
        // while the ComboBox is still closing its own popup is what makes a picked item look like it
        // did nothing at all.
        partial void OnSelectedVersionChanged(GameVersionRowViewModel? value)
        {
            if (isSelecting || value is null)
                return;

            if (App.AppWindow?.DispatcherQueue is { } queue)
                queue.TryEnqueue(async () => await SwitchAsync(value));
            else
                _ = SwitchAsync(value);
        }

        // The one way anything outside Play makes a version active. The Versions page's own menu item
        // comes through here rather than calling InstanceManager.SetActive itself, so a switch
        // started from either page runs the same refusals, writes the same sentence and rebuilds the
        // same list: one route rather than two that can drift apart.
        public async Task SwitchToInstanceAsync(string instanceId)
        {
            var row = InstalledVersions.FirstOrDefault(candidate =>
                string.Equals(candidate.Instance?.Record.Id, instanceId, StringComparison.Ordinal));

            if (row is null)
            {
                StatusMessage = Strings.Current["Versions.Switch.NotListed"];
                return;
            }

            await SwitchAsync(row);
        }

        // Picking a version on Play is the whole gesture, and the gesture is a switch: every row in
        // that dropdown is already on this machine, so nothing here reaches the network or the
        // downloader. Every refusal puts the dropdown back on the version that is actually playing,
        // so it never shows a version the user is not on.
        private async Task SwitchAsync(GameVersionRowViewModel row)
        {
            if (IsBusy)
            {
                SelectActive();
                return;
            }

            if (row.IsActive)
                return;

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Versions.Switch.GameRunning"];
                SelectActive();
                return;
            }

            // Every row here is either backed by an instance or is the install BEM has not adopted
            // yet, and that one is always the active row, which returned above. This is what the
            // nullable type demands rather than a case the dropdown can produce.
            if (row.Instance is not { } instance)
            {
                SelectActive();
                return;
            }

            IsBusy = true;

            try
            {
                instanceManager.SetActive(instance.Record.Id);

                var restingId = instanceManager.RestingInstanceId;
                var isResting = string.Equals(instance.Record.Id, restingId, StringComparison.Ordinal);

                StatusMessage = isResting
                    ? Strings.Current.Format("Versions.Switch.Success", row.DisplayLabel)
                    : Strings.Current.Format("Versions.Switch.SuccessNotResting", row.DisplayLabel);

                await RefreshAsync();
                ActiveInstanceChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message;
                SelectActive();
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or HttpRequestException
                    or System.ComponentModel.Win32Exception)
            {
                LoggingService.LogException(ex, $"Failed to switch to {row.Label}");
                StatusMessage = Strings.Current.Format("Versions.Switch.Error", row.DisplayLabel, ex.Message);
                SelectActive();
            }
            finally
            {
                IsBusy = false;
            }
        }

        // The running download's own source, held for as long as that download runs and dropped the
        // moment it ends. One at a time, because IsBusy already refuses a second.
        private CancellationTokenSource? downloadCancellation;

        private bool CanStopDownload() => IsDownloading;

        // The other half of "start it". The stop reaches DepotDownloader itself rather than only the
        // code awaiting it, and everything already transferred stays on the disk: the folder the
        // download was writing into is exactly the shape Unfinished Downloads recognizes, so a
        // stopped 58 GB fetch is carried on rather than started over.
        [RelayCommand(CanExecute = nameof(CanStopDownload))]
        private void StopDownload()
        {
            StatusMessage = Strings.Current["Versions.Download.Stopping"];
            downloadCancellation?.Cancel();
        }

        // Downloads a version this machine does not have. Reached only from the Versions page's own
        // Download button, so the click that reaches this method is always the one that started it:
        // no page load, no refresh and no selection changing anywhere gets here. The download does
        // not switch Play to what it fetched, because the user asked to download, not to play: the
        // new version turns up in Play's dropdown and is switched to there.
        // resumeFolder is the folder a stopped download already filled, and a resume goes back into
        // it whatever the dialog composes now: the name and the purpose still reach the record, and
        // the next start brings the folder into line with them, but bytes already on the disk are
        // never fetched a second time over a name.
        public async Task<bool> DownloadAsync(GameVersionRowViewModel row, string? resumeFolder = null)
        {
            ArgumentNullException.ThrowIfNull(row);

            if (IsBusy)
                return false;

            // The download itself writes only into a new instance folder, but the first one on a
            // machine has to adopt the current install first, and that moves what sits at the game's
            // own paths.
            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Versions.AddDlc.GameRunning"];
                return false;
            }

            using var cancellation = new CancellationTokenSource();

            IsBusy = true;
            IsDownloading = true;
            downloadCancellation = cancellation;

            try
            {
                if (await AcquireAsync(row, cancellation.Token, resumeFolder) is null)
                    return false;

                StatusMessage = acquireWarning ?? Strings.Current.Format("Versions.Download.Succeeded", row.Label);
                await RefreshAsync();
                return true;
            }
            catch (Exception ex) when (DownloadCancellation.WasStoppedByUser(ex, cancellation.Token))
            {
                LoggingService.Log($"The download of {row.Label} was stopped from the Versions page.");
                StatusMessage = Strings.Current.Format("Versions.Download.Stopped", row.Label);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message;
                return false;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or HttpRequestException
                    or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // OperationCanceledException is in that list because not every one of them is a
                // stop: HttpClient raises TaskCanceledException on its own timeout, and a download
                // that timed out failed. The filter above has already taken the ones the user asked
                // for.
                LoggingService.LogException(ex, $"Failed to download {row.Label}");
                StatusMessage = Strings.Current.Format("Versions.Download.Error", row.Label, ex.Message);
                return false;
            }
            finally
            {
                downloadCancellation = null;
                IsDownloading = false;
                IsBusy = false;
            }
        }

        // Two ways to get a version that is not here, and the user picks. BEM's own downloader signs
        // in by QR, which needs a phone in front of the screen and is no use over a remote desktop
        // session; the Steam client on this machine is already signed in and can fetch the same build
        // from its console. Returns the instance whichever route produced, or null when the user said
        // no, chose the Steam route (which finishes later, by importing), or it failed.
        private async Task<InstalledInstance?> AcquireAsync(
            GameVersionRowViewModel row, CancellationToken cancellationToken, string? resumeFolder = null)
        {
            acquireWarning = null;
            branchWarning = null;
            EnsureGamesRoot();

            var depots = await DepotsForAsync(row.Branch, cancellationToken);
            var adoptionNeeded = instanceManager.RestingInstanceId is null;
            var remembered = settingsStore.Read().SteamAccountName;

            // Each DLC in the row's variant is its own Steam app with its own depots, so its bytes
            // never show up in the base app's own info; without adding them here the confirmation
            // dialog quoted the base game's size for a "+War Sails" row and understated it by the
            // DLC's own ~31 GB.
            var dlcDownloadBytes = 0L;
            var dlcInstalledBytes = 0L;

            foreach (var dlcAppId in row.Entry.Variant.AppIds)
            {
                var dlcDepots = await DlcDepotsForAsync(dlcAppId, row.Branch, cancellationToken);
                dlcDownloadBytes += SteamDepotCatalog.TotalDownloadBytes(dlcDepots);
                dlcInstalledBytes += SteamDepotCatalog.InstalledSizeBytes(dlcDepots);
            }

            var size = depots.Count == 0 && dlcInstalledBytes == 0
                ? Strings.Current["Versions.Acquire.WholeGame"]
                : Strings.Current.Format(
                    "Versions.Acquire.Size",
                    FormatBytes(SteamDepotCatalog.TotalDownloadBytes(depots) + dlcDownloadBytes),
                    FormatBytes(SteamDepotCatalog.InstalledSizeBytes(depots) + dlcInstalledBytes));

            var body = Strings.Current.Format("Versions.Acquire.Dialog.Body", size, GamesRoot)
                + (remembered.Length > 0
                    ? Strings.Current.Format("Versions.AddDlc.Dialog.SignedIn", remembered)
                    : Strings.Current["Versions.AddDlc.Dialog.NeedsSignIn"])
                + (adoptionNeeded
                    ? Strings.Current["Versions.Acquire.Dialog.AdoptionNeeded"]
                    : string.Empty);

            // The name and the purpose are asked for in the same breath as the download and never
            // after it, because the folder is composed from both of them before a byte is fetched:
            // a version declared afterwards is born under a name that is already wrong.
            //
            // A second copy of a version already here is asked for precisely because two rows reading
            // the same thing is no use, so its box arrives holding a bare disambiguator, selected so
            // a better name replaces it whole, and carrying no version because the folder composes
            // the version itself and would otherwise state it twice. An emptied box falls back to it
            // rather than to the name the first copy wears. A first download's box arrives empty and
            // an empty box is the version's own name: naming is offered to every download all the
            // same, because a testing install has the same reason to be told apart as a copy does.
            var suggestedName = ResumedName(row, resumeFolder)
                ?? (row.Entry.IsAnotherCopy ? SuggestedCopyName() : string.Empty);

            var nameBox = new TextBox
            {
                Header = Strings.Current["Versions.Acquire.Dialog.NameLabel"],
                Text = suggestedName,
                SelectionStart = 0,
                SelectionLength = suggestedName.Length
            };

            ToolTipService.SetToolTip(nameBox, Strings.Current["Versions.Acquire.Dialog.NameTooltip"]);

            var panel = AcquirePanel(
                body,
                row.Entry.IsAnotherCopy
                    ? Strings.Current.Format("Versions.Acquire.Dialog.CopyName", row.Label)
                    : null,
                nameBox);

#if DEV_BEM
            var purposeBox = PurposeBox();
            panel.Children.Add(purposeBox);
#endif

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current.Format("Versions.Acquire.Dialog.Title", row.Label)),
                Content = panel,
                PrimaryButtonText = remembered.Length > 0
                    ? Strings.Current.Format("Versions.AddDlc.Dialog.PrimaryButton.SignedIn", remembered)
                    : Strings.Current["Versions.AddDlc.Dialog.PrimaryButton.QrCode"],
                SecondaryButtonText = remembered.Length > 0
                    ? Strings.Current["Versions.AddDlc.Dialog.SecondaryButton.QrCode"]
                    : Strings.Current["Versions.AddDlc.Dialog.SecondaryButton.Account"],
                CloseButtonText = Strings.Current["Versions.AddDlc.Dialog.Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            // The primary button carries the account name, which is as long as the account name is.
            DialogText.KeepTextWhole(dialog);

            var choice = await dialog.ShowAsync();

            if (choice == ContentDialogResult.None)
            {
                StatusMessage = Strings.Current.Format("Versions.AddDlc.NotAdded", row.Label);
                return null;
            }

            // The box is not capped, so a name too long to store is a sentence the user reads before a
            // 60 GB download starts rather than a name that silently went missing after one. Rename
            // says the same thing about the same limit.
            var chosen = InstanceNaming.Read(nameBox.Text);

            if (chosen.Outcome == InstanceNameOutcome.TooLong)
            {
                StatusMessage = Strings.Current.Format("Versions.Rename.TooLong", InstanceNaming.MaxLength);
                return null;
            }

            // An emptied box falls back to the offer, which is a disambiguator for a copy and nothing
            // at all for a first download: there is no offer to fall back to there, and the version's
            // own name is what the folder composes without one.
            var copyName = chosen.Stored ?? suggestedName;

            // Nothing in the public product declares a testing install, so nothing there composes the
            // prefix either: the folder is the name and the version, and that is the whole of it.
            var purpose = InstancePurpose.Unspecified;

#if DEV_BEM
            purpose = PurposeChoices[Math.Max(purposeBox.SelectedIndex, 0)];
#endif

            // With an account remembered the primary button reuses it; without one the primary button
            // is the QR code, because it is the route that asks for nothing at all.
            var wantsAccount = remembered.Length > 0
                ? choice == ContentDialogResult.Primary
                : choice == ContentDialogResult.Secondary;

            var signIn = SteamSignIn.QrCode;

            if (wantsAccount)
            {
                if (await AccountSignInAsync(remembered) is not { } account)
                {
                    StatusMessage = Strings.Current.Format("Versions.AddDlc.NotAdded", row.Label);
                    return null;
                }

                signIn = account;
            }

            if (adoptionNeeded && !AdoptCurrentInstall())
                return null;

            IsProgressVisible = true;
            ProgressValue = 0;
            QrBlock = string.Empty;

            // The folder is picked from the very record Register will write, composed by the one
            // factory both of them use, and without touching the filesystem: FolderForDownload only
            // reads what already exists. Everything the folder states has to go in here - the
            // version, the variant, the name just typed and the purpose just chosen - or the download
            // creates a folder the instance does not keep.
            var registry = new InstanceRegistry(GamesRoot);
            var namingRecord = InstanceDraft.ForDownload(
                row.Version, row.Branch, row.BuildId, row.Entry.Variant,
                copyName.Length > 0 ? copyName : null, purpose);

            var instanceFolder = resumeFolder ?? registry.FolderForDownload(namingRecord);

            try
            {
                var tool = new DepotDownloaderTool(GamesRoot, message => LoggingService.Log(message));
                var gameFolder = InstanceLayout.GameFolder(instanceFolder);
                InstalledInstance? registered = null;

                LoggingService.Log(
                    $"Downloading {row.Label} (branch {row.Branch}) into '{instanceFolder}' as "
                    + $"{(signIn.Kind == SteamSignInKind.Account ? signIn.AccountName : "a QR code sign-in")}.");

                StatusMessage = Strings.Current["Versions.AddDlc.Preparing"];
                await tool.EnsureInstalledAsync(cancellationToken);

                // Last thing before a process exists: the tool is already on disk, so what stands
                // between the code being read and DepotDownloader needing it is one process start
                // and Steam's own login handshake.
                if (await PrepareGuardCodeAsync(signIn) is not { } plan)
                {
                    StatusMessage = Strings.Current["Versions.SignIn.Declined"];
                    return null;
                }

                guardPlan = plan;

                // Every Steam app this flow will touch, signed in to here and nowhere else. The set
                // is worked out from the row's own variant before Steam is contacted at all, so a
                // DLC's own DepotDownloader run an hour from now is already authenticated rather
                // than opening a second Steam Guard prompt of its own.
                var signedIn = await SignInForFlowAsync(
                    tool,
                    FlowSignInPlan.ForVariantDownload(row.Entry.Variant),
                    row.Branch,
                    new Dictionary<int, IReadOnlyList<SteamDepot>> { [DepotDownloaderTool.BannerlordAppId] = depots },
                    signIn,
                    cancellationToken);

                if (signedIn.Aborted)
                {
                    StatusMessage = SignInStoppedText();
                    return null;
                }

                // A DLC whose sign-in the user closed is dropped from the download rather than
                // attempted: attempting it would open the same prompt again, and the instance record
                // only ever names what actually landed.
                var attemptable = new GameDlcSet(
                    row.Entry.Variant.AppIds.Where(appId => !signedIn.Declined.Contains(appId)));

                if (signedIn.Declined.Count > 0)
                {
                    acquireWarning = string.Join(
                        " ",
                        signedIn.Declined.Select(appId =>
                            Strings.Current.Format("Versions.SignIn.DlcSkipped", DlcNameFor(appId))));
                    StatusMessage = acquireWarning;
                }

                // VariantDownloadFlow, not statement order, is what guarantees the DLC below is never
                // attempted before this delegate returns true, and never attempted at all if it
                // returns false: that guarantee is tested in Core, against fakes, not trusted to stay
                // true because these two blocks happen to be written in this order today.
                var flow = await VariantDownloadFlow.RunAsync(
                    async baseCancellationToken =>
                    {
                        DownloadProgress? last = null;
                        string? retryNotice = null;
                        var progress = new Progress<DownloadProgress>(update =>
                        {
                            last = update;
                            ProgressValue = update.Percent;
                            QrBlock = update.QrBlock;

                            StatusMessage = update.Stage == DownloadStage.AwaitingSignIn
                                ? Strings.Current["Versions.Acquire.Progress.AwaitingSignIn"]
                                : WithNotice(retryNotice, update.Message);
                        });

                        // Into Game\, which is where every instance keeps its game: downloading into
                        // the instance folder itself left the version registered against a Game
                        // folder that did not exist, so it listed as installed and could never be
                        // launched.
                        var run = await tool.DownloadAsync(
                            row.Branch, gameFolder, signIn, AnswerPromptAsync, progress, baseCancellationToken);

                        // One more attempt, without restarting a 58 GB download: a run
                        // that was given a Steam Guard code and still ended has either had the code
                        // accepted, so the token is stored and this run signs in silently, or ended
                        // before the code reached it, which is the same run worth repeating.
                        if (DownloadRetryPolicy.ShouldRetry(run, alreadyRetried: false, baseCancellationToken))
                        {
                            retryNotice = Strings.Current["Versions.Acquire.Retrying"];
                            StatusMessage = retryNotice;
                            LoggingService.Log(
                                $"The download of {row.Label} ended after a Steam Guard code was given ({run.Message}), "
                                + "so BEM is running it once more.");
                            guardPlan.StartRun();
                            last = null;

                            run = await tool.DownloadAsync(
                                row.Branch, gameFolder, signIn, AnswerPromptAsync, progress, baseCancellationToken);
                        }

                        // Recorded whatever else the run did: a version assembled out of two
                        // branches is worth saying even when every byte arrived.
                        if (run.BranchFallbacks.Count > 0)
                            branchWarning = Strings.Current.Format(
                                "Versions.Acquire.MixedBranch",
                                row.Label,
                                row.Branch,
                                BranchFallbackReader.DepotList(run.BranchFallbacks),
                                run.BranchFallbacks[0].UsedBranch);

                        if (last is { Stage: DownloadStage.Failed } failed)
                        {
                            StatusMessage = Strings.Current.Format("Versions.Acquire.Progress.Failed", failed.Message);
                            return false;
                        }

                        // The bytes decide, not the exit code. DepotDownloader has exited quietly
                        // having transferred nothing, and taking that as a finished download wrote
                        // the version down, switched Play to it, and showed seven official modules as
                        // the load order.
                        var published = SteamDepotCatalog.InstalledSizeBytes(depots);
                        var onDisk = await Task.Run(() => UninstallReport.SizeOf(gameFolder), baseCancellationToken);

                        if (!InstanceCompleteness.Holds(onDisk, published))
                        {
                            var toolLog = DepotDownloaderTool.LogPathFor(
                                Path.Combine(InstanceLayout.ToolsFolder(GamesRoot), "DepotDownloader.exe"));

                            StatusMessage = Strings.Current.Format(
                                "Versions.Acquire.Incomplete",
                                row.Label,
                                InstanceCompleteness.Shortfall(onDisk, published),
                                instanceFolder,
                                LastLineOf(toolLog),
                                toolLog);
                            return false;
                        }

                        // What the files say beats what the branch was called: the branch moves and
                        // the version in the folder is the one about to be played.
                        var downloaded = GameVersionReader.Read(gameFolder);

                        if (downloaded.IsEmpty)
                        {
                            StatusMessage = Strings.Current.Format("Versions.Acquire.NotAGame", row.Label, instanceFolder);
                            return false;
                        }

                        var displayName = downloaded.ToString();

                        // Only after a download that worked: a name that could not sign in is worse
                        // than no name, because it would be reused silently on every later download.
                        if (signIn.Kind == SteamSignInKind.Account)
                            settingsStore.Write(settingsStore.Read() with { SteamAccountName = signIn.AccountName });

                        // The base instance is recorded here, with no DLC, whether or not the row
                        // asked for one: a DLC download that fails afterward must still leave a
                        // working, correctly-recorded base instance behind, not an unregistered
                        // folder. The row's variant is handed over all the same, because the Id is
                        // minted once and here: it names the same variant the folder already does.
                        registered = instanceManager.Register(
                            instanceFolder, displayName, row.Branch, row.BuildId, row.Entry.Variant,
                            copyName.Length > 0 ? copyName : null, purpose);
                        return true;
                    },
                    attemptable,
                    (dlcAppId, dlcCancellationToken) =>
                        DownloadDlcAsync(tool, dlcAppId, row.Branch, gameFolder, signIn, dlcCancellationToken),
                    cancellationToken);

                if (!flow.BaseSucceeded)
                    return null;

                // Recorded even on a partial run: what actually landed, never what was asked for.
                if (!flow.Dlc.Downloaded.IsEmpty)
                    registered = instanceManager.SetDlc(registered!.Record.Id, flow.Dlc.Downloaded);

                if (!attemptable.IsEmpty && !flow.Dlc.Succeeded)
                {
                    // Steam's own words, not a guess about ownership: BEM does not know why the DLC
                    // was refused, only that it was.
                    StatusMessage = Strings.Current.Format(
                        "Versions.Acquire.DlcPartial",
                        registered!.Record.DisplayName,
                        DlcNameFor(flow.Dlc.FailedAppId ?? 0),
                        flow.Dlc.FailureMessage);
                    acquireWarning = StatusMessage;
                }

                // Both can be true at once, and neither replaces the other: a DLC that was refused
                // and a base game assembled out of two branches are two different things to know.
                if (branchWarning is not null)
                {
                    acquireWarning = acquireWarning is null ? branchWarning : $"{acquireWarning} {branchWarning}";
                    StatusMessage = acquireWarning;
                }

                return registered;
            }
            finally
            {
                pendingPassword = null;
                guardPlan?.Discard();
                guardPlan = null;
                IsProgressVisible = false;
                ProgressValue = 0;
                QrBlock = string.Empty;
            }
        }

        // Every Steam app this flow is going to ask for, signed in to before anything downloads and
        // back to back, so all of them fall inside the life of the one code the user typed. A Steam
        // Guard code rotates roughly every 30 seconds: two authenticating runs seconds apart can
        // share one, two an hour apart cannot, and the DLC run an hour into a variant download is
        // exactly the second prompt this closes.
        //
        // DepotDownloader's remembered token is keyed by account name rather than by app, so in the
        // ordinary case only the first run here is challenged and the rest sign in silently. The
        // runs are still made one per app on purpose: each is -manifest-only and transfers no
        // content, it is the only arrangement that still asks once if that token turns out not to be
        // account-wide, and an app Steam will not grant is found out now rather than after 88 GB.
        private Task<FlowSignInOutcome> SignInForFlowAsync(
            DepotDownloaderTool tool,
            IReadOnlyList<SteamSignInTarget> targets,
            string branch,
            IReadOnlyDictionary<int, IReadOnlyList<SteamDepot>> knownDepots,
            SteamSignIn signIn,
            CancellationToken cancellationToken) =>
            FlowSignInSequence.RunAsync(
                targets,
                async (appId, appCancellationToken) =>
                {
                    var depots = knownDepots.TryGetValue(appId, out var already)
                        ? already
                        : await DepotHintForAsync(appId, branch, appCancellationToken);

                    return await SignInOnceAsync(tool, appId, branch, depots, signIn, appCancellationToken);
                },
                cancellationToken);

        // The smallest depot only makes an authenticating run cheaper, so a fetch that fails costs
        // the hint and not the sign-in: without one the run fetches every manifest that app
        // publishes, which is still manifests rather than content.
        private static async Task<IReadOnlyList<SteamDepot>> DepotHintForAsync(
            int appId, string branch, CancellationToken cancellationToken)
        {
            try
            {
                return await DlcDepotsForAsync(appId, branch, cancellationToken);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                LoggingService.Log(
                    $"The depot list for app {appId} could not be read ({ex.Message}), so its Steam sign-in run "
                    + "fetches every manifest instead of one.",
                    LogLevel.Warn);

                return [];
            }
        }

        // Steam is signed in to before the download rather than during it. DepotDownloader answers a
        // Steam Guard prompt on its standard input, and Steam asks for one when it feels like it: on
        // a first sign-in that moment landed minutes into a 58.7 GB run, and a prompt BEM would not
        // answer twice ended the run with 0.2 GB on the disk. A short run that authenticates and
        // fetches one manifest moves the prompt to the front of the job, where answering it costs
        // seconds, and -remember-password leaves the token behind so the download itself never needs
        // standard input at all. It is the same thing that makes a second attempt work today.
        //
        // Declined only when the user closed the prompt: a sign-in that failed for any other reason
        // is logged and the download goes ahead and signs in on its own, exactly as it did before
        // this existed.
        private async Task<SteamSignInStatus> SignInOnceAsync(
            DepotDownloaderTool tool,
            int appId,
            string branch,
            IReadOnlyList<SteamDepot> depots,
            SteamSignIn signIn,
            CancellationToken cancellationToken)
        {
            var runningText = appId == DepotDownloaderTool.BannerlordAppId
                ? Strings.Current["Versions.SignIn.Running"]
                : Strings.Current.Format("Versions.SignIn.RunningForDlc", DlcNameFor(appId));
            StatusMessage = runningText;

            var progress = new Progress<DownloadProgress>(update =>
            {
                QrBlock = update.QrBlock;

                StatusMessage = update.Stage == DownloadStage.AwaitingSignIn
                    ? Strings.Current["Versions.Acquire.Progress.AwaitingSignIn"]
                    : runningText;
            });

            var depotId = SteamDepotCatalog.SmallestDepot(depots)?.DepotId;
            var retried = false;

            while (true)
            {
                // Each run of the tool gets its own single live ask, so a retry can still answer a
                // prompt the first run never got to.
                guardPlan?.StartRun();

                var result = await tool.SignInAsync(
                    appId, branch, depotId, signIn, AnswerPromptAsync, progress, cancellationToken);

                QrBlock = string.Empty;

                // The exit code is asked first on purpose. A sign-in run that ends at zero has
                // signed in, and a question BEM could not answer along the way does not undo that:
                // reading the question first reported three successful runs as failures and asked
                // for a Steam Guard code after each of them.
                if (result.Succeeded)
                    return SteamSignInStatus.Authenticated;

                if (result.UnansweredPrompt is not null)
                    return SteamSignInStatus.Declined;

                if (DownloadRetryPolicy.ShouldRetry(result, retried, cancellationToken))
                {
                    retried = true;
                    runningText = Strings.Current["Versions.SignIn.Retrying"];
                    StatusMessage = runningText;
                    LoggingService.Log(
                        $"The Steam sign-in run for app {appId} ended after a Steam Guard code was given "
                        + $"({result.Message}), so BEM is running it once more.");
                    continue;
                }

                LoggingService.Log(
                    $"The Steam sign-in run for app {appId} exited with code {result.ExitCode}, so its download "
                    + "will sign in on its own.",
                    LogLevel.Warn);

                return SteamSignInStatus.Failed;
            }
        }

        private static string DlcNameFor(int appId) =>
            GameDlc.ById(appId)?.DisplayName ?? Strings.Current.Format("Versions.Acquire.UnknownDlcName", appId);

        // A status line that keeps saying it is a retry while the retry runs. Progress reports
        // several times a second, so a one-off "trying again" line is gone before it can be read.
        private static string WithNotice(string? notice, string message) =>
            notice is null || notice.Length == 0 ? message : $"{notice} {message}";

        // One DLC download, reduced to what DlcDownloadFlow needs to decide whether to keep going:
        // whether it worked, and Steam's own last word if it did not. Read off the last progress
        // reported the same way the base download already is, at AcquireAsync's own "the bytes
        // decide, not the exit code" line - a DepotDownloader run that exits quietly having
        // transferred nothing is not success there, and is not success here either.
        // DlcDownloadStep owns the decision (does this app publish the branch, did the tool report
        // failure, did the bytes actually land); this method only supplies what Core cannot reach on
        // its own - the network fetch, the process, and the folder on disk.
        private Task<DlcDownloadResult> DownloadDlcAsync(
            DepotDownloaderTool tool, int dlcAppId, string branch, string targetFolder, SteamSignIn signIn,
            CancellationToken cancellationToken) =>
            DlcDownloadStep.RunAsync(
                dlcAppId,
                branch,
                fetchCancellationToken => SteamBranchCatalog.FetchJsonAsync(dlcAppId.ToString(), fetchCancellationToken),
                async runCancellationToken =>
                {
                    DownloadProgress? last = null;
                    string? retryNotice = null;
                    var progress = new Progress<DownloadProgress>(update =>
                    {
                        last = update;
                        ProgressValue = update.Percent;
                        QrBlock = update.QrBlock;

                        StatusMessage = update.Stage == DownloadStage.AwaitingSignIn
                            ? Strings.Current["Versions.Acquire.Progress.AwaitingSignIn"]
                            : WithNotice(retryNotice, update.Message);
                    });

                    var run = await tool.DownloadDlcAsync(
                        dlcAppId, branch, targetFolder, signIn, AnswerPromptAsync, progress, runCancellationToken);

                    // The same one retry the base download gets, for the same reason: a run that
                    // answered a Steam Guard code and still ended is the run worth repeating.
                    if (DownloadRetryPolicy.ShouldRetry(run, alreadyRetried: false, runCancellationToken))
                    {
                        retryNotice = Strings.Current["Versions.Acquire.Retrying"];
                        StatusMessage = retryNotice;
                        LoggingService.Log(
                            $"The download of app {dlcAppId} ended after a Steam Guard code was given ({run.Message}), "
                            + "so BEM is running it once more.");
                        guardPlan?.StartRun();
                        last = null;

                        await tool.DownloadDlcAsync(
                            dlcAppId, branch, targetFolder, signIn, AnswerPromptAsync, progress, runCancellationToken);
                    }

                    // null tells DlcDownloadStep the tool itself reported nothing wrong, so it goes
                    // on to check the bytes; a result here short-circuits straight to Steam's own
                    // failure message.
                    return last is { Stage: DownloadStage.Failed } failed
                        ? new DlcDownloadResult(false, failed.Message)
                        : null;
                },
                () => UninstallReport.SizeOf(targetFolder),
                cancellationToken);

        // Adds one DLC BEM knows about to an instance that does not have it yet, into the game
        // folder that instance already occupies: the base game is never touched. Called only from
        // the Versions page's own Add button, so the click that reaches this method is always the
        // one that started it, never a refresh or a page navigation. IsBusy is checked and held for
        // the whole run, the same guard SwitchAsync and DownloadAsync use, so a second click while
        // this one is still running does nothing rather than starting a second download.
        public async Task<bool> AddDlcAsync(InstalledInstance instance, GameDlcInfo dlc)
        {
            ArgumentNullException.ThrowIfNull(instance);
            ArgumentNullException.ThrowIfNull(dlc);

            if (IsBusy)
                return false;

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Versions.AddDlc.GameRunning"];
                return false;
            }

            var remembered = settingsStore.Read().SteamAccountName;

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(
                    Strings.Current.Format("Versions.AddDlc.Dialog.Title", dlc.DisplayName, instance.Record.DisplayName)),
                Content = Strings.Current.Format("Versions.AddDlc.Dialog.Body", dlc.DisplayName)
                    + (remembered.Length > 0
                        ? Strings.Current.Format("Versions.AddDlc.Dialog.SignedIn", remembered)
                        : Strings.Current["Versions.AddDlc.Dialog.NeedsSignIn"]),
                PrimaryButtonText = remembered.Length > 0
                    ? Strings.Current.Format("Versions.AddDlc.Dialog.PrimaryButton.SignedIn", remembered)
                    : Strings.Current["Versions.AddDlc.Dialog.PrimaryButton.QrCode"],
                SecondaryButtonText = remembered.Length > 0
                    ? Strings.Current["Versions.AddDlc.Dialog.SecondaryButton.QrCode"]
                    : Strings.Current["Versions.AddDlc.Dialog.SecondaryButton.Account"],
                CloseButtonText = Strings.Current["Versions.AddDlc.Dialog.Cancel"],
                // Unlike AcquireAsync's dialog, this one starts a roughly 31 GB download: an Enter
                // keystroke on a focused dialog is not the deliberate click that should begin that,
                // so Close (Cancel) is the default here rather than Primary.
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            var choice = await dialog.ShowAsync();

            if (choice == ContentDialogResult.None)
            {
                StatusMessage = Strings.Current.Format("Versions.AddDlc.NotAdded", dlc.DisplayName);
                return false;
            }

            var wantsAccount = remembered.Length > 0
                ? choice == ContentDialogResult.Primary
                : choice == ContentDialogResult.Secondary;

            var signIn = SteamSignIn.QrCode;

            if (wantsAccount)
            {
                if (await AccountSignInAsync(remembered) is not { } account)
                {
                    StatusMessage = Strings.Current.Format("Versions.AddDlc.NotAdded", dlc.DisplayName);
                    return false;
                }

                signIn = account;
            }

            using var cancellation = new CancellationTokenSource();

            IsBusy = true;
            IsDownloading = true;
            downloadCancellation = cancellation;
            IsProgressVisible = true;
            ProgressValue = 0;
            QrBlock = string.Empty;

            try
            {
                var tool = new DepotDownloaderTool(GamesRoot, message => LoggingService.Log(message));

                StatusMessage = Strings.Current["Versions.AddDlc.Preparing"];
                await tool.EnsureInstalledAsync(cancellation.Token);

                LoggingService.Log(
                    $"Downloading {dlc.DisplayName} (app {dlc.AppId}, branch {instance.Record.Branch}) into "
                    + $"'{InstanceLayout.GameFolder(instance.Folder)}' as "
                    + $"{(signIn.Kind == SteamSignInKind.Account ? signIn.AccountName : "a QR code sign-in")}.");

                var depots = await DlcDepotsForAsync(dlc.AppId, instance.Record.Branch, cancellation.Token);

                if (await PrepareGuardCodeAsync(signIn) is not { } plan)
                {
                    StatusMessage = Strings.Current["Versions.SignIn.Declined"];
                    return false;
                }

                guardPlan = plan;

                // One app, so one authenticating run, and the same flow-level step the variant
                // download uses rather than a second way of doing it.
                var signedIn = await SignInForFlowAsync(
                    tool,
                    FlowSignInPlan.ForDlcAddition(dlc.AppId),
                    instance.Record.Branch,
                    new Dictionary<int, IReadOnlyList<SteamDepot>> { [dlc.AppId] = depots },
                    signIn,
                    cancellation.Token);

                if (signedIn.Aborted)
                {
                    StatusMessage = SignInStoppedText();
                    return false;
                }

                var result = await DownloadDlcAsync(
                    tool, dlc.AppId, instance.Record.Branch, InstanceLayout.GameFolder(instance.Folder), signIn,
                    cancellation.Token);

                if (!result.Succeeded)
                {
                    StatusMessage = Strings.Current.Format("Versions.AddDlc.Failed", dlc.DisplayName, result.FailureMessage);
                    return false;
                }

                if (signIn.Kind == SteamSignInKind.Account)
                    settingsStore.Write(settingsStore.Read() with { SteamAccountName = signIn.AccountName });

                // Replaces the recorded set, so what was already on the instance has to be carried
                // forward: SetDlc writes exactly the set it is given, never a union of its own.
                var updated = new GameDlcSet([.. instance.Record.Dlc.AppIds, dlc.AppId]);
                instanceManager.SetDlc(instance.Record.Id, updated);

                StatusMessage = Strings.Current.Format("Versions.AddDlc.Succeeded", dlc.DisplayName, instance.Record.DisplayName);
                await RefreshAsync();
                return true;
            }
            catch (Exception ex) when (DownloadCancellation.WasStoppedByUser(ex, cancellation.Token))
            {
                LoggingService.Log($"Adding {dlc.DisplayName} was stopped from the Versions page.");
                StatusMessage = Strings.Current.Format("Versions.AddDlc.Stopped", dlc.DisplayName);
                return false;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or HttpRequestException
                    or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to add {dlc.DisplayName} to {instance.Record.DisplayName}");
                StatusMessage = Strings.Current.Format("Versions.AddDlc.Error", dlc.DisplayName, ex.Message);
                return false;
            }
            finally
            {
                pendingPassword = null;
                guardPlan?.Discard();
                guardPlan = null;
                IsProgressVisible = false;
                ProgressValue = 0;
                QrBlock = string.Empty;
                downloadCancellation = null;
                IsDownloading = false;
                IsBusy = false;
            }
        }

        // Asks for the account name and password once, then never again: DepotDownloader is run with
        // -remember-password and keeps its own token, and BEM writes down only the name. The password
        // lives in this method and in the process it is handed to, and nowhere else; a remembered
        // account skips the dialog entirely.
        private async Task<SteamSignIn?> AccountSignInAsync(string remembered)
        {
            if (remembered.Length > 0)
                return SteamSignIn.ForAccount(remembered);

            var account = new TextBox
            {
                Header = Strings.Current["Versions.SignIn.Dialog.AccountLabel"],
                PlaceholderText = Strings.Current["Versions.SignIn.Dialog.AccountPlaceholder"]
            };
            var password = new PasswordBox
            {
                Header = Strings.Current["Versions.SignIn.Dialog.PasswordLabel"],
                Margin = new Thickness(0, 10, 0, 0)
            };

            var panel = new StackPanel { Spacing = 4, MinWidth = 320 };
            panel.Children.Add(account);
            panel.Children.Add(password);
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Text = Strings.Current["Versions.SignIn.Dialog.Disclaimer"]
            });

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current["Versions.SignIn.Dialog.Title"]),
                Content = panel,
                PrimaryButtonText = Strings.Current["Versions.SignIn.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.AddDlc.Dialog.Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            var name = account.Text.Trim();

            if (name.Length == 0)
            {
                StatusMessage = Strings.Current["Versions.SignIn.Dialog.NameRequired"];
                return null;
            }

            pendingPassword = password.Password;

            return SteamSignIn.ForAccount(name);
        }

        // DepotDownloader asks on its standard input and waits. The password is answered from what was
        // just typed, once; a Steam Guard code has to be asked for as it happens, because Steam only
        // demands one at that moment.
        private async Task<string?> AnswerPromptAsync(SteamPrompt prompt)
        {
            if (prompt.Kind == SteamPromptKind.Password && pendingPassword is { Length: > 0 } password)
            {
                pendingPassword = null;
                return password;
            }

            // A code read off the authenticator before the process started is written straight
            // back, in microseconds, which is the whole reason it was collected then. Only when
            // there is none does a dialog open over a waiting process, and only once per run: a
            // Steam Guard code cannot be reused, so a second ask in one run would be the start of a
            // parade of dialogs across a long download.
            if (prompt.Kind == SteamPromptKind.GuardCode)
            {
                switch (guardPlan?.NextSource() ?? SteamGuardSource.Ask)
                {
                    case SteamGuardSource.Prepared:
                        return guardPlan!.TakePrepared();

                    case SteamGuardSource.Refuse:
                        // Recorded, not only shown: this status line is about to be overwritten by
                        // the flow reporting that the sign-in ended, and "you did not finish it" is
                        // the wrong reason for a stop BEM decided on.
                        guardPlan!.RefuseRepeat();
                        StatusMessage = Strings.Current["Versions.Prompt.GuardRepeat"];
                        return null;

                    default:
                        guardPlan?.Asked();
                        break;
                }
            }

            var input = prompt.Kind == SteamPromptKind.Password
                ? new PasswordBox { Header = Strings.Current["Versions.SignIn.Dialog.PasswordLabel"] } as Control
                : new TextBox
                {
                    Header = Strings.Current["Versions.Prompt.GuardLabel"],
                    PlaceholderText = Strings.Current["Versions.Prompt.GuardPlaceholder"]
                };

            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(new TextBlock { Text = prompt.Question, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(prompt.Kind == SteamPromptKind.Password
                    ? Strings.Current["Versions.Prompt.Dialog.Title.Password"]
                    : Strings.Current["Versions.Prompt.Dialog.Title.Guard"]),
                Content = panel,
                PrimaryButtonText = Strings.Current["Versions.Prompt.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.Prompt.Dialog.Close"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            return input switch
            {
                PasswordBox box => box.Password,
                TextBox box => box.Text.Trim(),
                _ => null
            };
        }

        // The Steam Guard code, asked for while nothing is waiting on it. DepotDownloader answers
        // Steam Guard on its standard input, and a code typed while that process waits is a code
        // typed into a window that can close underneath it: the process exits, the write throws
        // "The pipe is being closed", and a 58.7 GB download has ended with that sentence as its
        // whole explanation. A code is time-based and rotates, so it is legitimate to ask for the
        // current one and start immediately.
        //
        // Null means the user backed out and no download starts. An empty code is not a refusal: an
        // account without Steam Guard, or one Steam has already remembered here, is asked for
        // nothing, and if Steam does ask, BEM falls back to asking then, exactly as it used to.
        private async Task<SteamGuardPlan?> PrepareGuardCodeAsync(SteamSignIn signIn)
        {
            // A QR sign-in is the authenticator: the phone that scans the code approves the session,
            // and Steam never asks the downloader for a Guard code at all.
            if (signIn.Kind != SteamSignInKind.Account)
                return new SteamGuardPlan(null);

            var input = new TextBox
            {
                Header = Strings.Current["Versions.Prompt.GuardLabel"],
                PlaceholderText = Strings.Current["Versions.Guard.Dialog.Placeholder"]
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Current["Versions.Guard.Dialog.Body"],
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current["Versions.Prompt.Dialog.Title.Guard"]),
                Content = panel,
                PrimaryButtonText = Strings.Current["Versions.Guard.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.AddDlc.Dialog.Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? new SteamGuardPlan(input.Text)
                : null;
        }

        private async Task<IReadOnlyList<SteamDepot>> DepotsForAsync(string branch, CancellationToken cancellationToken)
        {
            if (appInfoJson.Length == 0)
                appInfoJson = await SteamBranchCatalog.FetchJsonAsync(cancellationToken);

            return SteamDepotCatalog.Parse(appInfoJson, branch);
        }

        // A DLC's depots are not in the base app's own info at all - it is its own Steam app - so
        // sizing one for the confirmation dialog needs its own fetch. This is for display only: the
        // download itself checks the DLC's branch and completeness again through DlcDownloadStep,
        // which does not trust a size gathered here.
        private static async Task<IReadOnlyList<SteamDepot>> DlcDepotsForAsync(
            int dlcAppId, string branch, CancellationToken cancellationToken)
        {
            var appId = dlcAppId.ToString();
            var json = await SteamBranchCatalog.FetchJsonAsync(appId, cancellationToken);

            return SteamDepotCatalog.Parse(json, appId, branch);
        }

        // Returns how many folders were taken in, so the caller re-reads only when something changed.
        private int Sweep(IReadOnlyList<GameVersionOption> offered)
        {
            instanceManager.RepairInstanceLayouts();

            return instanceManager.AdoptUnregisteredFolders(version =>
            {
                var option = offered.FirstOrDefault(candidate => candidate.Version == version);

                if (option is null)
                    return null;

                var expected = SteamDepotCatalog.InstalledSizeBytes(SteamDepotCatalog.Parse(appInfoJson, option.Branch));

                return new InstanceManager.AdoptCandidate(expected, option.Branch, option.BuildId);
            });
        }

        // The last thing the downloader said, which is the difference between "it refused" and "it
        // thought there was nothing to do".
        private static string LastLineOf(string logPath)
        {
            try
            {
                if (!File.Exists(logPath))
                    return string.Empty;

                var last = File.ReadLines(logPath).LastOrDefault(line => line.Trim().Length > 0);

                return last is null ? string.Empty : Strings.Current.Format("Versions.Acquire.LastLine", last.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
        };

        // The first version has to exist before a second one can be switched to: LaunchAsync restores
        // the resting instance around every run, and with none recorded there is nothing to come back
        // to. Adoption leaves the install where it is and backs up what sits at the canonical paths.
        private bool AdoptCurrentInstall()
        {
            var gameFolder = CurrentInstall();

            if (gameFolder is null)
            {
                StatusMessage = Strings.Current["Versions.Switch.NoInstall"];
                return false;
            }

            var version = GameVersionReader.Read(gameFolder);
            var (branch, buildId) = SteamBranchInfo(gameFolder);
            var displayName = version.IsEmpty ? branch : version.ToString();

            instanceManager.AdoptCurrentInstall(gameFolder, displayName, branch, buildId);
            return true;
        }

        // The install the user configured, never whichever one a scan happens to find first.
        // Adoption makes this folder the resting instance every junction operation on the machine is
        // measured against, so a scan that picked the Steam copy while the user plays a GOG or
        // relocated one would anchor every later restore on a version they never launch. Locate()
        // stays as the last resort, for a machine where nothing has been configured at all.
        internal static string? CurrentInstall()
        {
            var active = ShellViewModels.Instance.Environment.GameInstallPath;

            if (GameInstallLocator.IsValidInstall(active))
                return active;

            var configured = ShellViewModels.Instance.Install.GameInstallPath;

            return GameInstallLocator.IsValidInstall(configured) ? configured : GameInstallLocator.Locate();
        }

        // A games root nobody has chosen is proposed rather than refused: the drive with the most
        // free space is the same answer the Versions page offers, and saying where the download went
        // is more use than a dialog asking before anything has been downloaded at all.
        private void EnsureGamesRoot()
        {
            if (!string.IsNullOrWhiteSpace(GamesRoot))
                return;

            var proposed = InstanceSettingsStore.ProposeGamesRoot();
            settingsStore.Write(settingsStore.Read() with { GamesRoot = proposed });
            StatusMessage = Strings.Current.Format("Versions.GamesRoot.Proposed", proposed);
        }

        private void SelectActive()
        {
            var active = InstalledVersions.FirstOrDefault(row => row.IsActive);

            if (ReferenceEquals(SelectedVersion, active))
                return;

            isSelecting = true;

            try
            {
                SelectedVersion = active;
            }
            finally
            {
                isSelecting = false;
            }
        }

        // The name to fall back on when an instance's version could not be read at all, so a row
        // is never blank.
        private static string DisplayNameOf(GameVersionEntry entry) =>
            entry.Instance?.Record.DisplayName ?? entry.Branch;

        // A record written by an older build, or an instance whose metadata was never given a
        // version, still has the game on disk to read it from.
        private static ModuleVersion VersionOf(InstalledInstance instance)
        {
            var recorded = ModuleVersion.Parse(instance.Record.RecordedGameVersion);

            return recorded.IsEmpty ? GameVersionReader.Read(instance.Record.GameFolder) : recorded;
        }

        // Steam never says which version the public branch is on, so it is named from the install
        // that branch produced. With no such install BEM cannot name it, and GameVersionCatalog
        // leaves it out rather than offering a version by a name that is not one.
        private static ModuleVersion PublicVersion(IReadOnlyList<InstalledInstance> installed)
        {
            var adopted = installed.FirstOrDefault(instance =>
                string.Equals(instance.Record.Branch, SteamAppManifest.PublicBranch, StringComparison.OrdinalIgnoreCase));

            if (adopted is not null && VersionOf(adopted) is { IsEmpty: false } version)
                return version;

            if (GameInstallLocator.Locate() is not { } gameFolder)
                return ModuleVersion.Empty;

            var (branch, _) = SteamBranchInfo(gameFolder);

            return string.Equals(branch, SteamAppManifest.PublicBranch, StringComparison.OrdinalIgnoreCase)
                ? GameVersionReader.Read(gameFolder)
                : ModuleVersion.Empty;
        }

        // The manifest sits two levels above the install folder: steamapps\common\<Game> to
        // steamapps\appmanifest_261550.acf. A non-Steam install (GOG, Epic, Game Pass) has no such
        // manifest, so this falls back to the public branch with no build id rather than guessing.
        private static (string Branch, string BuildId) SteamBranchInfo(string gameFolder)
        {
            var steamAppsFolder = Path.GetDirectoryName(Path.GetDirectoryName(gameFolder));

            if (steamAppsFolder is null)
                return (SteamAppManifest.PublicBranch, string.Empty);

            var acfPath = Path.Combine(steamAppsFolder, $"appmanifest_{DepotDownloaderTool.BannerlordAppId}.acf");
            var manifest = SteamAppManifest.Read(acfPath);

            return manifest is null
                ? (SteamAppManifest.PublicBranch, string.Empty)
                : (manifest.Branch, manifest.BuildId);
        }

        // Why the sign-in stopped, in the order the reasons are true. A repeat Steam Guard prompt is
        // BEM's own decision and the plan is the only thing that remembers it, so the flow's generic
        // "you did not finish it" line is used only when the user really did close something.
        private string SignInStoppedText() =>
            guardPlan?.RefusedRepeatPrompt == true
                ? Strings.Current["Versions.Prompt.GuardRepeat"]
                : Strings.Current["Versions.SignIn.Declined"];
    }
}
