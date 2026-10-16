using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.ModuleManager;

namespace BannerlordEnvironmentManager.Companion;

// A submodule base carries no reference back to the module that supplied it, so the mapping is
// rebuilt from ModuleHelper: every ModuleInfo lists its SubModuleInfo entries and each of those
// names the class the game will construct. Where a concrete type is not in that table, because a
// mod's SubModule class derives from another mod's, the assembly path is mapped back to
// Modules\<id>\ instead, which is the same rule the design uses to attribute a Harmony patch.
internal sealed class ModuleMap
{
    private readonly Dictionary<string, string> _byTypeName =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly List<KeyValuePair<string, string>> _byFolder =
        new List<KeyValuePair<string, string>>();

    private readonly List<KeyValuePair<string, string>> _byNormalizedId =
        new List<KeyValuePair<string, string>>();

    private ModuleMap()
    {
    }

    public static ModuleMap Empty { get; } = new ModuleMap();

    public static ModuleMap Build()
    {
        var map = new ModuleMap();

        try
        {
            foreach (var module in ModuleHelper.GetAllModules())
            {
                if (module is null || string.IsNullOrEmpty(module.Id))
                    continue;

                if (!string.IsNullOrEmpty(module.FolderPath))
                    map._byFolder.Add(new KeyValuePair<string, string>(Normalize(module.FolderPath), module.Id));

                map._byNormalizedId.Add(new KeyValuePair<string, string>(NormalizeId(module.Id), module.Id));

                if (module.SubModules is null)
                    continue;

                foreach (var subModule in module.SubModules)
                {
                    var typeName = subModule?.SubModuleClassTypeName;

                    if (string.IsNullOrEmpty(typeName) || map._byTypeName.ContainsKey(typeName!))
                        continue;

                    map._byTypeName[typeName!] = module.Id;
                }
            }
        }
        catch
        {
            // A partial map still attributes most modules, and an empty one still produces
            // breadcrumbs keyed by type name.
        }

        return map;
    }

    public string Resolve(Type type)
    {
        if (type is null)
            return string.Empty;

        if (type.FullName != null && _byTypeName.TryGetValue(type.FullName, out var byName))
            return byName;

        string location;

        try
        {
            location = type.Assembly.Location ?? string.Empty;
        }
        catch
        {
            location = string.Empty;
        }

        return ResolveLocation(location);
    }

    // The path of an assembly on disk mapped back to Modules\<id>\. This is the rule the design uses
    // to attribute a Harmony patch and the one the first-chance handler uses to attribute a frame.
    public string ResolveLocation(string location)
    {
        if (string.IsNullOrEmpty(location))
            return string.Empty;

        var normalized = Normalize(location);

        foreach (var pair in _byFolder)
        {
            if (pair.Key.Length > 0 && normalized.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return string.Empty;
    }

    // Only ever reached for a Harmony patch emitted into a dynamic assembly, which has no location
    // to map. The owner is author-chosen, so this claims a module only when the owner actually
    // begins with that module's id once both are stripped to letters and digits: uiextender's
    // "bannerlord.uiextender.ex.viewmodels.Diplomacy" resolves, "com.rbmcombat" does not resolve to
    // anything and comes back empty rather than guessing.
    public string ResolveOwner(string owner)
    {
        if (string.IsNullOrEmpty(owner))
            return string.Empty;

        var normalized = NormalizeId(owner);

        if (normalized.Length == 0)
            return string.Empty;

        var best = string.Empty;
        var bestLength = 0;

        foreach (var pair in _byNormalizedId)
        {
            if (pair.Key.Length <= bestLength || pair.Key.Length == 0)
                continue;

            if (normalized.StartsWith(pair.Key, StringComparison.Ordinal))
            {
                best = pair.Value;
                bestLength = pair.Key.Length;
            }
        }

        return best;
    }

    private static string NormalizeId(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var buffer = new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return length == 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? full
                : full + Path.DirectorySeparatorChar;
        }
        catch
        {
            return path;
        }
    }
}
