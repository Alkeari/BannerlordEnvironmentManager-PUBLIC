using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BannerlordEnvironmentManager.Companion;

internal sealed class CapturedPatch
{
    public CapturedPatch(
        string targetType,
        string targetMethod,
        string kind,
        string moduleId,
        string owner,
        string attributedBy,
        string patchMethod)
    {
        TargetType = targetType;
        TargetMethod = targetMethod;
        Kind = kind;
        ModuleId = moduleId;
        Owner = owner;
        AttributedBy = attributedBy;
        PatchMethod = patchMethod;
    }

    public string TargetType { get; }

    public string TargetMethod { get; }

    public string Kind { get; }

    public string ModuleId { get; }

    public string Owner { get; }

    public string AttributedBy { get; }

    public string PatchMethod { get; }
}

internal sealed class PatchCaptureResult
{
    public PatchCaptureResult(IList<CapturedPatch> patches, int methodCount, int unattributed)
    {
        Patches = patches;
        MethodCount = methodCount;
        Unattributed = unattributed;
    }

    public static PatchCaptureResult Empty { get; } = new PatchCaptureResult(new List<CapturedPatch>(), 0, 0);

    public IList<CapturedPatch> Patches { get; }

    public int MethodCount { get; }

    public int Unattributed { get; }
}

// Asked of Harmony itself at the FIRST OnApplicationTick, not at OnBeforeInitialModuleScreenSetAsRoot.
// The companion loads second, so its screen callback fires at the START of that pass and anything
// patching later in the pass would be missed. By the first tick every module has finished every
// initialization callback.
//
// Attribution is by PatchMethod.DeclaringType.Assembly.Location mapped back to Modules\<id>\.
// Harmony's owner is an author-chosen string (com.rbmcombat, bannerlord.uiextender.ex.viewmodels.X)
// and frequently is not the module id, so it is used ONLY for a dynamic assembly with no location,
// and a patch attributed that way is labeled so the reader can weigh it accordingly.
internal static class PatchCapture
{
    private const string ByAssembly = "AssemblyLocation";

    private const string ByOwner = "HarmonyOwner";

    private const string Unattributed = "Unattributed";

    public static PatchCaptureResult Capture(ModuleMap map, Action<string> fail)
    {
        var patches = new List<CapturedPatch>();
        var methods = 0;
        var unattributed = 0;

        IEnumerable<MethodBase> patched;

        try
        {
            patched = Harmony.GetAllPatchedMethods();
        }
        catch (Exception ex)
        {
            fail("Harmony.GetAllPatchedMethods() failed, so no patch registry was captured: " + Describe(ex));
            return PatchCaptureResult.Empty;
        }

        foreach (var method in patched)
        {
            if (method == null)
                continue;

            methods++;

            try
            {
                var info = Harmony.GetPatchInfo(method);

                if (info == null)
                    continue;

                var type = SafeTypeName(method);
                var name = SafeMethodName(method);

                Add(patches, map, type, name, "Transpiler", info.Transpilers, ref unattributed);
                Add(patches, map, type, name, "Prefix", info.Prefixes, ref unattributed);
                Add(patches, map, type, name, "Finalizer", info.Finalizers, ref unattributed);
                Add(patches, map, type, name, "Postfix", info.Postfixes, ref unattributed);
            }
            catch (Exception ex)
            {
                fail("Reading the patches on " + SafeTypeName(method) + "." + SafeMethodName(method)
                    + " failed: " + Describe(ex));
            }
        }

        return new PatchCaptureResult(patches, methods, unattributed);
    }

    private static void Add(
        List<CapturedPatch> patches,
        ModuleMap map,
        string targetType,
        string targetMethod,
        string kind,
        IEnumerable<Patch>? group,
        ref int unattributed)
    {
        if (group == null)
            return;

        foreach (var patch in group)
        {
            if (patch == null)
                continue;

            var owner = patch.owner ?? string.Empty;
            var declaring = SafeDeclaringType(patch);
            var moduleId = declaring == null ? string.Empty : map.Resolve(declaring);
            var attributedBy = ByAssembly;

            if (moduleId.Length == 0)
            {
                // A Harmony replacement emitted into a dynamic assembly has no location to map, and
                // the owner string is all that is left. It is a hint, never a fact.
                moduleId = map.ResolveOwner(owner);
                attributedBy = moduleId.Length == 0 ? Unattributed : ByOwner;
            }

            if (moduleId.Length == 0)
                unattributed++;

            patches.Add(new CapturedPatch(
                targetType,
                targetMethod,
                kind,
                moduleId,
                owner,
                attributedBy,
                SafePatchMethodName(patch)));
        }
    }

    private static Type? SafeDeclaringType(Patch patch)
    {
        try
        {
            var method = patch.PatchMethod;

            if (method == null)
                return null;

            var declaring = method.DeclaringType;

            if (declaring == null)
                return null;

            // A dynamic assembly has no location at all, and asking for one throws on some runtimes.
            return declaring.Assembly != null && declaring.Assembly.IsDynamic ? null : declaring;
        }
        catch
        {
            return null;
        }
    }

    private static string SafePatchMethodName(Patch patch)
    {
        try
        {
            var method = patch.PatchMethod;

            if (method == null)
                return string.Empty;

            var declaring = method.DeclaringType;

            return declaring == null || declaring.FullName == null
                ? method.Name
                : declaring.FullName + "." + method.Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Written in the shape a crash frame carries, so a registry lookup off a stack trace matches
    // without either side normalizing: a constructor is Type..ctor, so the method name is ".ctor".
    private static string SafeTypeName(MethodBase method)
    {
        try
        {
            var declaring = method.DeclaringType;

            return declaring == null ? string.Empty : declaring.FullName ?? declaring.Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeMethodName(MethodBase method)
    {
        try
        {
            return method.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Describe(Exception ex) => ex.GetType().FullName + ": " + ex.Message;
}
