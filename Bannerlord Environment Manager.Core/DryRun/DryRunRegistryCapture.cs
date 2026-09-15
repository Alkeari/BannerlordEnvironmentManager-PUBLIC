using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

public sealed record PatchRegistrySave(bool Saved, int Patches, int PatchedMethods, string Message);

// A dry run captures the registry from Harmony and then the process is gone. A crash report is
// analyzed hours later, so the capture has to outlive the run, keyed to the module set it came from.
// This is the step that was missing: everything either side of it already existed.
public static class DryRunRegistryCapture
{
    public static PatchRegistrySave Save(
        DryRunVerdict verdict,
        IReadOnlyList<ModuleId> moduleSet,
        PatchRegistryStore store)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return SaveFromResult(verdict.Result, moduleSet, store);
    }

    // A watched play session captures the registry from Harmony exactly as a boot check does, and it
    // captures it from the launch the user actually plays. The guards below are the same ones and
    // live in one place, so a watch run cannot store a registry a boot check would have refused.
    public static PatchRegistrySave SaveFromResult(
        DryRunResult? result,
        IReadOnlyList<ModuleId> moduleSet,
        PatchRegistryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (result is null)
            return new PatchRegistrySave(false, 0, 0, Strings.Current["Core.DryRun.RegistryCapture.NoResultFile"]);

        if (!result.ReachedFirstTick)
            return new PatchRegistrySave(false, 0, 0, Strings.Current["Core.DryRun.RegistryCapture.NoFirstTick"]);

        var registry = PatchRegistryCapture.From(result, moduleSet ?? []);

        // Harmony reporting nothing on an install that is running Harmony is a companion failure, not
        // a measurement. Storing it would answer every later lookup with a fresh but empty registry,
        // which silently removes the "no registry was captured" caveat and reads stronger than having
        // none at all.
        if (registry is null || registry.Patches.Count == 0)
            return new PatchRegistrySave(false, 0, 0, Strings.Current["Core.DryRun.RegistryCapture.EmptyRegistry"]);

        store.Save(registry);

        return new PatchRegistrySave(
            true,
            registry.Patches.Count,
            registry.PatchedMethodCount,
            Strings.Current.Plural("Core.DryRun.RegistryCapture.Saved.Patches", registry.Patches.Count)
            + Strings.Current.Plural("Core.DryRun.RegistryCapture.Saved.Methods", registry.PatchedMethodCount)
            + Strings.Current.Plural("Core.DryRun.RegistryCapture.Saved.Modules", registry.ModuleSet.Count));
    }
}
