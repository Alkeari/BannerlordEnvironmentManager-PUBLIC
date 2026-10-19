using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Stalled says a pass applied every fix it had and nothing changed, which is a different statement
// from "issues remain": it means the fixes themselves are not working, and running more passes would
// only repeat that. Reporting it as a dependency loop, which is what "did not converge" used to be
// explained as, would name a cause that was never established.
public sealed record FixAllResult(ModuleEnvironment Environment, bool Converged, bool Stalled = false);

public static class LoadOrderValidator
{
    private const int MaxFixPasses = 5;

    public static IReadOnlyList<LoadOrderIssue> Validate(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var issues = new List<LoadOrderIssue>();
        var positions = new Dictionary<ModuleId, int>();
        var byId = environment.ById;

        for (var i = 0; i < environment.Entries.Count; i++)
            positions[environment.Entries[i].Id] = i;

        var enabledOnly = environment.WithEntries([.. environment.Entries.Where(e => e.IsEnabled)]);
        var cycles = LoadOrderSorter.Sort(enabledOnly).Cycles;

        // Read before the dependencies are graded, because a broken order between two modules in a loop
        // is the one kind no reordering can repair, and the fix offered for it has to know that.
        var looped = cycles.SelectMany(cycle => cycle).ToHashSet();

        var emptyFolderNames = environment.FoldersWithoutManifest
            .Select(folder => folder.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in environment.Entries)
        {
            if (entry.IsOrphan)
            {
                var orphanId = entry.Id;

                // "No folder in Modules" was asserted for every orphan, and it is false whenever the
                // folder is there and merely has no SubModule.xml in it. The two situations need
                // different things from the user: one is a mod that is gone, the other is a mod that is
                // half there, and only the second leaves something on disk to deal with.
                var message = emptyFolderNames.Contains(orphanId.Value)
                    ? Strings.Current.Format("Core.LoadOrder.Validator.OrphanEmptyFolder", orphanId)
                    : Strings.Current.Format("Core.LoadOrder.Validator.OrphanNoFolder", orphanId);

                issues.Add(new LoadOrderIssue(
                    IssueKind.OrphanEntry,
                    IssueSeverity.Warning,
                    orphanId,
                    null,
                    message,
                    e => e.WithoutOrphan(orphanId),
                    IsDestructive: true));
                continue;
            }

            if (!entry.IsEnabled)
                continue;

            foreach (var dependency in entry.Dependencies)
                ValidateDependency(byId, positions, looped, entry, dependency, issues);
        }

        // The demotions are different findings and get different sentences. Saying "not signed by
        // TaleWorlds" over a module whose files are signed by somebody else understates it; saying it
        // over an unsigned module whose name is not the game's own was simply false on an install
        // whose official modules carry a TaleWorlds signature Windows will not chain; a folder
        // wearing an official module's name over code the game did not sign is the one a user most
        // needs told apart from a mod that merely overreached in its manifest; and a copy of an
        // official folder is named by its own path, because naming it by its id points at the real
        // module, which is sitting untouched in Modules and is not what was refused.
        foreach (var entry in environment.Entries.Where(e => e.Manifest?.OfficialClaimDemoted == true))
        {
            var manifest = entry.Manifest!;

            var message = manifest.OfficialClaimBasis switch
            {
                OfficialClaimBasis.DemotedForeignSignature => Strings.Current.Format(
                    "Core.LoadOrder.Validator.OfficialClaimForeignSignature", entry.DisplayName),
                OfficialClaimBasis.DemotedUnrecognizedCertificate => Strings.Current.Format(
                    "Core.LoadOrder.Validator.OfficialClaimUnrecognizedCertificate", entry.DisplayName),
                OfficialClaimBasis.DemotedWithoutGameCode => Strings.Current.Format(
                    "Core.LoadOrder.Validator.OfficialClaimWithoutGameCode", entry.DisplayName),
                OfficialClaimBasis.DemotedBorrowedOfficialId => Strings.Current.Format(
                    "Core.LoadOrder.Validator.OfficialClaimBorrowedId", manifest.FolderPath, manifest.Id.Value),
                _ => Strings.Current.Format(
                    "Core.LoadOrder.Validator.UnverifiedOfficialClaim", entry.DisplayName)
            };

            issues.Add(new LoadOrderIssue(
                IssueKind.UnverifiedOfficialClaim,
                IssueSeverity.Warning,
                entry.Id,
                null,
                message));
        }

        foreach (var unreadable in environment.Unreadable)
        {
            issues.Add(new LoadOrderIssue(
                IssueKind.UnreadableManifest,
                IssueSeverity.Warning,
                new ModuleId(unreadable.FolderName),
                null,
                Strings.Current.Format(
                    "Core.LoadOrder.Validator.UnreadableManifest", unreadable.FolderName, unreadable.Error)));
        }

        foreach (var folder in environment.FoldersWithoutManifest)
            ValidateFolderWithoutManifest(byId, folder, issues);

        foreach (var duplicate in environment.Duplicates)
        {
            var shadowed = string.Join(", ", duplicate.ShadowedFolderPaths.Select(p => $"'{p}'"));

            issues.Add(new LoadOrderIssue(
                IssueKind.DuplicateModuleId,
                IssueSeverity.Warning,
                duplicate.Id,
                null,
                Strings.Current.Format(
                    "Core.LoadOrder.Validator.DuplicateModuleId", duplicate.Id, duplicate.UsedFolderPath, shadowed)));
        }

        ValidateNativeBoundary(environment, issues);

        foreach (var cycle in cycles)
        {
            issues.Add(new LoadOrderIssue(
                IssueKind.CyclicDependency,
                IssueSeverity.Error,
                cycle[0],
                null,
                Strings.Current.Format("Core.LoadOrder.Validator.CyclicDependency", string.Join(", ", cycle))));
        }

        return issues;
    }

    // Graded by what the folder actually costs, which is never a crash. The game skips it, so nothing
    // fails to load; what it costs is that BUTR's ModuleInfoHelper enumerates every directory under
    // Modules and reads <folder>\SubModule.xml without testing for it first, so each library that asks
    // takes a caught FileNotFoundException with a full stack, once per launch.
    //
    // Only the case that costs the user something. A folder with no manifest whose module is installed
    // and running somewhere else is a Workshop mod writing its own log back into a conventional path,
    // which is the author's habit and not a fault in the setup: the mod works, the load order is right,
    // and the folder comes back after every launch. BEM reported it anyway, on every scan, with no
    // fix that lasts, which is a note that can only be read and never acted on.
    //
    // Where nothing supplies the module at all, a mod the user believes is installed is not, and that
    // is worth a warning.
    private static void ValidateFolderWithoutManifest(
        IReadOnlyDictionary<ModuleId, ModuleEntry> byId,
        ModuleFolderWithoutManifest folder,
        List<LoadOrderIssue> issues)
    {
        var id = new ModuleId(folder.FolderName);
        var contents = Describe(folder);

        if (byId.TryGetValue(id, out var installed) && installed.Manifest is not null)
            return;

        // Never a Fix. Every repair here is on disk, and a Fix is an in-memory rewrite of the load
        // order, so wiring one would report a removal that never happened.
        issues.Add(new LoadOrderIssue(
            IssueKind.FolderWithoutManifest,
            IssueSeverity.Warning,
            id,
            null,
            Strings.Current.Format(
                "Core.LoadOrder.Validator.FolderWithoutManifest", folder.FolderPath, folder.FolderName, contents),
            Path: folder.FolderPath));
    }

    private static string Describe(ModuleFolderWithoutManifest folder)
    {
        if (folder.FileCount == 0)
            return Strings.Current["Core.LoadOrder.Validator.FolderEmpty"];

        var files = Strings.Current.Plural("Core.LoadOrder.Validator.FileCount", folder.FileCount);
        var named = string.Join(", ", folder.FileNames);
        var more = folder.FileCount > folder.FileNames.Count ? ", and more" : string.Empty;

        return Strings.Current.Format(
            "Core.LoadOrder.Validator.FolderContents",
            files, ModuleUninstaller.DescribeSize(folder.SizeBytes), named + more);
    }

    private static void ValidateDependency(
        IReadOnlyDictionary<ModuleId, ModuleEntry> byId,
        Dictionary<ModuleId, int> positions,
        IReadOnlySet<ModuleId> looped,
        ModuleEntry entry,
        ModuleDependency dependency,
        List<LoadOrderIssue> issues)
    {
        if (dependency.TargetId == entry.Id)
            return;

        if (!byId.TryGetValue(dependency.TargetId, out var target) || target.IsOrphan)
        {
            if (DependencyDescriber.DescribeMissing(entry.DisplayName, dependency) is not { } missing)
                return;

            issues.Add(dependency.IsOptional
                ? new LoadOrderIssue(
                    IssueKind.MissingOptionalDependency,
                    IssueSeverity.Information,
                    entry.Id,
                    dependency.TargetId,
                    missing)
                : new LoadOrderIssue(
                    IssueKind.MissingDependency,
                    IssueSeverity.Error,
                    entry.Id,
                    dependency.TargetId,
                    missing));
            return;
        }

        if (dependency.IsIncompatible)
        {
            if (target.IsEnabled)
            {
                issues.Add(new LoadOrderIssue(
                    IssueKind.Incompatible,
                    IssueSeverity.Error,
                    entry.Id,
                    target.Id,
                    Strings.Current.Format(
                        "Core.LoadOrder.Validator.IncompatibleSuffix",
                        DependencyDescriber.DescribeIncompatible(entry.DisplayName, target.DisplayName))));
            }

            return;
        }

        if (!target.IsEnabled)
        {
            if (!dependency.IsOptional)
            {
                var targetId = target.Id;

                issues.Add(new LoadOrderIssue(
                    IssueKind.DisabledDependency,
                    IssueSeverity.Error,
                    entry.Id,
                    targetId,
                    Strings.Current.Format(
                        "Core.LoadOrder.Validator.DisabledDependency", entry.DisplayName, target.DisplayName),
                    e => Enable(e, targetId)));
            }

            return;
        }

        // An unreadable manifest has no version to compare against, and reporting it as a mismatch
        // would blame the dependent module for the wrong problem.
        if (!target.IsUnreadable &&
            DependencyDescriber.DescribeVersionMismatch(entry.DisplayName, target.DisplayName, dependency, target.Version) is { } mismatch)
        {
            // The engine never compares a declared dependency version against an installed module's
            // version (only StoryMode/NavalDLC and three specific rules ever block), and declared
            // floors like "v1.0.0.*" go stale while the mod keeps working, so this is a note, not a
            // real error.
            issues.Add(new LoadOrderIssue(
                IssueKind.VersionMismatch,
                IssueSeverity.Information,
                entry.Id,
                target.Id,
                mismatch));
        }

        if (dependency.Order == DependencyOrder.None)
            return;

        var here = positions[entry.Id];
        var there = positions[target.Id];

        var satisfied = dependency.Order == DependencyOrder.LoadBeforeThis
            ? there < here
            : there > here;

        if (satisfied)
            return;

        var before = dependency.Order == DependencyOrder.LoadBeforeThis;

        // These two declare each other in a loop, so no order satisfies both and reordering cannot
        // repair it. Offering a Fix here reports success and changes nothing, which is a worse answer
        // than no button: the issue says what is wrong and the loop's own issue says what to do.
        var unrepairable = looped.Contains(entry.Id) || looped.Contains(target.Id);
        var loop = unrepairable ? Strings.Current["Core.LoadOrder.Validator.LoopSuffix"] : string.Empty;
        var repair = unrepairable ? null : (Func<ModuleEnvironment, ModuleEnvironment>)RepairOrder;

        // An explicit ordering declaration is the author saying where the module goes, so breaking it is
        // an error. A plain DependedModule only says what is needed; the ordering falls out of it and is
        // graded as a warning, because the author never wrote it down as a requirement.
        issues.Add(dependency.IsOrderExplicit
            ? new LoadOrderIssue(
                IssueKind.OrderViolation,
                IssueSeverity.Error,
                entry.Id,
                target.Id,
                Strings.Current.Format(
                    before
                        ? "Core.LoadOrder.Validator.OrderViolation.Before"
                        : "Core.LoadOrder.Validator.OrderViolation.After",
                    target.DisplayName, entry.DisplayName, loop),
                repair)
            : new LoadOrderIssue(
                IssueKind.ImpliedOrderViolation,
                IssueSeverity.Warning,
                entry.Id,
                target.Id,
                Strings.Current.Format(
                    before
                        ? "Core.LoadOrder.Validator.ImpliedOrderViolation.Before"
                        : "Core.LoadOrder.Validator.ImpliedOrderViolation.After",
                    entry.DisplayName, target.DisplayName, loop),
                repair));
    }

    // Where a module sits relative to Native is a convention, not a declaration, so none of this is an
    // error. The exemption is keyed off what a module IS rather than a list of names: the crash handler
    // and infrastructure tiers are the ones that legitimately hook the engine before the base game
    // module. A library recognized by fan-in alone is not one of them. It belongs after the game's own
    // modules, so it is graded here as ordinary content rather than told to move above Native.
    private static void ValidateNativeBoundary(ModuleEnvironment environment, List<LoadOrderIssue> issues)
    {
        var entries = environment.Entries;
        var native = new ModuleId("Native");
        var nativeAt = -1;

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == native && entries[i].IsEnabled)
                nativeAt = i;
        }

        if (nativeAt < 0)
            return;

        var tiers = ModuleTierMap.For(entries);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (i == nativeAt || !entry.IsEnabled || entry.IsOrphan || entry.IsOfficial)
                continue;

            // Nothing to add where the module already declares its own relationship to Native: that is
            // reported above, at whatever severity the declaration earns.
            if (entry.Dependencies.Any(d => d.TargetId == native && !d.IsIncompatible && d.Order != DependencyOrder.None))
                continue;

            var before = i < nativeAt;
            var loadsEarly = tiers[i] is ModuleTier.CrashHandler or ModuleTier.Infrastructure;

            // Neither of these carries a Fix. Both fire only where the module declares no relationship to
            // Native, so a topological sort has no constraint to move it by and hands back the order it
            // was given: the button reported success and changed nothing. Moving it anyway would act on a
            // hint against a placement the user made on purpose, so the issue says where to drag it.
            if (loadsEarly && !before)
            {
                issues.Add(new LoadOrderIssue(
                    IssueKind.InfrastructureAfterNative,
                    IssueSeverity.Warning,
                    entry.Id,
                    native,
                    Strings.Current.Format(
                        "Core.LoadOrder.Validator.InfrastructureAfterNative", entry.DisplayName)));
                continue;
            }

            if (!loadsEarly && before)
            {
                issues.Add(new LoadOrderIssue(
                    IssueKind.ContentBeforeNative,
                    IssueSeverity.Warning,
                    entry.Id,
                    native,
                    Strings.Current.Format(
                        "Core.LoadOrder.Validator.ContentBeforeNative", entry.DisplayName)));
            }
        }
    }

    // Everything that says a module is in the wrong place, at the severity it earns. Used by the drag
    // guard and the import report so both grade a placement exactly as the Issues panel does.
    public static IReadOnlyList<LoadOrderIssue> OrderingIssues(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return [.. Validate(environment).Where(issue => issue.Kind
            is IssueKind.OrderViolation
            or IssueKind.ImpliedOrderViolation
            or IssueKind.InfrastructureAfterNative
            or IssueKind.ContentBeforeNative)];
    }

    public static FixAllResult FixAll(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var current = environment;

        for (var pass = 0; pass < MaxFixPasses; pass++)
        {
            var fixable = AutoFixable(current);

            if (fixable.Count == 0)
                return new FixAllResult(current, true);

            var before = current;

            foreach (var issue in fixable)
                current = issue.Fix!(current);

            // Every fix ran and the list is byte for byte what it was, so running the same fixes again
            // can only do the same nothing. Burning the remaining passes and then blaming a dependency
            // loop would name a cause nothing here established.
            if (Unchanged(before, current))
                return new FixAllResult(current, false, Stalled: true);
        }

        // Running out of passes is not the same as failing: the last pass may well have fixed the
        // last issue, so convergence is decided by what is left, not by how the loop exited.
        return new FixAllResult(current, AutoFixable(current).Count == 0);
    }

    private static bool Unchanged(ModuleEnvironment before, ModuleEnvironment after) =>
        before.Entries.Count == after.Entries.Count
        && before.Entries.Select(e => (e.Id, e.IsEnabled))
            .SequenceEqual(after.Entries.Select(e => (e.Id, e.IsEnabled)));

    private static IReadOnlyList<LoadOrderIssue> AutoFixable(ModuleEnvironment environment) =>
        [.. Validate(environment).Where(i => i.IsAutoFixable)];

    // Repairing one broken ordering is not the same act as sorting the load order, and it must not
    // quietly become one. The empty plan makes the topological pass fall back to the position each
    // module came in at, so it satisfies every declared constraint and leaves everything the
    // constraints do not touch exactly where the user put it. The default plan, tier then module id,
    // belongs to Auto-Sort, where the user has actually asked for a re-sort: applying it here moved
    // 169 of 195 modules on a real install behind a button labeled Fix.
    //
    // A sort that fails its own compliance check returns the order it was given, so the environment
    // comes back untouched rather than through a WithEntries that would look like a repair and change
    // nothing.
    private static ModuleEnvironment RepairOrder(ModuleEnvironment environment)
    {
        var result = LoadOrderSorter.Sort(environment, ModuleSortPlan.Empty);

        return result.Failed ? environment : environment.WithEntries(result.Entries);
    }

    private static ModuleEnvironment Enable(ModuleEnvironment environment, ModuleId id) =>
        environment.WithEntries([.. environment.Entries.Select(e =>
            e.Id == id && !e.IsOrphan ? e with { IsEnabled = true } : e)]);
}
