using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The reverse of LoadOrderDividerExtraction: given the entries an export is already going to write
// (only the enabled modules - see LoadOrderExporter) and the full order (enabled and disabled, so a
// divider anchored to a disabled module can still find what comes after it), produces one synthesized
// LoadOrderFileEntry per divider at the right position. The empty version string is deliberate:
// BmListFile.Write already substitutes its own "no version" placeholder for an empty version, and
// NovusPresetFile.Write already writes an empty RequiredVersion attribute for one - both are the
// format-idiomatic answer that already exists for an ordinary module BEM could not read a version
// for, so a divider needs nothing new invented for it.
public static class LoadOrderDividerInterleave
{
    public static IReadOnlyList<LoadOrderFileEntry> Interleave(
        IReadOnlyList<LoadOrderFileEntry> exportedEntries,
        IReadOnlyList<ModuleId> fullOrder,
        IReadOnlyList<LoadOrderDivider> dividers)
    {
        ArgumentNullException.ThrowIfNull(exportedEntries);
        ArgumentNullException.ThrowIfNull(fullOrder);
        ArgumentNullException.ThrowIfNull(dividers);

        if (dividers.Count == 0)
            return exportedEntries;

        var positionInFullOrder = new Dictionary<ModuleId, int>();

        for (var i = 0; i < fullOrder.Count; i++)
            positionInFullOrder.TryAdd(fullOrder[i], i);

        var exportedIds = exportedEntries.Select(e => e.Id).ToList();

        string? TargetExportedId(ModuleId? anchor)
        {
            if (anchor is not { } id || !positionInFullOrder.TryGetValue(id, out var anchorPosition))
                return null;

            foreach (var exportedId in exportedIds)
            {
                if (positionInFullOrder.TryGetValue(new ModuleId(exportedId), out var exportedPosition)
                    && exportedPosition >= anchorPosition)
                    return exportedId;
            }

            return null;
        }

        var byTarget = new Dictionary<string, List<LoadOrderDivider>>(StringComparer.OrdinalIgnoreCase);
        var atEnd = new List<LoadOrderDivider>();

        foreach (var divider in dividers)
        {
            var target = TargetExportedId(divider.AnchorId);

            if (target is null)
            {
                atEnd.Add(divider);
            }
            else if (byTarget.TryGetValue(target, out var list))
            {
                list.Add(divider);
            }
            else
            {
                byTarget[target] = [divider];
            }
        }

        var usedIds = new HashSet<string>(exportedIds, StringComparer.OrdinalIgnoreCase);

        LoadOrderFileEntry ToEntry(LoadOrderDivider divider)
        {
            var id = DividerConvention.ToId(divider.Label, usedIds.Contains);
            usedIds.Add(id);
            return new LoadOrderFileEntry(id, "");
        }

        var result = new List<LoadOrderFileEntry>(exportedEntries.Count + dividers.Count);

        foreach (var entry in exportedEntries)
        {
            if (byTarget.TryGetValue(entry.Id, out var here))
            {
                foreach (var divider in here)
                    result.Add(ToEntry(divider));
            }

            result.Add(entry);
        }

        foreach (var divider in atEnd)
            result.Add(ToEntry(divider));

        return result;
    }
}
