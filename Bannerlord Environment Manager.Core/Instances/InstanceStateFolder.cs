using BannerlordEnvironmentManager.Core.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Instances;

// Where BEM keeps its own opinions about one version's load order: the pinned modules, the section
// dividers, the saved profiles, the accepted findings and risks, the launch history and the settings
// attributions. Every one of those describes one specific list of modules, so one machine-wide copy
// showed a risk accepted on the Steam install a fortnight ago while a freshly downloaded v1.5.2 was
// selected, against mods that version does not even have.
//
// This stays inside BEM's own AppData folder rather than moving into the user-visible games root. The
// design puts instance data in the games root so that uninstalling BEM can never take a save with it,
// and none of this is data of that kind: it is BEM's judgment about a load order, regenerable by
// re-running the checks, and the uninstall contract already says the AppData folder holds preferences
// rather than user data.
//
// The resting version keeps the exact paths it has always used, at the top of that folder. Only a
// version that is not the resting one gets a subfolder of its own. That is what makes this change need
// no migration: nothing that exists on a machine today moves, and nothing that exists today is
// orphaned, because the version those files were written for is the one still reading them.
//
// The price of filing by position rather than by identity is that changing which version rests has to
// move two sets of files past each other. InstanceStateRelocation is that move, and StateEntryNames
// below is the set it moves.
public static class InstanceStateFolder
{
    public const string AppFolderName = "Bannerlord Environment Manager";

    public const string InstancesFolderName = "instances";

    public static string MachineRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    // Every file and folder this scheme files per version, named once so that changing which version
    // rests can move the whole set. Adding a store without adding its name here would leave that store
    // behind on a resting change, handing one version's judgment to another, so PerInstanceStateTests
    // asserts this list is exactly what the stores themselves resolve to.
    public static IReadOnlyList<string> StateEntryNames { get; } =
    [
        "accepted-findings.json",
        "accepted-preflight-risks.json",
        "launches.json",
        "pinned-modules.json",
        "dividers.json",
        "settings-attributions.json",
        "profiles",
        "backups",
        "cleared",
        "patch-registry",
        "SettingsArchive",
        "bisect",
        "launch-settings.json",
        "bin-backups",
        "module-archive-links.json"
    ];

    public static string For(InstanceDataRoot? dataRoot) => FolderIn(MachineRoot(), KeyFor(dataRoot));

    // Every root that files state by version. BEM's own AppData folder holds all of it but one: the
    // captured crash artifacts sit in a folder of their own, because a folder a crash scan walks may
    // never sit under one holding the Nexus key. Both are keyed the same way, so a removal and a
    // fresh install can reach both the same way. Read rather than cached, because a test points
    // LocalApplicationData somewhere of its own.
    public static IReadOnlyList<string> StateRoots() =>
        [MachineRoot(), Path.GetDirectoryName(CrashArtifactPaths.GetDefaultRoot())!];

    // Where this one version's state sits, in every root that files it by version.
    //
    // Empty when the folder yields no key, which is also what keeps a root itself from ever being
    // named: the resting version's state is the root, and answering with it would let a removal
    // delete every version's state at once.
    public static IReadOnlyList<string> FoldersFor(string instanceFolder) =>
        FoldersFor(StateRoots(), instanceFolder);

    public static IReadOnlyList<string> FoldersFor(IEnumerable<string> stateRoots, string instanceFolder)
    {
        ArgumentNullException.ThrowIfNull(stateRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        return KeyFor(InstanceDataRoot.ForInstance(instanceFolder)) is { } key
            ? [.. stateRoots.Select(root => FolderIn(root, key))]
            : [];
    }

    // A version BEM has just downloaded has no state of its own yet, so anything already filed under
    // the folder name it is taking was written for a version that is gone: another version's launch
    // settings, accepted findings, dividers, launch history and saved profiles, one of which is the
    // manual load-order override gate. Inheriting them silently is the defect.
    //
    // Renamed out of the way rather than deleted. A removal that kept the user's data is one of the
    // two ways state is orphaned, and answering "you asked to keep this" by deleting it would be the
    // opposite mistake; the folder stays beside where it was, under a name nothing keys on, and the
    // new install starts with none. Returns what was moved and where.
    //
    // The roots are handed in rather than read from StateRoots(). This method moves real folders, and
    // a caller that does not say which roots it means would move the machine's own under any test
    // that registered an instance whose name matched one of the user's versions.
    public static IReadOnlyList<string> SetAside(IEnumerable<string> stateRoots, string instanceFolder)
    {
        ArgumentNullException.ThrowIfNull(stateRoots);

        var moved = new List<string>();

        foreach (var folder in FoldersFor(stateRoots, instanceFolder))
        {
            if (!Directory.Exists(folder))
                continue;

            try
            {
                var destination = FreeSetAsideName(folder);
                Directory.Move(folder, destination);
                moved.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }

        return moved;
    }

    private static string FreeSetAsideName(string folder)
    {
        var named = $"{folder} (removed)";

        if (!Directory.Exists(named))
            return named;

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{folder} (removed {suffix})";

            if (!Directory.Exists(candidate))
                return candidate;
        }

        throw new IOException($"'{folder}' has already been set aside 99 times.");
    }

    // The same layout against a root the caller names, which is what lets the resting swap be driven
    // over a temporary folder instead of the machine's own AppData.
    public static string FolderIn(string machineRoot, string? key) =>
        key is null ? machineRoot : Path.Combine(machineRoot, InstancesFolderName, key);

    public static string File(InstanceDataRoot? dataRoot, string fileName) =>
        Path.Combine(For(dataRoot), fileName);

    // The instance folder's own name, which is what the user sees and what the registry keys an
    // instance on. Sanitized rather than trusted: the folder is named after a display name, which is
    // free text, and a separator that slipped through would put BEM's state a level down where nothing
    // would ever look for it again.
    public static string? KeyFor(InstanceDataRoot? dataRoot)
    {
        if (dataRoot?.InstanceFolder is not { } folder || string.IsNullOrWhiteSpace(folder))
            return null;

        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '-' : c)]).Trim().Trim('.', ' ');

        return cleaned.Any(char.IsLetterOrDigit) ? cleaned : null;
    }
}
