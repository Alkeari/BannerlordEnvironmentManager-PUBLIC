using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public static class ModuleTierMap
{
    // Fan-in is what separates an infrastructure library from ordinary content, and it is only
    // meaningful relative to the list being classified, so the whole list is counted at once.
    public static ModuleTier[] For(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var indexOf = new Dictionary<ModuleId, int>();

        for (var i = 0; i < entries.Count; i++)
            indexOf[entries[i].Id] = i;

        var fanIn = new int[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var counted = new HashSet<int>();

            foreach (var dependency in entries[i].Dependencies)
            {
                // LoadAfterThis says the target loads after this module, so the declarer is not one of
                // the target's dependents and counting it would promote the wrong side of the pair.
                if (dependency.IsIncompatible || dependency.Order == DependencyOrder.LoadAfterThis)
                    continue;

                if (!indexOf.TryGetValue(dependency.TargetId, out var target) || target == i)
                    continue;

                if (counted.Add(target))
                    fanIn[target]++;
            }
        }

        var tiers = new ModuleTier[entries.Count];

        for (var i = 0; i < entries.Count; i++)
            tiers[i] = ModuleTiers.Of(entries[i], fanIn[i]);

        return tiers;
    }

    // The tier leads the default sort, so it is the sort's rationale and belongs on the row. Every
    // member is named here: a badge on some tiers and not others would make its absence a statement.
    public static string Describe(ModuleTier tier) => tier switch
    {
        ModuleTier.CrashHandler => Strings.Current["Core.LoadOrder.Tier.CrashHandler"],
        ModuleTier.Infrastructure => Strings.Current["Core.LoadOrder.Tier.Infrastructure"],
        ModuleTier.Official => Strings.Current["Core.LoadOrder.Tier.Official"],
        ModuleTier.Library => Strings.Current["Core.LoadOrder.Tier.Library"],
        ModuleTier.TrailingPatch => Strings.Current["Core.LoadOrder.Tier.TrailingPatch"],
        _ => Strings.Current["Core.LoadOrder.Tier.Content"]
    };

    // The badge form. Describe stays lower case because it is read mid-sentence in the detail pane,
    // and a badge that says "crash handler" beside a Title Case name reads like a typo.
    public static string Label(ModuleTier tier) => tier switch
    {
        ModuleTier.CrashHandler => Strings.Current["Core.LoadOrder.TierLabel.CrashHandler"],
        ModuleTier.Infrastructure => Strings.Current["Core.LoadOrder.TierLabel.Infrastructure"],
        ModuleTier.Official => Strings.Current["Core.LoadOrder.TierLabel.Official"],
        ModuleTier.Library => Strings.Current["Core.LoadOrder.TierLabel.Library"],
        ModuleTier.TrailingPatch => Strings.Current["Core.LoadOrder.TierLabel.TrailingPatch"],
        _ => Strings.Current["Core.LoadOrder.TierLabel.Content"]
    };

    public static string Explain(ModuleTier tier) => tier switch
    {
        ModuleTier.CrashHandler => Strings.Current["Core.LoadOrder.TierExplain.CrashHandler"],
        ModuleTier.Infrastructure => Strings.Current["Core.LoadOrder.TierExplain.Infrastructure"],
        ModuleTier.Official => Strings.Current["Core.LoadOrder.TierExplain.Official"],
        ModuleTier.Library => Strings.Current["Core.LoadOrder.TierExplain.Library"],
        ModuleTier.TrailingPatch => Strings.Current["Core.LoadOrder.TierExplain.TrailingPatch"],
        _ => Strings.Current["Core.LoadOrder.TierExplain.Content"]
    };
}
