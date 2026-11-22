using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The Steam Workshop mods the player is subscribed to, each with one press that copies it into the
    // game's Modules folder, and one press for all of them. The Workshop original is only ever read, so
    // a copy made here keeps the mod installed after its author or Steam takes the item down.
    public partial class InstallViewModel
    {
        public ObservableCollection<WorkshopCopyRowViewModel> WorkshopCopies { get; } = [];

        [ObservableProperty]
        public partial string WorkshopCopyStatus { get; set; } = string.Empty;

        public bool HasWorkshopCopies => WorkshopCopies.Count > 0;

        public bool CanCopyAllWorkshop => !IsBusy && WorkshopCopies.Any(row => row.Item.NeedsCopy);

        [RelayCommand]
        private async Task CheckWorkshopCopiesAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                if (WorkshopGameFolder() is not { } folder)
                {
                    WorkshopCopyStatus = Strings.Current["Install.WorkshopCopies.NoInstall"];
                    return;
                }

                ShowWorkshopCopies(await Task.Run(() => WorkshopLocalCopy.Survey(folder)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Reading the Steam Workshop mods failed");
                WorkshopCopyStatus = ex.Message;
            }
            finally
            {
                FinishWorkshopCopyWork();
            }
        }

        [RelayCommand(CanExecute = nameof(CanCopyAllWorkshop))]
        private async Task CopyAllWorkshopAsync()
        {
            if (IsBusy || WorkshopGameFolder() is not { } folder)
                return;

            await RunWorkshopCopyAsync(folder, () => WorkshopLocalCopy.CopyAll(folder));
        }

        internal async Task CopyWorkshopItemAsync(WorkshopCopyRowViewModel row)
        {
            if (IsBusy || WorkshopGameFolder() is not { } folder)
                return;

            await RunWorkshopCopyAsync(folder, () => [WorkshopLocalCopy.Copy(folder, row.Item)]);
        }

        private async Task RunWorkshopCopyAsync(string folder, Func<IReadOnlyList<WorkshopCopyResult>> copy)
        {
            IsBusy = true;

            try
            {
                var results = await Task.Run(copy);
                var survey = await Task.Run(() => WorkshopLocalCopy.Survey(folder));

                ShowWorkshopCopies(survey);

                var done = results.Count(r => r.Outcome is WorkshopCopyOutcome.Copied or WorkshopCopyOutcome.Refreshed);
                var problems = results.Where(r => r.Outcome is WorkshopCopyOutcome.Failed or WorkshopCopyOutcome.Skipped
                    && r.Message.Length > 0).ToList();

                WorkshopCopyStatus = results.Count == 1 && problems.Count == 0 && done == 1
                    ? Strings.Current.Format(
                        results[0].Outcome == WorkshopCopyOutcome.Refreshed
                            ? "Install.WorkshopCopies.Refreshed"
                            : "Install.WorkshopCopies.Copied",
                        results[0].Item.Name)
                    : problems.Count == 1 && results.Count == 1
                        ? problems[0].Message
                        : Strings.Current.Format(
                            "Install.WorkshopCopies.Finished", done, problems.Count)
                            + (problems.Count > 0 ? " " + string.Join(" ", problems.Select(p => p.Message)) : string.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Copying Steam Workshop mods failed");
                WorkshopCopyStatus = ex.Message;
            }
            finally
            {
                FinishWorkshopCopyWork();
            }
        }

        private void ShowWorkshopCopies(IReadOnlyList<WorkshopCopyItem> items)
        {
            WorkshopCopies.Clear();

            foreach (var item in items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                WorkshopCopies.Add(new WorkshopCopyRowViewModel(this, item));

            WorkshopCopyStatus = items.Count == 0
                ? Strings.Current["Install.WorkshopCopies.None"]
                : Strings.Current.Format(
                    "Install.WorkshopCopies.Summary",
                    items.Count,
                    items.Count(i => i.CopyFolder is not null),
                    items.Count(i => i.NeedsCopy),
                    items.Count(i => i.IsFlagged));
        }

        private void FinishWorkshopCopyWork()
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasWorkshopCopies));
            OnPropertyChanged(nameof(CanCopyAllWorkshop));
            CopyAllWorkshopCommand.NotifyCanExecuteChanged();

            foreach (var row in WorkshopCopies)
                row.CopyCommand.NotifyCanExecuteChanged();
        }

        private string? WorkshopGameFolder()
        {
            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();

            return ChosenDestinations().FirstOrDefault(target => Directory.Exists(target.GameFolder))?.GameFolder;
        }
    }

    public sealed partial class WorkshopCopyRowViewModel : ObservableObject
    {
        public WorkshopCopyRowViewModel(InstallViewModel owner, WorkshopCopyItem item)
        {
            Item = item;
            CopyCommand = new AsyncRelayCommand(() => owner.CopyWorkshopItemAsync(this), () => item.NeedsCopy && !owner.IsBusy);
        }

        public WorkshopCopyItem Item { get; }

        public string Name => Item.Name;

        public string StateText => Strings.Current[Item.State switch
        {
            WorkshopCopyState.NotCopied => "Install.WorkshopCopies.State.NotCopied",
            WorkshopCopyState.Copied => "Install.WorkshopCopies.State.Copied",
            WorkshopCopyState.WorkshopUpdated => "Install.WorkshopCopies.State.Updated",
            WorkshopCopyState.WorkshopUpdatedCopyEdited => "Install.WorkshopCopies.State.Edited",
            WorkshopCopyState.Conflict => "Install.WorkshopCopies.State.Conflict",
            WorkshopCopyState.RemovedFromSubscriptions => "Install.WorkshopCopies.State.Removed",
            _ => "Install.WorkshopCopies.State.FolderMissing"
        }];

        public string ButtonText => Strings.Current[Item.State == WorkshopCopyState.WorkshopUpdated
            ? "Install.WorkshopCopies.RefreshButton"
            : "Install.WorkshopCopies.CopyButton"];

        public string ButtonTooltip => Strings.Current[Item.State == WorkshopCopyState.WorkshopUpdated
            ? "Install.WorkshopCopies.RefreshButton.Tooltip"
            : "Install.WorkshopCopies.CopyButton.Tooltip"];

        public bool CanCopy => Item.NeedsCopy;

        public AsyncRelayCommand CopyCommand { get; }
    }
}
