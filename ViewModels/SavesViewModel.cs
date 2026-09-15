using System.Collections.ObjectModel;
using System.Globalization;
using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Services;
using BannerlordEnvironmentManager.Views;
using Microsoft.VisualBasic.FileIO;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed class SaveRowViewModel(SaveFile save)
    {
        public SaveFile Save { get; } = save;

        public string Name { get; } = save.Name;

        public string Version { get; } = save.ApplicationVersion;

        public string Character { get; } = save.CharacterName;

        public string Level { get; } =
            save.Failed ? string.Empty : save.MainHeroLevel.ToString(CultureInfo.CurrentCulture);

        public string Days { get; } =
            save.Failed ? string.Empty : save.Days.ToString(CultureInfo.CurrentCulture);

        public string Created { get; } = save.Created?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

        // A save BEM could not read is listed with the reason rather than dropped, so a corrupt file is
        // visible as a corrupt file instead of quietly missing from a list the user counts.
        public string Detail { get; } = save.Failed
            ? save.Error
            : Strings.Current.Plural("Saves.Row.ModulesRecorded", save.Modules.Count);

        public bool Failed { get; } = save.Failed;

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud for every row.
        public override string ToString() => Name;
    }

    public sealed class SaveDifferenceRowViewModel(SaveModuleDifference difference)
    {
        public SaveModuleDifference Difference { get; } = difference;

        public string Heading { get; } =
            string.Equals(difference.DisplayName, difference.Id.Value, StringComparison.Ordinal)
                ? difference.Id.Value
                : $"{difference.DisplayName} ({difference.Id})";

        public string Detail { get; } = Describe(difference);

        private static string Describe(SaveModuleDifference difference) => difference switch
        {
            { Change: SaveModuleChange.NotRecorded } =>
                Strings.Current.Format(
                    "Saves.Diff.NotRecorded",
                    Version(difference.InstalledVersion),
                    When(difference.InstalledOn)),
            { Change: SaveModuleChange.Added, InstalledOn: not null } =>
                Strings.Current.Format(
                    "Saves.Diff.AddedWithDate",
                    When(difference.InstalledOn),
                    Version(difference.InstalledVersion)),
            { Change: SaveModuleChange.Added } =>
                Strings.Current.Format("Saves.Diff.Added", Version(difference.InstalledVersion)),
            { Change: SaveModuleChange.Removed, IsInstalled: false } =>
                Strings.Current.Format("Saves.Diff.RemovedNotInstalled", Version(difference.SaveVersion)),
            { Change: SaveModuleChange.Removed } =>
                Strings.Current.Format(
                    "Saves.Diff.RemovedInstalledOff",
                    Version(difference.SaveVersion),
                    Version(difference.InstalledVersion)),
            _ => Strings.Current.Format(
                "Saves.Diff.VersionChanged",
                Version(difference.SaveVersion),
                Version(difference.InstalledVersion))
        };

        private static string Version(string text) => text.Length > 0 ? text : Strings.Current["Saves.Diff.NoVersionRecorded"];

        private static string When(DateTime? moment) =>
            moment?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? Strings.Current["Saves.Diff.NoDateRecorded"];
    }

    // The Saves page reads only the metadata header each .sav opens with. The campaign body is a
    // compressed stream BEM never decompresses and never writes to: nothing here can change a save.
    public partial class SavesViewModel : BaseViewModel
    {
        public SavesViewModel()
        {
            Title = "Saves";
            SavesFolder = SaveReader.DefaultFolder();
        }

        public ObservableCollection<SaveRowViewModel> Saves { get; } = [];

        public ObservableCollection<SaveDifferenceRowViewModel> Added { get; } = [];

        public ObservableCollection<SaveDifferenceRowViewModel> Removed { get; } = [];

        public ObservableCollection<SaveDifferenceRowViewModel> VersionChanged { get; } = [];

        public ObservableCollection<SaveDifferenceRowViewModel> NotRecorded { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RestoreLoadOrderCommand))]
        [NotifyCanExecuteChangedFor(nameof(LaunchIntoSelectedSaveCommand))]
        public partial SaveRowViewModel? SelectedSave { get; set; }

        [ObservableProperty]
        public partial string SavesFolder { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        // Empty unless BEM has something the list itself cannot say: a Game Pass install with no BLSE
        // reads as "no saves" here when the truth is that the saves are somewhere BEM cannot look.
        [ObservableProperty]
        public partial string LocationNote { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DiffSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ComparedAgainst { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string AddedEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RemovedEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string VersionChangedEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NotRecordedEmptyText { get; set; } = string.Empty;

        private bool CanRestoreLoadOrder => SelectedSave is { Failed: false };

        partial void OnSelectedSaveChanged(SaveRowViewModel? value)
        {
            _ = value;
            RefreshDiff();
        }

        [RelayCommand]
        public void Refresh()
        {
            var previous = SelectedSave?.Save.Path;

            // Re-read on every refresh rather than fixed at construction: the saves of the version
            // picked in the Play dropdown live in that version's own store, and only the resting
            // version's saves are the ones sitting in Documents.
            SavesFolder = SaveReader.DefaultFolder(ShellViewModels.Instance.Environment.ActiveDataRoot);

            var read = SaveReader.ReadFolder(SavesFolder);

            Saves.Clear();
            foreach (var save in read.Saves)
                Saves.Add(new SaveRowViewModel(save));

            SelectedSave = Saves.FirstOrDefault(row =>
                                 string.Equals(row.Save.Path, previous, StringComparison.OrdinalIgnoreCase))
                             ?? Saves.FirstOrDefault();

            StatusMessage = Describe(read);

            LocationNote = SaveLocationNote.For(
                ShellViewModels.Instance.Environment.GameInstallPath,
                read.Saves.Count > 0);

            RefreshDiff();
        }

        // "You have no saves" and "BEM could not look" mean opposite things to whoever reads this line.
        private static string Describe(SaveFolderRead read)
        {
            if (!read.Exists)
                return Strings.Current.Format("Saves.Status.NoFolder", read.Folder);

            if (read.Error.Length > 0)
                return read.Error;

            if (read.Saves.Count == 0)
                return Strings.Current.Format("Saves.Status.Empty", read.Folder);

            var unreadable = read.Saves.Count(s => s.Failed);

            return unreadable == 0
                ? Strings.Current.Plural("Saves.Status.CountOnly", read.Saves.Count, read.Folder)
                : Strings.Current.Plural("Saves.Status.CountWithUnreadable", read.Saves.Count, read.Folder)
                  + Strings.Current.Plural("Saves.Status.UnreadableTail", unreadable);
        }

        public void RefreshDiff()
        {
            Added.Clear();
            Removed.Clear();
            VersionChanged.Clear();
            NotRecorded.Clear();

            if (SelectedSave is null)
            {
                DiffSummary = Saves.Count == 0
                    ? Strings.Current["Saves.Diff.NothingYet"]
                    : Strings.Current["Saves.Diff.PickSave"];
                ComparedAgainst = string.Empty;
                SetEmptyText(Strings.Current["Saves.Diff.EmptyDefault"]);
                return;
            }

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                DiffSummary = Strings.Current["Saves.Diff.NotLoaded"];
                ComparedAgainst = string.Empty;
                SetEmptyText(Strings.Current["Saves.Diff.EmptyNotLoaded"]);
                return;
            }

            var current = environment.BuildEnvironment();
            var diff = SaveComparison.Compare(SelectedSave.Save, current);

            foreach (var difference in diff.Added)
                Added.Add(new SaveDifferenceRowViewModel(difference));

            foreach (var difference in diff.Removed)
                Removed.Add(new SaveDifferenceRowViewModel(difference));

            foreach (var difference in diff.VersionChanged)
                VersionChanged.Add(new SaveDifferenceRowViewModel(difference));

            foreach (var difference in diff.NotRecorded)
                NotRecorded.Add(new SaveDifferenceRowViewModel(difference));

            DiffSummary = diff.Summary;

            // Nothing was compared, so the four lists are empty because there was no comparison, not
            // because nothing changed. Printing the all-clear text under each one next to "this save
            // could not be read" states four things BEM has no way of knowing.
            if (!diff.CanCompare)
            {
                ComparedAgainst = string.Empty;
                SetEmptyText(Strings.Current["Saves.Diff.EmptyCannotCompare"]);
                return;
            }

            ComparedAgainst = Strings.Current.Plural("Saves.Diff.ComparedAgainst.Modules", current.Entries.Count)
                + Strings.Current.Plural("Saves.Diff.ComparedAgainst.Enabled", current.Entries.Count(e => e.IsEnabled));

            // Added counts enabled modules only, so an empty list is not on its own a statement about
            // what is on disk. The diff says which sentence it has earned.
            AddedEmptyText = Added.Count == 0 ? diff.DescribeNothingAdded() : string.Empty;

            NotRecordedEmptyText = NotRecorded.Count == 0
                ? Strings.Current["Saves.Diff.NotRecordedEmpty"]
                : string.Empty;

            RemovedEmptyText = Removed.Count == 0
                ? Strings.Current["Saves.Diff.RemovedEmpty"]
                : string.Empty;

            VersionChangedEmptyText = VersionChanged.Count == 0
                ? Strings.Current["Saves.Diff.VersionChangedEmpty"]
                : string.Empty;
        }

        private void SetEmptyText(string text)
        {
            AddedEmptyText = text;
            RemovedEmptyText = text;
            VersionChangedEmptyText = text;
            NotRecordedEmptyText = text;
        }

        // The order a save recorded is what that campaign actually ran with, so it is applied exactly as
        // recorded and the rules it breaks are reported rather than repaired. It goes through the same
        // reconciliation an imported file goes through, so every guard holds, Undo covers it, and nothing
        // reaches LauncherData.xml until the user saves.
        [RelayCommand(CanExecute = nameof(CanRestoreLoadOrder))]
        public async Task RestoreLoadOrderAsync()
        {
            if (SelectedSave?.Save is not { } save)
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                StatusMessage = Strings.Current["Saves.Restore.NotLoaded"];
                return;
            }

            var read = SaveComparison.ToLoadOrder(save);

            if (read.Failed)
            {
                StatusMessage = read.Error ?? Strings.Current.Format("Saves.Restore.CouldNotRead", save.Name);
                return;
            }

            var preview = LoadOrderImport.Preview(environment.BuildEnvironment(), read, "save");

            if (preview.CanApply && !preview.HasChanges)
            {
                StatusMessage = Strings.Current.Format("Saves.Restore.AlreadyMatches", save.Name);
                return;
            }

            if (!await SaveRestoreDialog.ConfirmAsync(save.Name, preview))
            {
                StatusMessage = preview.CanApply
                    ? Strings.Current.Format("Saves.Restore.Declined", save.Name)
                    : Strings.Current.Format("Saves.Restore.DeclinedWithSummary", preview.Summary);
                return;
            }

            environment.RestoreOrderFrom(preview, Strings.Current.Format("Saves.Restore.UndoReason", save.Name));

            StatusMessage = Strings.Current.Format("Saves.Restore.Success", save.Name, preview.Summary);

            RefreshDiff();
        }

        // Which row the right-click landed on, recorded before the flyout opens so the items can bind the
        // page's own commands rather than one set per row. Its CanExecute is what grays an item out, so
        // an action whose target is missing cannot be clicked at all.
        public SaveRowViewModel? ContextSave { get; private set; }

        public void SetContextSave(SaveRowViewModel? row)
        {
            ContextSave = row;
            ShowContextSaveInFolderCommand.NotifyCanExecuteChanged();
            CopyContextSaveNameCommand.NotifyCanExecuteChanged();
            CopyContextSavePathCommand.NotifyCanExecuteChanged();
            CopyContextSaveModulesCommand.NotifyCanExecuteChanged();
            RestoreContextSaveLoadOrderCommand.NotifyCanExecuteChanged();
            LaunchIntoContextSaveCommand.NotifyCanExecuteChanged();
            RecycleContextSaveCommand.NotifyCanExecuteChanged();
        }

        private bool HasContextSave() => ContextSave is not null;

        // A save BEM could not read still has a name, a path and a folder, so those stay available. What
        // it does not have is a module list or an order to restore.
        private bool HasReadableContextSave() => ContextSave is { Failed: false };

        [RelayCommand(CanExecute = nameof(HasContextSave))]
        private void ShowContextSaveInFolder()
        {
            var path = ContextSave!.Save.Path;

            if (!File.Exists(path))
            {
                StatusMessage = Strings.Current.Format("Saves.ShowInFolder.Missing", path);
                return;
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe")
                {
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false
                });

                StatusMessage = Strings.Current.Format("Saves.ShowInFolder.Success", ContextSave!.Save.FileName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           or IOException)
            {
                LoggingService.LogException(ex, $"Failed to show a save in its folder: {path}");
                StatusMessage = Strings.Current.Format("Saves.ShowInFolder.Failed", ex.Message);
            }
        }

        [RelayCommand(CanExecute = nameof(HasContextSave))]
        private void CopyContextSaveName() => Copy(ContextSave!.Save.Name, Strings.Current["Saves.CopyTarget.Name"]);

        [RelayCommand(CanExecute = nameof(HasContextSave))]
        private void CopyContextSavePath() => Copy(ContextSave!.Save.Path, Strings.Current["Saves.CopyTarget.Path"]);

        // The order as the save recorded it, one id and version per line, which is what makes it useful
        // to paste into a bug report next to what the load order looks like now.
        [RelayCommand(CanExecute = nameof(HasReadableContextSave))]
        private void CopyContextSaveModules() => Copy(
            string.Join(
                Environment.NewLine,
                ContextSave!.Save.Modules.Select(m => $"{m.Id.Value} {m.VersionText}".TrimEnd())),
            Strings.Current.Plural("Saves.CopyTarget.Modules", ContextSave!.Save.Modules.Count));

        [RelayCommand(CanExecute = nameof(HasReadableContextSave))]
        private Task RestoreContextSaveLoadOrderAsync()
        {
            // The same command the button runs, against the row that was clicked, so there is one
            // implementation of restoring an order and the list shows which save it acted on.
            SelectedSave = ContextSave;

            return RestoreLoadOrderAsync();
        }

        // The button in the page header, acting on whichever save is selected.
        [RelayCommand(CanExecute = nameof(CanLaunchIntoSelectedSave))]
        private Task LaunchIntoSelectedSaveAsync() =>
            SelectedSave is { } row ? LaunchIntoSaveAsync(row.Save) : Task.CompletedTask;

        private bool CanLaunchIntoSelectedSave() => SelectedSave is { Failed: false };

        // The same launch, from the row's context menu.
        [RelayCommand(CanExecute = nameof(HasReadableContextSave))]
        private Task LaunchIntoContextSaveAsync() =>
            ContextSave is { } row ? LaunchIntoSaveAsync(row.Save) : Task.CompletedTask;

        // The command line carries the save's own recorded module list (BLSE will not start without a
        // _MODULES_ argument, even under /continuesave), so this needs no current load order and no
        // preflight: the save is the order. Only BLSE (direct) can do it, so an install without it is
        // refused with the reason rather than silently started the wrong way.
        private async Task LaunchIntoSaveAsync(SaveFile save)
        {
            var environment = ShellViewModels.Instance.Environment;

            if (string.IsNullOrWhiteSpace(environment.GameInstallPath))
            {
                StatusMessage = Strings.Current["Saves.Launch.NoInstall"];
                return;
            }

            var target = LaunchTargetResolver.ResolveForSave(
                environment.GameInstallPath,
                save.Name,
                save.Modules,
                environment.CrashHandling,
                environment.ExtraArguments);

            if (target is null)
            {
                StatusMessage = Strings.Current["Saves.Launch.NoBlse"];
                return;
            }

            try
            {
                StatusMessage = Strings.Current.Format("Saves.Launch.Success", save.FileName, target.DisplayName);

                // Through the Play page's own activation rather than started here, so a save belonging
                // to a version that is not the resting one is played against that version's data. A
                // direct start would run the game against whatever rests at the canonical paths, where
                // this save does not even exist. This waits for the run, because that is how long the
                // isolation has to hold.
                await environment.LaunchUnderActiveVersionAsync(target);
            }
            // InvalidOperationException here is the instance manager refusing the launch to protect the
            // isolation, and its message already says why and what to do.
            catch (InvalidOperationException ex)
            {
                LoggingService.LogException(ex, "Launching into a save was refused to protect version isolation");
                StatusMessage = ex.Message;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                LoggingService.LogException(ex, "Failed to launch the game into a save");
                StatusMessage = Strings.Current.Format("Saves.Launch.Failed", save.FileName, ex.Message);
            }
        }

        // A save is the most irreplaceable file on this machine, so this is the Recycle Bin and nothing
        // else, it names the campaign it is about to move, and the default button is the one that keeps
        // it. BEM never removes a save any other way.
        [RelayCommand(CanExecute = nameof(HasContextSave))]
        private async Task RecycleContextSaveAsync()
        {
            if (ContextSave is not { } row)
                return;

            var describe = row.Failed
                ? Strings.Current["Saves.Recycle.UnknownCampaign"]
                : Strings.Current.Plural(
                    "Saves.Recycle.Campaign",
                    row.Save.Modules.Count,
                    row.Character,
                    row.Level,
                    row.Days);

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Saves.RecycleConfirm.Title", row.Save.FileName),
                Content = new TextBlock
                {
                    Text = Strings.Current.Format("Saves.RecycleConfirm.Body", describe),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Saves.RecycleConfirm.PrimaryButton"],
                CloseButtonText = Strings.Current["Saves.RecycleConfirm.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                StatusMessage = Strings.Current.Format("Saves.Recycle.Kept", row.Save.FileName);
                return;
            }

            try
            {
                FileSystem.DeleteFile(row.Save.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to recycle a save: {row.Save.Path}");
                StatusMessage = Strings.Current.Format("Saves.Recycle.Failed", row.Save.FileName, ex.Message);
                return;
            }

            SetContextSave(null);
            Refresh();

            StatusMessage = Strings.Current.Format("Saves.Recycle.Success", row.Save.FileName);
        }

        // Whatever else has the clipboard open (a clipboard manager, an RDP session) makes this throw,
        // and an unhandled throw here would close the app.
        private void Copy(string text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                StatusMessage = Strings.Current["Saves.Copy.Empty"];
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);

            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current.Format("Saves.Copy.Success", what);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                           or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy save text to the clipboard");
                StatusMessage = Strings.Current["Saves.Copy.Failed"];
            }
        }
    }

    // Restoring a save's order is a reorder like every other one: it records an undo step, goes through
    // the same Apply, and marks the list as differing from the saved LauncherData.xml.
    public partial class EnvironmentViewModel
    {
        public void RestoreOrderFrom(LoadOrderImportPreview preview, string description)
        {
            ArgumentNullException.ThrowIfNull(preview);

            RecordUndo(BuildEnvironment(), description);
            Apply(preview.Result);
            PersistOrder();
        }
    }

    // The reconciliation report, exactly as an import shows one, plus what the save's order costs in
    // ordering rules. A save's order is a historical fact rather than a proposal, so the rules it breaks
    // are listed and then kept, never quietly corrected.
    internal static class SaveRestoreDialog
    {
        private const int MaxLines = 200;

        public static async Task<bool> ConfirmAsync(string saveName, LoadOrderImportPreview preview)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Saves.RestoreDialog.Title", saveName),
                Content = new ScrollViewer { MaxHeight = 520, Content = Report(preview) },
                PrimaryButtonText = preview.CanApply ? Strings.Current["Saves.RestoreDialog.PrimaryButton"] : null,
                CloseButtonText = preview.CanApply
                    ? Strings.Current["Saves.RestoreDialog.CancelButton"]
                    : Strings.Current["Saves.RestoreDialog.CloseButton"],
                DefaultButton = preview.CanApply ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private static StackPanel Report(LoadOrderImportPreview preview)
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = DialogText.ContentWidth };

            panel.Children.Add(Paragraph(preview.Summary));

            if (!preview.CanApply)
                return panel;

            panel.Children.Add(Paragraph(Strings.Current["Saves.RestoreDialog.WriteNote"]));

            Section(panel, Strings.Current.Format("Saves.RestoreDialog.MatchedHeading", preview.Matched.Count),
                Strings.Current["Saves.RestoreDialog.MatchedExplanation"],
                preview.Matched.Select(m => m.KeptAsIs
                    ? Strings.Current.Format("Saves.RestoreDialog.MatchedRow.KeptState", m.DisplayName, m.Id)
                    : $"{m.DisplayName} ({m.Id})"));

            Section(panel, Strings.Current.Format("Saves.RestoreDialog.MissingHeading", preview.Missing.Count),
                Strings.Current["Saves.RestoreDialog.MissingExplanation"],
                preview.Missing.Select(m => $"{m.Id} {m.Version}".TrimEnd()));

            Section(panel, Strings.Current.Format("Saves.RestoreDialog.AbsentHeading", preview.Absent.Count),
                Strings.Current["Saves.RestoreDialog.AbsentExplanation"],
                preview.Absent.Select(a => a.KeptAsIs
                    ? Strings.Current.Format("Saves.RestoreDialog.AbsentRow.GameOwn", a.DisplayName, a.Id)
                    : a.WasEnabled
                        ? Strings.Current.Format("Saves.RestoreDialog.AbsentRow.WouldTurnOff", a.DisplayName, a.Id)
                        : Strings.Current.Format("Saves.RestoreDialog.AbsentRow.AlreadyOff", a.DisplayName, a.Id)));

            Section(panel, Strings.Current.Format("Saves.RestoreDialog.DeltasHeading", preview.Deltas.Count),
                Strings.Current["Saves.RestoreDialog.DeltasExplanation"],
                preview.Deltas.Select(d =>
                    Strings.Current.Format(
                        "Saves.RestoreDialog.DeltaRow", d.DisplayName, d.Id, d.FileVersion, d.InstalledVersion)));

            if (preview.OrderIssues.Count > 0)
            {
                Section(panel,
                    Strings.Current.Format("Saves.RestoreDialog.OrderIssuesHeading", preview.OrderIssues.Count),
                    Strings.Current.Plural("Saves.RestoreDialog.OrderIssuesExplanation.Stated", preview.OrderErrors)
                        + Strings.Current.Plural("Saves.RestoreDialog.OrderIssuesExplanation.Implied", preview.OrderWarnings),
                    preview.OrderIssues.Select(i => i.Message));
            }

            return panel;
        }

        private static void Section(StackPanel panel, string heading, string explanation, IEnumerable<string> lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = heading,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            panel.Children.Add(Paragraph(explanation, 0.7));

            var listed = lines.Take(MaxLines).ToList();
            var total = lines.Count();

            panel.Children.Add(Paragraph(
                listed.Count == 0 ? Strings.Current["Saves.RestoreDialog.NoneLabel"] : string.Join(Environment.NewLine, listed)));

            if (total > listed.Count)
                panel.Children.Add(Paragraph(Strings.Current.Plural("Saves.RestoreDialog.AndMore", total - listed.Count), 0.7));
        }

        private static TextBlock Paragraph(string text, double opacity = 1) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Opacity = opacity
        };
    }
}
