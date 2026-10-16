using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// One module's reference to a Harmony method that has to work out for itself which assembly called
// it. AssemblyPath is the file the reference was read from, so any row can be checked by hand.
public sealed record HarmonyCallSite(
    ModuleId ModuleId,
    string AssemblyPath,
    string MethodName,
    int ParameterCount)
{
    public string Signature => $"{HarmonyCallSites.HarmonyType}.{MethodName}({Placeholders()})";

    public string Describe() =>
        Strings.Current.Format("Core.Modules.Harmony.CallSites.CallSite", ModuleId, Signature, Path.GetFileName(AssemblyPath));

    private string Placeholders() => ParameterCount switch
    {
        0 => string.Empty,
        1 => "string",
        _ => string.Join(", ", Enumerable.Repeat("?", ParameterCount))
    };
}

public sealed record HarmonyCallSiteReport(
    int ModulesScanned,
    int AssembliesScanned,
    long ElapsedMilliseconds,
    IReadOnlyList<HarmonyCallSite> CallSites,
    IReadOnlyList<HarmonyPatchProblem> Problems,
    string Summary)
{
    public static HarmonyCallSiteReport CouldNotLook(string reason) => new(0, 0, 0, [], [], reason);

    // Whether BEM looked at all. "Found nothing" and "could not look" mean opposite things here: the
    // second one must never be read as a module being cleared.
    public bool Looked => AssembliesScanned > 0;

    public IReadOnlyList<ModuleId> Modules =>
    [
        .. CallSites
            .Select(site => site.ModuleId)
            .Distinct()
            .Order(Comparer<ModuleId>.Create((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Value, y.Value)))
    ];

    // Every module whose assemblies reference exactly this method at exactly this arity. The arity is
    // the whole point: PatchAll() and PatchAll(Assembly) share a name and only one of them reads the
    // call stack.
    public IReadOnlyList<ModuleId> Callers(string typeFullName, string methodName, int parameterCount)
    {
        if (!string.Equals(typeFullName, HarmonyCallSites.HarmonyType, StringComparison.Ordinal))
            return [];

        return
        [
            .. CallSites
                .Where(site => string.Equals(site.MethodName, methodName, StringComparison.Ordinal)
                    && site.ParameterCount == parameterCount)
                .Select(site => site.ModuleId)
                .Distinct()
                .Order(Comparer<ModuleId>.Create((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Value, y.Value)))
        ];
    }

    public IReadOnlyList<HarmonyCallSite> SitesOf(ModuleId module) =>
        [.. CallSites.Where(site => site.ModuleId == module)];
}

// Which modules call a Harmony method that resolves its own assembly from the call stack.
//
// Harmony's PatchAll(), PatchAllUncategorized() and PatchCategory(string) take no assembly. They work
// out which one to patch by walking back up the stack to their caller and reading that method's
// reflected type's assembly. That is a behavior which depends on the shape of the stack at the moment
// of the call, which the explicit overloads do not have, and it is worth knowing about a module
// whether or not anything has crashed: an inlined caller, a call made from a lambda or a delegate,
// or a frame the runtime declined to keep is enough to turn it into a null dereference thrown inside
// Harmony and attributed to Harmony.
//
// This reads assembly metadata only. No mod code is loaded and nothing is executed. A call to a
// method in another assembly is a MemberReference row whose signature blob carries the parameter
// count, so no decompiler is involved either. Verified on this install: 574 module assemblies read in
// about a quarter of a second.
//
// What it cannot see: a module that merges Harmony's own code into its assembly calls a method
// defined in the same file, which is not a MemberReference at all, and a call made through
// reflection names nothing. Both are misses rather than false accusations.
public static class HarmonyCallSites
{
    public const string HarmonyType = "HarmonyLib.Harmony";

    // Name and parameter count of every Harmony overload that resolves its assembly from the stack.
    private static readonly (string Name, int Parameters)[] StackResolving =
    [
        ("PatchAll", 0),
        ("PatchAllUncategorized", 0),
        ("PatchCategory", 1)
    ];

    private const int GenericCallingConvention = 0x10;

    // Whether this exact method at this exact arity is one of the overloads that has to work out its
    // own caller. PatchAll() does; PatchAll(Assembly) and PatchAll(Type) name the assembly outright
    // and behave the same whatever the stack looks like.
    public static bool ResolvesAssemblyFromStack(string typeFullName, string methodName, int parameterCount) =>
        string.Equals(typeFullName, HarmonyType, StringComparison.Ordinal)
        && StackResolving.Contains((methodName, parameterCount));

    // The same method by name alone, whatever its arity. This is what separates "the explicit overload
    // ran, so there is nothing here to narrow" from "this frame is not Harmony at all".
    public static bool IsPatchEntryPoint(string typeFullName, string methodName) =>
        string.Equals(typeFullName, HarmonyType, StringComparison.Ordinal)
        && StackResolving.Any(overload => string.Equals(overload.Name, methodName, StringComparison.Ordinal));

    public static HarmonyCallSiteReport Inspect(AssemblyIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (index.Failed)
        {
            return HarmonyCallSiteReport.CouldNotLook(
                Strings.Current.Format("Core.Modules.Harmony.CallSites.CouldNotRead", index.Error));
        }

        var timer = Stopwatch.StartNew();

        var scanned = index.Assemblies
            .Where(assembly => assembly.Origin == AssemblyOrigin.Module && assembly.ModuleId is not null)
            .ToList();

        var sites = new List<HarmonyCallSite>();
        var problems = new List<HarmonyPatchProblem>();

        foreach (var assembly in scanned)
        {
            try
            {
                sites.AddRange(Read(assembly));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new HarmonyPatchProblem(
                    assembly.ModuleId ?? ModuleId.None,
                    assembly.Path,
                    Strings.Current.Format("Core.Modules.Harmony.CallSites.AssemblyUnreadable", ex.Message)));
            }
        }

        timer.Stop();

        var ordered = sites
            .OrderBy(site => site.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(site => site.MethodName, StringComparer.Ordinal)
            .ToList();

        return new HarmonyCallSiteReport(
            scanned.Select(assembly => assembly.ModuleId).Distinct().Count(),
            scanned.Count,
            timer.ElapsedMilliseconds,
            ordered,
            problems,
            Summarize(ordered, scanned.Count));
    }

    private static string Summarize(IReadOnlyList<HarmonyCallSite> sites, int assemblies)
    {
        if (assemblies == 0)
            return Strings.Current["Core.Modules.Harmony.CallSites.NoAssembly"];

        var modules = sites.Select(site => site.ModuleId).Distinct().Count();

        return modules == 0
            ? Strings.Current.Plural("Core.Modules.Harmony.CallSites.NoneFound", assemblies)
            : Strings.Current.Plural("Core.Modules.Harmony.CallSites.Found", modules, assemblies);
    }

    private static IReadOnlyList<HarmonyCallSite> Read(IndexedAssembly assembly)
    {
        using var stream = File.OpenRead(assembly.Path);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return [];

        var reader = peReader.GetMetadataReader();
        var sites = new List<HarmonyCallSite>();

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);

            if (member.GetKind() is not MemberReferenceKind.Method
                || member.Parent.Kind is not HandleKind.TypeReference)
            {
                continue;
            }

            var name = reader.GetString(member.Name);

            if (!StackResolving.Any(overload => string.Equals(overload.Name, name, StringComparison.Ordinal)))
                continue;

            var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            var owner = reader.GetString(parent.Namespace) + "." + reader.GetString(parent.Name);

            if (!string.Equals(owner, HarmonyType, StringComparison.Ordinal))
                continue;

            var parameters = ParameterCount(reader, member);

            if (!StackResolving.Contains((name, parameters)))
                continue;

            sites.Add(new HarmonyCallSite(assembly.ModuleId!.Value, assembly.Path, name, parameters));
        }

        return
        [
            .. sites
                .DistinctBy(site => (site.ModuleId, site.AssemblyPath, site.MethodName, site.ParameterCount))
        ];
    }

    // A MethodRefSig per ECMA-335 II.23.2.2: one calling convention byte, a generic parameter count
    // when the GENERIC flag is set, and then the parameter count. Reading it directly avoids building
    // a signature type provider to answer a question about arity alone.
    private static int ParameterCount(MetadataReader reader, MemberReference member)
    {
        var blob = reader.GetBlobReader(member.Signature);
        var convention = blob.ReadByte();

        if ((convention & GenericCallingConvention) != 0)
            blob.ReadCompressedInteger();

        return blob.ReadCompressedInteger();
    }
}
