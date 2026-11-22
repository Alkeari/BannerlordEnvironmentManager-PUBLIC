using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// What a row's category badge says. It is a label and nothing else: the tier underneath keeps deciding
// where a module sorts and which load-order warnings it raises, whatever the badge reads.
//
// A tier BEM worked out from what a module is (a crash handler, infrastructure the list leans on, an
// official module, a library, a patch that must load last) stays as BEM's own word for it. Only the
// catch-all Content tier, which says nothing beyond "none of those", gives way to the category Nexus
// Mods files the page under, and falls back to Content where BEM knows no Nexus category.
public static class ModuleCategoryLabel
{
    public static string For(ModuleTier tier, string? nexusCategory) =>
        tier == ModuleTier.Content && nexusCategory is { Length: > 0 } category
            ? category.Trim()
            : ModuleTierMap.Label(tier);
}
