using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public sealed record ModuleFact(string Label, string Value);

// What a module folder weighs. Measured on demand for one module rather than for all of them on every
// scan: it is a recursive enumeration, and 195 of them would be paid for on every refresh.
public sealed record ModuleFootprint(long Bytes, int Files, string? Error = null)
{
    public static ModuleFootprint Measure(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return new ModuleFootprint(0, 0, Strings.Current["Core.LoadOrder.ModuleFootprint.NoFolder"]);

        if (!Directory.Exists(folderPath))
        {
            return new ModuleFootprint(
                0, 0, Strings.Current.Format("Core.LoadOrder.ModuleFootprint.NotThere", folderPath));
        }

        try
        {
            long bytes = 0;
            var files = 0;

            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
                files++;
            }

            return new ModuleFootprint(bytes, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleFootprint(0, 0, ex.Message);
        }
    }

    // "Could not look" and "found nothing" are opposite answers, so a folder that could not be read
    // never reports a size of zero.
    public string Describe() => Error is null
        ? Strings.Current.Plural("Core.LoadOrder.ModuleFootprint.Describe", Files, DescribeSize(Bytes))
        : Strings.Current.Format("Core.LoadOrder.ModuleFootprint.NotMeasured", Error);

    private static string DescribeSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => bytes == 1 ? "1 byte" : $"{bytes} bytes"
    };
}

// One module, everything BEM already knows about it. Most of this was computed on every scan and shown
// nowhere: the dependencies in the direction nothing displayed, the ten diagnoses collapsed into three
// severity words, the official-optional distinction, the source, the size and the mod page.
public sealed record ModuleDetails(
    ModuleId Id,
    string DisplayName,
    int Position,
    IReadOnlyList<ModuleFact> Facts,
    IReadOnlyList<string> Declares,
    IReadOnlyList<string> NeededBy,
    IReadOnlyList<string> Diagnoses,
    string? Url = null,
    string? FolderPath = null,
    string? ManifestPath = null)
{
    public static ModuleDetails? For(ModuleEnvironment environment, ModuleId id, bool isPinned = false)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var index = -1;

        for (var i = 0; i < environment.Entries.Count; i++)
        {
            if (environment.Entries[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return null;

        var entry = environment.Entries[index];
        var manifest = entry.Manifest;
        var installed = environment.Entries.ToDictionary(e => e.Id, e => e);
        var tiers = ModuleTierMap.For(environment.Entries);

        var noManifest = Strings.Current["Core.LoadOrder.ModuleDetails.UnknownNoManifest"];

        var facts = new List<ModuleFact>
        {
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Position"],
                Strings.Current.Format(
                    "Core.LoadOrder.ModuleDetails.PositionValue", index + 1, environment.Entries.Count)),
            new(Strings.Current["Core.LoadOrder.ModuleDetails.Label.State"], DescribeState(entry)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Pinned"],
                isPinned
                    ? Strings.Current["Core.LoadOrder.ModuleDetails.PinnedYes"]
                    : Strings.Current["Core.LoadOrder.ModuleDetails.PinnedNo"]),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Tier"],
                manifest is null
                    ? Strings.Current["Core.LoadOrder.ModuleDetails.TierNoManifest"]
                    : Strings.Current.Format(
                        "Core.LoadOrder.ModuleDetails.TierValue",
                        ModuleTierMap.Describe(tiers[index]), ModuleTierMap.Explain(tiers[index]))),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Type"],
                manifest is null ? noManifest : DescribeType(manifest.Type)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Category"],
                manifest is null ? noManifest : DescribeCategory(manifest.Category)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Origin"],
                manifest is null ? noManifest : DescribeOrigin(manifest.Origin)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Source"],
                manifest is null ? noManifest : DescribeSource(manifest.Source)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Version"],
                manifest is null ? noManifest : DescribeVersion(manifest)),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Size"],
                ModuleFootprint.Measure(manifest?.FolderPath).Describe()),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Folder"],
                manifest?.FolderPath ?? Strings.Current["Core.LoadOrder.ModuleDetails.NoneOnDisk"]),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.Manifest"],
                manifest?.ManifestPath ?? Strings.Current["Core.LoadOrder.ModuleDetails.NoneOnDisk"]),
            new(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.ModPage"],
                string.IsNullOrWhiteSpace(manifest?.Url)
                    ? Strings.Current["Core.LoadOrder.ModuleDetails.NoModPage"]
                    : manifest!.Url!)
        };

        // An accepted claim says which evidence carried it. "Official" with no basis behind it reads
        // the same whether BEM proved the signature or only recognized the name, and those are not
        // the same assurance.
        if (OfficialClaimFact(manifest?.OfficialClaimBasis) is { } claim)
        {
            facts.Add(new ModuleFact(
                Strings.Current["Core.LoadOrder.ModuleDetails.Label.OfficialClaim"],
                claim));
        }

        return new ModuleDetails(
            entry.Id,
            entry.DisplayName,
            index + 1,
            facts,
            [.. DescribeDeclared(entry, installed)],
            [.. DescribeDependents(environment, entry)],
            [.. DescribeDiagnoses(environment, entry)],
            string.IsNullOrWhiteSpace(manifest?.Url) ? null : manifest!.Url,
            manifest?.FolderPath,
            manifest?.ManifestPath);
    }

    private static string DescribeState(ModuleEntry entry) => entry switch
    {
        { IsOrphan: true } => Strings.Current["Core.LoadOrder.ModuleDetails.State.Orphan"],
        { IsUnreadable: true, IsEnabled: true } => Strings.Current["Core.LoadOrder.ModuleDetails.State.EnabledUnreadable"],
        { IsUnreadable: true } => Strings.Current["Core.LoadOrder.ModuleDetails.State.DisabledUnreadable"],
        { IsEnabled: true } => Strings.Current["Core.LoadOrder.ModuleDetails.State.Enabled"],
        _ => Strings.Current["Core.LoadOrder.ModuleDetails.State.Disabled"]
    };

    private static string DescribeType(ModuleType type) => type switch
    {
        ModuleType.Official => Strings.Current["Core.LoadOrder.ModuleDetails.Type.Official"],
        ModuleType.OfficialOptional => Strings.Current["Core.LoadOrder.ModuleDetails.Type.OfficialOptional"],
        _ => Strings.Current["Core.LoadOrder.ModuleDetails.Type.Community"]
    };

    private static string DescribeCategory(ModuleCategory category) => category switch
    {
        ModuleCategory.Multiplayer => Strings.Current["Core.LoadOrder.ModuleDetails.Category.Multiplayer"],
        ModuleCategory.Both => Strings.Current["Core.LoadOrder.ModuleDetails.Category.Both"],
        _ => Strings.Current["Core.LoadOrder.ModuleDetails.Category.Singleplayer"]
    };

    // Null for the cases with nothing to report: a module that never claimed official, and one whose
    // claim BEM had no verifier for.
    private static string? OfficialClaimFact(OfficialClaimBasis? basis) => basis switch
    {
        OfficialClaimBasis.SignatureVerified => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.SignatureVerified"],
        OfficialClaimBasis.Identity => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.Identity"],
        OfficialClaimBasis.Vouched => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.Vouched"],
        OfficialClaimBasis.Unverifiable => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.Unverifiable"],
        OfficialClaimBasis.DemotedForeignSignature => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.DemotedForeignSignature"],
        OfficialClaimBasis.DemotedUnrecognizedCertificate => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.DemotedUnrecognizedCertificate"],
        OfficialClaimBasis.DemotedBorrowedOfficialId => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.DemotedBorrowedOfficialId"],
        OfficialClaimBasis.DemotedUnrecognized => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaimDemoted"],
        OfficialClaimBasis.DemotedWithoutGameCode => Strings.Current["Core.LoadOrder.ModuleDetails.OfficialClaim.DemotedWithoutGameCode"],
        _ => null
    };

    private static string DescribeOrigin(ModuleOrigin origin) => origin switch
    {
        ModuleOrigin.Official => Strings.Current["Core.LoadOrder.ModuleDetails.Origin.Official"],
        ModuleOrigin.Workshop => Strings.Current["Core.LoadOrder.ModuleDetails.Origin.Workshop"],
        _ => Strings.Current["Core.LoadOrder.ModuleDetails.Origin.Manual"]
    };

    private static string DescribeSource(ModuleSource source) =>
        source == ModuleSource.Workshop
            ? Strings.Current["Core.LoadOrder.ModuleDetails.Source.Workshop"]
            : Strings.Current["Core.LoadOrder.ModuleDetails.Source.Modules"];

    // The parsed version drops a zero fourth component, so the manifest's own text is shown beside it
    // when they differ rather than quietly reporting a version the file does not contain.
    private static string DescribeVersion(ModuleManifest manifest)
    {
        var parsed = manifest.Version.ToString();

        return string.IsNullOrWhiteSpace(manifest.VersionText) || manifest.VersionText == parsed
            ? parsed
            : Strings.Current.Format("Core.LoadOrder.ModuleDetails.VersionMismatch", parsed, manifest.VersionText);
    }

    private static IEnumerable<string> DescribeDeclared(
        ModuleEntry entry, IReadOnlyDictionary<ModuleId, ModuleEntry> installed)
    {
        foreach (var dependency in entry.Dependencies)
        {
            var target = installed.GetValueOrDefault(dependency.TargetId);

            var need = dependency switch
            {
                { IsIncompatible: true } =>
                    Strings.Current.Format("Core.LoadOrder.ModuleDetails.Need.Incompatible", dependency.TargetId),
                { IsOptional: true } =>
                    Strings.Current.Format("Core.LoadOrder.ModuleDetails.Need.Optional", dependency.TargetId),
                _ => Strings.Current.Format("Core.LoadOrder.ModuleDetails.Need.Required", dependency.TargetId)
            };

            var version = dependency.VersionRange.IsAny ? string.Empty : $" {dependency.VersionRange.Text}";

            yield return $"{need}{version}{DescribeOrder(dependency)}. {DescribePresence(target)}";
        }
    }

    private static IEnumerable<string> DescribeDependents(ModuleEnvironment environment, ModuleEntry entry)
    {
        foreach (var other in environment.Entries)
        {
            if (other.Id == entry.Id)
                continue;

            foreach (var dependency in other.Dependencies)
            {
                if (dependency.TargetId != entry.Id)
                    continue;

                var need = dependency switch
                {
                    { IsIncompatible: true } =>
                        Strings.Current.Format("Core.LoadOrder.ModuleDetails.NeededBy.Incompatible", other.Id),
                    { IsOptional: true } =>
                        Strings.Current.Format("Core.LoadOrder.ModuleDetails.NeededBy.Optional", other.Id),
                    _ => Strings.Current.Format("Core.LoadOrder.ModuleDetails.NeededBy.Required", other.Id)
                };

                yield return $"{need}{DescribeOrder(dependency, fromTheOtherSide: true)}. "
                    + $"{DescribePresence(other)}";
            }
        }
    }

    private static string DescribeOrder(ModuleDependency dependency, bool fromTheOtherSide = false)
    {
        var stated = dependency.IsOrderExplicit
            ? Strings.Current["Core.LoadOrder.ModuleDetails.Order.Stated"]
            : Strings.Current["Core.LoadOrder.ModuleDetails.Order.Implied"];

        return dependency switch
        {
            { IsIncompatible: true } => string.Empty,
            { Order: DependencyOrder.LoadBeforeThis } when fromTheOtherSide =>
                Strings.Current.Format("Core.LoadOrder.ModuleDetails.Order.LoadsAfterOtherSide", stated),
            { Order: DependencyOrder.LoadBeforeThis } =>
                Strings.Current.Format("Core.LoadOrder.ModuleDetails.Order.LoadsBefore", stated),
            { Order: DependencyOrder.LoadAfterThis } when fromTheOtherSide =>
                Strings.Current.Format("Core.LoadOrder.ModuleDetails.Order.LoadsBeforeOtherSide", stated),
            { Order: DependencyOrder.LoadAfterThis } =>
                Strings.Current.Format("Core.LoadOrder.ModuleDetails.Order.LoadsAfter", stated),
            _ => Strings.Current["Core.LoadOrder.ModuleDetails.Order.NoneStated"]
        };
    }

    private static string DescribePresence(ModuleEntry? entry) => entry switch
    {
        null => Strings.Current["Core.LoadOrder.ModuleDetails.Presence.NotInstalled"],
        { IsOrphan: true } => Strings.Current["Core.LoadOrder.ModuleDetails.Presence.Orphan"],
        { IsEnabled: true } => Strings.Current["Core.LoadOrder.ModuleDetails.Presence.Enabled"],
        _ => Strings.Current["Core.LoadOrder.ModuleDetails.Presence.Disabled"]
    };

    // The kind, not only the severity. Three severity words stand for fourteen distinct diagnoses, and
    // the one the module actually has is the thing worth reading.
    private static IEnumerable<string> DescribeDiagnoses(ModuleEnvironment environment, ModuleEntry entry)
    {
        foreach (var issue in LoadOrderValidator.Validate(environment))
        {
            if (issue.ModuleId != entry.Id)
                continue;

            yield return $"{IssueKinds.Describe(issue.Severity)}, {IssueKinds.Describe(issue.Kind)}: {issue.Message}";
        }
    }
}
