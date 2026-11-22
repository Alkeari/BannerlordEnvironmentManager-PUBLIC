using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum MissingApiKind
{
    // No assembly the game would load under the referenced name defines the type, and none forwards it.
    // The runtime throws TypeLoadException the first time a method touching it is compiled.
    TypeMissing,

    // The type resolves, and neither it nor any base it inherits from declares a member of that name.
    MemberMissing,

    // A member of that name exists, with a different signature. Binding is exact, so the call throws
    // MissingMethodException or MissingFieldException as surely as if the name were gone.
    SignatureChanged
}

// One reference, in one enabled module's assembly, to an API the selected install does not have. This is
// the "confirmed binary incompatibility" evidence level and no more: the reference is provably
// unresolvable, but whether the game ever runs the code holding it (at load, in a later feature, or never)
// is not something metadata can answer, so nothing here says it caused a particular crash.
public sealed record MissingApiReference(
    ModuleId ModuleId,
    string ModuleName,
    string AssemblyPath,
    string ReferencedAssembly,
    string TypeName,
    string? MemberName,
    string? Signature,
    MissingApiKind Kind,
    // Where the install has the type under another assembly, with nothing forwarding it there.
    string? DefinedElsewhere,
    // The signatures a member of that name does have, for SignatureChanged.
    IReadOnlyList<string> Available,
    // The methods of the referring assembly whose bodies use the reference. Empty means it is used only
    // in declarations (a base type, a field type, an attribute), which the runtime resolves when it loads
    // the declaring type.
    IReadOnlyList<string> UsedBy)
{
    public string Api => MemberName is null ? TypeName : $"{TypeName}.{MemberName}";

    public string Describe()
    {
        var file = Path.GetFileName(AssemblyPath);

        var head = Kind switch
        {
            MissingApiKind.TypeMissing => DefinedElsewhere is null
                ? Strings.Current.Format("Core.Modules.MissingApi.TypeMissing", file, TypeName, ReferencedAssembly)
                : Strings.Current.Format("Core.Modules.MissingApi.TypeMoved", file, TypeName, ReferencedAssembly, DefinedElsewhere),
            MissingApiKind.MemberMissing =>
                Strings.Current.Format("Core.Modules.MissingApi.MemberMissing", file, Signature ?? Api, TypeName),
            _ => Strings.Current.Format(
                "Core.Modules.MissingApi.SignatureChanged", file, Signature ?? Api, string.Join("; ", Available))
        };

        return UsedBy.Count switch
        {
            0 => head + Strings.Current["Core.Modules.MissingApi.UsedInDeclarations"],
            1 => head + Strings.Current.Format("Core.Modules.MissingApi.UsedByOne", UsedBy[0]),
            _ => head + Strings.Current.Plural("Core.Modules.MissingApi.UsedByMany", UsedBy.Count - 1, UsedBy[0])
        };
    }
}

// A reference to an assembly no enabled module and nothing in the game supplies. Kept apart from the
// findings on purpose: a mod commonly references an optional integration it only touches when that mod
// is present, so an absent assembly is an unresolved dependency, not proof of a missing API.
public sealed record UnresolvedAssemblyReference(
    ModuleId ModuleId,
    string ModuleName,
    string AssemblyPath,
    string ReferencedAssembly);

public sealed record MissingApiProblem(ModuleId ModuleId, string Path, string Reason);

public sealed record MissingApiReport(
    int ModulesScanned,
    int AssembliesScanned,
    int ReferencesChecked,
    IReadOnlyList<MissingApiReference> Findings,
    IReadOnlyList<UnresolvedAssemblyReference> Unresolved,
    IReadOnlyList<MissingApiProblem> Problems,
    string Summary)
{
    public static MissingApiReport CouldNotLook(string reason) => new(0, 0, 0, [], [], [], reason);

    // Types of enabled modules that leave a member of a game class or interface unimplemented, which the
    // runtime refuses to load.
    public IReadOnlyList<UnimplementedMember> Unimplemented { get; init; } = [];

    public IReadOnlyList<IGrouping<ModuleId, MissingApiReference>> ByModule =>
    [
        .. Findings
            .GroupBy(f => f.ModuleId)
            .OrderBy(g => g.First().ModuleName, StringComparer.OrdinalIgnoreCase)
    ];

    // The findings a crash's own message names. Matching one raises it to a crash suspect supported by
    // the log, never to the cause: the same API can be referenced by several enabled modules, and only a
    // managed stack or a controlled reproduction says which one the game was running.
    public IReadOnlyList<MissingApiReference> NamedBy(string crashText)
    {
        ArgumentNullException.ThrowIfNull(crashText);

        var types = TypeLoad.Matches(crashText).Select(m => Dotted(m.Groups["type"].Value)).ToHashSet(StringComparer.Ordinal);
        var members = MemberLoad.Matches(crashText).Select(m => Dotted(m.Groups["member"].Value)).ToList();

        return
        [
            .. Findings.Where(f =>
                (f.Kind == MissingApiKind.TypeMissing && types.Contains(Dotted(f.TypeName)))
                || (f.MemberName is { } member
                    && members.Any(text => text.Contains(Dotted(f.TypeName) + "." + member, StringComparison.Ordinal))))
        ];
    }

    // The runtime writes a nested type with "+" in one message and "/" or "." in another, so both sides
    // are compared with every separator as a dot.
    private static string Dotted(string name) => name.Replace('+', '.').Replace('/', '.').Replace("::", ".");

    private static readonly Regex TypeLoad = new(
        @"Could not load type '(?<type>[^']+)' from assembly",
        RegexOptions.CultureInvariant);

    private static readonly Regex MemberLoad = new(
        @"(?:Method|Field) not found: '?(?<member>[^'\r\n]+)",
        RegexOptions.CultureInvariant);
}

// Every type and member reference an enabled module's assemblies make into the game or into another
// enabled module, resolved against exactly the assemblies this install would load. Metadata only: no mod
// code is loaded or run.
public static class MissingGameApis
{
    private const string DefaultPlatformFolder = "Win64_Shipping_Client";

    private static readonly string[] FrameworkAssemblyNames =
    [
        "mscorlib",
        "netstandard",
        "System",
        "Microsoft",
        "Mono",
        "WindowsBase",
        "PresentationCore",
        "PresentationFramework",
        "Accessibility",
        "Facades"
    ];

    // System.Object's own members. A member referenced through a type whose chain ends at Object is
    // missing unless it is one of these; one ending at any other framework type cannot be judged, since
    // the framework's types are not indexed.
    private static readonly HashSet<string> ObjectMembers =
        new(["ToString", "Equals", "GetHashCode", "GetType", "Finalize", "MemberwiseClone", ".ctor", "ReferenceEquals"],
            StringComparer.Ordinal);

    public static MissingApiReport Inspect(
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        string? loadedPlatformFolder = null,
        ModuleVersion gameVersion = default,
        IReadOnlySet<ModuleId>? onlyModules = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        using var tagged = Diagnostics.UiStallMonitor.Op("Missing game API check");

        if (index.Failed)
            return MissingApiReport.CouldNotLook(Strings.Current.Format("Core.Modules.MissingApi.CouldNotLook", index.Error));

        var enabled = entries.Where(e => e.IsEnabled).Select(e => e.Id).ToHashSet();
        var names = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().DisplayName);
        var platform = loadedPlatformFolder ?? DefaultPlatformFolder;

        // Resolution is lenient and scanning is strict. Every enabled module's assembly counts as
        // loadable, wherever it sits, so a dependency a loader picks out of a versioned subfolder is not
        // mistaken for an absent one. Only the assemblies the engine itself opens are scanned, the files
        // directly in bin\<platform>, because a mod shipping one build per game version leaves the
        // builds for other versions beside them, and those never load.
        var loadable = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Game || (a.ModuleId is { } id && enabled.Contains(id)))
            .ToList();

        var byName = loadable.ToLookup(a => a.Name, StringComparer.OrdinalIgnoreCase);

        var scanned = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Module
                && a.ModuleId is { } id && enabled.Contains(id)
                && (onlyModules is null || onlyModules.Contains(id))
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(a.Path)), platform, StringComparison.OrdinalIgnoreCase))
            .ToList();

        scanned = LoadedForGame(scanned, gameVersion);

        var problems = new List<MissingApiProblem>();
        var references = new List<ReadReference>();
        var unresolved = new List<UnresolvedAssemblyReference>();

        // Each assembly is read on its own, so they are read side by side (half the processors, to leave
        // the machine usable while Launch waits) and merged in their original order.
        var reads = new (ReadAssembly? Read, Exception? Error)[scanned.Count];

        Parallel.For(
            0,
            scanned.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            i =>
            {
                try
                {
                    reads[i] = (ReadReferences(scanned[i]), null);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    reads[i] = (null, ex);
                }
            });

        for (var i = 0; i < scanned.Count; i++)
        {
            var assembly = scanned[i];
            var moduleId = assembly.ModuleId!.Value;
            var moduleName = names.TryGetValue(moduleId, out var name) ? name : moduleId.Value;

            try
            {
                if (reads[i].Error is { } error)
                    throw error;

                var read = reads[i].Read!;

                foreach (var missing in read.Assemblies.Where(n => !IsFramework(n) && !byName.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                    unresolved.Add(new UnresolvedAssemblyReference(moduleId, moduleName, assembly.Path, missing));

                references.AddRange(read.References
                    .Where(r => !IsFramework(r.Assembly) && byName.Contains(r.Assembly))
                    .Select(r => r with { ModuleId = moduleId, ModuleName = moduleName, AssemblyPath = assembly.Path }));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add(new MissingApiProblem(
                    moduleId, assembly.Path, Strings.Current.Format("Core.Modules.MissingApi.AssemblyUnreadable", ex.Message)));
            }
        }

        using var scope = new Scope(index, loadable, byName);
        var findings = new List<MissingApiReference>();

        foreach (var reference in references)
        {
            if (scope.Check(reference) is { } finding)
                findings.Add(finding);
        }

        var ordered = findings
            .OrderBy(f => f.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.AssemblyPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Api, StringComparer.Ordinal)
            .ThenBy(f => f.Signature, StringComparer.Ordinal)
            .ToList();

        var unimplemented = new List<UnimplementedMember>();

        using (var members = new UnimplementedMembers(index, scope.LoadablePaths))
        {
            foreach (var assembly in scanned)
            {
                var moduleId = assembly.ModuleId!.Value;

                unimplemented.AddRange(members.Find(
                    assembly, moduleId, names.TryGetValue(moduleId, out var moduleName) ? moduleName : moduleId.Value));
            }
        }

        return new MissingApiReport(
            scanned.Select(a => a.ModuleId).Distinct().Count(),
            scanned.Count,
            references.Count,
            ordered,
            unresolved,
            problems,
            Summarize(references.Count, scanned.Count, ordered))
        {
            Unimplemented = unimplemented
        };
    }

    // A mod shipping one build per game version (Bannerlord.ButterLib.Implementation.1.4.8.dll beside
    // ...1.4.7.dll and forty others) has only one of them loaded: the highest version not above the game's,
    // the convention GameVersionSupport parses. Judging every build put 270 rows on the owner's install
    // from DLLs built for other game versions while 86 of 86 watched boots succeeded. With no game version
    // known, nothing is ruled out, which is the old behavior and errs toward saying too much.
    internal static List<IndexedAssembly> LoadedForGame(List<IndexedAssembly> scanned, ModuleVersion gameVersion)
    {
        if (gameVersion.IsEmpty)
            return scanned;

        var kept = new List<IndexedAssembly>();

        var families = scanned
            .Select(a => (Assembly: a, Split: GameVersionSupport.Split(Path.GetFileName(a.Path))))
            .GroupBy(x => x.Split is { } split
                ? Path.GetDirectoryName(x.Assembly.Path) + "|" + split.Stem
                : "file:" + x.Assembly.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            var members = family.ToList();

            // A lone file with a version in its name is not a family and is loaded like any other.
            if (members.Count < 2 || members.Any(m => m.Split is null))
            {
                kept.AddRange(members.Select(m => m.Assembly));
                continue;
            }

            var pick = members
                .Where(m => m.Split!.Value.Version.CompareTo(gameVersion) <= 0)
                .OrderBy(m => m.Split!.Value.Version)
                .LastOrDefault();

            // Every build newer than the game: the loader has nothing to open here, which GameVersionSupport
            // reports as a gap, so none of them is judged as if it loaded.
            if (pick.Assembly is not null)
                kept.Add(pick.Assembly);
        }

        return kept;
    }

    private static string Summarize(int checkedCount, int assemblies, IReadOnlyList<MissingApiReference> findings)
    {
        if (assemblies == 0)
            return Strings.Current["Core.Modules.MissingApi.NoAssembly"];

        var head = Strings.Current.Plural("Core.Modules.MissingApi.Summary.Head", checkedCount, assemblies);

        return findings.Count == 0
            ? head + Strings.Current["Core.Modules.MissingApi.Summary.Clean"]
            : head + Strings.Current.Plural(
                "Core.Modules.MissingApi.Summary.Findings", findings.Count, findings.Select(f => f.ModuleId).Distinct().Count());
    }

    internal static bool IsFramework(string name) =>
        FrameworkAssemblyNames.Any(prefix =>
            name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    // What one assembly asks of the outside world, read once. A type reference is checked whether or not a
    // method body uses it, because a base type or a field type fails the declaring type's load just the
    // same; a member reference is checked only through a type the assembly references.
    private static readonly FileResultCache<ReadAssembly> ReadCache = new();

    private static ReadAssembly ReadReferences(IndexedAssembly assembly) =>
        ReadCache.GetOrRead(assembly.Path, () => ReadReferencesUncached(assembly));

    private static ReadAssembly ReadReferencesUncached(IndexedAssembly assembly)
    {
        using var stream = File.OpenRead(assembly.Path);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata)
            return new ReadAssembly([], []);

        var reader = pe.GetMetadataReader();
        var usedBy = UsedBy(pe, reader);
        var result = new List<ReadReference>();

        // A missing type is named by the methods that use it directly and by the methods that call or read
        // any member of it, since the member's row is folded into the type's.
        var throughMembers = new Dictionary<TypeReferenceHandle, List<string>>();

        foreach (var handle in reader.MemberReferences)
        {
            if (ParentType(reader, reader.GetMemberReference(handle).Parent) is not { } parent)
                continue;

            if (!throughMembers.TryGetValue(parent, out var list))
                throughMembers[parent] = list = [];

            list.AddRange(Uses(usedBy, handle));
        }

        foreach (var handle in reader.TypeReferences)
        {
            if (AssemblyOf(reader, handle) is not { } scope)
                continue;

            IReadOnlyList<string> uses = throughMembers.TryGetValue(handle, out var members)
                ? [.. Uses(usedBy, handle).Concat(members).Distinct(StringComparer.Ordinal)]
                : Uses(usedBy, handle);

            result.Add(new ReadReference(
                default, string.Empty, string.Empty, scope, TypeName(reader, handle), null, null, null, uses));
        }

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);

            if (ParentType(reader, member.Parent) is not { } parent || AssemblyOf(reader, parent) is not { } scope)
                continue;

            var memberName = reader.GetString(member.Name);
            string signature;
            bool isField;

            try
            {
                if (member.GetKind() == MemberReferenceKind.Field)
                {
                    signature = memberName + " : " + member.DecodeFieldSignature(SignatureNames.Instance, null);
                    isField = true;
                }
                else
                {
                    var decoded = member.DecodeMethodSignature(SignatureNames.Instance, null);

                    // A vararg call site carries its extra arguments in the reference, so it never matches
                    // the definition's signature and comparing them would accuse every one.
                    if (decoded.Header.CallingConvention == SignatureCallingConvention.VarArgs)
                        continue;

                    signature = SignatureNames.Method(memberName, decoded);
                    isField = false;
                }
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            result.Add(new ReadReference(
                default, string.Empty, string.Empty, scope, TypeName(reader, parent), memberName, signature, isField,
                Uses(usedBy, handle)));
        }

        var assemblies = reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();

        return new ReadAssembly(result, assemblies);
    }

    private static IReadOnlyList<string> Uses(IReadOnlyDictionary<EntityHandle, List<string>> usedBy, EntityHandle handle) =>
        usedBy.TryGetValue(handle, out var methods) ? methods : [];

    // The member's declaring type: a type reference directly, or the generic type a TypeSpec instantiates.
    // Arrays, pointers and generic parameters as parents are framework constructs and are not checked.
    private static TypeReferenceHandle? ParentType(MetadataReader reader, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference)
            return (TypeReferenceHandle)parent;

        if (parent.Kind != HandleKind.TypeSpecification)
            return null;

        var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);

        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return null;

        blob.ReadCompressedInteger();
        var generic = blob.ReadTypeHandle();

        return generic.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)generic : null;
    }

    // The assembly a type reference binds into, walking out of nested references. Null for a type in
    // the referring assembly's own modules, which needs no resolving.
    private static string? AssemblyOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;

        while (scope.Kind == HandleKind.TypeReference)
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;

        return scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);

        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? TypeName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name
            : Qualify(reader.GetString(reference.Namespace), name);
    }

    private static string TypeName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();

        return declaring.IsNil
            ? Qualify(reader.GetString(definition.Namespace), name)
            : TypeName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
    }

    private static string Qualify(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;

    // Which methods of the assembly touch each type and member reference, read from the IL. This is what
    // lets a row say where the reference lives, which is the first thing needed to judge whether a path
    // runs at load, in one feature, or only behind a check for another mod.
    private static IReadOnlyDictionary<EntityHandle, List<string>> UsedBy(PEReader pe, MetadataReader reader)
    {
        var result = new Dictionary<EntityHandle, List<string>>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = TypeName(reader, type);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);

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

                var where = typeName + "." + reader.GetString(method.Name);

                foreach (var token in Tokens(body.GetILReader()))
                {
                    var handle = Resolve(reader, token);

                    if (handle.IsNil)
                        continue;

                    if (!result.TryGetValue(handle, out var list))
                        result[handle] = list = [];

                    if (!list.Contains(where))
                        list.Add(where);
                }
            }
        }

        return result;
    }

    private static EntityHandle Resolve(MetadataReader reader, int token)
    {
        EntityHandle handle;

        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return default;
        }

        return handle.Kind switch
        {
            HandleKind.TypeReference or HandleKind.MemberReference => handle,
            HandleKind.MethodSpecification => reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method is
                { Kind: HandleKind.MemberReference } method ? method : default,
            _ => default
        };
    }

    // A reference as this file names it, for a reader that walks IL itself (StartupPath) and has to land
    // on exactly the finding Inspect reports: the same type name, member name and signature text. Null for
    // anything Inspect does not judge.
    internal static (string TypeName, string? MemberName, string? Signature)? Describe(MetadataReader reader, int token)
    {
        var handle = Resolve(reader, token);

        if (handle.IsNil)
            return null;

        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = (TypeReferenceHandle)handle;

            return AssemblyOf(reader, type) is null ? null : (TypeName(reader, type), null, null);
        }

        var member = reader.GetMemberReference((MemberReferenceHandle)handle);

        if (ParentType(reader, member.Parent) is not { } parent || AssemblyOf(reader, parent) is null)
            return null;

        var name = reader.GetString(member.Name);

        try
        {
            if (member.GetKind() == MemberReferenceKind.Field)
                return (TypeName(reader, parent), name, name + " : " + member.DecodeFieldSignature(SignatureNames.Instance, null));

            var decoded = member.DecodeMethodSignature(SignatureNames.Instance, null);

            return decoded.Header.CallingConvention == SignatureCallingConvention.VarArgs
                ? null
                : (TypeName(reader, parent), name, SignatureNames.Method(name, decoded));
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    internal static string NameOf(MetadataReader reader, TypeDefinition definition) => TypeName(reader, definition);

    internal static string NameOf(MetadataReader reader, TypeReferenceHandle handle) => TypeName(reader, handle);

    internal static string? AssemblyNameOf(MetadataReader reader, TypeReferenceHandle handle) => AssemblyOf(reader, handle);

    private static readonly IReadOnlyDictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .GroupBy(op => (ushort)op.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    private static IEnumerable<int> Tokens(BlobReader il)
    {
        var tokens = new List<int>();

        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();

            if (code == 0xFE && il.RemainingBytes > 0)
                code = (ushort)(0xFE00 | il.ReadByte());

            if (!OperandTypes.TryGetValue(code, out var operand))
                break;

            switch (operand)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                    tokens.Add(il.ReadInt32());
                    break;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    il.Offset += count * 4;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.ShortInlineR:
                    il.Offset += 4;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
            }
        }

        return tokens;
    }

    private sealed record ReadReference(
        ModuleId ModuleId,
        string ModuleName,
        string AssemblyPath,
        string Assembly,
        string TypeName,
        string? MemberName,
        string? Signature,
        bool? IsField,
        IReadOnlyList<string> UsedBy);

    private sealed record ReadAssembly(IReadOnlyList<ReadReference> References, IReadOnlyList<string> Assemblies);

    private sealed record InstalledType(
        string FullName,
        string? BaseTypeFullName,
        IReadOnlyDictionary<string, List<string>> Members);

    // Counts the installed assemblies a run opens, so a test can prove each is opened once.
    [ThreadStatic]
    public static int MetadataOpensOnThisThread;

    // The install as the game would see it, read lazily: a loadable assembly is opened only when a
    // reference names a type in it, and only the types asked for are read in full. Indexing every member
    // of every type on the install to answer the questions a few hundred mods ask would cost more memory
    // than the rest of the app.
    private sealed class Scope(
        AssemblyIndex index,
        IReadOnlyList<IndexedAssembly> loadable,
        ILookup<string, IndexedAssembly> byName) : IDisposable
    {
        private readonly HashSet<string> _loadablePaths = loadable.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> LoadablePaths => _loadablePaths;
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _forwarders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Path, string Type), InstalledType?> _types = [];

        public MissingApiReference? Check(ReadReference reference)
        {
            var declaring = DeclaringCached(reference.Assembly, reference.TypeName);

            if (declaring.Count == 0)
            {
                if (reference.MemberName is not null)
                    return null;

                var elsewhere = index.FindDeclaring(reference.TypeName)
                    .Where(a => _loadablePaths.Contains(a.Path))
                    .Select(a => a.Name)
                    .FirstOrDefault();

                return Finding(reference, MissingApiKind.TypeMissing, elsewhere, []);
            }

            // The type itself was reported by its own TypeReference row; the members are judged only on a
            // type that resolves, so one missing class is one finding rather than one per use.
            if (reference.MemberName is not { } member)
                return null;

            var available = new List<string>();

            foreach (var path in declaring)
            {
                switch (FindMember(path, reference.TypeName, member, reference.Signature!, available, depth: 0))
                {
                    case true:
                    case null:
                        return null;
                }
            }

            return Finding(
                reference,
                available.Count == 0 ? MissingApiKind.MemberMissing : MissingApiKind.SignatureChanged,
                null,
                [.. available.Distinct(StringComparer.Ordinal)]);
        }

        private static MissingApiReference Finding(
            ReadReference reference, MissingApiKind kind, string? elsewhere, IReadOnlyList<string> available) =>
            new(
                reference.ModuleId,
                reference.ModuleName,
                reference.AssemblyPath,
                reference.Assembly,
                reference.TypeName,
                reference.MemberName,
                reference.Signature,
                kind,
                elsewhere,
                available,
                reference.UsedBy);

        // Every loadable copy of the named assembly that defines the type, following type forwarders.
        // Checked against every copy rather than the one that binds: a copy that lacks the type while
        // another has it is a shadowing question the Overlaps page answers, and naming it here would
        // accuse a mod of what the load order decides.
        private readonly Dictionary<(string Assembly, string Type), List<string>> _declaring = [];

        private List<string> DeclaringCached(string assembly, string typeName)
        {
            var key = (assembly.ToLowerInvariant(), typeName);

            if (!_declaring.TryGetValue(key, out var list))
                _declaring[key] = list = Declaring(assembly, typeName, depth: 0);

            return list;
        }

        private List<string> Declaring(string assembly, string typeName, int depth)
        {
            var found = new List<string>();

            foreach (var copy in byName[assembly])
            {
                if (DefinedTypes(copy).Contains(typeName))
                    found.Add(copy.Path);
                else if (depth < 4 && Forwarders(copy.Path).TryGetValue(typeName, out var target))
                    found.AddRange(Declaring(target, typeName, depth + 1));
            }

            return found;
        }

        // True when the member exists on the type or a base, false when the chain was read to its end
        // without it, null when the chain leaves what BEM indexes before an answer, which is no answer.
        private bool? FindMember(string path, string typeName, string member, string signature, List<string> available, int depth)
        {
            if (depth > 8 || Type(path, typeName) is not { } type)
                return null;

            if (type.Members.TryGetValue(member, out var signatures))
            {
                if (signatures.Contains(signature, StringComparer.Ordinal))
                    return true;

                available.AddRange(signatures);
            }

            if (type.BaseTypeFullName is not { } baseName)
                return false;

            if (baseName == "System.Object")
                return ObjectMembers.Contains(member) ? null : false;

            var bases = index.FindDeclaring(baseName).Where(a => _loadablePaths.Contains(a.Path)).ToList();

            if (bases.Count == 0)
                return null;

            bool? verdict = false;

            foreach (var basePath in bases)
            {
                switch (FindMember(basePath.Path, baseName, member, signature, available, depth + 1))
                {
                    case true:
                        return true;
                    case null:
                        verdict = null;
                        break;
                }
            }

            return verdict;
        }

        private readonly Dictionary<string, HashSet<string>> _defined = new(StringComparer.OrdinalIgnoreCase);

        // The list on the index is searched once per reference otherwise, thousands of names deep for a game assembly.
        private HashSet<string> DefinedTypes(IndexedAssembly assembly)
        {
            if (!_defined.TryGetValue(assembly.Path, out var set))
                _defined[assembly.Path] = set = new HashSet<string>(assembly.DefinedTypeFullNames, StringComparer.Ordinal);

            return set;
        }

        private IReadOnlyDictionary<string, string> Forwarders(string path)
        {
            if (_forwarders.TryGetValue(path, out var cached))
                return cached;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);

                if (pe.HasMetadata)
                {
                    var reader = pe.GetMetadataReader();

                    foreach (var handle in reader.ExportedTypes)
                    {
                        var exported = reader.GetExportedType(handle);

                        if (!exported.IsForwarder || exported.Implementation.Kind != HandleKind.AssemblyReference)
                            continue;

                        var target = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
                        result[Qualify(reader.GetString(exported.Namespace), reader.GetString(exported.Name))] = target;
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
            }

            return _forwarders[path] = result;
        }

        // One open reader and one name table per assembly for the whole run. Reopening the file for every
        // type asked for re-read the assembly's whole metadata block into a fresh large byte array each
        // time, which filled the large object heap and made the collector stop the UI thread for seconds.
        private sealed class OpenAssembly(PEReader pe, MetadataReader reader, Dictionary<string, TypeDefinitionHandle> types) : IDisposable
        {
            public MetadataReader Reader { get; } = reader;
            public Dictionary<string, TypeDefinitionHandle> Types { get; } = types;
            public void Dispose() => pe.Dispose();
        }

        private readonly Dictionary<string, OpenAssembly?> _open = new(StringComparer.OrdinalIgnoreCase);

        private OpenAssembly? OpenOnce(string path)
        {
            if (_open.TryGetValue(path, out var cached))
                return cached;

            OpenAssembly? opened = null;
            MetadataOpensOnThisThread++;

            try
            {
                var pe = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchMetadata);

                if (pe.HasMetadata)
                {
                    var reader = pe.GetMetadataReader();
                    var table = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);

                    foreach (var handle in reader.TypeDefinitions)
                        table.TryAdd(TypeName(reader, reader.GetTypeDefinition(handle)), handle);

                    opened = new OpenAssembly(pe, reader, table);
                }
                else
                    pe.Dispose();
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
            }

            return _open[path] = opened;
        }

        public void Dispose()
        {
            foreach (var opened in _open.Values)
                opened?.Dispose();

            _open.Clear();
        }

        private InstalledType? Type(string path, string typeName)
        {
            if (_types.TryGetValue((path, typeName), out var cached))
                return cached;

            InstalledType? found = null;

            if (OpenOnce(path) is { } assembly && assembly.Types.TryGetValue(typeName, out var handle))
            {
                try
                {
                    found = Read(assembly.Reader, assembly.Reader.GetTypeDefinition(handle), typeName);
                }
                catch (BadImageFormatException)
                {
                }
            }

            return _types[(path, typeName)] = found;
        }

        private static InstalledType Read(MetadataReader reader, TypeDefinition definition, string typeName)
        {
            var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            void Add(string name, string signature)
            {
                if (!members.TryGetValue(name, out var list))
                    members[name] = list = [];

                list.Add(signature);
            }

            foreach (var handle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(handle);
                var name = reader.GetString(method.Name);

                try
                {
                    Add(name, SignatureNames.Method(name, method.DecodeSignature(SignatureNames.Instance, null)));
                }
                catch (BadImageFormatException)
                {
                }
            }

            foreach (var handle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(handle);
                var name = reader.GetString(field.Name);

                try
                {
                    Add(name, name + " : " + field.DecodeSignature(SignatureNames.Instance, null));
                }
                catch (BadImageFormatException)
                {
                }
            }

            var baseHandle = definition.BaseType;

            var baseName = baseHandle.Kind switch
            {
                HandleKind.TypeDefinition => TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)baseHandle)),
                HandleKind.TypeReference => TypeName(reader, (TypeReferenceHandle)baseHandle),
                HandleKind.TypeSpecification => GenericBase(reader, (TypeSpecificationHandle)baseHandle),
                _ => null
            };

            return new InstalledType(typeName, baseName, members);
        }

        private static string? GenericBase(MetadataReader reader, TypeSpecificationHandle handle)
        {
            var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);

            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return null;

            blob.ReadCompressedInteger();
            var generic = blob.ReadTypeHandle();

            return generic.Kind switch
            {
                HandleKind.TypeDefinition => TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)generic)),
                HandleKind.TypeReference => TypeName(reader, (TypeReferenceHandle)generic),
                _ => null
            };
        }
    }

    // Signatures as text, written the same way for a reference and for a definition so two of them match
    // exactly when the runtime would bind one to the other. Type names carry no assembly, which is what
    // lets a type the install forwards still match.
    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        public static SignatureNames Instance { get; } = new();

        public static string Method(string name, MethodSignature<string> signature)
        {
            var generic = signature.GenericParameterCount > 0 ? $"<{signature.GenericParameterCount}>" : string.Empty;
            var instance = signature.Header.IsInstance ? string.Empty : "static ";

            return $"{instance}{signature.ReturnType} {name}{generic}({string.Join(", ", signature.ParameterTypes)})";
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
        {
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => typeCode.ToString()
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(", ", typeArguments) + ">";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            "method " + Method("*", signature);

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
    }
}
