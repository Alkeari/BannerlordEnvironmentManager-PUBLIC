using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// A mod that carries its own copy of a BUTR stack member and declares that member's SubModule classes
// itself, beside the member's own enabled module. The game then constructs those classes twice, and
// on Tales from the Age of Men that crashed the boot (DuplicateSubModuleClasses). Disabling the
// standalone makes the bundled copy the only one; this is the other way out, where the bundler stops
// declaring and carrying what the installed stack already provides.
//
// Files are matched by name against the stack module's own bin folder for the same platform, because
// that is how the runtime binds them: one simple name, one assembly. A bundler's own code never shares
// a name with a stack file, so whatever is left behind is the mod itself.
public sealed record BundledStackCopy(
    ModuleId Bundler,
    string BundlerFolder,
    IReadOnlyList<ModuleId> StackModules,
    IReadOnlyList<string> SubModuleClasses,
    IReadOnlyList<string> Files);

public static class BundledStackCopies
{
    public static IReadOnlyList<BundledStackCopy> Find(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var stack = loadOrder
            .Where(entry => entry.IsEnabled
                && entry.Manifest is { FolderPath.Length: > 0 }
                && Prerequisites.ById(entry.Id.Value) is { Kind: PrerequisiteKind.Module })
            .ToList();

        if (stack.Count == 0)
            return [];

        var copies = new List<BundledStackCopy>();

        foreach (var entry in loadOrder)
        {
            if (!entry.IsEnabled
                || entry.IsOfficial
                || entry.Manifest is not { FolderPath.Length: > 0 } manifest
                || manifest.DeclaredSubModuleClasses is not { } declared
                || stack.Any(member => member.Id == entry.Id))
            {
                continue;
            }

            var owners = stack
                .Where(member => member.Manifest!.DeclaredSubModuleClasses is { } classes
                    && classes.Intersect(declared, StringComparer.Ordinal).Any())
                .ToList();

            if (owners.Count == 0)
                continue;

            var classes = declared
                .Where(name => owners.Any(member => member.Manifest!.DeclaredSubModuleClasses!.Contains(name, StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            copies.Add(new BundledStackCopy(
                entry.Id,
                manifest.FolderPath,
                [.. owners.Select(member => member.Id)],
                classes,
                SharedFiles(manifest.FolderPath, stack.Select(member => member.Manifest!.FolderPath))));
        }

        return copies;
    }

    // Relative to the bundler's folder, as bin\<platform>\<file>.
    private static IReadOnlyList<string> SharedFiles(string bundlerFolder, IEnumerable<string> stackFolders)
    {
        var bin = Path.Combine(bundlerFolder, "bin");

        if (!Directory.Exists(bin))
            return [];

        var stackRoots = stackFolders.ToList();
        var shared = new List<string>();

        foreach (var platform in Directory.EnumerateDirectories(bin))
        {
            var platformName = Path.GetFileName(platform);

            var stackNames = stackRoots
                .Select(root => Path.Combine(root, "bin", platformName))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateFiles)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            shared.AddRange(Directory.EnumerateFiles(platform)
                .Where(file => stackNames.Contains(Path.GetFileName(file)))
                .Select(file => Path.GetRelativePath(bundlerFolder, file))
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        return shared;
    }
}
