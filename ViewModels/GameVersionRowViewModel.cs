using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One line in the version dropdown. A row is either a version already on this machine, which
    // carries the instance it belongs to, or one Steam publishes that BEM would have to download
    // first. Both are shown by version and never by branch, because a branch name is Steam's
    // bookkeeping and tells the user nothing about which game they are about to run.
    public sealed class GameVersionRowViewModel(GameVersionEntry entry, string fallbackLabel = "")
    {
        public GameVersionEntry Entry { get; } = entry;

        public ModuleVersion Version => Entry.Version;

        public string Branch => Entry.Branch;

        public string BuildId => Entry.BuildId;

        public InstalledInstance? Instance => Entry.Instance;

        public bool IsInstalled => Entry.IsInstalled;

        public bool IsActive => Entry.IsActive;

        // An instance whose version could not be read anywhere keeps its own name rather than
        // vanishing from the list: a version the user cannot see is a version they cannot play.
        // A variant spells its DLC out after the version, so "v1.5.2 + War Sails" reads as what it
        // is rather than as the short, folder-facing key the variant also carries.
        //
        // This is the derived identity and it never moves: a download names the instance it registers
        // after it, and the unfinished-download scan re-derives a folder name from it. What the user
        // reads is DisplayLabel.
        public string Label { get; } = DerivedLabel(entry, fallbackLabel);

        // The label as it is shown: the name the user gave this version when they gave one, then the
        // version itself, then the marker for the version resting at the canonical paths or for what
        // the instance is declared to be for. All of it comes off the instance behind the row, so a
        // version Steam merely offers reads exactly as Label does. The version is kept even when
        // there is a name, because this dropdown is the one place a version is chosen and it carries
        // no column of its own to say which build a name stands for.
        public string DisplayLabel { get; } = InstanceLabel.For(
            InstanceLabel.NamedVersionOf(entry.Instance?.Record, DerivedLabel(entry, fallbackLabel)),
            entry.IsResting,
            entry.Instance?.Record.Purpose ?? InstancePurpose.Unspecified);

        private static string DerivedLabel(GameVersionEntry entry, string fallbackLabel) =>
            entry.Version.IsEmpty ? fallbackLabel : GameVersionLabel.For(entry.Version, entry.Variant);

        // A download row whose version and variant this machine already holds. Picking it fetches a
        // second, independent copy rather than the one that is here.
        public bool IsAnotherCopy => Entry.IsAnotherCopy;

        // One indicator, on the rows it distinguishes. Play's dropdown lists only what is on this
        // machine and the Versions picker lists only what can be downloaded, so tagging those rows
        // "Installed" and "Download" told the reader what the list they opened is already for. Which
        // version is the active one, and which download row would make a second copy of something
        // already here rather than bring a new version in, are the two facts neither list carries in
        // its own right.
        public string StateText => IsActive
            ? Strings.Current["Environment.VersionRow.StateText.Active"]
            : IsAnotherCopy
                ? Strings.Current["Environment.VersionRow.StateText.AnotherCopy"]
                : string.Empty;

        public string Tooltip => IsActive
            ? Strings.Current.Format("Environment.VersionRow.Tooltip.Active", DisplayLabel)
            : IsInstalled
                ? Strings.Current.Format("Environment.VersionRow.Tooltip.Installed", DisplayLabel)
                : IsAnotherCopy
                    ? Strings.Current.Format("Environment.VersionRow.Tooltip.AnotherCopy", DisplayLabel)
                    : Strings.Current.Format("Environment.VersionRow.Tooltip.NotInstalled", DisplayLabel);

        // What a ComboBox draws for the selected item when its ItemTemplate is not in play.
        public override string ToString() => DisplayLabel;
    }
}
