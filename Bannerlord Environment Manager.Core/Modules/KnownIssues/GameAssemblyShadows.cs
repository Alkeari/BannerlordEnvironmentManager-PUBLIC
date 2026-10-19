using System.Security.Cryptography;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record ShadowedGameAssembly(
    ModuleId ModuleId,
    string DisplayName,
    string Name,
    string ModulePath,
    string GamePath,
    long SizeBytes,
    bool Identical);

public sealed record GameAssemblyShadowReport(IReadOnlyList<ShadowedGameAssembly> Shadows)
{
    public IReadOnlyList<ShadowedGameAssembly> Differing =>
        [.. Shadows.Where(shadow => !shadow.Identical)];

    public IEnumerable<IGrouping<ModuleId, ShadowedGameAssembly>> ByModule =>
        Shadows.GroupBy(shadow => shadow.ModuleId);

    // One line per module rather than per file. Slavery ships fifty-three of these on the reference
    // install, and fifty-three rows saying the same thing would bury the one that matters.
    public string Describe(IGrouping<ModuleId, ShadowedGameAssembly> module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var name = module.First().DisplayName;
        var differing = module.Where(shadow => !shadow.Identical).ToList();
        var megabytes = module.Sum(shadow => shadow.SizeBytes) / (double)(1024 * 1024);

        if (differing.Count == 0)
        {
            return Strings.Current.Plural(
                "Core.Modules.GameShadow.Identical", module.Count(), name, megabytes);
        }

        return Strings.Current.Plural(
            "Core.Modules.GameShadow.Differ",
            module.Count(),
            name,
            differing.Count,
            string.Join(", ", differing.Take(5).Select(shadow => shadow.Name)));
    }
}

// A module shipping the game's own assemblies. DuplicateAssemblies compares modules against each other
// and never against the game's bin, so a mod carrying its whole build output, references and all, was
// invisible to every check BEM had.
public static class GameAssemblyShadows
{
    public static GameAssemblyShadowReport Inspect(AssemblyIndex index, IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
            return new GameAssemblyShadowReport([]);

        var enabled = entries.Where(entry => entry.IsEnabled).Select(entry => entry.Id).ToHashSet();

        var names = entries
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);

        var game = index.Assemblies
            .Where(assembly => assembly.Origin == AssemblyOrigin.Game)
            .GroupBy(assembly => Path.GetFileName(assembly.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);

        var shadows = new List<ShadowedGameAssembly>();

        foreach (var assembly in index.Assemblies)
        {
            if (assembly.Origin != AssemblyOrigin.Module || assembly.ModuleId is not { } moduleId)
                continue;

            if (!enabled.Contains(moduleId))
                continue;

            var fileName = Path.GetFileName(assembly.Path);

            if (!game.TryGetValue(fileName, out var gamePath))
                continue;

            var size = Length(assembly.Path);

            shadows.Add(new ShadowedGameAssembly(
                moduleId,
                names.GetValueOrDefault(moduleId, moduleId.Value),
                fileName,
                assembly.Path,
                gamePath,
                size,
                Identical(assembly.Path, gamePath, size)));
        }

        return new GameAssemblyShadowReport(
            [.. shadows
                .OrderByDescending(shadow => !shadow.Identical)
                .ThenBy(shadow => shadow.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(shadow => shadow.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Length first, because two builds of the same assembly almost never match on it and hashing every
    // copy of a five-megabyte game assembly to learn that would be the slowest part of the scan.
    private static bool Identical(string left, string right, long size)
    {
        try
        {
            if (size == 0 || size != new FileInfo(right).Length)
                return false;

            return Hash(left).SequenceEqual(Hash(right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);

        return SHA256.HashData(stream);
    }
}
