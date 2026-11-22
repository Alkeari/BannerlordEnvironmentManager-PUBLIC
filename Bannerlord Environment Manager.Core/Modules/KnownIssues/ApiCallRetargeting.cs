using System.Security.Cryptography;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record ApiRetargetCall(string Caller, int Offset, string OriginalSignature, string TargetSignature);

public sealed record ApiRetargetPlan(
    string AssemblyPath, string TargetAssemblyPath, string OriginalHash, string TargetHash,
    IReadOnlyList<ApiRetargetCall> Calls);

// Reads and rewrites metadata, never loading a mod into BEM. A proposal is bound to both inputs;
// applying it after a mod or game update refuses before a backup or replacement is written.
public static class ApiCallRetargeting
{
    private enum Adaptation { OptionalDefaults, DeathForAllPerkHelper }
    private sealed record Change(MethodDefinition Caller, Instruction Call, MethodReference Original,
        MethodDefinition Target, Adaptation Kind);

    public static ApiRetargetPlan? Inspect(string assemblyPath, string targetAssemblyPath)
    {
        var original = File.ReadAllBytes(assemblyPath);
        var target = File.ReadAllBytes(targetAssemblyPath);
        using var sourceModule = Read(original);
        using var targetModule = Read(target);
        if (IsSigned(original, sourceModule))
            return null;

        var changes = FindChanges(sourceModule, targetModule);
        return changes.Count == 0 ? null : new ApiRetargetPlan(
            assemblyPath, targetAssemblyPath, Hash(original), Hash(target),
            changes.Select(c => new ApiRetargetCall(c.Caller.FullName, c.Call.Offset,
                c.Original.FullName, c.Target.FullName)).ToList().AsReadOnly());
    }

    public static byte[] BuildReplacement(ApiRetargetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var original = File.ReadAllBytes(plan.AssemblyPath);
        var target = File.ReadAllBytes(plan.TargetAssemblyPath);
        if (Hash(original) != plan.OriginalHash || Hash(target) != plan.TargetHash)
            throw new IOException(Strings.Current["Core.Modules.ApiRetarget.InputChanged"]);

        using var sourceModule = Read(original);
        using var targetModule = Read(target);
        var changes = FindChanges(sourceModule, targetModule);
        var calls = changes.Select(c => new ApiRetargetCall(c.Caller.FullName, c.Call.Offset,
            c.Original.FullName, c.Target.FullName)).ToList();
        if (IsSigned(original, sourceModule)
            || calls.Count == 0 || !calls.SequenceEqual(plan.Calls))
            throw new IOException(Strings.Current["Core.Modules.ApiRetarget.InputChanged"]);

        foreach (var change in changes)
        {
            var il = change.Caller.Body.GetILProcessor();
            if (change.Kind == Adaptation.DeathForAllPerkHelper)
            {
                // The exact compatibility adaptation already used for Death for All 7: its enum
                // constant 2 becomes the older API's default false, and its discarded return is gone.
                change.Call.Previous.OpCode = OpCodes.Ldc_I4_0;
                change.Call.Previous.Operand = null;
                change.Call.Next.OpCode = OpCodes.Nop;
                change.Call.Next.Operand = null;
            }
            else
            {
                foreach (var parameter in change.Target.Parameters.Skip(change.Original.Parameters.Count))
                    il.InsertBefore(change.Call, DefaultValue(parameter)!);
            }

            change.Call.Operand = sourceModule.ImportReference(change.Target);
        }

        // Inserting even one default can push an existing short branch beyond its signed-byte range.
        // Keep instruction targets, including exception boundaries, and let the writer recompute offsets.
        foreach (var method in changes.Select(c => c.Caller).Distinct())
        foreach (var instruction in method.Body.Instructions)
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave,
                _ => instruction.OpCode
            };

        using var output = new MemoryStream();
        sourceModule.Write(output);
        return output.ToArray();
    }

    public static FileReplacementOutcome Apply(ApiRetargetPlan plan)
    {
        WorkshopContent.Refuse(plan.AssemblyPath);
        var replacement = BuildReplacement(plan);
        return FileReplacementInstaller.Install(plan.AssemblyPath, replacement, merge: false);
    }

    private static ModuleDefinition Read(byte[] bytes) => ModuleDefinition.ReadModule(
        new MemoryStream(bytes, writable: false), new ReaderParameters { InMemory = true, ReadSymbols = false });

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsSigned(byte[] bytes, ModuleDefinition module)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes, writable: false));
        return module.Attributes.HasFlag(ModuleAttributes.StrongNameSigned) || module.Assembly?.Name.HasPublicKey == true
            || pe.PEHeaders.PEHeader?.CertificateTableDirectory.Size > 0;
    }

    private static List<Change> FindChanges(ModuleDefinition source, ModuleDefinition target)
    {
        var changes = new List<Change>();
        foreach (var type in Types(source.Types))
        foreach (var caller in type.Methods.Where(m => m.HasBody))
        foreach (var call in caller.Body.Instructions)
        {
            if (call.OpCode != OpCodes.Call || call.Operand is not MethodReference original
                || original.HasThis || original.HasGenericParameters || original is GenericInstanceMethod
                || original.DeclaringType is GenericInstanceType
                || original.CallingConvention != MethodCallingConvention.Default
                || original.DeclaringType.Scope is not AssemblyNameReference scope
                || scope.Name != target.Assembly?.Name.Name
                || IsTargeted(caller, call) || call.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
                continue;

            var definition = Types(target.Types).FirstOrDefault(t => t.FullName == original.DeclaringType.FullName);
            if (definition is null || !IsPublicType(definition)) continue;
            var methods = definition.Methods.Where(m => m.Name == original.Name && m.IsPublic && m.IsStatic
                && !m.HasGenericParameters && m.CallingConvention == MethodCallingConvention.Default).ToList();

            // A present API needs no repair, even when a second overload could accept extra defaults.
            if (methods.Any(m => SameType(m.ReturnType, original.ReturnType)
                && m.Parameters.Count == original.Parameters.Count && PrefixMatches(original, m)))
                continue;

            var candidates = new List<(MethodDefinition Method, Adaptation Kind)>();
            foreach (var method in methods)
            {
                if (SameType(original.ReturnType, method.ReturnType)
                    && method.Parameters.Count > original.Parameters.Count && PrefixMatches(original, method)
                    && method.Parameters.Skip(original.Parameters.Count).All(p => p.IsOptional && p.HasConstant
                        && DefaultValue(p) is not null))
                    candidates.Add((method, Adaptation.OptionalDefaults));
                else if (KnownPerkAdaptation(source, caller, call, original, method))
                    candidates.Add((method, Adaptation.DeathForAllPerkHelper));
            }

            if (candidates.Count == 1)
                changes.Add(new Change(caller, call, original, candidates[0].Method, candidates[0].Kind));
        }
        // Do not repair only the easy uses of one missing reference while leaving another call or
        // function pointer to that same reference unresolved in the assembly.
        var uses = Types(source.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.Count());
        var repairable = changes.GroupBy(c => c.Original.FullName)
            .Where(g => uses[g.Key] == g.Count()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return changes.Where(c => repairable.Contains(c.Original.FullName)).ToList();
    }

    private static bool PrefixMatches(MethodReference original, MethodDefinition target) =>
        original.Parameters.Count <= target.Parameters.Count
        && original.Parameters.Select((p, i) => SameType(p.ParameterType, target.Parameters[i].ParameterType)).All(x => x);

    private static bool SameType(TypeReference left, TypeReference right)
    {
        if (left.FullName != right.FullName) return false;
        // Function-pointer ABIs and custom modifiers are not covered by these compatibility rules.
        // In particular, FunctionPointerType has no element type to recurse into.
        if (left is FunctionPointerType or OptionalModifierType or RequiredModifierType
            || right is FunctionPointerType or OptionalModifierType or RequiredModifierType)
            return false;
        if (left is GenericInstanceType a && right is GenericInstanceType b)
            return SameType(a.ElementType, b.ElementType) && a.GenericArguments.Count == b.GenericArguments.Count
                && a.GenericArguments.Select((t, i) => SameType(t, b.GenericArguments[i])).All(x => x);
        if (left is TypeSpecification x && right is TypeSpecification y)
            return SameType(x.ElementType, y.ElementType);
        if (IsBuiltinKind(left) || IsBuiltinKind(right))
            return IsBuiltinKind(left) && IsBuiltinKind(right) && left.MetadataType == right.MetadataType
                && IsRuntimeScope(left) && IsRuntimeScope(right);
        return ScopeName(left) == ScopeName(right);
    }

    private static bool IsBuiltinKind(TypeReference type) => type.IsPrimitive
        || type.MetadataType is MetadataType.Void or MetadataType.String or MetadataType.Object;

    // Cecil recognizes primitive-looking names even on a type defined by a game assembly. Only
    // runtime-scoped primitive signatures are interchangeable across framework forwarding facades.
    private static bool IsRuntimeScope(TypeReference type) => ScopeName(type) is
        "mscorlib" or "System.Private.CoreLib" or "System.Runtime" or "netstandard";

    private static bool IsPublicType(TypeDefinition type) => type.IsNested
        ? type.IsNestedPublic && type.DeclaringType is { } outer && IsPublicType(outer)
        : type.IsPublic;

    private static string? ScopeName(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        ModuleDefinition module => module.Assembly?.Name.Name ?? module.Name,
        _ => type.Scope?.Name
    };

    private static bool KnownPerkAdaptation(ModuleDefinition source, MethodDefinition caller, Instruction call,
        MethodReference original, MethodDefinition target) =>
        source.Assembly?.Name.Name == "DeathForAll"
        && caller.DeclaringType.FullName == "DeathForAll.Patches.SurvivalChancePatch"
        && caller.Name == "GetSurvivalChance"
        && original.DeclaringType.FullName == "Helpers.PerkHelper" && original.Name == "AddPerkBonusForParty"
        && original.ReturnType.MetadataType == MetadataType.Boolean && target.ReturnType.MetadataType == MetadataType.Void
        && original.Parameters.Count == 5 && target.Parameters.Count == 5
        && original.Parameters.Take(4).Select((p, i) => SameType(p.ParameterType, target.Parameters[i].ParameterType)).All(x => x)
        && original.Parameters[0].ParameterType.FullName == "TaleWorlds.CampaignSystem.CharacterDevelopment.PerkObject"
        && original.Parameters[1].ParameterType.FullName == "TaleWorlds.CampaignSystem.Party.MobileParty"
        && original.Parameters[2].ParameterType.MetadataType == MetadataType.Boolean
        && original.Parameters[3].ParameterType.FullName == "TaleWorlds.CampaignSystem.ExplainedNumber&"
        && original.Parameters[4].ParameterType.FullName == "TaleWorlds.Core.BattleEnvironment"
        && target.Parameters[4].ParameterType.MetadataType == MetadataType.Boolean
        && target.Parameters[4].IsOptional && target.Parameters[4].Constant is false
        && call.Previous?.OpCode == OpCodes.Ldc_I4_2
        && call.Next?.OpCode == OpCodes.Pop && !IsTargeted(caller, call.Next);

    private static bool IsTargeted(MethodDefinition method, Instruction instruction) =>
        method.Body.Instructions.Any(i => ReferenceEquals(i.Operand, instruction)
            || i.Operand is Instruction[] targets && targets.Contains(instruction))
        || method.Body.ExceptionHandlers.Any(h => ReferenceEquals(h.TryStart, instruction)
            || ReferenceEquals(h.TryEnd, instruction) || ReferenceEquals(h.HandlerStart, instruction)
            || ReferenceEquals(h.HandlerEnd, instruction) || ReferenceEquals(h.FilterStart, instruction));

    private static Instruction? DefaultValue(ParameterDefinition parameter) => parameter.Constant switch
    {
        bool value when parameter.ParameterType.MetadataType == MetadataType.Boolean => Instruction.Create(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
        int value when parameter.ParameterType.MetadataType == MetadataType.Int32 => Instruction.Create(OpCodes.Ldc_I4, value),
        long value when parameter.ParameterType.MetadataType == MetadataType.Int64 => Instruction.Create(OpCodes.Ldc_I8, value),
        float value when parameter.ParameterType.MetadataType == MetadataType.Single => Instruction.Create(OpCodes.Ldc_R4, value),
        double value when parameter.ParameterType.MetadataType == MetadataType.Double => Instruction.Create(OpCodes.Ldc_R8, value),
        string value when parameter.ParameterType.MetadataType == MetadataType.String => Instruction.Create(OpCodes.Ldstr, value),
        null when !parameter.ParameterType.IsValueType && !parameter.ParameterType.IsByReference
            && !parameter.ParameterType.IsPointer && !parameter.ParameterType.IsGenericParameter => Instruction.Create(OpCodes.Ldnull),
        _ => null
    };

    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes)) yield return nested;
        }
    }
}
