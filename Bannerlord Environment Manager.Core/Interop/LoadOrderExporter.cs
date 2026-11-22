using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Interop;

public sealed record LoadOrderExportPlan(
    IReadOnlyList<LoadOrderFileEntry> Entries,
    IReadOnlyList<string> WithoutVersion);

// Presence means enabled in every format BEM writes, and none of them has a way to say "installed but
// off". A disabled module is therefore left out rather than marked, because the marker anyone might
// invent for it (a "#" prefix, say) comes back in on the next import as a module id that does not exist.
public static class LoadOrderExporter
{
    public static LoadOrderExportPlan Describe(ModuleEnvironment environment) =>
        Describe(environment, []);

    public static LoadOrderExportPlan Describe(ModuleEnvironment environment, IReadOnlyList<LoadOrderDivider> dividers)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(dividers);

        var entries = new List<LoadOrderFileEntry>();
        var withoutVersion = new List<string>();

        foreach (var entry in environment.Entries.Where(e => e.IsEnabled))
        {
            var version = Version(entry);

            if (version.Length == 0)
                withoutVersion.Add(entry.Id.Value);

            entries.Add(new LoadOrderFileEntry(
                entry.Id.Value,
                version,
                IsEnabled: true,
                Name: entry.DisplayName,
                Url: entry.Manifest?.Url));
        }

        var interleaved = LoadOrderDividerInterleave.Interleave(
            entries, [.. environment.Entries.Select(e => e.Id)], dividers);

        return new LoadOrderExportPlan(interleaved, withoutVersion);
    }

    // The raw text out of SubModule.xml, never the parsed value, which drops a zero fourth component.
    // A bare "1.4.8" does not parse on the .bmlist path at all, so a missing version-type prefix is the
    // one thing added.
    public static string Version(ModuleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var declared = entry.Manifest?.VersionText?.Trim();

        if (string.IsNullOrEmpty(declared))
            declared = entry.Version.ToString();

        if (declared.Length == 0)
            return string.Empty;

        return char.IsDigit(declared[0]) ? $"v{declared}" : declared;
    }
}
