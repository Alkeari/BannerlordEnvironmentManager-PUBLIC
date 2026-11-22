using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// A mod that changes a list while it is looping over it throws InvalidOperationException: "Collection was
// modified". The message names no mod, so what ties it to one is the stack under it: a frame in a type that
// only one enabled module's assemblies define. RecentRunErrors keeps the message and the frames after it.
public sealed record CollectionModifiedHit(ModuleId ModuleId, string Frame);

public static class CollectionModifiedErrors
{
    private static readonly Regex Frame = new(
        @"^\s*at (?<type>[^\s(]+)\.[^\s.(]+(?:\[[^\]]*\])?\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsMarker(string line) =>
        line.Contains("Collection was modified", StringComparison.Ordinal);

    // One hit per module, the first of its frames the text shows. A frame owned by two modules (a bundled
    // copy of the same library) is no evidence against either, so it is skipped.
    public static IReadOnlyList<CollectionModifiedHit> Find(string logText, IEnumerable<IndexedAssembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(logText);
        ArgumentNullException.ThrowIfNull(assemblies);

        if (logText.Length == 0)
            return [];

        var owners = new Dictionary<string, HashSet<ModuleId>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies.Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is not null))
        {
            foreach (var type in assembly.DefinedTypeFullNames)
            {
                var key = Dotted(type);

                if (!owners.TryGetValue(key, out var set))
                    owners[key] = set = [];

                set.Add(assembly.ModuleId!.Value);
            }
        }

        var hits = new Dictionary<ModuleId, CollectionModifiedHit>();
        var inBlock = false;

        foreach (var raw in logText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (IsMarker(line))
            {
                inBlock = true;
                continue;
            }

            if (!inBlock)
                continue;

            var match = Frame.Match(line);

            if (!match.Success)
            {
                inBlock = false;
                continue;
            }

            var type = Dotted(match.Groups["type"].Value);

            // A frame names a nested or compiler-generated type (Type+<>c__DisplayClass), so walk up to
            // the nearest type the module defines.
            while (type.Length > 0)
            {
                if (owners.TryGetValue(type, out var set))
                {
                    if (set.Count == 1)
                    {
                        var id = set.First();
                        hits.TryAdd(id, new CollectionModifiedHit(id, line.Trim()));
                    }

                    break;
                }

                var cut = type.LastIndexOf('.');
                type = cut < 0 ? string.Empty : type[..cut];
            }
        }

        return [.. hits.Values];
    }

    private static string Dotted(string name)
    {
        var tick = name.IndexOf('`');

        return (tick < 0 ? name : name[..tick]).Replace('+', '.').Replace('/', '.');
    }
}
