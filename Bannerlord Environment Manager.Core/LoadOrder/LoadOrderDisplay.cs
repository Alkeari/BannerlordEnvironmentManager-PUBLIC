using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public abstract record LoadOrderDisplayRow
{
    private LoadOrderDisplayRow() { }

    public sealed record Module(ModuleEntry Entry) : LoadOrderDisplayRow;

    // HiddenCount is only meaningful when LoadOrderDivider.Collapsed is true; it is always 0
    // otherwise, since nothing is hidden for an expanded divider to report.
    // The parameter is named LoadOrderDivider, not Divider, because CS0542 forbids a member
    // sharing its enclosing type's own name (this record is itself named Divider).
    public sealed record Divider(LoadOrderDivider LoadOrderDivider, int HiddenCount) : LoadOrderDisplayRow;
}

// The one place that combines the real module order with the divider overlay into what the Play
// tab actually shows. Modules and Auto-Sort never see this output - it exists purely for display,
// rebuilt on every change to either input.
public static class LoadOrderDisplay
{
    // modules is what is on screen, which a search filter can narrow; installedIds is every module
    // the list holds, filtered or not. The two are separate because they answer different questions
    // about an anchor that is not in modules: still installed but filtered out of view (the divider
    // keeps its anchor and simply is not drawn this pass, so it comes back in place when the filter
    // clears), or gone from the install entirely (the divider is appended at the end, the same
    // fallback LoadOrderDividerInterleave already uses, so an uninstall can never make a section
    // disappear). Passing null means modules is the whole list, so the two cases coincide.
    public static IReadOnlyList<LoadOrderDisplayRow> Merge(
        IReadOnlyList<ModuleEntry> modules,
        IReadOnlyList<LoadOrderDivider> dividers,
        IReadOnlySet<ModuleId>? installedIds = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(dividers);

        var known = installedIds ?? modules.Select(m => m.Id).ToHashSet();

        var byAnchor = new Dictionary<ModuleId, List<LoadOrderDivider>>();
        var atEnd = new List<LoadOrderDivider>();

        foreach (var divider in dividers)
        {
            if (divider.AnchorId is not { } anchor || !known.Contains(anchor))
            {
                atEnd.Add(divider);
                continue;
            }

            if (byAnchor.TryGetValue(anchor, out var list))
                list.Add(divider);
            else
                byAnchor[anchor] = [divider];
        }

        var flat = new List<LoadOrderDisplayRow>(modules.Count + dividers.Count);

        foreach (var module in modules)
        {
            if (byAnchor.TryGetValue(module.Id, out var here))
            {
                foreach (var divider in here)
                    flat.Add(new LoadOrderDisplayRow.Divider(divider, HiddenCount: 0));
            }

            flat.Add(new LoadOrderDisplayRow.Module(module));
        }

        foreach (var divider in atEnd)
            flat.Add(new LoadOrderDisplayRow.Divider(divider, HiddenCount: 0));

        var result = new List<LoadOrderDisplayRow>(flat.Count);
        var i = 0;

        while (i < flat.Count)
        {
            if (flat[i] is not LoadOrderDisplayRow.Divider { LoadOrderDivider.Collapsed: true } collapsed)
            {
                result.Add(flat[i]);
                i++;
                continue;
            }

            var hidden = 0;
            var j = i + 1;

            while (j < flat.Count && flat[j] is LoadOrderDisplayRow.Module)
            {
                hidden++;
                j++;
            }

            result.Add(collapsed with { HiddenCount = hidden });
            i = j;
        }

        return result;
    }
}
