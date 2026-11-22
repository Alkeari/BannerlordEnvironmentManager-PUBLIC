using System.Text.Json;
using System.Text.Json.Nodes;

namespace BannerlordEnvironmentManager.Core.Nexus;

// A BEM collection in the shape Nexus collections use (the collection.json Vortex writes and reads):
// mods listed with a "nexus" source, and the curator's settings carried as one bundled mod
// (source.type "bundle") whose files sit under bundled\BEM Settings\<Root>\<path> beside the json.
// The load order is the order of the mods array, and each entry keeps its module id and switch in
// details so reading it back loses nothing.
public static class VortexCollectionJson
{
    public const string FileName = "collection.json";

    public const string DomainName = "mountandblade2bannerlord";

    public const string SettingsModName = "BEM Settings";

    public static string BundleFolder => "bundled/" + SettingsModName + "/";

    // The json and the bundled files (archive path, content) a collection needs.
    public sealed record Package(string Json, IReadOnlyList<KeyValuePair<string, byte[]>> BundledFiles);

    public static Package Export(BemCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var mods = new JsonArray();

        foreach (var mod in collection.Mods)
        {
            JsonObject source = mod.NexusModId is { } modId && mod.NexusFileId is { } fileId
                ? new JsonObject
                {
                    ["type"] = "nexus",
                    ["modId"] = modId,
                    ["fileId"] = fileId,
                    ["fileExpression"] = mod.NexusFileName ?? mod.Name,
                    ["logicalFilename"] = mod.NexusFileName ?? mod.Name,
                    ["updatePolicy"] = "exact"
                }
                : new JsonObject { ["type"] = "browse", ["fileExpression"] = mod.Name, ["url"] = "", ["updatePolicy"] = "exact" };

            mods.Add(new JsonObject
            {
                ["name"] = mod.Name,
                ["version"] = mod.Version ?? "",
                ["optional"] = false,
                ["domainName"] = DomainName,
                ["source"] = source,
                ["details"] = new JsonObject { ["bemModuleId"] = mod.ModuleId, ["bemEnabled"] = mod.Enabled },
                ["phase"] = 0
            });
        }

        var bundled = new List<KeyValuePair<string, byte[]>>();

        foreach (var setting in collection.Settings)
            bundled.Add(new(BundleFolder + setting.Root + "/" + setting.Path, Convert.FromBase64String(setting.ContentBase64)));

        if (bundled.Count > 0)
            mods.Add(new JsonObject
            {
                ["name"] = SettingsModName,
                ["version"] = "",
                ["optional"] = false,
                ["domainName"] = DomainName,
                ["source"] = new JsonObject
                {
                    ["type"] = "bundle",
                    ["fileSize"] = 0,
                    ["updatePolicy"] = "exact",
                    ["fileExpression"] = SettingsModName
                },
                ["details"] = new JsonObject(),
                ["phase"] = 0
            });

        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["author"] = "BEM",
                ["authorUrl"] = "",
                ["name"] = collection.Name,
                ["description"] = "",
                ["installInstructions"] = "",
                ["domainName"] = DomainName,
                ["gameVersions"] = new JsonArray()
            },
            ["mods"] = mods,
            ["modRules"] = new JsonArray()
        };

        return new Package(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), bundled);
    }

    // Writes collection.json and the bundled folder into a directory.
    public static string Write(string directory, BemCollection collection)
    {
        var package = Export(collection);

        Directory.CreateDirectory(directory);

        foreach (var (path, content) in package.BundledFiles)
        {
            var target = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        var json = Path.Combine(directory, FileName);
        File.WriteAllText(json, package.Json);
        return json;
    }

    // Null for text that is not a collection.json. A bundled mod named BEM Settings turns back into
    // settings through readBundled (archive path to content), which returns null for a missing file.
    public static BemCollection? Read(string json, Func<string, IReadOnlyList<string>> listBundled, Func<string, byte[]?> readBundled)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["mods"] is not JsonArray modsNode)
                return null;

            var mods = new List<BemCollectionMod>();
            var settings = new List<BemCollectionSetting>();

            foreach (var node in modsNode.OfType<JsonObject>())
            {
                var name = node["name"]?.GetValue<string>() ?? string.Empty;
                var source = node["source"] as JsonObject;
                var type = source?["type"]?.GetValue<string>();

                if (type == "bundle")
                {
                    if (!name.Equals(SettingsModName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var path in listBundled(BundleFolder))
                    {
                        var relative = path[BundleFolder.Length..];
                        var slash = relative.IndexOf('/');

                        if (slash <= 0 || readBundled(path) is not { } content)
                            continue;

                        settings.Add(new BemCollectionSetting(relative[..slash], relative[(slash + 1)..], Convert.ToBase64String(content)));
                    }

                    continue;
                }

                var details = node["details"] as JsonObject;
                var moduleId = details?["bemModuleId"]?.GetValue<string>();

                if (string.IsNullOrEmpty(moduleId))
                    continue;

                var nexus = type == "nexus";

                mods.Add(new BemCollectionMod(
                    moduleId,
                    name,
                    node["version"]?.GetValue<string>() is { Length: > 0 } version ? version : null,
                    nexus ? source?["modId"]?.GetValue<int>() : null,
                    nexus ? source?["fileId"]?.GetValue<int>() : null,
                    nexus ? source?["logicalFilename"]?.GetValue<string>() : null,
                    details?["bemEnabled"]?.GetValue<bool>() ?? true));
            }

            return new BemCollection(
                BemCollection.CurrentFormat,
                root["info"]?["name"]?.GetValue<string>() ?? "Collection",
                DateTimeOffset.UtcNow,
                mods,
                settings,
                []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // Reads collection.json from disk with its bundled folder beside it.
    public static BemCollection? ReadFile(string jsonPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;

            IReadOnlyList<string> List(string prefix)
            {
                var folder = Path.Combine(directory, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));

                return Directory.Exists(folder)
                    ? [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Select(file => "bundled/" + Path.GetRelativePath(Path.Combine(directory, "bundled"), file).Replace('\\', '/'))]
                    : [];
            }

            byte[]? Content(string path)
            {
                var file = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }

            return Read(File.ReadAllText(jsonPath), List, Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
