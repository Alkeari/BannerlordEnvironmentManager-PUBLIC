using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public sealed record LoadOrderProfile(
    string Name,
    DateTime SavedUtc,
    LoadOrderSnapshot Order,
    bool IsKnownGood = false)
{
    public string DisplayName =>
        $"{Name} - "
        + Strings.Current.Plural("Core.LoadOrder.ModuleCountSummary", Order.Entries.Count, Order.EnabledCount)
        + $" - {SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

// A profile that is off the list and still on disk. The path is what a restore or a discard is
// addressed by: two profiles put here under the same name are two separate files, and a name would
// not tell them apart. Replaced marks the ones that got here by being saved over rather than removed.
public sealed record DeletedProfile(
    string Path,
    DateTime DeletedUtc,
    LoadOrderProfile Profile,
    bool Replaced = false)
{
    public string Name => Profile.Name;

    public string DisplayName =>
        $"{Name} - "
        + Strings.Current.Plural("Core.LoadOrder.ModuleCountSummary", Profile.Order.Entries.Count, Profile.Order.EnabledCount)
        + " - "
        + Strings.Current[Replaced ? "Core.LoadOrder.Profile.Replaced" : "Core.LoadOrder.Profile.Removed"]
        + $" {DeletedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

public sealed class LoadOrderProfileStore(string rootPath)
{
    private const string DeletedFolder = "deleted";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private sealed record ProfileFile(
        string Name,
        DateTime SavedUtc,
        bool IsKnownGood,
        List<LoadOrderSnapshotEntry> Entries,
        DateTime? DeletedUtc = null,
        bool Replaced = false,
        List<LoadOrderDividerSnapshotEntry>? Dividers = null);

    public string RootPath { get; } = rootPath;

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "profiles");

    // The longest name this store will write. ProfileShareFile refuses a longer one on import, and
    // its message says no profile BEM writes ever is: the cap belongs here, where names are made,
    // rather than only on the read that would otherwise be the first thing to turn away a file BEM
    // itself wrote and exported.
    public const int MaxNameLength = 200;

    // The name, and whatever has to follow it, kept inside the cap by shortening the name rather than
    // the marker: " (imported)" appended to a legal 200-character name is what made a 211-character
    // profile that could be saved and shared and never read back.
    private static string Fit(string name, string tail = "")
    {
        var room = Math.Max(MaxNameLength - tail.Length, 0);

        return (name.Length <= room ? name : name[..room].TrimEnd()) + tail;
    }

    public LoadOrderProfile Save(string name, LoadOrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A profile needs a name.", nameof(name));

        var trimmed = Fit(name.Trim());
        var existing = FindFile(trimmed);
        var path = existing?.Path ?? Reserve(trimmed);

        var profile = new LoadOrderProfile(
            trimmed, DateTime.UtcNow, order, existing?.Profile.IsKnownGood ?? false);

        Directory.CreateDirectory(RootPath);

        // Saving over a name destroys a load order the user has no other copy of, and a store whose
        // Delete keeps a copy cannot quietly not keep one here. The version being written over goes to
        // the same folder a removed profile goes to, so it comes back from the same list.
        if (existing is not null)
            KeepAside(existing, replaced: true);

        Write(path, profile);

        return profile;
    }

    public IReadOnlyList<LoadOrderProfile> List() =>
        [.. Stored().Select(s => s.Profile).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public LoadOrderProfile? Find(string name) => FindFile(name)?.Profile;

    public LoadOrderProfile? GetKnownGood() => Stored().Select(s => s.Profile).FirstOrDefault(p => p.IsKnownGood);

    // Exactly one profile is the one to fall back to, so marking a new one clears the old mark in the
    // same pass rather than leaving two candidates and a coin toss over which the button returns to.
    public bool MarkKnownGood(string name)
    {
        var stored = Stored();
        var target = stored.FirstOrDefault(s => Matches(s.Profile.Name, name));

        if (target is null)
            return false;

        foreach (var other in stored)
        {
            var wanted = ReferenceEquals(other, target);

            if (other.Profile.IsKnownGood != wanted)
                Write(other.Path, other.Profile with { IsKnownGood = wanted });
        }

        return true;
    }

    // Moved rather than deleted: a named profile is user data, and the one deliberate exception to
    // that rule in BEM is pruning backups past the retention limit.
    public bool Delete(string name)
    {
        var stored = FindFile(name);

        if (stored is null)
            return false;

        // The copy is written before the original goes, so an interrupted delete leaves the profile
        // listed rather than gone.
        KeepAside(stored, replaced: false);
        File.Delete(stored.Path);

        return true;
    }

    // The time is stamped into the file: the filename is only a uniquifier, and a file time changes
    // with anything that touches the folder.
    private void KeepAside(StoredProfile stored, bool replaced)
    {
        Directory.CreateDirectory(DeletedFolderPath);

        var stem = $"{Path.GetFileNameWithoutExtension(stored.Path)} {DateTime.UtcNow:yyyy-MM-dd HH-mm-ss-fff}";
        var target = Path.Combine(DeletedFolderPath, stem + ".json");
        var counter = 2;

        while (File.Exists(target))
            target = Path.Combine(DeletedFolderPath, $"{stem} {counter++}.json");

        Write(target, stored.Profile, DateTime.UtcNow, replaced);
    }

    public IReadOnlyList<DeletedProfile> ListDeleted()
    {
        if (!Directory.Exists(DeletedFolderPath))
            return [];

        var found = new List<DeletedProfile>();

        foreach (var path in Directory.EnumerateFiles(DeletedFolderPath, "*.json"))
        {
            if (ReadFile(path) is not { } file || string.IsNullOrWhiteSpace(file.Name))
                continue;

            // Profiles removed before the removal time was recorded fall back to the file's own time,
            // which is when it was moved into this folder.
            found.Add(new DeletedProfile(
                path,
                file.DeletedUtc ?? File.GetLastWriteTimeUtc(path),
                ToProfile(file),
                file.Replaced));
        }

        return [.. found.OrderByDescending(d => d.DeletedUtc)];
    }

    // Restoring never overwrites a live profile that has taken the name back: recovering data by
    // destroying data is not a recovery. The copy comes back under a free name and the caller is told
    // which one, so the message can say what actually happened.
    public LoadOrderProfile? Restore(string deletedPath)
    {
        if (!IsDeletedFile(deletedPath) || ReadFile(deletedPath) is not { } file)
            return null;

        if (string.IsNullOrWhiteSpace(file.Name))
            return null;

        // The known-good mark is not restored with it. Exactly one profile is the one to fall back to,
        // and a profile coming back cannot silently take that role from whichever holds it now.
        var profile = new LoadOrderProfile(
            FreeName(file.Name.Trim(), "restored"),
            file.SavedUtc,
            new LoadOrderSnapshot(file.Entries ?? [], file.Dividers));

        Directory.CreateDirectory(RootPath);
        Write(Reserve(profile.Name), profile);
        File.Delete(deletedPath);

        return profile;
    }

    // The one deliberately destructive action in this store, so it may only ever reach the deleted
    // folder: a path anywhere else is refused rather than obeyed. How the file goes is the caller's,
    // so the app can send it to the Recycle Bin without dragging Windows into Core.
    public bool Discard(string deletedPath, Action<string>? removeFile = null)
    {
        if (!IsDeletedFile(deletedPath))
            return false;

        if (removeFile is null)
            File.Delete(deletedPath);
        else
            removeFile(deletedPath);

        return true;
    }

    public string DeletedFolderPath => Path.Combine(RootPath, DeletedFolder);

    private bool IsDeletedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));

        return folder is not null
            && string.Equals(folder, Path.GetFullPath(DeletedFolderPath), StringComparison.OrdinalIgnoreCase);
    }

    // A caller that is about to Save under a name someone else chose asks for this first: Save takes
    // a name back by keeping the previous version aside, which is right for the user's own overwrite
    // and wrong for a profile arriving from somewhere else.
    public string AvailableName(string name, string suffix)
    {
        ArgumentNullException.ThrowIfNull(name);

        return FreeName(name.Trim(), suffix);
    }

    private string FreeName(string name, string suffix)
    {
        var taken = Stored().Select(s => s.Profile.Name).ToList();
        var fitted = Fit(name);

        if (!taken.Any(t => Matches(t, fitted)))
            return fitted;

        var candidate = Fit(name, $" ({suffix})");
        var counter = 2;

        while (taken.Any(t => Matches(t, candidate)))
            candidate = Fit(name, $" ({suffix} {counter++})");

        return candidate;
    }

    private sealed record StoredProfile(string Path, LoadOrderProfile Profile);

    private StoredProfile? FindFile(string name) =>
        Stored().FirstOrDefault(s => Matches(s.Profile.Name, name));

    private static bool Matches(string left, string right) =>
        string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private List<StoredProfile> Stored()
    {
        if (!Directory.Exists(RootPath))
            return [];

        var found = new List<StoredProfile>();

        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            if (Read(path) is { } profile)
                found.Add(new StoredProfile(path, profile));
        }

        return found;
    }

    private static LoadOrderProfile? Read(string path) =>
        ReadFile(path) is { } file && !string.IsNullOrWhiteSpace(file.Name) ? ToProfile(file) : null;

    private static ProfileFile? ReadFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static LoadOrderProfile ToProfile(ProfileFile file) =>
        new(file.Name, file.SavedUtc, new LoadOrderSnapshot(file.Entries ?? [], file.Dividers), file.IsKnownGood);

    private static void Write(
        string path, LoadOrderProfile profile, DateTime? deletedUtc = null, bool replaced = false) =>
        File.WriteAllText(path, JsonSerializer.Serialize(
            new ProfileFile(
                profile.Name, profile.SavedUtc, profile.IsKnownGood, [.. profile.Order.Entries], deletedUtc,
                replaced, [.. profile.Order.Dividers]),
            Format));

    // The name is kept verbatim inside the file, so the filename only has to be legal and unique;
    // two names that sanitize to the same text still get their own file rather than overwriting.
    private string Reserve(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string([.. name.Where(c => !invalid.Contains(c) && c != '.')]).Trim();

        if (stem.Length == 0)
            stem = "profile";

        var path = Path.Combine(RootPath, stem + ".json");
        var counter = 2;

        while (File.Exists(path))
            path = Path.Combine(RootPath, $"{stem} {counter++}.json");

        return path;
    }
}
