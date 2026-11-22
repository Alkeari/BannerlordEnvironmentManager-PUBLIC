using System.Text.Json;

using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Modules;

// Who put a module on disk, as far as the disk and BEM's own records can tell.
public enum ModManager
{
    Hand,
    Bem,
    Vortex,
    Mo2
}

// NexusModId is only ever filled from MO2's meta.ini, the one place another manager keeps the id
// where BEM can read it. Detail names the evidence, for a tooltip.
public sealed record ModuleManagement(ModManager Manager, int? NexusModId = null, string? Detail = null)
{
    public static ModuleManagement ByHand { get; } = new(ModManager.Hand);
}

// A module to label: the folder under Modules\ and the id BEM's install records key it by.
public sealed record ModuleToLabel(string FolderName, string? ModuleId = null);

// Read-only. Every source is a hint that may be missing, locked or malformed, and none of that is
// allowed to throw: an unreadable source simply claims nothing.
public static class ModManagerDetector
{
    public static IReadOnlyDictionary<string, ModuleManagement> Detect(
        string gameRoot,
        IEnumerable<ModuleToLabel> modules,
        ModuleArchiveLinkStore? bemRecords = null,
        IEnumerable<string>? mo2Roots = null)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var vortex = VortexOwnedFolders(gameRoot);
        var mo2 = Mo2Modules(gameRoot, mo2Roots);
        var recorded = bemRecords?.ByModuleId();
        var result = new Dictionary<string, ModuleManagement>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            if (vortex.Contains(module.FolderName))
                result[module.FolderName] = new ModuleManagement(ModManager.Vortex, null, "vortex.deployment");
            else if (mo2.TryGetValue(module.FolderName, out var owned))
                result[module.FolderName] = owned;
            else if (module.ModuleId is { } id && recorded?.ContainsKey(id) == true)
                result[module.FolderName] = new ModuleManagement(ModManager.Bem);
            else
                result[module.FolderName] = ModuleManagement.ByHand;
        }

        return result;
    }

    // How many of these module folders (each ...\Modules\<name>) Vortex deployed. A module outside a
    // game root's Modules folder, a Workshop copy for one, is never Vortex's.
    public static int CountVortexOwned(IEnumerable<string> moduleFolderPaths)
    {
        var owned = 0;
        var cache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in moduleFolderPaths)
        {
            var modules = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            var root = modules is null ? null : Path.GetDirectoryName(modules);

            if (root is null || !Path.GetFileName(modules)!.Equals("Modules", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!cache.TryGetValue(root, out var set))
                cache[root] = set = VortexOwnedFolders(root);

            if (set.Contains(Path.GetFileName(folder.TrimEnd('\\', '/'))))
                owned++;
        }

        return owned;
    }

    // The default place MO2 keeps instances: portable ones sit wherever the owner put them, which is
    // what the optional configured path is for.
    public static IReadOnlyList<string> DefaultMo2Roots(string? configured = null)
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
            roots.Add(configured);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            roots.Add(Path.Combine(local, "ModOrganizer"));

        return roots;
    }

    // Vortex lists every deployed file as a path relative to the game root, in vortex.deployment.json
    // or vortex.deployment.<modtype>.json. The file's shape has moved between versions, so any string
    // that reads as Modules\<folder>\... anywhere in it counts.
    public static HashSet<string> VortexOwnedFolders(string gameRoot)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(gameRoot))
                return folders;

            foreach (var file in Directory.EnumerateFiles(gameRoot, "vortex.deployment*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(
                        File.ReadAllText(file),
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    CollectModuleFolders(document.RootElement, folders, depth: 0);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return folders;
    }

    private static void CollectModuleFolders(JsonElement element, HashSet<string> folders, int depth)
    {
        if (depth > 8)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (FolderOf(element.GetString()) is { } folder)
                    folders.Add(folder);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectModuleFolders(item, folders, depth + 1);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // "source" names the Vortex mod and "instance" its profile; neither is a game path.
                    if (property.Name is "source" or "instance" or "deploymentMethod" or "gameId")
                        continue;

                    CollectModuleFolders(property.Value, folders, depth + 1);
                }
                break;
        }
    }

    private static string? FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3 && parts[0].Equals("Modules", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    // Every module folder found in an MO2 mod whose instance points at this game root.
    // Where the other managers keep the archives they downloaded for this game, so a Player B's archives
    // name their Nexus pages too: Vortex's downloads folder for Bannerlord, and the downloads folder of
    // every MO2 instance whose gamePath is this game. Only folders that exist are returned.
    public static IReadOnlyList<string> OtherManagerDownloadFolders(string gameRoot, IEnumerable<string>? mo2Roots = null, string? roamingAppData = null)
    {
        var folders = new List<string>();
        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrEmpty(roaming))
            folders.Add(Path.Combine(roaming, "Vortex", "downloads", "mountandblade2bannerlord"));

        var wanted = NormalizePath(gameRoot);

        foreach (var root in mo2Roots ?? DefaultMo2Roots())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                var instances = new List<string>();
                if (File.Exists(Path.Combine(root, "ModOrganizer.ini")))
                    instances.Add(root);
                instances.AddRange(Directory.EnumerateDirectories(root));

                foreach (var instance in instances)
                {
                    var ini = Path.Combine(instance, "ModOrganizer.ini");
                    if (!File.Exists(ini))
                        continue;

                    var lines = File.ReadAllLines(ini);
                    if (!IniSection(lines, "General").TryGetValue("gamePath", out var gamePath) || NormalizePath(UnwrapIniPath(gamePath)) != wanted)
                        continue;

                    folders.Add(IniSection(lines, "Settings").TryGetValue("download_directory", out var custom) && !string.IsNullOrWhiteSpace(custom)
                        ? UnwrapIniPath(custom).Replace("%BASE_DIR%", instance, StringComparison.OrdinalIgnoreCase)
                        : Path.Combine(instance, "downloads"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // One MO2 instance that manages this game: its mod folders mapped to the module ids each carries
    // (mods\<name>\Modules\<folder>, the id read from SubModule.xml and the folder name when that is
    // unreadable), and the modlist.txt of every profile, the selected one first.
    public sealed record Mo2Instance(
        string Path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> FolderModules,
        IReadOnlyList<string> ModLists);

    public static IReadOnlyList<Mo2Instance> Mo2Instances(string gameRoot, IEnumerable<string>? roots = null)
    {
        var result = new List<Mo2Instance>();
        var wanted = NormalizePath(gameRoot);

        foreach (var root in roots ?? DefaultMo2Roots())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                var instances = new List<string>();
                if (File.Exists(System.IO.Path.Combine(root, "ModOrganizer.ini")))
                    instances.Add(root);
                instances.AddRange(Directory.EnumerateDirectories(root));

                foreach (var instance in instances)
                    if (ReadMo2Instance(instance, wanted) is { } read)
                        result.Add(read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static Mo2Instance? ReadMo2Instance(string instance, string wantedGameRoot)
    {
        try
        {
            var ini = System.IO.Path.Combine(instance, "ModOrganizer.ini");
            if (!File.Exists(ini))
                return null;

            var lines = File.ReadAllLines(ini);
            var general = IniSection(lines, "General");
            var settings = IniSection(lines, "Settings");

            if (!general.TryGetValue("gamePath", out var gamePath) || NormalizePath(UnwrapIniPath(gamePath)) != wantedGameRoot)
                return null;

            var modsFolder = settings.TryGetValue("mod_directory", out var custom) && !string.IsNullOrWhiteSpace(custom)
                ? UnwrapIniPath(custom).Replace("%BASE_DIR%", instance, StringComparison.OrdinalIgnoreCase)
                : System.IO.Path.Combine(instance, "mods");

            var folders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(modsFolder))
            {
                foreach (var mod in Directory.EnumerateDirectories(modsFolder))
                {
                    var modules = System.IO.Path.Combine(mod, "Modules");
                    if (!Directory.Exists(modules))
                        continue;

                    var ids = Directory.EnumerateDirectories(modules)
                        .Select(folder => ReadSubModuleId(folder) ?? System.IO.Path.GetFileName(folder))
                        .ToList();

                    if (ids.Count > 0)
                        folders[System.IO.Path.GetFileName(mod)] = ids;
                }
            }

            var lists = new List<string>();
            var profiles = System.IO.Path.Combine(instance, "profiles");

            if (Directory.Exists(profiles))
            {
                general.TryGetValue("selected_profile", out var selected);
                selected = selected is null ? null : UnwrapIniPath(selected).Replace('\\', ' ').Trim();

                lists.AddRange(Directory.EnumerateDirectories(profiles)
                    .OrderByDescending(p => string.Equals(System.IO.Path.GetFileName(p), selected, StringComparison.OrdinalIgnoreCase))
                    .Select(p => System.IO.Path.Combine(p, "modlist.txt"))
                    .Where(File.Exists));
            }

            return new Mo2Instance(instance, folders, lists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadSubModuleId(string moduleFolder)
    {
        try
        {
            var file = System.IO.Path.Combine(moduleFolder, "SubModule.xml");
            if (!File.Exists(file))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(file), "<Id\\s+value\\s*=\\s*\"([^\"]+)\"");

            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Dictionary<string, ModuleManagement> Mo2Modules(string gameRoot, IEnumerable<string>? roots)
    {
        var found = new Dictionary<string, ModuleManagement>(StringComparer.OrdinalIgnoreCase);
        var wanted = NormalizePath(gameRoot);

        foreach (var root in roots ?? DefaultMo2Roots())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                var instances = new List<string>();
                if (File.Exists(Path.Combine(root, "ModOrganizer.ini")))
                    instances.Add(root);
                instances.AddRange(Directory.EnumerateDirectories(root));

                foreach (var instance in instances)
                    ReadInstance(instance, wanted, found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    private static void ReadInstance(string instance, string wantedGameRoot, Dictionary<string, ModuleManagement> found)
    {
        try
        {
            var ini = Path.Combine(instance, "ModOrganizer.ini");
            if (!File.Exists(ini))
                return;

            var lines = File.ReadAllLines(ini);
            var general = IniSection(lines, "General");
            var settings = IniSection(lines, "Settings");

            if (!general.TryGetValue("gamePath", out var gamePath) || NormalizePath(UnwrapIniPath(gamePath)) != wantedGameRoot)
                return;

            var modsFolder = settings.TryGetValue("mod_directory", out var custom) && !string.IsNullOrWhiteSpace(custom)
                ? UnwrapIniPath(custom).Replace("%BASE_DIR%", instance, StringComparison.OrdinalIgnoreCase)
                : Path.Combine(instance, "mods");

            if (!Directory.Exists(modsFolder))
                return;

            var enabled = ReadModList(instance);

            foreach (var mod in Directory.EnumerateDirectories(modsFolder))
            {
                var modules = Path.Combine(mod, "Modules");
                if (!Directory.Exists(modules))
                    continue;

                var name = Path.GetFileName(mod);
                var id = ReadNexusModId(Path.Combine(mod, "meta.ini"));
                var detail = enabled is not null && enabled.TryGetValue(name, out var on) && !on
                    ? name + " (disabled)"
                    : name;

                foreach (var folder in Directory.EnumerateDirectories(modules))
                    found[Path.GetFileName(folder)] = new ModuleManagement(ModManager.Mo2, id, detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // modlist.txt: "+Name" is enabled, "-Name" disabled. Missing is fine. Profiles are merged, and a
    // mod enabled in any of them reads as enabled.
    private static Dictionary<string, bool>? ReadModList(string instance)
    {
        try
        {
            var profiles = Path.Combine(instance, "profiles");
            if (!Directory.Exists(profiles))
                return null;

            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var list in Directory.EnumerateFiles(profiles, "modlist.txt", SearchOption.AllDirectories))
                foreach (var line in File.ReadLines(list))
                {
                    if (line.Length < 2 || (line[0] != '+' && line[0] != '-'))
                        continue;

                    var name = line[1..].Trim();
                    result[name] = result.GetValueOrDefault(name) || line[0] == '+';
                }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static int? ReadNexusModId(string metaIni)
    {
        try
        {
            if (!File.Exists(metaIni))
                return null;

            return IniSection(File.ReadAllLines(metaIni), "General").TryGetValue("modid", out var raw)
                && int.TryParse(raw.Trim(), out var id) && id > 0
                    ? id
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> IniSection(IEnumerable<string> lines, string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inside = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (inside && equals > 0 && !line.StartsWith(';'))
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return values;
    }

    // Qt writes the path wrapped in @ByteArray(...) with doubled backslashes, or with forward slashes.
    private static string UnwrapIniPath(string value)
    {
        value = value.Trim().Trim('"');
        if (value.StartsWith("@ByteArray(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
            value = value["@ByteArray(".Length..^1];

        return value.Replace("\\\\", "\\").Replace('/', '\\');
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\', '/').ToLowerInvariant();
        }
    }
}
