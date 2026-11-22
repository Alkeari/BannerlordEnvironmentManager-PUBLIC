using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Saves;

public sealed record ProfileSavesSwitchResult(int Stored, int Brought, int Kept)
{
    public static ProfileSavesSwitchResult None { get; } = new(0, 0, 0);
}

// Saves that belong to a load-order profile, as Mod Organizer 2's profile-local saves do. A campaign
// made with one set of mods rarely loads under another, so each profile can keep its own saves:
// switching moves the live .sav files into the profile being left and brings the target's back.
//
// Files are moved, never copied and never deleted, so a save exists in exactly one place at any time
// and an interrupted switch loses nothing. A name that is already taken where a file is going is kept
// under a numbered name rather than overwritten. The feature is off until a profile is switched on,
// so nobody's saves move by surprise, and a profile with no stored folder leaves the live saves where
// they are: the first switch away from it is what gives it a folder.
public sealed class ProfileSavesStore(string rootPath)
{
    private const string SaveExtension = ".sav";

    private const string EnabledMarker = "enabled.marker";

    private const string ActiveFile = "active.profile.txt";

    public string RootPath { get; } = rootPath;

    public static string DefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "profile-saves");

    public bool IsEnabled(string profileName) => File.Exists(Path.Combine(FolderFor(profileName), EnabledMarker));

    // Turning it on creates the profile's folder, which is what makes the next switch to it bring
    // saves; turning it off leaves whatever is stored, so a profile can be switched back on later.
    public void SetEnabled(string profileName, bool enabled)
    {
        var marker = Path.Combine(FolderFor(profileName), EnabledMarker);

        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, string.Empty);
        }
        else if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    public IReadOnlyList<string> StoredSaves(string profileName)
    {
        var folder = FolderFor(profileName);

        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*" + SaveExtension).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    public string? ActiveName
    {
        get
        {
            var path = Path.Combine(RootPath, ActiveFile);

            try
            {
                return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } name ? name : null;
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
        File.WriteAllText(Path.Combine(RootPath, ActiveFile), profileName);
    }

    // Leave the active profile, enter this one. Nothing moves unless the profile being left is switched
    // on (its saves go home) or the target is (its saves come in).
    public ProfileSavesSwitchResult SwitchTo(string profileName, string liveFolder)
    {
        var result = Switch(ActiveName, profileName, liveFolder);

        SetActive(profileName);
        return result;
    }

    public ProfileSavesSwitchResult Switch(string? fromProfile, string toProfile, string liveFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveFolder);

        if (string.Equals(fromProfile, toProfile, StringComparison.OrdinalIgnoreCase))
            return ProfileSavesSwitchResult.None;

        var stored = 0;
        var kept = 0;

        if (!string.IsNullOrWhiteSpace(fromProfile) && IsEnabled(fromProfile) && Directory.Exists(liveFolder))
        {
            foreach (var file in Directory.EnumerateFiles(liveFolder, "*" + SaveExtension))
            {
                if (MoveInto(file, FolderFor(fromProfile)))
                    stored++;
                else
                    kept++;
            }
        }

        var brought = 0;

        if (IsEnabled(toProfile))
        {
            Directory.CreateDirectory(liveFolder);

            foreach (var file in Directory.EnumerateFiles(FolderFor(toProfile), "*" + SaveExtension).ToList())
            {
                if (MoveInto(file, liveFolder))
                    brought++;
                else
                    kept++;
            }
        }

        return new ProfileSavesSwitchResult(stored, brought, kept);
    }

    private static bool MoveInto(string file, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var name = Path.GetFileNameWithoutExtension(file);
            var target = Path.Combine(folder, name + SaveExtension);
            var counter = 2;

            while (File.Exists(target))
                target = Path.Combine(folder, $"{name} ({counter++}){SaveExtension}");

            File.Move(file, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string FolderFor(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var safe = string.Concat(profileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

        return Path.Combine(RootPath, safe.Length == 0 ? "_" : safe);
    }
}
