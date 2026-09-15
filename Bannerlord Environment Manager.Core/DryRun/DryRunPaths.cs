using System.Globalization;

namespace BannerlordEnvironmentManager.Core.DryRun;

// The companion resolves these same paths independently from LOCALAPPDATA, knowing only the run id.
// That is deliberate: it writes to a place BEM knows without needing to know anything about BEM, and
// it never writes into the game folder.
public static class DryRunPaths
{
    public const string BreadcrumbSuffix = ".breadcrumbs.jsonl";

    public const string ResultSuffix = ".json";

    private const string AppFolderName = "Bannerlord Environment Manager";

    private const string CurrentRunFolderName = "runs";

    // What this folder was called before watching existed, when everything written to it really was
    // a throwaway boot check. Kept only so MigrateLegacyRunFolder can find history left under it.
    private const string LegacyRunFolderName = "dry-run";

    // Run once per process, the first time anything asks for the root: a rename in code is not a
    // rename on disk, and a boot check's own history from before watching existed would otherwise
    // sit under the old name forever, invisible to LaunchRunLocator, which is exactly the kind of
    // orphaned leftover this rename exists to stop making.
    private static readonly string CachedRoot = MigrateLegacyRunFolder();

    // Named "runs" rather than "dry-run": a boot check and a watched play session both land here as
    // one LaunchRun each, and have since watching was added. Must match Companion's DryRunFiles
    // .RunFolderName, which the companion resolves independently.
    public static string GetDefaultRoot() => CachedRoot;

    private static string MigrateLegacyRunFolder()
    {
        var appFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

        var current = Path.Combine(appFolder, CurrentRunFolderName);
        var legacy = Path.Combine(appFolder, LegacyRunFolderName);

        // One-way door, not a merge: once "runs" exists this never looks at "dry-run" again. A build
        // older than this one still writes to "dry-run" by its own compiled-in name, so running an
        // older build after a newer one leaves that later history stranded under the legacy name with
        // no future migration to find it. Acceptable because BEM ships one version at a time; not
        // acceptable to build a merge for.
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
                Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A migration that cannot run leaves history under the old name rather than losing it.
            // LaunchRunLocator simply will not find it until a later launch succeeds at moving it.
        }

        return current;
    }

    // Config snapshotting is DryRun-only: a boot check restores LauncherData.xml and Configs
    // afterward because the run is throwaway, and a watched session must never do that to a real
    // play session's own settings changes. "dry-run-snapshot" is accurate as it stands.
    public static string GetDefaultSnapshotRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "dry-run-snapshot");

    public static string BreadcrumbPath(string root, string runId) =>
        Path.Combine(root, runId + BreadcrumbSuffix);

    public static string ResultPath(string root, string runId) =>
        Path.Combine(root, runId + ResultSuffix);

    // The run id travels on the command line and becomes a file name at the other end, so it stays
    // inside the character set the companion accepts: letters, digits, hyphen and underscore.
    public static string NewRunId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
        + "-"
        + Guid.NewGuid().ToString("N")[..4];
}
