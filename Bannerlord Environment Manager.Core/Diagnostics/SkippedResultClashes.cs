using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record SkippedResultClash(
    ModuleId SkipperId,
    string SkipperName,
    string SkipperPatch,
    ModuleId ReaderId,
    string ReaderName,
    string ReaderPatch,
    string Target,
    string ReaderAssemblyPath)
{
    // Stable across runs and languages, so an acceptance survives a rescan.
    public string Subject => $"{SkipperPatch}->{ReaderPatch}->{Target}";
}

public sealed record SkippedResultReport(
    IReadOnlyList<SkippedResultClash> Findings,
    IReadOnlyList<string> Problems,
    string Summary);

// Two mods that are each fine alone and crash together. One mod's Harmony prefix returns false, which
// skips the game method, and it does not assign __result, so the method's return value stays null.
// Harmony still runs every postfix, and another mod's postfix that takes __result of a reference type
// then reads that null. The case this was built from: ProceduralScenes' FieldBattleScenePatch.Prefix
// defers a land battle by skipping OpenBattleMission, and Couriers' Postfix on the same method calls
// __result.AddMissionBehavior, which threw a NullReferenceException at battle start.
//
// The targets come from the registry a dry run captured, because the prefix side commonly computes its
// targets in code (ProceduralScenes does) and attributes alone cannot name them.
//
// Metadata only: a method body may be encrypted, and BEM never decrypts a mod. So a finding says "could":
// the postfix may check for null and the prefix may skip only sometimes. The registry names a target's
// method without its overload, so two overloads of one method read as one target.
//
// Excluded because they prove the author handled the case: a postfix asking for __runOriginal, and a
// prefix taking __result, which can assign one.
//
// Disproof condition: decompile the two patch methods a row names. If the prefix can never return false,
// or the postfix checks __result for null first, the row is wrong and can be accepted.
public static class SkippedResultClashes
{
    private const string Result = "__result";

    private const string RunOriginal = "__runOriginal";

    public static SkippedResultReport Inspect(
        PatchRegistryLookup lookup,
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        // False for a patch its own mod does not apply on this install (a part of RBM that is switched
        // off, RbmSettings). Such a patch is not in the game and takes no part in any finding.
        Func<PatchRecord, bool>? isApplied = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (lookup.Registry is not { } registry)
            return new SkippedResultReport([], [], Strings.Current["Core.Diagnostics.SkippedResult.NoCapture"]);

        // A stored capture can come from another load order, so a patch only counts while its mod is on.
        var enabled = entries
            .Where(e => e.IsEnabled)
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        var problems = new List<string>();
        var signatures = new Dictionary<(ModuleId, string), PatchSignature?>();

        PatchSignature? Signature(PatchRecord patch)
        {
            var key = (patch.ModuleId, patch.PatchMethod);

            if (!signatures.TryGetValue(key, out var signature))
                signatures[key] = signature = ReadSignature(index, patch, problems);

            return signature;
        }

        var findings = new List<SkippedResultClash>();

        foreach (var group in registry.Patches
                     .Where(p => enabled.ContainsKey(p.ModuleId) && (isApplied is null || isApplied(p)))
                     .GroupBy(p => (p.TargetTypeFullName, p.TargetMethodName)))
        {
            var prefixes = group.Where(p => p.Kind == PatchKind.Prefix).ToList();
            var postfixes = group.Where(p => p.Kind == PatchKind.Postfix).ToList();

            if (prefixes.Count == 0 || postfixes.Count == 0)
                continue;

            var skippers = prefixes
                .Where(p => Signature(p) is { CanSkipWithoutResult: true })
                .DistinctBy(p => (p.ModuleId, p.PatchMethod))
                .ToList();

            if (skippers.Count == 0)
                continue;

            foreach (var reader in postfixes.DistinctBy(p => (p.ModuleId, p.PatchMethod)))
            {
                if (Signature(reader) is not { ReadsReferenceResult: true } read)
                    continue;

                foreach (var skipper in skippers.Where(s => s.ModuleId != reader.ModuleId))
                {
                    findings.Add(new SkippedResultClash(
                        skipper.ModuleId,
                        enabled[skipper.ModuleId],
                        skipper.PatchMethod,
                        reader.ModuleId,
                        enabled[reader.ModuleId],
                        reader.PatchMethod,
                        $"{group.Key.TargetTypeFullName}.{group.Key.TargetMethodName}",
                        read.AssemblyPath));
                }
            }
        }

        var ordered = findings
            .OrderBy(f => f.ReaderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Target, StringComparer.Ordinal)
            .ToList();

        var summary = Strings.Current.Format(
            "Core.Diagnostics.SkippedResult.Summary", registry.Patches.Count, ordered.Count, problems.Count);

        if (lookup.IsStale)
            summary += " " + lookup.Reason;

        return new SkippedResultReport(ordered, problems, summary);
    }

    // "Couriers.SystemPatcher+Nested.Postfix": the method follows the last dot.
    private static PatchSignature? ReadSignature(AssemblyIndex index, PatchRecord patch, List<string> problems)
    {
        var cut = patch.PatchMethod.LastIndexOf('.');

        if (cut <= 0)
            return null;

        var typeName = patch.PatchMethod[..cut];
        var methodName = patch.PatchMethod[(cut + 1)..];

        // A mod shipping one build per game version declares the type in each, and any one of them
        // answers for the shape of the patch.
        foreach (var assembly in index.FindDeclaring(typeName).Where(a => a.ModuleId == patch.ModuleId))
        {
            try
            {
                if (ReadFrom(assembly.Path, typeName, methodName) is { } signature)
                    return signature;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add($"{assembly.Path}: {ex.Message}");
            }
        }

        return null;
    }

    private static PatchSignature? ReadFrom(string path, string typeName, string methodName)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return null;

        var reader = peReader.GetMetadataReader();
        var found = false;
        var skips = false;
        var reads = false;

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);

            if (FullName(reader, type) != typeName)
                continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);

                if (reader.GetString(method.Name) != methodName)
                    continue;

                found = true;

                var shape = method.DecodeSignature(ShapeProvider.Instance, null);
                var names = method.GetParameters()
                    .Select(reader.GetParameter)
                    .Where(p => p.SequenceNumber > 0)
                    .ToDictionary(p => p.SequenceNumber - 1, p => reader.GetString(p.Name));

                var resultAt = names.FirstOrDefault(p => p.Value == Result, new(-1, string.Empty)).Key;

                if (shape.ReturnType == TypeShape.Boolean && resultAt < 0)
                    skips = true;

                if (resultAt >= 0
                    && resultAt < shape.ParameterTypes.Length
                    && shape.ParameterTypes[resultAt] == TypeShape.Reference
                    && !names.ContainsValue(RunOriginal))
                {
                    reads = true;
                }
            }
        }

        return found ? new PatchSignature(path, skips, reads) : null;
    }

    private static string FullName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();

        if (!declaring.IsNil)
            return FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;

        var space = reader.GetString(definition.Namespace);

        return space.Length == 0 ? name : space + "." + name;
    }

    private sealed record PatchSignature(string AssemblyPath, bool CanSkipWithoutResult, bool ReadsReferenceResult);

    private enum TypeShape
    {
        Other,
        Boolean,
        Reference
    }

    // Only what the check asks: is this a bool, is this a reference type. A by-ref parameter takes the
    // shape of what it refers to, because `ref Mission __result` holds a Mission. A generic parameter is
    // Other because a value type may stand behind it, and a row must not rest on a guess.
    private sealed class ShapeProvider : ISignatureTypeProvider<TypeShape, object?>
    {
        private const byte ClassKind = 0x12;

        public static ShapeProvider Instance { get; } = new();

        public TypeShape GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => TypeShape.Boolean,
            PrimitiveTypeCode.String or PrimitiveTypeCode.Object => TypeShape.Reference,
            _ => TypeShape.Other
        };

        public TypeShape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            rawTypeKind == ClassKind ? TypeShape.Reference : TypeShape.Other;

        public TypeShape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            rawTypeKind == ClassKind ? TypeShape.Reference : TypeShape.Other;

        public TypeShape GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            rawTypeKind == ClassKind ? TypeShape.Reference : TypeShape.Other;

        public TypeShape GetSZArrayType(TypeShape elementType) => TypeShape.Reference;

        public TypeShape GetArrayType(TypeShape elementType, ArrayShape shape) => TypeShape.Reference;

        public TypeShape GetByReferenceType(TypeShape elementType) => elementType;

        public TypeShape GetGenericInstantiation(TypeShape genericType, ImmutableArray<TypeShape> typeArguments) => genericType;

        public TypeShape GetPointerType(TypeShape elementType) => TypeShape.Other;

        public TypeShape GetPinnedType(TypeShape elementType) => elementType;

        public TypeShape GetModifiedType(TypeShape modifier, TypeShape unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeShape GetFunctionPointerType(MethodSignature<TypeShape> signature) => TypeShape.Other;

        public TypeShape GetGenericMethodParameter(object? genericContext, int index) => TypeShape.Other;

        public TypeShape GetGenericTypeParameter(object? genericContext, int index) => TypeShape.Other;
    }
}
