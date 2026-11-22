namespace BannerlordEnvironmentManager.Core.Modules;

// BLSE.AssemblyResolver, BLSE.LoadingInterceptor and friends are declared as <DependedModule>
// ids so the vanilla launcher grays out mods that need BLSE, but they have no folder and never
// will: BLSE itself patches the launcher to treat them as always present. Treating them as a
// normal, missing dependency is a false error, not a real one.
public static class BlseFeatures
{
    private const string FeaturePrefix = "BLSE.";

    private static readonly HashSet<string> KnownIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "BLSE.LoadingInterceptor",
        "BLSE.AssemblyResolver",
        "BLSE.ContinueSaveFile",
        "BLSE.Commands",
        "BLSE.ExceptionInterceptor",
        "BLSE.Xbox",
        "BUTRLoader.BUTRLoadingInterceptor",
        "BUTRLoader.BUTRAssemblyResolver"
    };

    public static bool IsFeatureId(ModuleId id) =>
        !id.IsEmpty
        && (KnownIds.Contains(id.Value) || id.Value.StartsWith(FeaturePrefix, StringComparison.OrdinalIgnoreCase));
}
