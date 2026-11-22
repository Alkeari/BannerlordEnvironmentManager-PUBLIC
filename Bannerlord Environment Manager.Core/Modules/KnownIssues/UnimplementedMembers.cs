using Mono.Cecil;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// A concrete type of an enabled module that leaves unimplemented an abstract method of a class it derives
// from, or a method of an interface it implements, that the installed copy of that class or interface
// declares. The runtime refuses to load such a type ("Method X in type Y does not have an
// implementation"), so Assembly.GetTypes on the module's file throws ReflectionTypeLoadException: the
// shape of FamilyControl v1.1.3 on Bannerlord v1.5.7, whose log entries lack IsVisibleInEncyclopediaPageOf.
public sealed record UnimplementedMember(
    ModuleId ModuleId,
    string ModuleName,
    string AssemblyPath,
    // The module's own type that fails to load.
    string TypeName,
    // The game (or other module) type that declares the member.
    string DeclaringType,
    string MemberName,
    bool IsInterface);

// Metadata only, read with Cecil: no mod code is loaded or run. Judged conservatively, because a finding
// here is an Error: a member counts as implemented when any class in the type's chain has a method of the
// same name and parameter count (or names it in an explicit override), so only a member nothing in the
// chain could be implementing is reported. Anything that cannot be resolved ends the judgement of that
// type without a finding.
internal sealed class UnimplementedMembers(AssemblyIndex index, IReadOnlySet<string> loadablePaths) : IDisposable
{
    private const int MaxDepth = 16;

    private readonly Dictionary<string, ModuleDefinition?> _modules = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var module in _modules.Values)
            module?.Dispose();
    }

    public List<UnimplementedMember> Find(IndexedAssembly assembly, ModuleId moduleId, string moduleName)
    {
        var found = new List<UnimplementedMember>();

        if (Open(assembly.Path) is not { } module)
            return found;

        foreach (var type in module.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsInterface || type.BaseType is null)
                continue;

            foreach (var (declaring, member, isInterface) in Check(type))
            {
                found.Add(new UnimplementedMember(
                    moduleId, moduleName, assembly.Path, Name(type), Name(declaring), member, isInterface));
            }
        }

        return [.. found.DistinctBy(f => (f.TypeName, f.DeclaringType, f.MemberName))];
    }

    private List<(TypeDefinition Declaring, string Member, bool IsInterface)> Check(TypeDefinition type)
    {
        var result = new List<(TypeDefinition, string, bool)>();

        // The chain from the type to the root, derived first. A base this cannot resolve ends the judgement,
        // since the unseen class may be the one that implements the member.
        var chain = new List<TypeDefinition>();
        var frameworkBase = false;

        try
        {
            var current = type;

            while (current is not null)
            {
                if (chain.Count > MaxDepth)
                    return [];

                chain.Add(current);

                if (current.BaseType is not { } baseType || baseType.FullName == "System.Object")
                    break;

                if (baseType is not TypeDefinition && MissingGameApis.IsFramework(baseType.GetElementType().Scope.Name))
                {
                    // A framework base is never read, and it may implement an interface member itself
                    // (System.Dynamic.GetMemberBinder's ReturnType satisfies BUTR.CrashReport's embedded
                    // ICSharpBinder), so no interface member of this type is judged.
                    frameworkBase = true;
                    break;
                }

                current = ResolveOne(baseType);

                if (current is null)
                    return [];
            }

            for (var i = 1; i < chain.Count; i++)
            {
                foreach (var method in AbstractMethods(chain[i]))
                {
                    var implemented = false;

                    for (var d = 0; d < i && !implemented; d++)
                        implemented = ImplementsAny(chain[d], method, concreteOnly: true);

                    if (!implemented)
                        result.Add((chain[i], method.Name, false));
                }
            }

            var seen = new HashSet<TypeDefinition>();
            var pending = new Stack<(TypeReference Reference, int Depth)>();

            // Interfaces the module's own classes list. The game's classes already satisfy the ones they list.
            foreach (var own in chain.Where(c => c.Module == type.Module))
            {
                foreach (var implementation in own.Interfaces)
                    pending.Push((implementation.InterfaceType, 0));
            }

            while (pending.Count > 0)
            {
                var (reference, depth) = pending.Pop();

                if (depth > MaxDepth
                    || (reference is not TypeDefinition && MissingGameApis.IsFramework(reference.GetElementType().Scope.Name))
                    || ResolveOne(reference) is not { } definition
                    || !seen.Add(definition))
                {
                    continue;
                }

                foreach (var method in AbstractMethods(definition))
                {
                    var implemented = frameworkBase || chain.Any(c => ImplementsAny(c, method, concreteOnly: false));

                    if (!implemented)
                        result.Add((definition, method.Name, true));
                }

                foreach (var inherited in definition.Interfaces)
                    pending.Push((inherited.InterfaceType, depth + 1));
            }
        }
        catch (Exception ex) when (ex is AssemblyResolutionException or BadImageFormatException or IOException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return [];
        }

        return result;
    }

    // Every class of every module walks the same game base types, so each type's abstract methods and its
    // candidate implementations are listed once (by name, and by the name an explicit override names)
    // instead of rescanning the whole method table per class.
    private readonly Dictionary<TypeDefinition, List<MethodDefinition>> _abstract = [];
    private readonly Dictionary<TypeDefinition, ILookup<string, MethodDefinition>> _concrete = [];
    private readonly Dictionary<TypeDefinition, ILookup<string, MethodDefinition>> _nonStatic = [];

    private List<MethodDefinition> AbstractMethods(TypeDefinition type)
    {
        if (!_abstract.TryGetValue(type, out var list))
            _abstract[type] = list = [.. type.Methods.Where(m => m.IsAbstract && !m.IsStatic)];

        return list;
    }

    private bool ImplementsAny(TypeDefinition type, MethodDefinition declared, bool concreteOnly)
    {
        var cache = concreteOnly ? _concrete : _nonStatic;

        if (!cache.TryGetValue(type, out var byName))
        {
            byName = type.Methods
                .Where(m => !m.IsStatic && !(concreteOnly && m.IsAbstract))
                .SelectMany(m => m.Overrides.Select(o => o.Name).Append(m.Name).Distinct(), (m, name) => (name, m))
                .ToLookup(x => x.name, x => x.m);
            cache[type] = byName;
        }

        return byName[declared.Name].Any(m => Implements(m, declared));
    }

    private static bool Same(string name, int parameters, int generics, MethodDefinition declared) =>
        name == declared.Name && parameters == declared.Parameters.Count && generics == declared.GenericParameters.Count;

    // Name and count are not enough on their own: a game update that changes one parameter's type (v1.5.7
    // gave AgentApplyDamageModel.CalculatePassiveAttackDamage an AttackInformation in place of a
    // BasicCharacterObject) leaves a mod's old override with the same name and count, and the runtime then
    // reports the new abstract method as unimplemented. Only a parameter whose two types are both plain
    // named types and differ counts as a different signature; a generic parameter on either side, or a
    // parameter that cannot be named, keeps the benefit of the doubt.
    private static bool Implements(MethodDefinition candidate, MethodDefinition declared) =>
        (Same(candidate.Name, candidate.Parameters.Count, candidate.GenericParameters.Count, declared)
            && !ParametersDiffer(candidate, declared))
        || candidate.Overrides.Any(o => Same(o.Name, o.Parameters.Count, o.GenericParameters.Count, declared));

    private static bool ParametersDiffer(MethodDefinition candidate, MethodDefinition declared)
    {
        for (var i = 0; i < candidate.Parameters.Count; i++)
        {
            var left = Plain(candidate.Parameters[i].ParameterType);
            var right = Plain(declared.Parameters[i].ParameterType);

            if (left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? Plain(TypeReference type)
    {
        try
        {
            var element = type.GetElementType();

            return element.IsGenericParameter || type.ContainsGenericParameter ? null : element.FullName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    // The one definition a reference names across the loadable copies of its assembly, or null when it has
    // none or the copies disagree on which assembly holds it.
    private TypeDefinition? ResolveOne(TypeReference reference)
    {
        var element = reference.GetElementType();

        if (element is TypeDefinition definition)
            return definition;

        var name = element.FullName.Replace('/', '+');
        var paths = index.FindDeclaring(name)
            .Where(a => loadablePaths.Contains(a.Path)
                && string.Equals(a.Name, element.Scope.Name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count != 1)
            return null;

        return Open(paths[0])?.GetType(element.FullName);
    }

    private ModuleDefinition? Open(string path)
    {
        if (_modules.TryGetValue(path, out var cached))
            return cached;

        ModuleDefinition? module = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            module = ModuleDefinition.ReadModule(stream, new ReaderParameters { InMemory = true });
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
        }

        return _modules[path] = module;
    }

    private static string Name(TypeDefinition type) => type.FullName.Replace('/', '+');
}
