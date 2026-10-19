using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
    // What the Versions page shows for one instance. InstalledInstance carries only what Core knows
    // about the instance itself; whether it is resting, whether it is active, how much disk it costs
    // and what DLC its Modules folder actually holds are all things only a disk read at refresh time
    // can answer, so they are computed once per refresh and carried on this row rather than asked of
    // Core on every bind.
    public sealed class InstanceRowViewModel(
        InstalledInstance instance, bool isResting, bool isActive, long sizeOnDiskBytes, GameDlcSet detectedDlc,
        bool gameFolderExists)
    {
        public InstalledInstance Instance { get; } = instance;

        public InstanceRecord Record => Instance.Record;

        public string Folder => Instance.Folder;

        // What the list sorts on: the version this instance is, not the name it was given.
        public ModuleVersion Version => ModuleVersion.Parse(Record.RecordedGameVersion);

        // What this instance actually is, read off its record and its disk rather than off any name:
        // the version, then every DLC its Modules folder carries. A chosen name is shown above this
        // and never in place of it, so a name cannot turn into a claim about a version.
        public string VersionText => Version.IsEmpty
            ? Record.DisplayName
            : GameVersionLabel.For(Version, DetectedDlc);

        // The name this row is called by: the one the user chose, or the derived version when they
        // chose none.
        public string Name => InstanceLabel.NameOf(Record, VersionText);

        public bool HasChosenName => !string.IsNullOrWhiteSpace(Record.ChosenName);

        // The whole label the row leads with. The resting marker is appended here, from the resting
        // instance id rather than from any text, which is what tells two installs of one version and
        // variant apart: a referenced v1.4.8 + War Sails and a managed copy of it
        // rendered identically until this existed.
        public string RowLabel => InstanceLabel.For(Name, IsResting, Record.Purpose);

        // Compares what this instance recorded against DataGameVersion, the version its own store was
        // last used with. The two can only disagree when something wrote to that store under a
        // different version than the one currently recorded; the saves list is not needed for that
        // comparison, only for naming which saves are affected, which this row does not show.
        private VersionDrift DataVersionDrift => VersionDrift.Detect(Record, Record.DataGameVersion, []);

        public bool HasVersionDrift => DataVersionDrift.Drifted;

        public string VersionDriftText => DataVersionDrift.Drifted
            ? Strings.Current.Format("Versions.VersionDriftSuffix", Record.DataGameVersion)
            : string.Empty;

        // The instance that sits at the canonical paths between launches. At most one row is ever
        // resting.
        public bool IsResting { get; } = isResting;

        // The instance the next Play launch would run. Equal to the resting instance until a version
        // switcher lets the user pick a different one for a single launch.
        public bool IsActive { get; } = isActive;

        public long SizeOnDiskBytes { get; } = sizeOnDiskBytes;

        // Measured once per refresh, off the UI thread, because a referenced install can sit on a
        // drive that is not attached: asking the disk when a menu opens would freeze the gesture that
        // opened it.
        public bool GameFolderExists { get; } = gameFolderExists;

        public string SizeOnDiskText => FormatBytes(SizeOnDiskBytes);

        // What GameDlcDetector actually found in this instance's Modules folder. This is the
        // authority, not Record.Dlc: a referenced or adopted instance, or one whose DLC was dropped
        // in outside BEM, never had a chance to write that field, and War Sails sitting unreported
        // on a real referenced install is exactly that case.
        public GameDlcSet DetectedDlc { get; } = detectedDlc;

        // Every DLC name this instance's own files carry, joined the same way a row's label spells
        // them out; empty for the base game.
        public string DlcLabel => string.Join(" + ", DetectedDlc.DisplayNames);

        // Every DLC BEM knows how to build that this instance's disk does not carry yet. Never
        // predicted ownership: picking one from here is what attempts the download, and Steam
        // answers. Read off DetectedDlc so an instance that already has a DLC on disk is never
        // offered a redundant download of it.
        public IReadOnlyList<GameDlcInfo> MissingDlc => GameDlc.MissingFrom(DetectedDlc);

        // Render an explicit "unknown" rather than a blank cell: a record BEM wrote before it learned
        // to keep the branch and build id (or one it can only guess at, such as a non-Steam install)
        // has neither, and a blank cell reads as a table that lost data rather than one that never had it.
        public string BranchText => string.IsNullOrEmpty(Record.Branch)
            ? Strings.Current["Versions.UnknownValue"]
            : Record.Branch;

        public string BuildIdText => string.IsNullOrEmpty(Record.BuildId)
            ? Strings.Current["Versions.UnknownValue"]
            : Record.BuildId;

        public string RestingLabel => IsResting ? "Resting" : string.Empty;

        public string ActiveLabel => IsActive ? "Active" : string.Empty;

        public string StatusTooltip => (IsResting, IsActive) switch
        {
            (true, true) => Strings.Current["Versions.StatusTooltip.RestingAndActive"],
            (true, false) => Strings.Current["Versions.StatusTooltip.RestingOnly"],
            (false, true) => Strings.Current["Versions.StatusTooltip.ActiveOnly"],
            _ => Strings.Current["Versions.StatusTooltip.Neither"]
        };

        public override string ToString() => RowLabel;

        // "bytes" is a real word a translator renders ("octets" in French); KB/MB/MG stay literal as
        // notation, not prose. The same helper is duplicated verbatim in VersionsViewModel.cs and
        // VersionSwitcherViewModel.cs, neither of which is in this batch's file list; migrating only
        // this copy leaves those two still literal until their own batches reach them.
        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => Strings.Current.Format("Versions.SizeText.Bytes", bytes),
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
        };
    }
}
