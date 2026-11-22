using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Settings;

public sealed record ProfileSettingsSwitchResult(int Saved, int Applied, int BackedUp);

// A load-order profile owns a copy of the mods' settings folders (the MCM and ModLib files under
// Configs\ModSettings). Switching profile saves what is live into the profile being left and puts the
// target's copy live. A folder the target replaces is first backed up into ModSettingsArchive, so no
// switch can lose a setting. A profile that has no saved copy yet leaves the live files alone: the
// first switch away from it is what gives it one.
public sealed class ProfileSettingsStore(string rootPath)
{
    public string RootPath { get; } = rootPath;

    public static string DefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "profile-settings");

    public bool Has(string profileName) => Directory.Exists(FolderFor(profileName));

    public int Capture(string profileName, string liveRoot)
    {
        var target = FolderFor(profileName);
        var folders = ModSettingsScanner.Scan(liveRoot).Where(folder => !ModSettingsScanner.IsEmpty(folder)).ToList();

        // Built beside the old copy and swapped in, so a failure halfway leaves the previous copy.
        var staging = target + ".new";

        DirectoryMover.TryDeleteTree(staging);
        Directory.CreateDirectory(staging);

        foreach (var folder in folders)
            DirectoryMover.Copy(folder.FullPath, Path.Combine(staging, folder.RelativePath));

        DirectoryMover.TryDeleteTree(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(staging, target);

        return folders.Count;
    }

    public ProfileSettingsSwitchResult Switch(
        string? fromProfile, string toProfile, string liveRoot, ModSettingsArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (string.Equals(fromProfile, toProfile, StringComparison.OrdinalIgnoreCase))
            return new ProfileSettingsSwitchResult(0, 0, 0);

        var saved = string.IsNullOrWhiteSpace(fromProfile) ? 0 : Capture(fromProfile, liveRoot);
        var source = FolderFor(toProfile);

        if (!Directory.Exists(source))
            return new ProfileSettingsSwitchResult(saved, 0, 0);

        var applied = 0;
        var backedUp = 0;

        foreach (var stored in ModSettingsScanner.Scan(source))
        {
            var live = Path.Combine(liveRoot, stored.RelativePath);

            if (Directory.Exists(live))
            {
                var current = ModSettingsScanner.Find(liveRoot, stored.RelativePath);

                if (current is not null && current.FileCount > 0)
                {
                    archive.Backup(current, ArchiveReason.ReplacedByRestore);
                    backedUp++;
                }

                DirectoryMover.DeleteTree(live);
            }

            DirectoryMover.Copy(stored.FullPath, live);
            applied++;
        }

        return new ProfileSettingsSwitchResult(saved, applied, backedUp);
    }

    private string ActivePath => Path.Combine(RootPath, "active.profile.txt");

    // The profile whose settings are live right now, remembered so the next switch knows which copy
    // to save the live files into. Null until the first profile is saved or restored.
    public string? ActiveName
    {
        get
        {
            try
            {
                return File.Exists(ActivePath) && File.ReadAllText(ActivePath).Trim() is { Length: > 0 } name ? name : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void SetActive(string profileName)
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(ActivePath, profileName);
    }

    // The one call the app makes when a profile is restored: leave the active profile, enter this one.
    public ProfileSettingsSwitchResult SwitchTo(string profileName, string liveRoot, ModSettingsArchive archive)
    {
        var result = Switch(ActiveName, profileName, liveRoot, archive);

        SetActive(profileName);
        return result;
    }

    public void Forget(string profileName) => DirectoryMover.TryDeleteTree(FolderFor(profileName));

    private string FolderFor(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var safe = string.Concat(profileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

        return Path.Combine(RootPath, safe.Length == 0 ? "_" : safe);
    }
}
