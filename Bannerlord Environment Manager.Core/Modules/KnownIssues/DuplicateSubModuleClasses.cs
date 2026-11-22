using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// One SubModuleClassType named by more than one enabled module.
//
// This is not the same problem as a shadowed assembly and it is not fixed by the same thing. The
// runtime binds a simple assembly name once, so two copies of a DLL settle into one type; but the
// game walks the enabled modules and constructs every SubModuleClassType each of them names, so that
// one type is constructed and initialized once per module naming it. A loader that keeps static state
// is then set up and torn through twice, and the second pass does not find what the first built.
//
// What it cost: an overhaul bundling the whole BUTR stack alongside the stack's own modules had six
// classes declared twice, and the game died at Bannerlord.ModuleLoader.Bannerlord_MBOptionScreen with
// "Value cannot be null. Parameter name: provider" six modules into a list of thirty-two, before the
// initial screen and before the first tick.
public sealed record DuplicateSubModuleClass(string ClassName, IReadOnlyList<ModuleId> Modules)
{
    // The one the game reaches first, which is the copy whose initialization is the one that stands a
    // chance of completing. Everything after it is a second construction of the same class.
    public ModuleId First => Modules[0];

    public IReadOnlyList<ModuleId> Later => [.. Modules.Skip(1)];

    public string Summarize() => Strings.Current.Format(
        "Core.Modules.DuplicateSubModuleClass.Summarize",
        ClassName,
        string.Join(", ", Modules.Select(m => m.Value)));

    public string Describe() => Strings.Current.Format(
        "Core.Modules.DuplicateSubModuleClass.Describe", First.Value, ClassName);
}

public static class DuplicateSubModuleClasses
{
    public static IReadOnlyList<DuplicateSubModuleClass> Find(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var order = new List<(string Class, ModuleId Module)>();

        foreach (var entry in loadOrder)
        {
            // Null is a manifest nobody read the SubModules block of, never a module that declares
            // nothing, so it contributes no evidence either way.
            if (!entry.IsEnabled || entry.Manifest?.DeclaredSubModuleClasses is not { } classes)
                continue;

            foreach (var className in classes.Distinct(StringComparer.Ordinal))
                order.Add((className, entry.Id));
        }

        return
        [
            .. order
                .GroupBy(item => item.Class, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.Module).Distinct().Count() > 1)
                .Select(group => new DuplicateSubModuleClass(
                    group.Key,
                    [.. group.Select(item => item.Module).Distinct()]))
        ];
    }

    // The keep-on answer to a duplicated class: the later module stops declaring what an earlier enabled
    // module already declares, so the game builds each class once and the mod stays on. Null when the
    // module is official, has no known folder, or has no class an earlier module also declares.
    public static BundledStackCopy? DeclareOncePlan(IReadOnlyList<ModuleEntry> loadOrder, ModuleId later)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var entry = loadOrder.FirstOrDefault(e => e.Id == later);

        if (entry is not { IsOfficial: false, IsEnabled: true } || entry.Manifest is not { FolderPath.Length: > 0 } manifest)
            return null;

        var mine = Find(loadOrder)
            .Where(clash => clash.Later.Contains(later))
            .ToList();

        return mine.Count == 0
            ? null
            : new BundledStackCopy(later, manifest.FolderPath, [.. mine.Select(clash => clash.First).Distinct()], [.. mine.Select(clash => clash.ClassName)], []);
    }
}
