using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Interop;

public sealed record ImportedModule(
    ModuleId Id,
    string DisplayName,
    string FileVersion,
    string InstalledVersion,
    bool KeptAsIs = false);

public sealed record MissingModule(string Id, string Version, string? Url = null, string? Name = null);

public sealed record AbsentModule(ModuleId Id, string DisplayName, bool WasEnabled, bool KeptAsIs);

public sealed record VersionDelta(ModuleId Id, string DisplayName, string FileVersion, string InstalledVersion);

// What can honestly be said about one module's version on each side. A blank on either side is
// NotKnown rather than Same: a module that declares nothing has no version to agree with, and
// calling that a match is the one answer that would mislead.
public enum VersionAgreement
{
    Same,
    Different,
    NotKnown
}

public sealed record LoadOrderImportPreview(
    ModuleEnvironment Result,
    IReadOnlyList<ImportedModule> Matched,
    IReadOnlyList<MissingModule> Missing,
    IReadOnlyList<AbsentModule> Absent,
    IReadOnlyList<VersionDelta> Deltas,
    IReadOnlyList<string> DuplicateIds,
    int UnreadableLines = 0,
    bool CanApply = true,
    bool HasChanges = false,
    string Refusal = "",
    // An import outranks every sorting rule BEM has: some heavily tweaked load orders work precisely
    // because they do not comply. The file is applied exactly as given and what it costs is reported
    // here rather than quietly repaired.
    IReadOnlyList<LoadOrderIssue> OrderIssues = null!,
    // The same reconciliation reports an imported file and a load order recovered from a save, and
    // calling a save "the file" misdescribes what the reader just did.
    string Source = LoadOrderImport.DefaultSource,
    IReadOnlyList<LoadOrderDivider> Dividers = null!)
{
    public IReadOnlyList<LoadOrderIssue> OrderIssues { get; init; } = OrderIssues ?? [];

    public IReadOnlyList<LoadOrderDivider> Dividers { get; init; } = Dividers ?? [];

    public string Source { get; init; } =
        string.IsNullOrWhiteSpace(Source) ? LoadOrderImport.DefaultSource : Source;

    public int OrderErrors => OrderIssues.Count(i => i.Severity == IssueSeverity.Error);

    public int OrderWarnings => OrderIssues.Count(i => i.Severity == IssueSeverity.Warning);

    public int WouldDisable => Absent.Count(a => a.WasEnabled && !a.KeptAsIs);

    public string Summary
    {
        get
        {
            if (!CanApply)
                return Refusal;

            var parts = new List<string>
            {
                Strings.Current.Plural("Core.Interop.LoadOrderImport.Summary.Matched", Matched.Count, Source)
            };

            if (Missing.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Interop.LoadOrderImport.Summary.Missing", Missing.Count, Source));

            if (Absent.Count > 0)
            {
                parts.Add(Strings.Current.Plural(
                    "Core.Interop.LoadOrderImport.Summary.Absent", Absent.Count, Source, WouldDisable));
            }

            if (Deltas.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Interop.LoadOrderImport.Summary.Deltas", Deltas.Count));

            if (OrderIssues.Count > 0)
            {
                // The noun is the two counts together, so the sentence selects on their total and
                // prints each of them.
                parts.Add(Strings.Current.Plural(
                    "Core.Interop.LoadOrderImport.Summary.OrderIssues",
                    OrderErrors + OrderWarnings, Source, OrderErrors, OrderWarnings));
            }

            return string.Join(", ", parts) + ".";
        }
    }
}

public static class LoadOrderImport
{
    public const string DefaultSource = "file";

    public static LoadOrderImportPreview Preview(
        ModuleEnvironment installed,
        LoadOrderFileRead file,
        string source = DefaultSource)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(source))
            source = DefaultSource;

        var byId = installed.ById;
        var matchedIds = new HashSet<ModuleId>();
        var pairs = new List<(LoadOrderFileEntry File, ModuleEntry Installed)>();
        var missing = new List<MissingModule>();

        var (realFileIds, dividers) = LoadOrderDividerExtraction.Extract(
            [.. file.Entries.Select(e => e.Id)], id => byId.ContainsKey(new ModuleId(id)));

        // First occurrence wins on a repeated id, matching the dedup DuplicateIds already records:
        // ToDictionary would throw on the second occurrence instead of ignoring it.
        var byFileId = new Dictionary<string, LoadOrderFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Entries)
            byFileId.TryAdd(entry.Id, entry);

        var realEntries = realFileIds.Select(id => byFileId[id]).ToList();

        foreach (var entry in realEntries)
        {
            var id = new ModuleId(entry.Id);

            if (!byId.TryGetValue(id, out var module))
                missing.Add(new MissingModule(entry.Id, entry.Version, entry.Url, entry.Name));
            else if (matchedIds.Add(module.Id))
                pairs.Add((entry, module));
        }

        // An import that matched nothing is the wrong file, not an instruction to turn everything off.
        // Applying it would disable the user's entire load order in one keystroke.
        if (pairs.Count == 0)
        {
            return new LoadOrderImportPreview(
                installed, [], missing, [], [], file.DuplicateIds, file.UnreadableLines,
                CanApply: false,
                HasChanges: false,
                Refusal: file.Entries.Count == 0
                    ? Strings.Current.Format("Core.Interop.LoadOrderImport.Refusal.Empty", source)
                    : Strings.Current.Plural(
                        "Core.Interop.LoadOrderImport.Refusal.NoneInstalled", file.Entries.Count, source),
                Source: source,
                Dividers: dividers);
        }

        var matched = new List<ImportedModule>(pairs.Count);
        var ordered = new List<ModuleEntry>(installed.Entries.Count);

        var fileChangeSet = ChangeSet(file.Entries.Select(e => (e.Id, e.Version)));
        var installedChangeSet = ChangeSet(installed.Entries.Select(e => (e.Id.Value, LoadOrderExporter.Version(e))));
        var deltas = new List<VersionDelta>();

        foreach (var (fileEntry, module) in pairs)
        {
            var enabled = Resolve(module, fileEntry.IsEnabled);
            var installedVersion = LoadOrderExporter.Version(module);

            ordered.Add(module with { IsEnabled = enabled });

            matched.Add(new ImportedModule(
                module.Id,
                module.DisplayName,
                fileEntry.Version,
                installedVersion,
                KeptAsIs: enabled != fileEntry.IsEnabled));

            if (Differs(fileEntry.Version, installedVersion, fileChangeSet, installedChangeSet))
                deltas.Add(new VersionDelta(module.Id, module.DisplayName, fileEntry.Version, installedVersion));
        }

        var absent = new List<AbsentModule>();
        var tail = new List<ModuleEntry>();

        for (var index = 0; index < installed.Entries.Count; index++)
        {
            var module = installed.Entries[index];

            if (matchedIds.Contains(module.Id))
                continue;

            // An official module the file left out is the difference between a modded game and one that
            // will not boot, and the sample files prove the case is live: a .bmlist exported by BLSE
            // omits Multiplayer entirely. It keeps its enabled state and its place among the modules
            // around it rather than being turned off and pushed to the bottom.
            if (module.IsOfficial)
            {
                absent.Add(new AbsentModule(module.Id, module.DisplayName, module.IsEnabled, KeptAsIs: true));
                Reinsert(ordered, installed.Entries, index);
                continue;
            }

            absent.Add(new AbsentModule(module.Id, module.DisplayName, module.IsEnabled, KeptAsIs: false));
            tail.Add(module with { IsEnabled = false });
        }

        ordered.AddRange(tail);

        var result = installed.WithEntries(ordered);

        return new LoadOrderImportPreview(
            result, matched, missing, absent, deltas, file.DuplicateIds, file.UnreadableLines,
            CanApply: true,
            HasChanges: !Signature(result.Entries).SequenceEqual(Signature(installed.Entries)),
            OrderIssues: LoadOrderValidator.OrderingIssues(result),
            Source: source,
            Dividers: dividers);
    }

    private static IEnumerable<(string Id, bool IsEnabled)> Signature(IReadOnlyList<ModuleEntry> entries) =>
        entries.Select(e => (e.Id.Value, e.IsEnabled));

    // Two guards, both of which already hold everywhere else in BEM, and both stated once on
    // ModuleEntry so every path that takes a state from a file reads the same rule. An import is a
    // sweep over the whole list, so neither is applied here; the report says which entries kept their
    // state.
    private static bool Resolve(ModuleEntry module, bool wanted) => module.EnabledFromOutside(wanted);

    private static void Reinsert(List<ModuleEntry> ordered, IReadOnlyList<ModuleEntry> original, int index)
    {
        var target = 0;

        for (var i = index - 1; i >= 0; i--)
        {
            var position = ordered.FindIndex(e => e.Id == original[i].Id);

            if (position < 0)
                continue;

            target = position + 1;
            break;
        }

        ordered.Insert(target, original[index]);
    }

    // Every official module carries the game's build number as its fourth component, so after a patch a
    // naive comparison reports a mismatch for every one of them. The changeset that the file's own
    // Native entry declares is treated as "this build" on each side and ignored, which leaves a real
    // fourth-component difference on a community mod still reported.
    public static int ChangeSet(IEnumerable<(string Id, string Version)> entries)
    {
        var native = entries.FirstOrDefault(e => string.Equals(e.Id, "Native", StringComparison.OrdinalIgnoreCase));

        return ModuleVersion.TryParse(native.Version, out var version) ? version.ChangeSet : 0;
    }

    // Differs answers a two-state question and reads an unparseable version as "no difference", which
    // is right where a delta is advisory and wrong where the absence of an answer is itself the
    // finding. This is the three-state form: identical text is a match whether or not it parses, and
    // text neither side can parse but that is not identical is reported as a difference with both
    // strings shown rather than swallowed.
    public static VersionAgreement Agreement(
        string fileVersion, string installedVersion, int fileChangeSet, int installedChangeSet)
    {
        var file = fileVersion?.Trim() ?? string.Empty;
        var installed = installedVersion?.Trim() ?? string.Empty;

        if (file.Length == 0 || installed.Length == 0)
            return VersionAgreement.NotKnown;

        if (string.Equals(file, installed, StringComparison.OrdinalIgnoreCase))
            return VersionAgreement.Same;

        if (!ModuleVersion.TryParse(file, out _) || !ModuleVersion.TryParse(installed, out _))
            return VersionAgreement.Different;

        return Differs(file, installed, fileChangeSet, installedChangeSet)
            ? VersionAgreement.Different
            : VersionAgreement.Same;
    }

    private static bool Differs(string fileVersion, string installedVersion, int fileChangeSet, int installedChangeSet)
    {
        if (!ModuleVersion.TryParse(fileVersion, out var file) || !ModuleVersion.TryParse(installedVersion, out var installed))
            return false;

        var fileChanges = file.ChangeSet == fileChangeSet ? 0 : file.ChangeSet;
        var installedChanges = installed.ChangeSet == installedChangeSet ? 0 : installed.ChangeSet;

        return file.Major != installed.Major
               || file.Minor != installed.Minor
               || file.Revision != installed.Revision
               || fileChanges != installedChanges;
    }
}
