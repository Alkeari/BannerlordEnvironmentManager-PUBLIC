using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.Nexus;

// One mod of a collection BEM wrote: the module, whether it was switched on, and where it came from on
// Nexus (the page and the exact file BEM recorded when it installed it). A mod BEM has no Nexus link
// for still travels, so the load order is whole, but the player is told it has nowhere to download from.
public sealed record BemCollectionMod(
    string ModuleId,
    string Name,
    string? Version,
    int? NexusModId,
    int? NexusFileId,
    string? NexusFileName,
    bool Enabled = true);

// A settings file the curator's mods wrote. Root is the Configs subfolder it lives in (ModSettings for
// MCM, ModLib for ModLib) and Path is relative to that root with forward slashes. The content is Base64
// so any encoding or line ending survives the trip.
public sealed record BemCollectionSetting(string Root, string Path, string ContentBase64);

// A collection saved by Create a Collection and read by Install a Collection. Mods are in load order.
// ExcludedForSecrets names the settings files left out because they looked like a key, token or
// password; they are named, never carried.
public sealed record BemCollection(
    int Format,
    string Name,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<BemCollectionMod> Mods,
    IReadOnlyList<BemCollectionSetting> Settings,
    IReadOnlyList<string> ExcludedForSecrets)
{
    public const int CurrentFormat = 1;

    public const string FileExtension = ".bemcollection";

    public const string LocalSlug = "bemcollection";

    // The mods Nexus can supply, in the shape the existing Install a Collection already plans and
    // fetches from, so a BEM collection is installed by the same code a Nexus collection is. Mods with
    // no recorded page or file are counted as Unlisted, which is how that plan already says "N of M".
    public NexusCollectionRevision ToRevision() =>
        new(LocalSlug, Name, 1,
            [.. Mods.Where(m => m.NexusModId is not null && m.NexusFileId is not null)
                .Select(m => new NexusCollectionMod(
                    m.NexusModId!.Value, m.NexusFileId!.Value, m.Name, m.NexusFileName, m.Version, Optional: false,
                    UpdatePolicy: "exact"))],
            Unlisted: Mods.Count(m => m.NexusModId is null || m.NexusFileId is null));
}

public static class BemCollectionFile
{
    public const long MaxSettingBytes = 1024 * 1024;

    // Root folders under Configs a collection may carry settings for, and nothing else.
    public static readonly string[] SettingRoots = ["ModSettings", "ModLib"];

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(string path, BemCollection collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(collection);

        var json = JsonSerializer.Serialize(collection, Format);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    // Null for a file that is not a collection BEM can read, or one written by a newer BEM.
    public static BemCollection? Read(string path)
    {
        try
        {
            var collection = JsonSerializer.Deserialize<BemCollection>(File.ReadAllText(path), Format);

            return collection is { Format: BemCollection.CurrentFormat, Mods: not null, Settings: not null }
                ? collection with { ExcludedForSecrets = collection.ExcludedForSecrets ?? [] }
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // What a mod keeps beside its settings and a collection must not carry: a cache it rebuilds, a log, a
    // backup, a save-game side file, and the emergency copies BEM itself writes next to a save.
    public static bool IsRealSetting(string relativePath)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return false;

        if (segments.Take(segments.Length - 1).Any(segment =>
                segment.Equals("saves", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("cache", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("logs", StringComparison.OrdinalIgnoreCase)))
            return false;

        var name = segments[^1];

        return !(name.Contains("cache", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BEM Emergency", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bem.bak", StringComparison.OrdinalIgnoreCase));
    }

    // A collection is a file from someone else, so a path that climbs out of its root, names a root BEM
    // does not manage or is rooted is refused rather than followed.
    public static string? SafeRelativePath(BemCollectionSetting setting)
    {
        if (!SettingRoots.Contains(setting.Root, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(setting.Path) || Path.IsPathRooted(setting.Path))
            return null;

        var segments = setting.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 || segments.Any(s => s is "." or ".." || s.Contains(':')))
            return null;

        return string.Join(Path.DirectorySeparatorChar, segments);
    }
}

// Create a Collection: what is switched on now, in order, with where each mod comes from and the
// settings its mods wrote.
public static class BemCollectionBuilder
{
    public static BemCollection Create(
        string name,
        IReadOnlyList<LauncherModEntry> launcherEntries,
        IEnumerable<NexusModuleLink> links,
        IEnumerable<string> moduleNames,
        string configsRoot,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(launcherEntries);
        ArgumentNullException.ThrowIfNull(links);

        var byModule = links.GroupBy(l => l.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var mods = new List<BemCollectionMod>();

        foreach (var entry in launcherEntries.Where(e => e.IsSelected))
        {
            byModule.TryGetValue(entry.Id.Value, out var link);

            mods.Add(new BemCollectionMod(
                entry.Id.Value,
                link?.ModuleName is { Length: > 0 } linked ? linked : entry.Id.Value,
                link?.NexusVersionAtInstall is { Length: > 0 } atInstall ? atInstall : link?.InstalledVersion,
                link?.NexusModId,
                link?.NexusFileId,
                link?.NexusFileNameAtInstall));
        }

        var wanted = mods.Select(m => m.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = new List<BemCollectionSetting>();
        var excluded = new List<string>();

        foreach (var root in BemCollectionFile.SettingRoots)
        {
            var rootPath = Path.Combine(configsRoot, root);

            foreach (var folder in ModSettingsScanner.Scan(rootPath).Where(f => wanted.Contains(f.Name)))
            {
                foreach (var file in ModSettingsScanner.EnumerateFiles(folder.FullPath))
                {
                    var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    var shown = root + "/" + relative;
                    var length = new FileInfo(file).Length;

                    if (SettingsBundle.IsBemCredentialFile(relative) || !BemCollectionFile.IsRealSetting(relative))
                        continue;

                    // Anything the scanner flagged, could not read or that is too large to be a settings
                    // file is left out: a collection leaves the machine, and "probably fine" is not enough.
                    var scan = SecretScanner.Inspect(file, relative);

                    if (scan.Findings.Count > 0 || !scan.WasRead || length > BemCollectionFile.MaxSettingBytes)
                    {
                        excluded.Add(shown);
                        continue;
                    }

                    settings.Add(new BemCollectionSetting(root, relative, Convert.ToBase64String(File.ReadAllBytes(file))));
                }
            }
        }

        return new BemCollection(
            BemCollection.CurrentFormat, name.Trim(), now ?? DateTimeOffset.UtcNow, mods,
            [.. settings.OrderBy(s => s.Root + "/" + s.Path, StringComparer.OrdinalIgnoreCase)],
            [.. excluded.Order(StringComparer.OrdinalIgnoreCase)]);
    }
}

public sealed record BemCollectionApplyResult(
    IReadOnlyList<string> OrderApplied,
    IReadOnlyList<string> NotInstalled,
    IReadOnlyList<string> SettingsWritten,
    IReadOnlyList<string> SettingsUnchanged,
    IReadOnlyList<string> SettingsSkipped,
    IReadOnlyList<ArchivedSettings> BackedUp);

// Install a Collection, after the mods are on disk: the curator's load order goes into LauncherData.xml
// (which keeps its own dated backup) and the curator's settings files go into Configs, with any file
// about to be overwritten backed up first through the settings archive so it can be restored.
public static class BemCollectionApplier
{
    public static BemCollectionApplyResult Apply(
        BemCollection collection,
        ISet<string> installedModuleIds,
        LauncherDataStore launcher,
        string configsRoot,
        ModSettingsArchive archive)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(installedModuleIds);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(archive);

        var installed = new HashSet<string>(installedModuleIds, StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        var missing = new List<string>();
        var curated = new List<LauncherModEntry>();

        foreach (var mod in collection.Mods)
        {
            if (!installed.Contains(mod.ModuleId))
            {
                missing.Add(mod.ModuleId);
                continue;
            }

            if (curated.Any(e => string.Equals(e.Id.Value, mod.ModuleId, StringComparison.OrdinalIgnoreCase)))
                continue;

            curated.Add(new LauncherModEntry(new Modules.ModuleId(mod.ModuleId), mod.Enabled));
            applied.Add(mod.ModuleId);
        }

        // The curator's mods come first in the curator's order; anything else the player has keeps its
        // own state after them, so applying a collection never forgets a mod it knows nothing about.
        if (curated.Count > 0)
        {
            var rest = launcher.Read().Where(e => !applied.Contains(e.Id.Value, StringComparer.OrdinalIgnoreCase));

            launcher.Write([.. curated, .. rest], Strings.Current["Core.Nexus.CollectionLoadOrderBackupReason"]);
        }

        return ApplySettings(collection, configsRoot, archive) with { OrderApplied = applied, NotInstalled = missing };
    }

    private static BemCollectionApplyResult ApplySettings(BemCollection collection, string configsRoot, ModSettingsArchive archive)
    {
        var written = new List<string>();
        var unchanged = new List<string>();
        var skipped = new List<string>();
        var backedUp = new List<ArchivedSettings>();
        var backedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in collection.Settings)
        {
            var shown = setting.Root + "/" + setting.Path;

            byte[] content;

            try
            {
                content = Convert.FromBase64String(setting.ContentBase64);
            }
            catch (FormatException)
            {
                skipped.Add(shown);
                continue;
            }

            if (BemCollectionFile.SafeRelativePath(setting) is not { } relative
                || content.Length > BemCollectionFile.MaxSettingBytes
                || SettingsBundle.IsBemCredentialFile(relative)
                || SecretScanner.InspectContent(relative, System.Text.Encoding.UTF8.GetString(content)).Findings.Count > 0)
            {
                skipped.Add(shown);
                continue;
            }

            var rootPath = Path.GetFullPath(Path.Combine(configsRoot, setting.Root));
            var destination = Path.GetFullPath(Path.Combine(rootPath, relative));

            if (!destination.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(shown);
                continue;
            }

            if (File.Exists(destination))
            {
                if (File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
                {
                    unchanged.Add(shown);
                    continue;
                }

                // The mod's whole settings folder is what the archive keeps, once, before the first
                // file in it is replaced.
                var owning = SettingsBundle.OwningFolder(relative);
                var key = setting.Root + "/" + owning;

                if (backedFolders.Add(key) && ModSettingsScanner.Find(rootPath, owning) is { } folder)
                    backedUp.Add(archive.Backup(folder, ArchiveReason.ReplacedByImport));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Io.InstanceFiles.WriteAllBytes(destination, content);
            written.Add(shown);
        }

        return new BemCollectionApplyResult([], [], written, unchanged, skipped, backedUp);
    }
}
