using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;
using Microsoft.VisualBasic.FileIO;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Managing the instances behind the versions: where they are stored, which one rests at the
    // canonical paths, adding one by download and removing one. Choosing which version plays belongs
    // to the dropdown at the top of Play, because that is the same act as deciding what Launch runs;
    // getting a version onto the machine belongs here, because it is a deliberate act of its own and
    // not something a dropdown should do as a side effect of a selection changing.
    // Everything that decides anything lives in Core.Instances; this reads what it returns.
    public partial class VersionsViewModel : BaseViewModel
    {
        private readonly InstanceSettingsStore settingsStore = new();
        private readonly CanonicalPathSet canonicalPaths = CanonicalPathSet.ForMachine();
        private readonly InstanceManager instanceManager;

        // The one list both pages read, and the one place the downloader and Steam sign-in are wired
        // up. Anything that adds or removes an instance here has to reach it, and the page binds
        // its download progress straight to it. Public because the page shows that progress.
        public VersionSwitcherViewModel VersionSwitcher => Views.ShellViewModels.Instance.VersionSwitcher;

        public VersionsViewModel()
        {
            instanceManager = new InstanceManager(canonicalPaths, settingsStore);
            GamesRoot = settingsStore.Read().GamesRoot;
            StatusMessage = string.Empty;

            // Fire-and-forget: the constructor cannot await, and the page shows an empty list for the
            // moment it takes to enumerate instance folders and fetch the branch catalog.
            _ = RefreshAsync();
        }

        public ObservableCollection<InstanceRowViewModel> Instances { get; } = [];

        // One line per UninstallItem: path, size and what deleting it costs, already formatted for
        // display rather than handed over as records the page would have to format itself.
        public ObservableCollection<string> UninstallLines { get; } = [];

        // Every folder in the games root that holds part of a download and no instance. A download
        // that stopped short used to leave nothing but bytes on a disk: no version, no row, and no
        // way to act on it from inside BEM at all.
        public ObservableCollection<UnfinishedDownloadRowViewModel> UnfinishedDownloads { get; } = [];

        // Every DLC the selected instance is still missing, rebuilt whenever the selection changes
        // or the list refreshes. A row carries the Add button's own command, since the DataTemplate
        // that renders it cannot reach this view model directly.
        public ObservableCollection<MissingDlcRowViewModel> SelectedInstanceMissingDlc { get; } = [];

        // Every version Steam offers that this machine does not have. The other half of the same
        // list, the versions already here, is what Play's dropdown switches between.
        public ObservableCollection<GameVersionRowViewModel> DownloadableVersions => VersionSwitcher.DownloadableVersions;

        // Which offered version the Download button would fetch. Setting it downloads nothing: the
        // button is the only thing that starts a download.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadVersionCommand))]
        public partial GameVersionRowViewModel? SelectedDownload { get; set; }

        [ObservableProperty]
        public partial InstanceRowViewModel? SelectedInstance { get; set; }

        // What the row menu offers for the selected row. Right-clicking a row selects it first, so
        // this is always the row the menu is about. Rebuilt whole rather than item by item: one
        // binding on the page then follows every item at once.
        [ObservableProperty]
        public partial InstanceMenuViewModel Menu { get; set; } = InstanceMenuViewModel.Nothing;

        partial void OnSelectedInstanceChanged(InstanceRowViewModel? value)
        {
            SelectedInstanceMissingDlc.Clear();

            RebuildMenu();

            if (value is null)
                return;

            foreach (var dlc in value.MissingDlc)
                SelectedInstanceMissingDlc.Add(new MissingDlcRowViewModel(dlc, AddDlcCommand));
        }

        private bool watchingTheDownloadList;

        // Not in the constructor: ShellViewModels is still building its own fields while this view
        // model is constructed, so VersionSwitcher cannot be reached until after the first await.
        // The switcher outlives this view model, so the handler is attached once and never detached.
        private void WatchTheDownloadList()
        {
            if (watchingTheDownloadList)
                return;

            watchingTheDownloadList = true;
            VersionSwitcher.DownloadableVersions.CollectionChanged += (_, _) => RebuildMenu();
        }

        // Also run when the download list changes, which is when Steam's branch list arrives: the
        // menu used to be built once per selection, so a row right-clicked before that fetch returned
        // kept "Steam no longer publishes this version" until the user selected another row and came
        // back. Until the list has been read the third answer is null, which is not knowing.
        private void RebuildMenu()
        {
            var instance = SelectedInstance;

            Menu = new InstanceMenuViewModel(InstanceMenu.For(
                instance?.Record, instance?.IsActive ?? false, instance?.IsResting ?? false,
                instance?.GameFolderExists ?? false,
                instance is null || !VersionSwitcher.HasSteamBranchList
                    ? null
                    : VersionSwitcher.OfferFor(instance.Version, instance.DetectedDlc) is not null));
        }

        [ObservableProperty]
        public partial string GamesRoot { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        // A section with nothing in it is clutter on a page that already carries five, so the
        // unfinished-download rows appear only when there are any.
        [ObservableProperty]
        public partial bool HasUnfinishedDownloads { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        // Every instance measured the same way: the game plus the data that belongs to it. A managed
        // instance keeps its game inside its own folder, so the folder walk already covers it. A
        // referenced instance's folder holds only UserData and the adopted backup, and its game lives
        // wherever Steam put it, so measuring the folder alone reported the adopted v1.4.8 install as
        // 25.9 MB beside a 58.9 GB v1.5.2.
        private static long SizeOf(InstalledInstance instance) =>
            instance.Record.GameFolderIsReferenced
                ? UninstallReport.SizeOf(instance.Folder) + UninstallReport.SizeOf(instance.Record.GameFolder)
                : UninstallReport.SizeOf(instance.Folder);

        [RelayCommand]
        private async Task RefreshAsync()
        {
            GamesRoot = settingsStore.Read().GamesRoot;

            var installed = instanceManager.List();
            var restingId = instanceManager.RestingInstanceId;
            var activeId = instanceManager.ActiveInstanceId;
            var previouslySelectedId = SelectedInstance?.Record.Id;

            // Size on disk is a full directory walk per instance, and DLC detection reads the
            // Modules folder for every known DLC; both are exactly what UninstallReport and
            // GameDlcDetector already do, off the UI thread so a large games root, or a referenced
            // install on a slow or disconnected drive, does not freeze the page on every refresh.
            var rows = await Task.Run(() => installed
                .Select(instance => new InstanceRowViewModel(
                    instance,
                    string.Equals(instance.Record.Id, restingId, StringComparison.Ordinal),
                    string.Equals(instance.Record.Id, activeId, StringComparison.Ordinal),
                    SizeOf(instance),
                    GameDlcDetector.Detect(instance.Record.GameFolder),
                    !string.IsNullOrWhiteSpace(instance.Record.GameFolder)
                        && Directory.Exists(instance.Record.GameFolder)))
                .OrderByDescending(row => GameVersionCatalog.SeriesRank(row.Version.Type))
                .ThenByDescending(row => row.Version)
                .ToList());

            WatchTheDownloadList();

            Instances.Clear();
            foreach (var row in rows)
                Instances.Add(row);

            SelectedInstance = Instances.FirstOrDefault(r => r.Record.Id == previouslySelectedId);

            // Every row object is new after a refresh, so the assignment above always fires
            // OnSelectedInstanceChanged; this is only the case where nothing was selected and
            // nothing matched, which has to put the menu back to offering nothing.
            if (SelectedInstance is null)
                Menu = InstanceMenuViewModel.Nothing;

            await RefreshUnfinishedDownloadsAsync();

            var summary = Instances.Count == 0
                ? Strings.Current["Versions.List.Empty"]
                : Strings.Current.Plural("Versions.List.Count", Instances.Count);

            // What the startup passes found, posted by App before the shell was built. Consumed once:
            // a later manual refresh should not keep repeating a notice from minutes ago. Anything
            // that changed the disk is also shown as a dialog by ShellPage, because this line is at
            // the foot of one page and the user may be standing on another.
            var notice = StartupNotices.Pending.TakeSummary();

            StatusMessage = string.IsNullOrEmpty(notice) ? summary : $"{notice} {summary}";
        }

        // Read fresh from the disk on every refresh, and only from a refresh: a scan while a download
        // is running would list the folder that download is filling. The folder walk is a size
        // measurement per leftover folder, so it goes off the UI thread the same way the instance
        // list's does.
        private async Task RefreshUnfinishedDownloadsAsync()
        {
            var root = GamesRoot;
            var found = await Task.Run(() => UnfinishedDownloadScan.In(root));

            UnfinishedDownloads.Clear();

            HasUnfinishedDownloads = found.Count > 0;

            foreach (var download in found)
                UnfinishedDownloads.Add(new UnfinishedDownloadRowViewModel(
                    download,
                    FormatBytes(download.Bytes),
                    VersionFor(download),
                    ResumeDownloadCommand,
                    DiscardDownloadCommand));
        }

        // The download that made this folder computed its name from the version it was fetching, so
        // the same computation run over every version still on offer says which one it was. The name
        // and the purpose the user chose are part of that folder too, and neither is known here, so
        // the match is on the version the folder ends with rather than on the whole name. A folder no
        // offered version names is left alone: BEM does not guess what a stranger's folder was.
        private GameVersionRowViewModel? VersionFor(UnfinishedDownload download) =>
            DownloadableVersions.FirstOrDefault(row =>
                InstanceRegistry.NamesDownloadFolder(
                    InstanceDraft.ForDownload(row.Version, row.Branch, row.BuildId, row.Entry.Variant),
                    download.Name));

        // Picks up a download where it stopped. DepotDownloader is pointed at the folder that already
        // holds the partial copy and asked to validate what is in it, so the bytes already fetched
        // are kept; the same confirmation dialog every download shows is shown here too, because
        // this is a download starting and the user has to be the one who starts it.
        [RelayCommand]
        private async Task ResumeDownloadAsync(UnfinishedDownloadRowViewModel? row)
        {
            if (row?.Version is null || IsBusy)
                return;

            SelectedDownload = row.Version;

            // The folder travels with the resume. The dialog composes a name and a purpose again, and
            // both reach the record, but the bytes already fetched are picked up where they are
            // rather than fetched a second time into a folder composed from a different answer.
            await DownloadAsync(row.Download.Folder);
        }

        // The other way out of a dead end. It goes to the Recycle Bin, so a folder discarded by
        // mistake is one restore away, and nothing else on the machine is touched.
        [RelayCommand]
        private async Task DiscardDownloadAsync(UnfinishedDownloadRowViewModel? row)
        {
            if (row is null || IsBusy)
                return;

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current.Format("Versions.Unfinished.Discard.Dialog.Title", row.Download.Name)),
                Content = Strings.Current.Format(
                    "Versions.Unfinished.Discard.Dialog.Content", row.Download.Folder),
                PrimaryButtonText = Strings.Current["Versions.Unfinished.Discard.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.Unfinished.Discard.Dialog.Close"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("Versions.Unfinished.Kept", row.Download.Name);
                return;
            }

            IsBusy = true;

            try
            {
                FileSystem.DeleteDirectory(row.Download.Folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                LoggingService.Log($"Discarded the unfinished download '{row.Download.Folder}' to the Recycle Bin.");
                UnfinishedDownloads.Remove(row);
                StatusMessage = Strings.Current.Format("Versions.Unfinished.Discarded", row.Download.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to discard the unfinished download: {row.Download.Folder}");
                StatusMessage = Strings.Current.Format("Versions.Unfinished.Discard.Error", row.Download.Name, ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task BrowseGamesRootAsync()
        {
            var picker = new FolderPicker();
            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Versions.Picker.FolderError"];
                return;
            }

            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();

            if (folder is null)
            {
                StatusMessage = Strings.Current["Versions.Picker.Canceled"];
                return;
            }

            GamesRoot = folder.Path;
            settingsStore.Write(settingsStore.Read() with { GamesRoot = GamesRoot });
            StatusMessage = Strings.Current.Format("Versions.Root.Set", GamesRoot);
        }

        [RelayCommand]
        private async Task AdoptCurrentInstallAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                if (string.IsNullOrWhiteSpace(GamesRoot))
                {
                    GamesRoot = InstanceSettingsStore.ProposeGamesRoot();
                    settingsStore.Write(settingsStore.Read() with { GamesRoot = GamesRoot });
                    StatusMessage = Strings.Current.Format("Versions.Root.Proposed", GamesRoot);
                }

                // The configured install first, a scan only when none is set: adoption fixes which
                // folder every junction operation is measured against, and a scan that found the
                // Steam copy while the user plays a GOG or relocated one would anchor every later
                // restore on a version they never launch.
                var gameFolder = VersionSwitcherViewModel.CurrentInstall();

                if (gameFolder is null)
                {
                    StatusMessage = Strings.Current["Versions.Adopt.NotFound"];
                    return;
                }

                if (instanceManager.List().Any(
                        i => string.Equals(i.Record.GameFolder, gameFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    StatusMessage = Strings.Current.Format("Versions.Adopt.AlreadyInstance", gameFolder);
                    return;
                }

                var version = GameVersionReader.Read(gameFolder).ToString();
                var (branch, buildId) = ReadSteamBranchInfo(gameFolder);

                // Named by version, never by branch: a branch name is Steam's bookkeeping and says
                // nothing about which game this is.
                var displayName = version.Length > 0 ? version : branch;

                var adopted = instanceManager.AdoptCurrentInstall(gameFolder, displayName, branch, buildId);

                await RefreshAsync();
                await VersionSwitcher.RefreshAsync();

                StatusMessage = Strings.Current.Format("Versions.Adopt.Success", gameFolder, adopted.Record.DisplayName);
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to adopt the current install");
                StatusMessage = Strings.Current.Format("Versions.Adopt.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Everything a version row offers lives on its context menu, and each of these is the only
        // route to what it does: the page carries no button that repeats one. An item the row cannot
        // use is disabled carrying its reason (InstanceMenu decides), so a command reached here has
        // already been offered honestly; the guards below are what the nullable types demand, and
        // what covers a row that went away between the menu opening and the press.

        // A name of the user's own, written to the record and to nothing else. The folder keeps the
        // name it was created under, because the folder is the instance's identity: renaming it too
        // would leave this version's saves in a folder nothing looks in again.
        [RelayCommand]
        private async Task RenameInstanceAsync()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            var current = instance.Record.ChosenName ?? string.Empty;

            var box = new TextBox
            {
                Text = current,
                SelectionStart = 0,
                SelectionLength = current.Length
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = DialogText.ContentWidth };

            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Strings.Current.Format("Versions.Rename.Dialog.Content", instance.VersionText)
            });

            panel.Children.Add(box);

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current.Format("Versions.Rename.Dialog.Title", instance.Name)),
                Content = panel,
                PrimaryButtonText = Strings.Current["Versions.Rename.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.Rename.Dialog.Close"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                return;

            // The box is not capped, so a name that is too long is a sentence the user can read
            // rather than typing that silently stops arriving.
            var chosen = InstanceNaming.Read(box.Text);

            if (!chosen.Accepted)
            {
                StatusMessage = Strings.Current.Format("Versions.Rename.TooLong", InstanceNaming.MaxLength);
                return;
            }

            try
            {
                instanceManager.Rename(instance.Record.Id, box.Text);

                await RefreshAsync();
                await VersionSwitcher.RefreshAsync();

                StatusMessage = chosen.Outcome == InstanceNameOutcome.Named
                    ? Strings.Current.Format("Versions.Rename.Renamed", chosen.Name)
                    : Strings.Current.Format("Versions.Rename.Cleared", instance.VersionText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException)
            {
                LoggingService.LogException(ex, $"Failed to rename instance: {instance.Record.Id}");
                StatusMessage = Strings.Current.Format("Versions.Rename.Error", ex.Message);
            }
        }

#if DEV_BEM
        // The outer ring. What this instance is for, declared once and read afterwards by anything
        // that needs to know whether a mod build may be installed into it: a mod's build tool, which
        // is a thing an author has and a player does not. Three items rather than a toggle, because
        // "not declared" is a real third answer and the one every instance starts at: without it a
        // declaration could be made but never taken back.
        //
        // Only the declaring moves. Every build still reads InstanceRecord.Purpose and still labels a
        // row with it (InstanceNaming.For), because the instances on this machine already carry one.
        [RelayCommand]
        private Task SetPurposeTestingAsync() => DeclarePurposeAsync(InstancePurpose.Testing);

        [RelayCommand]
        private Task SetPurposePlayingAsync() => DeclarePurposeAsync(InstancePurpose.Playing);

        [RelayCommand]
        private Task SetPurposeUndeclaredAsync() => DeclarePurposeAsync(InstancePurpose.Unspecified);

        private async Task DeclarePurposeAsync(InstancePurpose purpose)
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            try
            {
                instanceManager.SetPurpose(instance.Record.Id, purpose);

                await RefreshAsync();
                await VersionSwitcher.RefreshAsync();

                StatusMessage = purpose switch
                {
                    InstancePurpose.Testing => Strings.Current.Format("Versions.Purpose.SetTesting", instance.Name),
                    InstancePurpose.Playing => Strings.Current.Format("Versions.Purpose.SetPlaying", instance.Name),
                    _ => Strings.Current.Format("Versions.Purpose.Cleared", instance.Name)
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException)
            {
                LoggingService.LogException(ex, $"Failed to set the purpose of instance: {instance.Record.Id}");
                StatusMessage = Strings.Current.Format("Versions.Purpose.Error", ex.Message);
            }
        }
#endif

        [RelayCommand]
        private void OpenInstanceFolder()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            var folder = instance.Record.GameFolder;

            // Asked again here rather than trusted from the refresh: a drive can be pulled between
            // the list being built and this being pressed.
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                StatusMessage = Strings.Current.Format("Versions.OpenFolder.Missing", folder);
                return;
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe")
                {
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = false
                });

                StatusMessage = Strings.Current.Format("Versions.OpenFolder.Success", folder);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           or IOException)
            {
                LoggingService.LogException(ex, $"Failed to open the instance folder: {folder}");
                StatusMessage = Strings.Current.Format("Versions.OpenFolder.Failed", ex.Message);
            }
        }

        [RelayCommand]
        private void CopyInstancePath()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            var folder = instance.Record.GameFolder;

            if (string.IsNullOrWhiteSpace(folder))
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoPath"];
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(folder);

            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current.Format("Versions.CopyPath.Success", folder);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                           or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy an instance path to the clipboard");
                StatusMessage = Strings.Current["Versions.CopyPath.Failed"];
            }
        }

        // Which version the next launch runs. The switcher owns that decision and the sentence it
        // writes, because Play's dropdown makes the same one; this only moves the sentence into this
        // page's own status line, where a gesture made on this page is read, rather than leaving it
        // in the box headed Download a Version.
        [RelayCommand]
        private async Task SetActiveAsync()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            if (instance.IsActive)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.AlreadyActive"];
                return;
            }

            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                await VersionSwitcher.SwitchToInstanceAsync(instance.Record.Id);

                StatusMessage = VersionSwitcher.StatusMessage;
                VersionSwitcher.StatusMessage = string.Empty;

                await RefreshAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ShowInstanceDetailsAsync()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Menu.Blocked.NoInstance"];
                return;
            }

            var panel = new StackPanel { Spacing = 8, MinWidth = DialogText.ContentWidth };

            // A label column and a value column rather than one sentence per line: a colon glued on
            // in code would be a piece of text no catalog owns, and the two columns read down the
            // dialog the way the table on the page reads down its own.
            foreach (var detail in InstanceDetails.For(
                         instance.Record, instance.DetectedDlc, instance.SizeOnDiskText))
            {
                var line = new Grid { ColumnSpacing = 12 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock { Text = detail.Label, TextWrapping = TextWrapping.Wrap };

                var value = new TextBlock
                {
                    Text = detail.Value,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };

                Grid.SetColumn(value, 1);
                line.Children.Add(label);
                line.Children.Add(value);
                panel.Children.Add(line);
            }

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(
                    Strings.Current.Format("Versions.Details.Dialog.Title", instance.RowLabel)),
                Content = panel,
                CloseButtonText = Strings.Current["Versions.Details.Dialog.Close"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            await DialogText.ShowAsync(dialog);
        }

        [RelayCommand]
        private async Task SetRestingAsync()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.SetResting.NoSelection"];
                return;
            }

            if (RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Versions.SetResting.GameRunning"];
                return;
            }

            try
            {
                // The result's own sentence, not one written here: it is the only thing that knows
                // whether anything actually moved, and whether the incoming version's user data is
                // still sitting in its store, which is the state that makes launching it read the
                // other version's saves. This page has one plain status line and no severity, so the
                // warning rides in that sentence rather than in a control of its own.
                StatusMessage = instanceManager.SetResting(instance.Record.Id).Message;
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (IOException ex)
            {
                // A swap whose destination already holds state is refused, and a rollback is
                // reported, as an IOException. Uncaught it would take the window down over a
                // refusal the user is entitled to read as a sentence.
                LoggingService.LogException(ex, "Failed to change the resting instance");
                StatusMessage = ex.Message;
            }

            // Both lists carry the resting marker - the row label here and Play's dropdown - and the
            // row menu was built from the resting state as it was, so it would go on offering Remove
            // on the version that has just become resting. The sentence is put back afterwards
            // because RefreshAsync writes its own summary over it.
            var message = StatusMessage;

            await RefreshAsync();
            await VersionSwitcher.RefreshAsync();

            StatusMessage = message;
        }

        [RelayCommand]
        private async Task RemoveInstanceAsync()
        {
            if (SelectedInstance is not { } instance)
            {
                StatusMessage = Strings.Current["Versions.Remove.NoSelection"];
                return;
            }

            // Asked twice: once so a refusal costs no dialog, and once with the dialog answered and
            // the delete about to run, because the game can be started while that dialog is open.
            if (RemovalBlockFor(instance) is { } refusal)
            {
                StatusMessage = refusal;
                return;
            }

            var completeCheckBox = new CheckBox
            {
                Content = Strings.Current["Versions.Remove.Dialog.Complete"],
                IsChecked = false
            };

            ToolTipService.SetToolTip(completeCheckBox, Strings.Current["Versions.Remove.Dialog.Complete.Tooltip"]);

            var sizeText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Strings.Current["Versions.Remove.Dialog.Counting"]
            };

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(
                    Strings.Current.Format("Versions.Remove.Dialog.Title", instance.Name)),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = Strings.Current["Versions.Remove.Dialog.Content"]
                        },
                        completeCheckBox,
                        sizeText
                    }
                },
                PrimaryButtonText = Strings.Current["Versions.Remove.Dialog.Primary"],
                CloseButtonText = Strings.Current["Versions.Remove.Dialog.Close"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            DialogText.KeepTextWhole(dialog);

            // Shown first, counted second: walking a 60 GB instance folder before the dialog appears
            // would leave the Remove button dead for as long as the walk takes, and the number is
            // only needed by the time somebody reads the checkbox.
            var showing = dialog.ShowAsync();

            sizeText.Text = (await Task.Run(() => InstanceRemoval.Measure(instance.Folder))).Describe();

            if (await showing != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("Versions.Remove.Kept", instance.Name);
                return;
            }

            if (RemovalBlockFor(instance) is { } refusedNow)
            {
                StatusMessage = refusedNow;
                return;
            }

            var complete = completeCheckBox.IsChecked == true;

            // Read before anything is deleted: once the folder is gone the active id no longer names
            // a known instance, and the property answers with the resting one from then on.
            var wasActive = string.Equals(
                instanceManager.ActiveInstanceId, instance.Record.Id, StringComparison.Ordinal);

            IsBusy = true;

            try
            {
                var plan = InstanceRemoval.Plan(instance.Instance, complete);

                foreach (var item in plan.Items)
                {
                    if (item.IsFolder)
                        FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else
                        FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }

                Instances.Remove(instance);

                if (SelectedInstance == instance)
                    SelectedInstance = null;

                // Before the switcher rebuilds, so it reads the settled active id rather than one
                // naming an instance that is no longer there.
                if (wasActive)
                    instanceManager.ClearActive();

                await VersionSwitcher.RefreshAsync();

                var outcome = complete
                    ? plan.InstanceFolderRemains
                        ? Strings.Current.Format(
                            "Versions.Remove.CompleteLeftover", instance.Name, plan.InstanceFolder)
                        : plan.ReferencedGameFolderKept is { } kept
                            ? Strings.Current.Format(
                                "Versions.Remove.CompleteSuccessReferenced",
                                instance.Name, plan.InstanceFolder, kept)
                            : Strings.Current.Format(
                                "Versions.Remove.CompleteSuccess", instance.Name, plan.InstanceFolder)
                    : Strings.Current.Format(
                        "Versions.Remove.Success", instance.Name, InstanceLayout.UserDataFolder(instance.Folder));

                StatusMessage = wasActive
                    ? $"{outcome} {Strings.Current["Versions.Remove.ActiveCleared"]}"
                    : outcome;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to remove instance: {instance.Name}");
                StatusMessage = Strings.Current.Format("Versions.Remove.Error", instance.Name, ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Adds one DLC the selected instance does not have yet. Reached only by the button next to
        // that DLC's own row, so a click here is always the one that starts the download: nothing on
        // this page fetches anything on a refresh or a selection change. The switcher does the actual
        // work, since it already owns Steam sign-in and the DepotDownloader wiring the base download
        // uses; its own IsBusy guard, held for the whole run, is what stops a second click here or a
        // version switch on Play from starting a second download while this one is in flight.
        [RelayCommand]
        private async Task AddDlcAsync(GameDlcInfo? dlc)
        {
            if (dlc is null || SelectedInstance is not { } instance || IsBusy)
                return;

            IsBusy = true;

            try
            {
                // The switcher's own status line is the one place a download reports itself, on
                // this page and on Play both. Copying it into this page's status line as well put
                // the same sentence on the screen twice, once under the download area and once
                // above the uninstall report.
                await VersionSwitcher.AddDlcAsync(instance.Instance, dlc);
                await RefreshAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Downloads the version picked above. This is the one gesture on the machine that starts a
        // base game download, and it is a button press: nothing on this page or on Play fetches a
        // version on a refresh, a navigation or a selection changing. The switcher does the work,
        // since it owns Steam sign-in and the DepotDownloader wiring, and its own IsBusy guard is
        // what stops a second press, an Add DLC press, or a version switch on Play from starting a
        // second download while this one is in flight.
        [RelayCommand(CanExecute = nameof(CanDownloadVersion))]
        private async Task DownloadVersionAsync() => await DownloadAsync(resumeFolder: null);

        private async Task DownloadAsync(string? resumeFolder)
        {
            if (SelectedDownload is not { } row || IsBusy)
                return;

            IsBusy = true;

            try
            {
                // What the download did is said once, by the switcher, in the box directly under
                // the Download button: this page's own status line is for what this page does.
                await VersionSwitcher.DownloadAsync(row, resumeFolder);
                await RefreshAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanDownloadVersion() => SelectedDownload is not null;

        // The convenience route to a second copy of a version already here. It picks that version in
        // the Download list and presses the same button, rather than reaching the downloader itself:
        // the dropdown is where a version is installed, so the menu leaves the page showing exactly
        // what it just started and there is one download path to keep working rather than two.
        [RelayCommand]
        private async Task InstallAnotherCopyAsync()
        {
            if (SelectedInstance is not { } instance || IsBusy)
                return;

            // The same three answers the menu item carries: the item can be pressed by keyboard
            // before the menu has been rebuilt, and telling the user Steam has dropped a version when
            // BEM has simply not asked yet is the wrong sentence twice over.
            if (VersionSwitcher.OfferFor(instance.Version, instance.DetectedDlc) is not { } row)
            {
                StatusMessage = Strings.Current[VersionSwitcher.HasSteamBranchList
                    ? "Versions.Menu.Blocked.NotOffered"
                    : "Versions.Menu.Blocked.CatalogNotRead"];

                return;
            }

            SelectedDownload = row;
            await DownloadVersionAsync();
        }

        [RelayCommand]
        private async Task PrepareForUninstallAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                var registry = new InstanceRegistry(GamesRoot);
                var report = new UninstallReport(canonicalPaths, registry);
                var summary = await Task.Run(report.Build);

                UninstallLines.Clear();

                foreach (var item in summary.Items)
                    UninstallLines.Add($"{item.Path} ({FormatBytes(item.Bytes)}): {item.Explanation}");

                StatusMessage = summary.SafeToUninstall
                    ? Strings.Current.Plural("Versions.Uninstall.Safe", summary.Items.Count, FormatBytes(summary.TotalBytes))
                    : Strings.Current.Plural("Versions.Uninstall.NotSafe", summary.LiveJunctions.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to build the uninstall report");
                StatusMessage = Strings.Current.Format("Versions.Uninstall.Error", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // The manifest sits two levels above the install folder: steamapps\common\<Game> to
        // steamapps\appmanifest_261550.acf. A non-Steam install (GOG, Epic, Game Pass) has no such
        // manifest, so this falls back to the public branch with no build id rather than guessing.
        private static (string Branch, string BuildId) ReadSteamBranchInfo(string gameFolder)
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

        // The sentence to show, or null when the removal may go ahead. Core decides; this only says
        // it, because a refusal the user cannot tell apart from another one is a refusal they cannot
        // act on.
        private string? RemovalBlockFor(InstanceRowViewModel instance) =>
            InstanceRemoval.Check(
                instance.Instance,
                instanceManager.RestingInstanceId,
                RunningGame.AnyGameProcessRunning(),
                InstanceRemoval.LiveJunctionTargets(canonicalPaths)) switch
            {
                RemovalBlock.IsResting => Strings.Current["Versions.Remove.IsResting"],
                RemovalBlock.InPlay =>
                    Strings.Current.Format("Versions.Remove.InPlay", instance.Name),
                RemovalBlock.GameRunning => Strings.Current["Versions.Remove.GameRunning"],
                _ => null
            };

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
        };

        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;

            if (window is null)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            switch (picker)
            {
                case FolderPicker folderPicker:
                    InitializeWithWindow.Initialize(folderPicker, hwnd);
                    return true;

                default:
                    return false;
            }
        }
    }
}
