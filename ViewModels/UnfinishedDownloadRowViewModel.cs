using System.Windows.Input;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One folder in the games root holding part of a download that stopped. The commands travel with
    // the row for the same reason MissingDlcRowViewModel's does: the DataTemplate rendering it cannot
    // reach the page's view model.
    //
    // Version is the row in the download picker this folder was on its way to becoming, matched by
    // the folder name the download itself would compute. With one, the download can be picked up
    // where it stopped; without one, BEM will not guess, and the folder can only be discarded.
    public sealed class UnfinishedDownloadRowViewModel(
        UnfinishedDownload download,
        string sizeText,
        GameVersionRowViewModel? version,
        ICommand resumeCommand,
        ICommand discardCommand)
    {
        public UnfinishedDownload Download { get; } = download;

        public GameVersionRowViewModel? Version { get; } = version;

        public ICommand ResumeCommand { get; } = resumeCommand;

        public ICommand DiscardCommand { get; } = discardCommand;

        public string Folder => Download.Folder;

        public bool CanResume => Version is not null;

        public string Label { get; } = Strings.Current.Format("Versions.Unfinished.Row", download.Name, sizeText);

        public string Tooltip => CanResume
            ? Strings.Current.Format("Versions.Unfinished.Row.Tooltip", Download.Folder, Version!.Label)
            : Strings.Current.Format("Versions.Unfinished.Row.Tooltip.Unknown", Download.Folder);

        public string ResumeTooltip => CanResume
            ? Strings.Current.Format("Versions.Unfinished.ResumeButton.Tooltip", Version!.Label)
            : Strings.Current["Versions.Unfinished.ResumeButton.Tooltip.Unknown"];

        public string DiscardTooltip => Strings.Current.Format("Versions.Unfinished.DiscardButton.Tooltip", Download.Name);
    }
}
