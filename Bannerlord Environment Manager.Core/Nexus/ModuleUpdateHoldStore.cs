using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Nexus;

// The mods he chose to keep on their installed version, per game version, since a mod's last release for
// v1.4.8 is exactly the one a v1.5 install should not be held to. A hold ends only when he undoes it.
public sealed class ModuleUpdateHoldStore
{
    public const string FileName = "update-holds.json";

    public ModuleUpdateHoldStore(string? filePath) => FilePath = filePath;

    public string? FilePath { get; }

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public IReadOnlySet<string> Load()
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath))
            return held;

        try
        {
            if (File.Exists(FilePath))
                held.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return held;
        }

        return held;
    }

    public bool Set(string moduleId, bool held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        var ids = new HashSet<string>(Load(), StringComparer.OrdinalIgnoreCase);

        if (held)
            ids.Add(moduleId);
        else
            ids.Remove(moduleId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(ids.Order(StringComparer.OrdinalIgnoreCase).ToList()));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
