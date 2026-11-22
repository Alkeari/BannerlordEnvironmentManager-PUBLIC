using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

    // Harmony types whose Patch method applies one patch the mod chose by hand.
    // HarmonyExtensions is BUTR's HarmonyLib.BUTR.Extensions / PatchBuilder helper layer (harmony.Patch().Method(...)).
    private static readonly string[] ManualPatchOwners =
        [HarmonyType, "HarmonyLib.PatchProcessor", "HarmonyLib.PatchBuilder.HarmonyExtensions", "HarmonyLib.BUTR.Extensions.HarmonyExtensions"];

    // A [HarmonyPatch] class that computes its target in TargetMethod or TargetMethods patches whatever that body names.
    private static readonly string[] TargetComputers = ["TargetMethod", "TargetMethods"];

    // Which of the named types a mod patches by hand: a method of its assembly that calls Harmony's Patch
    // and, in the same body, names the type with typeof (a type token) or its full name as a string, which
    // is how AccessTools.Method(typeof(X), "Name") and AccessTools.TypeByName("Ns.X") both read. The attribute
    // route is HarmonyPatchTargets.DeclaredPatches; this is the other half, which PatchAll never sees.
    //
    // The two have to sit in one method body. A class that patches one thing and mentions another type for
    // an unrelated reason is not accused, and a patch whose target is built up in a helper is a miss.
    // patchedFromElsewhere: another assembly of the module runs PatchAll on this one while loading, so its
    // TargetMethod(s) bodies are asked for at load even though nothing in this file calls PatchAll.
    public static IReadOnlyList<string> ManualPatchTargets(IndexedAssembly assembly, IReadOnlySet<string> candidates, bool patchedFromElsewhere = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return [];

        using var stream = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return [];

        var reader = peReader.GetMetadataReader();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var scope = LoadTime(reader, peReader);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);

                // Only what runs while the module loads matters: a patch applied later (OnGameStart, a campaign
                // event) finds the text manager already set and is harmless.
                var isTargetComputer = (scope.PatchesAll || patchedFromElsewhere)
                    && TargetComputers.Contains(reader.GetString(method.Name), StringComparer.Ordinal);

                if (method.RelativeVirtualAddress == 0 || (!scope.Methods.Contains(methodHandle) && !isTargetComputer))
                    continue;

                MethodBodyBlock body;

                try
                {
                    body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                var instructions = IlInstructions.Read(body.GetILReader());

                // TargetMethod(s) is asked for by PatchAll, so it counts only when PatchAll runs at load too.
                var computesTarget = (scope.PatchesAll || patchedFromElsewhere)
                    && TargetComputers.Contains(reader.GetString(method.Name), StringComparer.Ordinal);

                if (!computesTarget && !instructions.Any(i => i.IsCall
                        && IlInstructions.Member(reader, i.Token) is { Member: "Patch" or "TryPatch" } call
                        && ManualPatchOwners.Contains(call.Type, StringComparer.Ordinal)))
                {
                    continue;
                }

                foreach (var instruction in instructions)
                {
                    var named = instruction.Code switch
                    {
                        IlInstruction.Ldtoken when IlInstructions.Handle(instruction.Token) is { } handle
                            => handle.Kind is HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.FieldDefinition
                                ? IlInstructions.Member(reader, instruction.Token)?.Type
                                : IlInstructions.TypeName(reader, handle),
                        IlInstruction.Ldstr
                            => TypeNameInString(reader.GetUserString(MetadataTokens.UserStringHandle(instruction.Token & 0x00FFFFFF))),
                        _ => null
                    };

                    if (named is not null && candidates.Contains(named))
                        found.Add(named);
                }
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    // What a module runs while it loads: every method of its own assembly reachable by direct calls from the
    // SubModule's OnSubModuleLoad, constructor and static constructor (a delegate or lambda is not followed,
    // since it runs when something later invokes it, so a patch applied there is a miss, never an accusation),
    // and whether that includes a PatchAll, which applies every [HarmonyPatch] class of the assembly.
    //
    // PatchAll(Assembly) may name another assembly of the same module, as BetterSmithingContinued does: its Main.dll
    // calls PatchAll on typeof(X).Assembly for each of its other DLLs, and its [HarmonyPatch] classes live in
    // MainFrame.dll. Which assembly the argument is cannot be followed through the stack, so the assemblies named
    // by a typeof (ldtoken) in the load-time methods stand in for it; the caller keeps only those that are files
    // of the same module, so a typeof of a game or library type never draws anything in.
    internal sealed record LoadTimeScope(HashSet<MethodDefinitionHandle> Methods, bool PatchesAll, IReadOnlySet<string> OtherAssemblies);

    // The roots Bannerlord calls before any Game exists, so before Game.Initialize hands GameTexts its manager:
    // the SubModule constructors (Module.LoadSubModules), OnSubModuleLoad, and OnBeforeInitialModuleScreenSetAsRoot,
    // which Module.SetInitialModuleScreenAsRootScreen calls each time the main menu becomes the root screen, the
    // first time at startup with no game created, and later after Campaign has called GameTexts.ClearInstance.
    private static readonly string[] LoadRoots = ["OnSubModuleLoad", "OnBeforeInitialModuleScreenSetAsRoot", ".ctor", ".cctor"];

    private const string SubModuleBase = "TaleWorlds.MountAndBlade.MBSubModuleBase";

    internal static LoadTimeScope LoadTime(MetadataReader reader, PEReader pe)
    {
        var reached = new HashSet<MethodDefinitionHandle>();
        var queue = new Queue<MethodDefinitionHandle>();
        var patchesAll = false;
        var patchesOther = false;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var methods = type.GetMethods().ToList();
            var baseName = type.BaseType.IsNil ? null : IlInstructions.TypeName(reader, type.BaseType);
            var isSubModule = baseName == SubModuleBase
                || methods.Any(m => reader.GetString(reader.GetMethodDefinition(m).Name) is "OnSubModuleLoad" or "OnBeforeInitialModuleScreenSetAsRoot");

            if (!isSubModule)
                continue;

            foreach (var m in methods)
            {
                if (LoadRoots.Contains(reader.GetString(reader.GetMethodDefinition(m).Name), StringComparer.Ordinal)
                    && reached.Add(m))
                {
                    queue.Enqueue(m);
                }
            }
        }

        while (queue.Count > 0)
        {
            var method = reader.GetMethodDefinition(queue.Dequeue());

            if (method.RelativeVirtualAddress == 0)
                continue;

            MethodBodyBlock body;

            try
            {
                body = pe.GetMethodBody(method.RelativeVirtualAddress);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            // Only a typeof seen earlier in the very method that calls PatchAll(Assembly) can be its argument: a
            // typeof in another method, or after the call (UIExtender.Register(typeof(X).Assembly)), is unrelated.
            var seenBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instruction in IlInstructions.Read(body.GetILReader()))
            {
                if (instruction.Code == IlInstruction.Ldtoken
                    && IlInstructions.Handle(instruction.Token) is { Kind: HandleKind.TypeReference } typeToken
                    && AssemblyOf(reader, (TypeReferenceHandle)typeToken) is { } assemblyName)
                {
                    seenBefore.Add(assemblyName);
                }

                if (!instruction.IsCall || IlInstructions.Handle(instruction.Token) is not { } handle)
                    continue;

                if (handle.Kind == HandleKind.MethodSpecification)
                    handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    var target = (MethodDefinitionHandle)handle;

                    if (reached.Add(target))
                        queue.Enqueue(target);
                }
                else if (IlInstructions.Member(reader, instruction.Token) is { Member: "PatchAll" or "PatchAllUncategorized" } call
                    && call.Type == HarmonyType)
                {
                    patchesAll = true;

                    if (handle.Kind == HandleKind.MemberReference
                        && ParameterCount(reader, reader.GetMemberReference((MemberReferenceHandle)handle)) > 0)
                    {
                        patchesOther = true;
                        named.UnionWith(seenBefore);
                    }
                }
            }
        }

        return new LoadTimeScope(reached, patchesAll, patchesOther ? named : new HashSet<string>());
    }

    // The assembly a type reference resolves to, following nested types out to their outermost declaring reference.
    private static string? AssemblyOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            var scope = reader.GetTypeReference(handle).ResolutionScope;

            if (scope.Kind == HandleKind.AssemblyReference)
                return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);

            if (scope.Kind != HandleKind.TypeReference)
                return null;

            handle = (TypeReferenceHandle)scope;
        }

        return null;
    }

    // The other assemblies a module's load-time PatchAll(Assembly) may be given, by simple name. Empty when it
    // calls only PatchAll().
    public static IReadOnlySet<string> PatchesAllOfOthersAtLoad(IndexedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);

        return peReader.HasMetadata ? LoadTime(peReader.GetMetadataReader(), peReader).OtherAssemblies : new HashSet<string>();
    }

    // Whether the module calls Harmony's PatchAll while it loads, so its [HarmonyPatch] classes are applied
    // before the game has set up its text table.
    public static bool PatchesAllAtLoad(IndexedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);

        return peReader.HasMetadata && LoadTime(peReader.GetMetadataReader(), peReader).PatchesAll;
    }

    // "Ns.Type" or "Ns.Type, Assembly" as AccessTools.TypeByName takes it.
    private static string TypeNameInString(string text)
    {
        var comma = text.IndexOf(',', StringComparison.Ordinal);

        return (comma < 0 ? text : text[..comma]).Trim().Replace('/', '+');
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
