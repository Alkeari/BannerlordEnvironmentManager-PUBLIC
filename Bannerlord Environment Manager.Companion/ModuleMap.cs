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
            // Read through reflection, since a compiled call to a member a version lacks is a
            // MissingMethodException thrown when this method is compiled and so before any catch in it runs.
            // v1.3 and later list modules with GetAllModules(); v1.0.0 to v1.2.12 have no such method and list
            // them with the parameterless GetModules(), which was replaced by GetModules(Func) from v1.3.
            // Each member below is likewise read under both of its names, newest first, because the folder is
            // GetFolderPath() before v1.0.2 and the class name is SubModuleClassType before v1.3.
            var modules = Invoke(typeof(ModuleHelper), "GetAllModules") ?? Invoke(typeof(ModuleHelper), "GetModules");

            if (modules is not System.Collections.IEnumerable list)
                return map;

            foreach (var module in list)
            {
                try
                {
                    map.Add(module);
                }
                catch
                {
                    // One module that cannot be read costs only its own name.
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

    private void Add(object? module)
    {
        var id = Read(module, "Id") as string;

        if (string.IsNullOrEmpty(id))
            return;

        var folder = (Read(module, "FolderPath") ?? Read(module, "GetFolderPath")) as string;

        if (!string.IsNullOrEmpty(folder))
            _byFolder.Add(new KeyValuePair<string, string>(Normalize(folder!), id!));

        _byNormalizedId.Add(new KeyValuePair<string, string>(NormalizeId(id!), id!));

        if (Read(module, "SubModules") is not System.Collections.IEnumerable subModules)
            return;

        foreach (var subModule in subModules)
        {
            var typeName = (Read(subModule, "SubModuleClassTypeName") ?? Read(subModule, "SubModuleClassType")) as string;

            if (string.IsNullOrEmpty(typeName) || _byTypeName.ContainsKey(typeName!))
                continue;

            _byTypeName[typeName!] = id!;
        }
    }

    // A public static member with no parameters, or null where this version has none.
    private static object? Invoke(Type type, string name)
    {
        var method = type.GetMethod(name, Type.EmptyTypes);

        return method is { IsStatic: true } ? method.Invoke(null, null) : null;
    }

    // A public property, field or parameterless method of that name, or null where there is none.
    private static object? Read(object? target, string name)
    {
        if (target is null)
            return null;

        var type = target.GetType();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        return type.GetProperty(name, flags)?.GetValue(target, null)
            ?? type.GetField(name, flags)?.GetValue(target)
            ?? type.GetMethod(name, flags, null, Type.EmptyTypes, null)?.Invoke(target, null);
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
