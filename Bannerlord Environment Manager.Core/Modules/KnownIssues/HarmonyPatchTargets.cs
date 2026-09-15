using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum HarmonyTargetVerdict
{
    Resolved,

    // Not checked rather than cleared. The runtime's own assemblies live under bin\mono, deeper than
    // anything BEM indexes, so a patch on a framework type has no evidence either way.
    NotChecked,

    // The class computes its own targets, so the answer only exists while the game is running.
    Computed,

    AssemblyNotInstalled,
    AssemblyNotEnabled,
    TypeMissing,
    MemberMissing,
    DeclaredOnBaseOnly,
    NoTargetNamed,

    // The target method exists, and the patch declares an argument it does not have. Harmony binds
    // those by name and throws the moment one cannot be matched, which aborts the module's entire
    // PatchAll rather than skipping the single patch.
    PatchParameterMissing
}

public sealed record HarmonyPatchTarget(
    ModuleId ModuleId,
    string ModuleName,
    string AssemblyPath,
    string PatchClass,
    string PatchMethod,
    string? TargetType,
    string? TargetAssembly,
    string? TargetMember,
    string MethodKind,
    HarmonyTargetVerdict Verdict,
    string Why,
    bool GuardedByPrepare,
    string? Category)
{
    public bool IsFinding => Verdict
        is HarmonyTargetVerdict.AssemblyNotInstalled
        or HarmonyTargetVerdict.AssemblyNotEnabled
        or HarmonyTargetVerdict.TypeMissing
        or HarmonyTargetVerdict.MemberMissing
        or HarmonyTargetVerdict.DeclaredOnBaseOnly
        or HarmonyTargetVerdict.NoTargetNamed
        or HarmonyTargetVerdict.PatchParameterMissing;

    public string Target => TargetType is null
        ? TargetMember ?? "(no target named)"
        : $"{TargetType}.{TargetMember ?? MethodKind}";

    public string Describe() =>
        Strings.Current.Format("Core.Modules.Harmony.Targets.Row", ModuleName, PatchClass, PatchMethod, Target, Why);
}

public sealed record HarmonyPatchProblem(ModuleId ModuleId, string Path, string Reason);

public sealed record HarmonyPatchTargetReport(
    int ModulesScanned,
    int AssembliesScanned,
    int PatchClasses,
    int TargetsRead,
    IReadOnlyList<HarmonyPatchTarget> Findings,
    IReadOnlyList<HarmonyPatchTarget> Undecidable,
    IReadOnlyList<HarmonyPatchProblem> Problems,
    string Summary)
{
    public static HarmonyPatchTargetReport CouldNotLook(string reason) =>
        new(0, 0, 0, 0, [], [], [], reason);

    public int Resolved => TargetsRead - Findings.Count - Undecidable.Count;

    // Everything this sweep did not answer, in one sentence, so a clean findings list never reads as
    // a clean install. An assembly whose body is encrypted yields no targets at all, so without this
    // the module simply vanishes from the check rather than being reported as uninspectable.
    public string DescribeUnchecked()
    {
        var parts = new List<string>();

        if (Undecidable.Count > 0)
        {
            var computed = Undecidable.Count(t => t.Verdict == HarmonyTargetVerdict.Computed);

            parts.Add(Strings.Current.Plural("Core.Modules.Harmony.Targets.Undecidable", TargetsRead, Undecidable.Count, computed));
        }

        if (Problems.Count > 0)
        {
            var modules = Problems.Select(p => p.ModuleId.Value).Distinct(StringComparer.OrdinalIgnoreCase);

            parts.Add(Strings.Current.Plural("Core.Modules.Harmony.Targets.Problems", Problems.Count, string.Join(", ", modules)));
        }

        return string.Join(" ", parts);
    }
}

// The target half of a Harmony patch, read from metadata and checked against what is on disk.
//
// Harmony 2.4.2's PatchClassProcessor.PatchWithAttributes throws
// ArgumentException("Undefined target method for patch method ...") the moment
// PatchTools.GetOriginalMethod returns null, and GetOriginalMethod reaches for
// AccessTools.DeclaredMethod, which passes BindingFlags.DeclaredOnly. So a patch naming a type that
// is not loaded, or a method that moved to a base class or changed name between game versions, stops
// PatchAll dead. That is a static question about static data, and this answers it without loading a
// single line of mod code.
//
// What it deliberately does not do: it never compares argument types. Overload resolution across
// generics and by-ref parameters is where a static reader earns false accusations, and a wrong name
// here costs more than a missing row. A patch whose name resolves is reported as resolved even if
// Harmony would go on to fail on its signature.
//
// Disproof condition: open the assembly named in a row and find the member. Every finding names the
// file it read, the type it looked in, and the member it did not find, so any row can be checked by
// hand in one step.
public static class HarmonyPatchTargets
{
    private const string PatchAttribute = "HarmonyLib.HarmonyPatch";

    private const string PatchAllAttribute = "HarmonyLib.HarmonyPatchAll";

    private const string CategoryAttribute = "HarmonyLib.HarmonyPatchCategory";

    private const string HarmonyNamespace = "HarmonyLib";

    private static readonly string[] PatchMethodNames =
        ["Prefix", "Postfix", "Transpiler", "Finalizer", "ReversePatch", "InnerPrefix", "InnerPostfix"];

    private static readonly string[] PatchMethodAttributes =
    [
        "HarmonyLib.HarmonyPrefix",
        "HarmonyLib.HarmonyPostfix",
        "HarmonyLib.HarmonyTranspiler",
        "HarmonyLib.HarmonyFinalizer",
        "HarmonyLib.HarmonyReversePatch",
        "HarmonyLib.HarmonyInnerPrefix",
        "HarmonyLib.HarmonyInnerPostfix"
    ];

    private static readonly string[] ComputedTargetNames = ["TargetMethod", "TargetMethods"];

    private static readonly string[] ComputedTargetAttributes =
        ["HarmonyLib.HarmonyTargetMethod", "HarmonyLib.HarmonyTargetMethods"];

    // The game runs on mono, whose class library sits in bin\Win64_Shipping_Client\mono, several
    // folders below anything the assembly index reads. A patch on a framework type therefore has no
    // evidence on either side and is counted as not checked rather than reported.
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

    public static HarmonyPatchTargetReport Inspect(AssemblyIndex index, IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
        {
            return HarmonyPatchTargetReport.CouldNotLook(
                Strings.Current.Format("Core.Modules.Harmony.Targets.CouldNotLook", index.Error));
        }

        var enabled = entries.Where(e => e.IsEnabled).Select(e => e.Id).ToHashSet();
        var names = entries
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        var scanned = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is { } id && enabled.Contains(id))
            .ToList();

        var declarations = new List<PatchDeclaration>();
        var problems = new List<HarmonyPatchProblem>();

        foreach (var assembly in scanned)
        {
            try
            {
                declarations.AddRange(Read(assembly));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new HarmonyPatchProblem(
                    assembly.ModuleId ?? ModuleId.None,
                    assembly.Path,
                    Strings.Current.Format("Core.Modules.Harmony.Targets.AssemblyUnreadable", ex.Message)));
            }
        }

        var scope = BuildScope(index, enabled, names, declarations);

        var resolved = Shadowed(declarations.Select(declaration => Resolve(declaration, scope, names)));

        var findings = resolved
            .Where(t => t.IsFinding)
            .OrderBy(t => t.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.PatchClass, StringComparer.Ordinal)
            .ToList();

        var undecidable = resolved
            .Where(t => t.Verdict is HarmonyTargetVerdict.Computed or HarmonyTargetVerdict.NotChecked)
            .OrderBy(t => t.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.PatchClass, StringComparer.Ordinal)
            .ToList();

        return new HarmonyPatchTargetReport(
            scanned.Select(a => a.ModuleId).Distinct().Count(),
            scanned.Count,
            declarations.Select(d => (d.AssemblyPath, d.PatchClass)).Distinct().Count(),
            resolved.Count,
            findings,
            undecidable,
            problems,
            Summarize(resolved.Count, findings, undecidable.Count, scanned.Count));
    }

    // A mod that ships one assembly per game version puts the same patch class in every build, and a
    // loader picks the one build that matches. Accusing the build for last year's game of naming a
    // method that game no longer has is true and useless: the file never loads. So where one build of
    // a module resolves a patch class's method, the copies in its siblings are reported as unchecked.
    private static IReadOnlyList<HarmonyPatchTarget> Shadowed(IEnumerable<HarmonyPatchTarget> targets)
    {
        var all = targets.ToList();

        // A build is only treated as the dormant one when the sibling copy of the same patch class
        // has nothing wrong with it at all. One clean copy of a class beside a broken one is the
        // loader's choice showing through; two broken copies are a mod with no build for this game.
        var clean = all
            .GroupBy(t => (t.ModuleId, t.PatchClass, t.AssemblyPath))
            .Where(g => g.All(t => !t.IsFinding))
            .Select(g => (g.Key.ModuleId, g.Key.PatchClass, g.Key.AssemblyPath))
            .ToList();

        return
        [
            .. all.Select(target =>
                target.IsFinding
                && clean.Any(other => other.ModuleId == target.ModuleId
                    && other.PatchClass == target.PatchClass
                    && !string.Equals(other.AssemblyPath, target.AssemblyPath, StringComparison.OrdinalIgnoreCase))
                    ? target with
                    {
                        Verdict = HarmonyTargetVerdict.NotChecked,
                        Why = Strings.Current.Format(
                            "Core.Modules.Harmony.Targets.Shadowed", target.PatchClass, Path.GetFileName(target.AssemblyPath))
                    }
                    : target)
        ];
    }

    private static string Summarize(
        int read,
        IReadOnlyList<HarmonyPatchTarget> findings,
        int undecidable,
        int assemblies)
    {
        if (read == 0)
        {
            return assemblies == 0
                ? Strings.Current["Core.Modules.Harmony.Targets.NoAssembly"]
                : Strings.Current.Format("Core.Modules.Harmony.Targets.NoPatchesDeclared", assemblies);
        }

        var head = Strings.Current.Plural("Core.Modules.Harmony.Targets.Summary.Head", read, assemblies, undecidable);

        return findings.Count == 0
            ? head + Strings.Current["Core.Modules.Harmony.Targets.Summary.Clean"]
            : head + Strings.Current.Plural("Core.Modules.Harmony.Targets.Summary.Findings", findings.Count);
    }

    // The scope is built from the target names the patches actually ask for, not from every type on
    // the install. Indexing the members of half a million types to answer a few hundred questions
    // would cost more memory than the whole app uses.
    private static InstalledScope BuildScope(
        AssemblyIndex index,
        HashSet<ModuleId> enabled,
        IReadOnlyDictionary<ModuleId, string> names,
        IReadOnlyList<PatchDeclaration> declarations)
    {
        var loadable = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Game
                || (a.ModuleId is { } id && enabled.Contains(id)))
            .ToList();

        var loadableNames = loadable
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dormant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in index.Assemblies)
        {
            if (loadableNames.Contains(assembly.Name) || dormant.ContainsKey(assembly.Name))
                continue;

            dormant[assembly.Name] = assembly.ModuleId is { } id && names.TryGetValue(id, out var name)
                ? name
                : Path.GetFileName(Path.GetDirectoryName(assembly.Path)) ?? assembly.Path;
        }

        var byPath = loadable.ToLookup(a => a.Path, StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, List<InstalledType>>(StringComparer.Ordinal);

        var pending = declarations
            .Select(d => d.TargetType)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var aliases = ResolveByName(index, loadable, declarations);

        foreach (var full in aliases.Values.SelectMany(names => names))
            pending.Add(full);

        // A member declared on a base class is still a failure, because Harmony asks with
        // DeclaredOnly. The chain is walked anyway so the row can say which it is, and eight rounds
        // is deeper than any hierarchy the game or a mod has.
        for (var round = 0; round < 8 && pending.Count > 0; round++)
        {
            var wanted = pending
                .SelectMany(name => index.FindDeclaring(name)
                    .Where(a => byPath.Contains(a.Path))
                    .Select(a => (a.Path, Name: name)))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            pending = [];

            foreach (var group in wanted)
            {
                var assembly = byPath[group.Key].First();
                var found = ReadTypes(assembly, group.Select(x => x.Name).ToHashSet(StringComparer.Ordinal));

                foreach (var type in found)
                {
                    if (!types.TryGetValue(type.FullName, out var list))
                        types[type.FullName] = list = [];

                    list.Add(type);

                    if (type.BaseTypeFullName is { } baseName && !types.ContainsKey(baseName))
                        pending.Add(baseName);
                }
            }
        }

        return new InstalledScope(
            types.ToDictionary(p => p.Key, p => (IReadOnlyList<InstalledType>)p.Value, StringComparer.Ordinal),
            aliases,
            loadableNames,
            dormant);
    }

    // AccessTools.TypeByName ends by matching on Type.Name alone, so [HarmonyPatch("Foo", "Bar")]
    // finds Some.Namespace.Foo even though the attribute never says where it lives. Skipping this
    // fallback is what turns a working patch into an accusation, so the same last resort is taken
    // here and only over what the game would actually have loaded.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveByName(
        AssemblyIndex index,
        IReadOnlyList<IndexedAssembly> loadable,
        IReadOnlyList<PatchDeclaration> declarations)
    {
        var unqualified = declarations
            .Where(d => d.TargetByName && d.TargetType is { } name && index.FindDeclaring(name).Count == 0)
            .Select(d => d.TargetType!)
            .ToHashSet(StringComparer.Ordinal);

        if (unqualified.Count == 0)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var found = unqualified.ToDictionary(
            name => name,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var assembly in loadable)
        {
            foreach (var full in assembly.DefinedTypeFullNames)
            {
                if (found.TryGetValue(SimpleName(full), out var matches))
                    matches.Add(full);
            }
        }

        return found
            .Where(p => p.Value.Count > 0)
            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }

    private static string SimpleName(string fullName)
    {
        var cut = Math.Max(
            fullName.LastIndexOf('.'),
            fullName.LastIndexOf('+'));

        return cut < 0 ? fullName : fullName[(cut + 1)..];
    }

    private static IReadOnlyList<InstalledType> ReadTypes(IndexedAssembly assembly, HashSet<string> wanted)
    {
        var found = new List<InstalledType>();

        try
        {
            using var stream = File.OpenRead(assembly.Path);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
                return found;

            var reader = peReader.GetMetadataReader();

            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var fullName = FullName(reader, definition);

                if (!wanted.Contains(fullName))
                    continue;

                found.Add(new InstalledType(
                    fullName,
                    assembly.Path,
                    BaseTypeName(reader, definition),
                    definition.GetMethods()
                        .Select(m => reader.GetString(reader.GetMethodDefinition(m).Name))
                        .ToHashSet(StringComparer.Ordinal),
                    definition.GetProperties()
                        .Select(p => reader.GetString(reader.GetPropertyDefinition(p).Name))
                        .ToHashSet(StringComparer.Ordinal),
                    definition.GetEvents()
                        .Select(e => reader.GetString(reader.GetEventDefinition(e).Name))
                        .ToHashSet(StringComparer.Ordinal),
                    definition.GetMethods()
                        .Select(reader.GetMethodDefinition)
                        .GroupBy(m => reader.GetString(m.Name), StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => (IReadOnlySet<string>)group
                                .SelectMany(m => m.GetParameters()
                                    .Select(reader.GetParameter)
                                    .Where(p => p.SequenceNumber > 0)
                                    .Select(p => reader.GetString(p.Name)))
                                .ToHashSet(StringComparer.Ordinal),
                            StringComparer.Ordinal)));
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    private static string? BaseTypeName(MetadataReader reader, TypeDefinition definition)
    {
        var handle = definition.BaseType;

        // A generic base arrives as a TypeSpecification whose name only exists inside a signature
        // blob. Leaving it null ends the walk one class early, which can only turn a
        // "declared on a base" row into a "not found at all" row: both are failures, so no row is
        // invented by stopping here.
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
            _ => null
        };
    }

    private static HarmonyPatchTarget Resolve(
        PatchDeclaration declaration,
        InstalledScope scope,
        IReadOnlyDictionary<ModuleId, string> names)
    {
        var moduleName = names.TryGetValue(declaration.ModuleId, out var name) ? name : declaration.ModuleId.Value;

        HarmonyPatchTarget Verdict(HarmonyTargetVerdict verdict, string why) => new(
            declaration.ModuleId,
            moduleName,
            declaration.AssemblyPath,
            declaration.PatchClass,
            declaration.PatchMethod,
            declaration.TargetType,
            declaration.TargetAssembly,
            declaration.TargetMember,
            declaration.MethodKind.ToString(),
            verdict,
            Qualify(why, declaration),
            declaration.GuardedByPrepare,
            declaration.Category);

        if (declaration.ComputesTargets)
        {
            return Verdict(
                HarmonyTargetVerdict.Computed,
                Strings.Current["Core.Modules.Harmony.Why.Computed"]);
        }

        if (declaration.TargetType is null)
        {
            return Verdict(
                HarmonyTargetVerdict.NoTargetNamed,
                Strings.Current["Core.Modules.Harmony.Why.NoTargetNamed"]);
        }

        if (declaration.TargetAssembly is { } assemblyName && IsFramework(assemblyName))
        {
            return Verdict(
                HarmonyTargetVerdict.NotChecked,
                Strings.Current.Format("Core.Modules.Harmony.Why.Framework", declaration.TargetType, assemblyName));
        }

        var candidates = Candidates(declaration, scope);

        if (candidates.Count == 0)
        {
            if (declaration.TargetAssembly is { } missing && !scope.LoadableAssemblies.Contains(missing))
            {
                return scope.DormantAssemblies.TryGetValue(missing, out var owner)
                    ? Verdict(
                        HarmonyTargetVerdict.AssemblyNotEnabled,
                        Strings.Current.Format("Core.Modules.Harmony.Why.AssemblyNotEnabled", missing, owner, declaration.TargetType))
                    : Verdict(
                        HarmonyTargetVerdict.AssemblyNotInstalled,
                        Strings.Current.Format("Core.Modules.Harmony.Why.AssemblyNotInstalled", missing, declaration.TargetType));
            }

            // A typeof() naming a corlib type is written into the blob with no assembly on it at all,
            // per ECMA-335, so the check above never sees one. Without this a patch on a framework
            // type would be reported as missing purely because the runtime lives out of reach.
            if (IsFramework(declaration.TargetType))
            {
                return Verdict(
                    HarmonyTargetVerdict.NotChecked,
                    Strings.Current.Format("Core.Modules.Harmony.Why.FrameworkType", declaration.TargetType));
            }

            // Harmony has to build the attribute before it can read a target out of it, so a type
            // that is not loaded fails this class earlier than a missing method does.
            return Verdict(
                HarmonyTargetVerdict.TypeMissing,
                Strings.Current.Format("Core.Modules.Harmony.Why.TypeMissing", declaration.TargetType));
        }

        if (declaration.MethodKind is HarmonyMethodKind.Operator)
        {
            return Verdict(
                HarmonyTargetVerdict.NotChecked,
                Strings.Current["Core.Modules.Harmony.Why.Operator"]);
        }

        // AccessTools.DeclaredIndexerGetter matches on the argument types rather than a name, and
        // comparing argument types statically is the one thing this check refuses to do.
        if (declaration.MethodKind is HarmonyMethodKind.Getter or HarmonyMethodKind.Setter
            && declaration.TargetMember is null)
        {
            return Verdict(
                HarmonyTargetVerdict.NotChecked,
                Strings.Current["Core.Modules.Harmony.Why.Indexer"]);
        }

        var member = MemberName(declaration);

        if (member is null)
        {
            return Verdict(
                HarmonyTargetVerdict.NoTargetNamed,
                Strings.Current.Format("Core.Modules.Harmony.Why.NoMemberName", declaration.MethodKind));
        }

        if (candidates.FirstOrDefault(type => Declares(type, declaration.MethodKind, member)) is { } declaring)
        {
            return UnbindableParameters(declaration, declaring, member) is { Count: > 0 } unbindable
                ? Verdict(
                    HarmonyTargetVerdict.PatchParameterMissing,
                    Strings.Current.Plural(
                        "Core.Modules.Harmony.Why.ParameterMissing",
                        unbindable.Count, member, string.Join(", ", unbindable)))
                : Verdict(HarmonyTargetVerdict.Resolved, Strings.Current["Core.Modules.Harmony.Why.Resolved"]);
        }

        var host = candidates[0];

        foreach (var basis in Ancestors(candidates, scope))
        {
            if (Declares(basis, declaration.MethodKind, member))
            {
                return Verdict(
                    HarmonyTargetVerdict.DeclaredOnBaseOnly,
                    Strings.Current.Format("Core.Modules.Harmony.Why.DeclaredOnBase", member, basis.FullName, declaration.TargetType));
            }
        }

        return Verdict(
            HarmonyTargetVerdict.MemberMissing,
            Strings.Current.Format("Core.Modules.Harmony.Why.MemberMissing", host.FullName, member, host.Path));
    }

    // A patch class in a Harmony category only runs when the mod asks for that category by name, so
    // the row says so rather than asserting the patch runs.
    private static string Qualify(string why, PatchDeclaration declaration)
    {
        if (declaration.Category is { } category)
            why += Strings.Current.Format("Core.Modules.Harmony.Why.InCategory", category);

        if (declaration.GuardedByPrepare)
            why += Strings.Current["Core.Modules.Harmony.Why.GuardedByPrepare"];

        return why;
    }

    private static IReadOnlyList<InstalledType> Candidates(PatchDeclaration declaration, InstalledScope scope)
    {
        if (declaration.TargetType is not { } wanted)
            return [];

        if (scope.Types.TryGetValue(wanted, out var exact) && exact.Count > 0)
            return exact;

        if (!declaration.TargetByName || !scope.NameAliases.TryGetValue(wanted, out var aliases))
            return [];

        return [.. aliases.SelectMany(alias =>
            scope.Types.TryGetValue(alias, out var found) ? found : [])];
    }

    private static IEnumerable<InstalledType> Ancestors(IReadOnlyList<InstalledType> candidates, InstalledScope scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(candidates.Select(c => c.BaseTypeFullName).OfType<string>());

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();

            if (!seen.Add(name) || !scope.Types.TryGetValue(name, out var types))
                continue;

            foreach (var type in types)
            {
                yield return type;

                if (type.BaseTypeFullName is { } next)
                    queue.Enqueue(next);
            }
        }
    }

    // What PatchTools.GetOriginalMethod actually asks the reflection layer for, per MethodType.
    private static string? MemberName(PatchDeclaration declaration) => declaration.MethodKind switch
    {
        HarmonyMethodKind.Constructor => ".ctor",
        HarmonyMethodKind.StaticConstructor => ".cctor",
        HarmonyMethodKind.Finalizer => "Finalize",
        _ => declaration.TargetMember
    };

    // Harmony's own injected arguments, which never bind to a target parameter. Anything starting with
    // a double underscore is Harmony's (__instance, __result, __state, __originalMethod, __args, and
    // ___field for a private field), and a transpiler or finalizer takes its own fixed set.
    // Calibrated against the reference install rather than against Harmony's documentation, because the
    // documented set is not the set that appears in real mods. Two false positives were measured and
    // both are here: a transpiler taking ilGenerator, and two working marriage mods taking instance.
    // The list is deliberately generous. Missing a real fault costs one crash the user can bisect;
    // firing on a healthy install costs every future finding its credibility.
    private static readonly string[] InjectedParameterNames =
    [
        "instructions", "generator", "il", "ilGenerator",
        "original", "originalMethod", "method",
        "instance", "state", "result", "exception", "args", "runOriginal"
    ];

    // Only a normal method patch binds by parameter name. A transpiler is handed instructions, and a
    // property or event accessor takes value rather than a named argument list, so neither is judged.
    private static IReadOnlyList<string> UnbindableParameters(
        PatchDeclaration declaration, InstalledType declaring, string member)
    {
        if (declaration.MethodKind is not HarmonyMethodKind.Normal || declaration.PatchParameters.Count == 0)
            return [];

        if (!declaring.MethodParameters.TryGetValue(member, out var available))
            return [];

        return
        [
            .. declaration.PatchParameters
                .Where(name => !name.StartsWith("__", StringComparison.Ordinal))
                .Where(name => !InjectedParameterNames.Contains(name, StringComparer.Ordinal))
                .Where(name => !available.Contains(name))
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private static bool Declares(InstalledType type, HarmonyMethodKind kind, string member) => kind switch
    {
        HarmonyMethodKind.Getter or HarmonyMethodKind.Setter =>
            type.Properties.Contains(member) || type.Methods.Contains((kind is HarmonyMethodKind.Getter ? "get_" : "set_") + member),
        HarmonyMethodKind.EventAdd or HarmonyMethodKind.EventRemove =>
            type.Events.Contains(member) || type.Methods.Contains((kind is HarmonyMethodKind.EventAdd ? "add_" : "remove_") + member),
        _ => type.Methods.Contains(member)
    };

    // Matches both an assembly's simple name and a type's full name, because the two share their
    // leading segment for everything the runtime ships.
    private static bool IsFramework(string name) =>
        FrameworkAssemblyNames.Any(prefix =>
            name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<PatchDeclaration> Read(IndexedAssembly assembly)
    {
        using var stream = File.OpenRead(assembly.Path);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return [];

        var reader = peReader.GetMetadataReader();
        var declarations = new List<PatchDeclaration>();

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);

            // PatchAll only walks types carrying a class-level Harmony attribute, so a class with
            // patch methods and no attribute of its own is never processed and is not a finding.
            if (!HasHarmonyAttribute(reader, definition.GetCustomAttributes()))
                continue;

            var classTarget = ReadTarget(reader, definition.GetCustomAttributes());
            var patchAll = Names(reader, definition.GetCustomAttributes()).Contains(PatchAllAttribute, StringComparer.Ordinal);
            var category = ReadCategory(reader, definition.GetCustomAttributes());

            var computes = patchAll;
            var guarded = false;
            var methods = new List<(string Name, HarmonyTarget Target, List<string> Parameters)>();

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var name = reader.GetString(method.Name);
                var attributes = Names(reader, method.GetCustomAttributes()).ToList();

                if (ComputedTargetNames.Contains(name, StringComparer.Ordinal)
                    || attributes.Intersect(ComputedTargetAttributes, StringComparer.Ordinal).Any())
                {
                    computes = true;
                }

                if (name is "Prepare" || attributes.Contains("HarmonyLib.HarmonyPrepare", StringComparer.Ordinal))
                    guarded = true;

                if (PatchMethodNames.Contains(name, StringComparer.Ordinal)
                    || attributes.Intersect(PatchMethodAttributes, StringComparer.Ordinal).Any())
                {
                    methods.Add((
                        name,
                        ReadTarget(reader, method.GetCustomAttributes()),
                        [
                            .. method.GetParameters()
                                .Select(reader.GetParameter)
                                .Where(p => p.SequenceNumber > 0)
                                .Select(p => reader.GetString(p.Name))
                        ]));
                }
            }

            // A class carrying a Harmony attribute and no patch method gives PatchWithAttributes an
            // empty list to walk, so Harmony does nothing and there is nothing to report.
            if (methods.Count == 0)
                continue;

            var typeName = FullName(reader, definition);

            // A prefix and a postfix on the same method are one target, not two. Reporting them
            // separately would double the count of a single missing method.
            var targets = methods
                .Select(m => (m.Name, Merged: classTarget.Merge(m.Target), m.Parameters))
                .GroupBy(m => m.Merged);

            foreach (var target in targets)
            {
                var merged = target.Key;

                declarations.Add(new PatchDeclaration(
                    assembly.ModuleId ?? ModuleId.None,
                    assembly.Path,
                    typeName,
                    string.Join(", ", target.Select(m => m.Name)),
                    merged.TypeName,
                    merged.AssemblyName,
                    merged.MemberName,
                    merged.ByName,
                    merged.Kind ?? HarmonyMethodKind.Normal,
                    computes,
                    guarded,
                    category,
                    [.. target.SelectMany(m => m.Parameters).Distinct(StringComparer.Ordinal)]));
            }
        }

        return declarations;
    }

    private static string? ReadCategory(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);

            if (AttributeTypeName(reader, attribute) != CategoryAttribute)
                continue;

            try
            {
                if (attribute.DecodeValue(TypeNameProvider.Instance).FixedArguments is [{ Value: string name }, ..])
                    return name;
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static HarmonyTarget ReadTarget(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        var target = HarmonyTarget.None;

        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);

            if (AttributeTypeName(reader, attribute) != PatchAttribute)
                continue;

            try
            {
                target = target.Merge(Interpret(attribute.DecodeValue(TypeNameProvider.Instance).FixedArguments));
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or InvalidOperationException)
            {
            }
        }

        return target;
    }

    // The constructor overloads of HarmonyPatch are told apart by the shape of their arguments alone,
    // which the blob already carries: a leading Type is the declaring type, two leading strings are
    // the type-by-name form, and one leading string is a method name.
    private static HarmonyTarget Interpret(ImmutableArray<CustomAttributeTypedArgument<string>> arguments)
    {
        string? typeName = null;
        string? assemblyName = null;
        string? memberName = null;
        HarmonyMethodKind? kind = null;
        var byName = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            var argumentType = ParseSerializedName(argument.Type ?? string.Empty).TypeName;

            if (argumentType == "System.Type" && argument.Value is string serialized)
            {
                if (typeName is null)
                    (typeName, assemblyName) = ParseSerializedName(serialized);

                continue;
            }

            if (argumentType == "HarmonyLib.MethodType" && argument.Value is int methodType)
            {
                kind = Classify(methodType);
                continue;
            }

            if (argumentType != "System.String" || argument.Value is not string text)
                continue;

            var typeByName = i == 0
                && arguments.Length > 1
                && ParseSerializedName(arguments[1].Type ?? string.Empty).TypeName == "System.String";

            if (typeByName)
            {
                if (typeName is null)
                {
                    (typeName, assemblyName) = ParseSerializedName(text);
                    byName = true;
                }

                continue;
            }

            memberName ??= text;
        }

        return new HarmonyTarget(typeName, assemblyName, memberName, kind, byName);
    }

    private static HarmonyMethodKind Classify(int methodType) => methodType switch
    {
        0 => HarmonyMethodKind.Normal,
        1 => HarmonyMethodKind.Getter,
        2 => HarmonyMethodKind.Setter,
        3 => HarmonyMethodKind.Constructor,
        4 => HarmonyMethodKind.StaticConstructor,
        5 => HarmonyMethodKind.Enumerator,
        6 => HarmonyMethodKind.Async,
        7 => HarmonyMethodKind.Finalizer,
        8 => HarmonyMethodKind.EventAdd,
        9 => HarmonyMethodKind.EventRemove,
        _ => HarmonyMethodKind.Operator
    };

    private static bool HasHarmonyAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes) =>
        Names(reader, attributes).Any(name =>
            name.StartsWith(HarmonyNamespace + ".Harmony", StringComparison.Ordinal));

    private static IEnumerable<string> Names(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            if (AttributeTypeName(reader, reader.GetCustomAttribute(handle)) is { } name)
                yield return name;
        }
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

                return member.Parent.Kind == HandleKind.TypeReference
                    ? FullName(reader, reader.GetTypeReference((TypeReferenceHandle)member.Parent))
                    : null;

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);

                return FullName(reader, reader.GetTypeDefinition(method.GetDeclaringType()));

            default:
                return null;
        }
    }

    // "Ns.Type`1[[Arg, asm]], TheAssembly, Version=..." is how the compiler writes typeof() into an
    // attribute blob. The assembly is the half worth having: it says which file the type should have
    // come out of, which turns "BEM cannot find this" into "this mod is not installed".
    internal static (string TypeName, string? AssemblyName) ParseSerializedName(string serialized)
    {
        var depth = 0;

        for (var i = 0; i < serialized.Length; i++)
        {
            switch (serialized[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    var rest = serialized[(i + 1)..].Split(',')[0].Trim();

                    return (Clean(serialized[..i]), rest.Length == 0 ? null : rest);
            }
        }

        return (Clean(serialized), null);
    }

    private static string Clean(string typeName)
    {
        var bracket = typeName.IndexOf('[', StringComparison.Ordinal);
        var name = bracket < 0 ? typeName : typeName[..bracket];

        return name.Trim().TrimEnd('&', '*');
    }

    private static string FullName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();

        return declaring.IsNil
            ? Qualify(reader.GetString(definition.Namespace), name)
            : FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
    }

    private static string FullName(MetadataReader reader, TypeReference reference)
    {
        var name = reader.GetString(reference.Name);

        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? FullName(reader, reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope)) + "+" + name
            : Qualify(reader.GetString(reference.Namespace), name);
    }

    private static string Qualify(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;

    internal enum HarmonyMethodKind
    {
        Normal,
        Getter,
        Setter,
        Constructor,
        StaticConstructor,
        Enumerator,
        Async,
        Finalizer,
        EventAdd,
        EventRemove,
        Operator
    }

    internal sealed record HarmonyTarget(
        string? TypeName,
        string? AssemblyName,
        string? MemberName,
        HarmonyMethodKind? Kind,
        bool ByName = false)
    {
        public static HarmonyTarget None { get; } = new(null, null, null, null);

        // HarmonyMethod.Merge takes the later value wherever there is one, and PatchClassProcessor
        // merges the class attribute first and the patch method's own attribute over it. The assembly
        // and the by-name flag travel with the type name, because on their own they would describe
        // the wrong type.
        public HarmonyTarget Merge(HarmonyTarget later) => new(
            later.TypeName ?? TypeName,
            later.TypeName is null ? AssemblyName : later.AssemblyName,
            later.MemberName ?? MemberName,
            later.Kind ?? Kind,
            later.TypeName is null ? ByName : later.ByName);
    }

    private sealed record PatchDeclaration(
        ModuleId ModuleId,
        string AssemblyPath,
        string PatchClass,
        string PatchMethod,
        string? TargetType,
        string? TargetAssembly,
        string? TargetMember,
        bool TargetByName,
        HarmonyMethodKind MethodKind,
        bool ComputesTargets,
        bool GuardedByPrepare,
        string? Category,
        // Every argument the patch methods declare. Harmony binds these to the target's parameters by
        // name, and a name the target does not have throws out of PatchAll, taking the whole module's
        // patching with it.
        IReadOnlyList<string> PatchParameters = null!)
    {
        public IReadOnlyList<string> PatchParameters { get; init; } = PatchParameters ?? [];
    }

    private sealed record InstalledType(
        string FullName,
        string Path,
        string? BaseTypeFullName,
        HashSet<string> Methods,
        HashSet<string> Properties,
        HashSet<string> Events,
        // Parameter names per method name, unioned across overloads. Harmony binds a patch argument to
        // the target's parameter of the same name, so this is what decides whether a patch can bind at
        // all. Unioned rather than per-overload deliberately: where a name exists on any overload the
        // binding is possible, and reporting it as missing would be a false accusation.
        IReadOnlyDictionary<string, IReadOnlySet<string>> MethodParameters = null!)
    {
        public IReadOnlyDictionary<string, IReadOnlySet<string>> MethodParameters { get; init; } =
            MethodParameters ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
    }

    private sealed record InstalledScope(
        IReadOnlyDictionary<string, IReadOnlyList<InstalledType>> Types,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NameAliases,
        IReadOnlySet<string> LoadableAssemblies,
        IReadOnlyDictionary<string, string> DormantAssemblies);

    // Every type in a custom attribute blob is wanted as the string the compiler wrote, so nothing
    // has to be loaded to read one.
    private sealed class TypeNameProvider : ICustomAttributeTypeProvider<string>
    {
        public static TypeNameProvider Instance { get; } = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            _ => typeCode.ToString()
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FullName(reader, reader.GetTypeReference(handle));

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetSystemType() => "System.Type";

        public bool IsSystemType(string type) => ParseSerializedName(type).TypeName == "System.Type";

        public string GetTypeFromSerializedName(string name) => name;

        // Every enum Harmony puts in a patch attribute is backed by an int, and the blob has to be
        // read as something: guessing wrong here would misread the rest of the arguments.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }
}
