using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

// Resolves a manifest's dependencies against a set of installed modules directly, with
// no notion of enabled/disabled or load-order position. LoadOrderValidator answers a
// different question (is a real load order valid) and only evaluates dependencies for
// entries it considers enabled; callers that just want "is this SubModule.xml missing
// anything it declares" - independent of any load order - use this instead.
public static class ModuleDependencyResolver
{
    public static IReadOnlyList<string> DescribeIssues(
        ModuleManifest module,
        IReadOnlyDictionary<ModuleId, ModuleManifest> installed)
    {
        var issues = new List<string>();

        foreach (var dependency in module.Dependencies)
        {
            if (dependency.TargetId == module.Id)
                continue;

            if (!installed.TryGetValue(dependency.TargetId, out var target))
            {
                if (DependencyDescriber.DescribeMissing(module.Name, dependency) is { } missing)
                    issues.Add(missing);

                continue;
            }

            if (dependency.IsIncompatible)
            {
                issues.Add(DependencyDescriber.DescribeIncompatible(module.Name, target.Name));
                continue;
            }

            if (DependencyDescriber.DescribeVersionMismatch(module.Name, target.Name, dependency, target.Version) is { } mismatch)
                issues.Add(mismatch);
        }

        return issues;
    }
}

// Both ModuleDependencyResolver and LoadOrderValidator report these three findings, and users
// search the web for the exact wording, so the text lives in one place. Each caller keeps its own
// concerns: the validator's ordering and enabled-state checks, the resolver's manifest-only view.
internal static class DependencyDescriber
{
    internal static string? DescribeMissing(string declaringName, ModuleDependency dependency)
    {
        // These ids are provided by BLSE at runtime and have no folder by design, so treating them
        // as missing is a false error rather than a real one. A real installed module whose id
        // happens to start with "BLSE." is found by its caller's lookup and never reaches here.
        if (BlseFeatures.IsFeatureId(dependency.TargetId))
            return null;

        if (dependency.IsIncompatible)
            return null;

        return dependency.IsOptional
            ? Strings.Current.Format("Core.Modules.Dependency.Missing.Optional", declaringName, dependency.TargetId)
            : Strings.Current.Format("Core.Modules.Dependency.Missing.Required", declaringName, dependency.TargetId);
    }

    internal static string DescribeIncompatible(string declaringName, string targetName) =>
        Strings.Current.Format("Core.Modules.Dependency.Incompatible", declaringName, targetName);

    internal static string? DescribeVersionMismatch(
        string declaringName,
        string targetName,
        ModuleDependency dependency,
        ModuleVersion installedVersion)
    {
        if (dependency.VersionRange.IsAny || dependency.VersionRange.Matches(installedVersion))
            return null;

        return Strings.Current.Format(
            "Core.Modules.Dependency.VersionMismatch", declaringName, targetName, dependency.VersionRange.Text, installedVersion);
    }
}
