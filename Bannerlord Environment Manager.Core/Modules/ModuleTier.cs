using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules;

// The order of the members is the order the default sort prefers, so a member's position is a claim
// about where that kind of module belongs. Only the named stack loads ahead of the game's own modules:
// a library recognized by fan-in alone sits below Official, because hoisting an unnamed module above
// Native and the DLCs moves the game's own content behind a community one that never asked for it.
public enum ModuleTier
{
    CrashHandler = 0,
    Infrastructure = 1,
    Official = 2,
    Library = 3,
    Content = 4,
    TrailingPatch = 5
}

public static class ModuleTiers
{
    public const int InfrastructureFanIn = 3;

    private const int MaxTrailingPrefix = 3;

    private static readonly HashSet<ModuleId> CrashHandlerIds =
    [
        new("Bannerlord.BetterExceptionWindow"),
        new("BetterExceptionWindow"),
        new("Bannerlord.Harmony"),
        new("Harmony")
    ];

    private static readonly HashSet<ModuleId> InfrastructureIds =
    [
        new("Bannerlord.ButterLib"),
        new("ButterLib"),
        new("Bannerlord.UIExtenderEx"),
        new("UIExtenderEx"),
        new("Bannerlord.MBOptionScreen"),
        new("MBOptionScreen")
    ];

    // The modules everything else is built on top of, read off the same two sets the tier map is built
    // from rather than from a second list that could drift out of step with it.
    //
    // These are never a module a remedy may propose relocating. Harmony and the crash handler have to
    // be loaded before anything they rewrite, and ButterLib, UIExtenderEx and MBOptionScreen have to be
    // loaded before Native so the content modules that bind them find them. A button offering to move
    // one below a content mod is offering an install that does not start, and BEM shipped exactly that:
    // "Move ButterLib below Fire Archers" for a shadowed System.Numerics.Vectors.
    public static bool LoadsBeforeContent(ModuleId id) =>
        CrashHandlerIds.Contains(id) || InfrastructureIds.Contains(id);

    public static ModuleTier Of(ModuleEntry entry, int fanIn)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (CrashHandlerIds.Contains(entry.Id))
            return ModuleTier.CrashHandler;

        if (InfrastructureIds.Contains(entry.Id))
            return ModuleTier.Infrastructure;

        // Official modules must be tested before the fan-in fallback: nearly every installed module
        // declares Native, so on a real install their fan-in runs to the hundreds and the fallback
        // would classify all of them as libraries, collapsing the boundary this tier exists for.
        if (entry.IsOfficial)
            return ModuleTier.Official;

        // A library found by fan-in alone, never one of the named stack, so it ranks below the game's
        // own modules. Realistic Battle Mod carries eleven dependents on a real install and was being
        // hoisted above Native and the War Sails DLC by this, which is the overhaul it then broke.
        if (fanIn >= InfrastructureFanIn)
            return ModuleTier.Library;

        return IsTrailingPatch(entry.Id) ? ModuleTier.TrailingPatch : ModuleTier.Content;
    }

    // Authors prefix an id with z, zz or zzz to sort last under a naive alphabetical sort. The run has
    // to end at a word boundary or an ordinary name such as Zephyr would be swept in with them: either
    // a separator (z_Patch, zzz-Final, zzz2Patch) or the start of a camel-case word (zzzUniversalPatch).
    // An uppercase run is an acronym, not a prefix, which is what keeps ZEN_Overhaul out.
    private static bool IsTrailingPatch(ModuleId id)
    {
        var value = id.Value;

        if (string.IsNullOrEmpty(value))
            return false;

        var prefix = 0;

        while (prefix < value.Length && (value[prefix] == 'z' || value[prefix] == 'Z'))
            prefix++;

        if (prefix is 0 or > MaxTrailingPrefix || prefix >= value.Length)
            return false;

        var next = value[prefix];

        if (!char.IsLetter(next))
            return true;

        return char.IsUpper(next) && prefix + 1 < value.Length && char.IsLower(value[prefix + 1]);
    }
}
