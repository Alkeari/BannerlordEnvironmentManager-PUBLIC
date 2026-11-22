using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum PatchReturn
{
    Void,
    Bool,
    Other
}

// What a bool prefix hands back to Harmony, read from its IL. Unknown is a body BEM cannot read (a
// method with no body, or one that never returns), and is graded as a prefix that can return false
// because a row must not clear a patch it could not look at.
public enum PrefixReturn
{
    NotBool,
    AlwaysTrue,
    AlwaysFalse,
    Conditional,
    Unknown
}

// What a patch does to a by-ref value it writes, when that is one of the few writes whose order cannot
// matter between two patches: only ever towards false (a constant false, or `&=`), only ever towards
// true (a constant true, or `|=`), or only ExplainedNumber.Add / AddFactor, which add to totals the
// game combines at the end. Anything else is Other, and two Other writers are order dependent.
public enum WriteKind
{
    Other,
    ToFalse,
    ToTrue,
    Additive
}

public sealed record PatchShape(
    string AssemblyPath,
    PatchReturn Return,
    PrefixReturn PrefixReturn,
    bool AffectsOriginal,
    bool ChangesResult,
    bool PassThrough,
    bool TakesRunOriginal,
    IReadOnlyList<string> ChangedArguments,
    int? Priority,
    bool HasBeforeOrAfter,
    IReadOnlyDictionary<string, WriteKind>? WriteKinds = null)
{
    public WriteKind KindOf(string parameter) =>
        WriteKinds is not null && WriteKinds.TryGetValue(parameter, out var kind) ? kind : WriteKind.Other;
}

// Reads one patch method's shape from metadata and IL without loading the mod, so it is safe on any
// mod: a method body that is encrypted or missing reads as Unknown and is never decrypted.
//
// What Harmony does with a patch is decided by its signature, which is why this reads signatures:
// a prefix "affects the original" when it returns bool, has any ref or out parameter, or takes a
// reference type other than __instance, __originalMethod and __state. Only such a prefix is skipped by
// an earlier prefix returning false.
public static class PatchBodyReader
{
    private const string Result = "__result";

    private const string RunOriginal = "__runOriginal";

    private const int DefaultPriority = 400;

    private static readonly string[] NotCounted = ["__instance", "__originalMethod", "__state"];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static int EffectivePriority(PatchShape? shape) => shape?.Priority ?? DefaultPriority;

    public static PatchShape? Read(string path, string typeName, string methodName, PatchKind kind)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return null;

        var reader = peReader.GetMetadataReader();

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

                return Describe(path, peReader, reader, type, method, kind);
            }
        }

        return null;
    }

    private static PatchShape Describe(
        string path, PEReader peReader, MetadataReader reader, TypeDefinition type, MethodDefinition method, PatchKind kind)
    {
        var signature = method.DecodeSignature(ShapeProvider.Instance, null);
        var names = method.GetParameters()
            .Select(reader.GetParameter)
            .Where(p => p.SequenceNumber > 0)
            .ToDictionary(p => p.SequenceNumber - 1, p => reader.GetString(p.Name));

        var parameters = new List<(string Name, Shape Shape)>();

        for (var i = 0; i < signature.ParameterTypes.Length; i++)
            parameters.Add((names.GetValueOrDefault(i, string.Empty), signature.ParameterTypes[i]));

        var returns = signature.ReturnType.Kind switch
        {
            ShapeKind.Void => PatchReturn.Void,
            ShapeKind.Bool when !signature.ReturnType.ByRef => PatchReturn.Bool,
            _ => PatchReturn.Other
        };

        // A by-ref parameter only changes anything when the body does more with it than read it: BEM
        // cannot tell a mutation through a call from a read, so any use but a plain load of the value
        // counts as a write, and a body it cannot read is assumed to write.
        var written = WrittenByRefParameters(
            peReader, method, (method.Attributes & MethodAttributes.Static) != 0, parameters.Select(p => p.Shape.ByRef).ToList());

        var changesResult = parameters.Select((p, i) => (p, i)).Any(x => x.p.Name == Result && x.p.Shape.ByRef && written.Contains(x.i));
        var passThrough = kind == PatchKind.Postfix && returns != PatchReturn.Void;

        var affects = returns == PatchReturn.Bool
            || parameters.Any(p => p.Shape.ByRef
                || (p.Shape.Kind == ShapeKind.Reference && !NotCounted.Contains(p.Name, StringComparer.Ordinal)));

        // An argument a patch can rewrite: a by-ref parameter that is not one of Harmony's injected names.
        var changed = parameters
            .Select((p, i) => (p, i))
            .Where(x => x.p.Shape.ByRef && x.p.Name.Length > 0 && !x.p.Name.StartsWith("__", StringComparison.Ordinal) && written.Contains(x.i))
            .Select(x => x.p.Name)
            .ToList();

        var prefixReturn = returns != PatchReturn.Bool || kind != PatchKind.Prefix
            ? PrefixReturn.NotBool
            : ClassifyReturns(peReader, reader, method);

        var priority = AttributeInt(reader, method.GetCustomAttributes(), "HarmonyPriority")
            ?? AttributeInt(reader, type.GetCustomAttributes(), "HarmonyPriority");

        var ordering = HasAttribute(reader, method.GetCustomAttributes(), "HarmonyBefore", "HarmonyAfter")
            || HasAttribute(reader, type.GetCustomAttributes(), "HarmonyBefore", "HarmonyAfter");

        return new PatchShape(
            path,
            returns,
            prefixReturn,
            affects,
            changesResult,
            passThrough,
            parameters.Any(p => p.Name == RunOriginal),
            changed,
            priority,
            ordering,
            WriteKindsOf(peReader, reader, method, (method.Attributes & MethodAttributes.Static) != 0, written, parameters.Select(p => p.Name).ToList()));
    }

    // For each by-ref parameter the body writes through, whether every write is of one commuting kind.
    private static Dictionary<string, WriteKind> WriteKindsOf(
        PEReader peReader, MetadataReader reader, MethodDefinition method, bool isStatic, HashSet<int> written, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, WriteKind>(StringComparer.Ordinal);

        foreach (var index in written.Where(i => names[i].Length > 0))
            result[names[index]] = WriteKind.Other;

        if (written.Count == 0 || method.RelativeVirtualAddress == 0)
            return result;

        List<Instruction>? code;

        try
        {
            code = Decode(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return result;
        }

        if (code is null)
            return result;

        var kinds = new Dictionary<int, List<WriteKind>>();

        for (var i = 0; i < code.Count; i++)
        {
            if (ArgOf(code[i]) is not { } arg)
                continue;

            var index = arg - (isStatic ? 0 : 1);

            if (!written.Contains(index))
                continue;

            var next = i + 1 < code.Count ? (ushort)code[i + 1].Code.Value : 0;

            if (next is >= 0x46 and <= 0x4F or 0x50 or 0x71)
                continue;

            if (!kinds.TryGetValue(index, out var list))
                kinds[index] = list = [];

            list.Add(ClassifyWrite(code, i, arg, reader));
        }

        foreach (var (index, list) in kinds.Where(k => names[k.Key].Length > 0))
            result[names[index]] = list.Distinct().Count() == 1 ? list[0] : WriteKind.Other;

        return result;
    }

    private static int? ArgOf(Instruction i)
    {
        var value = (ushort)i.Code.Value;

        return value switch
        {
            >= 0x02 and <= 0x05 => value - 0x02,
            0x0E or 0xFE09 => (int)i.Operand,
            _ => null
        };
    }

    private static WriteKind ClassifyWrite(List<Instruction> code, int at, int arg, MetadataReader reader)
    {
        const ushort StindI1 = 0x52;

        // ldarg; ldc.i4.0 or .1; stind.i1
        if (at + 2 < code.Count && ConstantOf(code[at + 1]) is { } constant && (ushort)code[at + 2].Code.Value == StindI1)
            return constant ? WriteKind.ToTrue : WriteKind.ToFalse;

        // ldarg; (ldarg | dup); ldind.u1; <expression>; and (or); stind.i1, which is `x &= expression` (`|=`).
        // The expression must be straight-line code with no store of its own and no branch, since a
        // conditional expression can end on an `and` while another path leaves the other value.
        if (at + 3 < code.Count
            && (ArgOf(code[at + 1]) == arg || (ushort)code[at + 1].Code.Value == 0x25)
            && (ushort)code[at + 2].Code.Value is 0x46 or 0x47)
        {
            for (var j = at + 3; j < code.Count; j++)
            {
                var op = code[j].Code;

                if ((ushort)op.Value == StindI1)
                {
                    return (ushort)code[j - 1].Code.Value switch
                    {
                        0x5F => WriteKind.ToFalse,
                        0x60 => WriteKind.ToTrue,
                        _ => WriteKind.Other
                    };
                }

                if (op.FlowControl is not (FlowControl.Next or FlowControl.Call) || op.Name?.StartsWith("st", StringComparison.Ordinal) == true)
                    return WriteKind.Other;
            }

            return WriteKind.Other;
        }

        return AdditiveCall(code, at, reader) ? WriteKind.Additive : WriteKind.Other;
    }

    private sealed record CallInfo(int Parameters, bool Instance, bool ReturnsVoid, bool Additive);

    // Walks the stack from the loaded reference to the call that consumes it, through straight-line code
    // only: that call must be ExplainedNumber.Add or AddFactor.
    private static bool AdditiveCall(List<Instruction> code, int at, MetadataReader reader)
    {
        var depth = 0;

        for (var j = at + 1; j < code.Count; j++)
        {
            var op = code[j].Code;

            if (op.FlowControl is not (FlowControl.Next or FlowControl.Call))
                return false;

            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                if (CallOf((int)code[j].Operand, reader) is not { } call)
                    return false;

                var isNew = op == OpCodes.Newobj;
                var pops = call.Parameters + (call.Instance && !isNew ? 1 : 0);

                if (!isNew && pops == depth + 1)
                    return call.Additive;

                if (pops > depth)
                    return false;

                depth += (isNew || !call.ReturnsVoid ? 1 : 0) - pops;
                continue;
            }

            var pop = StackCount(op.StackBehaviourPop);
            var push = StackCount(op.StackBehaviourPush);

            if (pop < 0 || push < 0 || pop > depth)
                return false;

            depth += push - pop;
        }

        return false;
    }

    private static int StackCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref or StackBehaviour.Push1
            or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
            or StackBehaviour.Pushref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
            or StackBehaviour.Popref_popi or StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            or StackBehaviour.Popref_popi_pop1 => 3,
        _ => -1
    };

    private static CallInfo? CallOf(int token, MetadataReader reader)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);

            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);

                    if (member.GetKind() != MemberReferenceKind.Method)
                        return null;

                    var memberSignature = member.DecodeMethodSignature(ShapeProvider.Instance, null);
                    var parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name),
                        HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name),
                        _ => string.Empty
                    };

                    return new CallInfo(
                        memberSignature.ParameterTypes.Length,
                        memberSignature.Header.IsInstance,
                        memberSignature.ReturnType.Kind == ShapeKind.Void,
                        IsAdditive(parent, reader.GetString(member.Name)));

                case HandleKind.MethodDefinition:
                    var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    var signature = definition.DecodeSignature(ShapeProvider.Instance, null);

                    return new CallInfo(
                        signature.ParameterTypes.Length,
                        signature.Header.IsInstance,
                        signature.ReturnType.Kind == ShapeKind.Void,
                        IsAdditive(reader.GetString(reader.GetTypeDefinition(definition.GetDeclaringType()).Name), reader.GetString(definition.Name)));

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ExplainedNumber.Add raises BaseNumber and AddFactor raises SumOfFactors; the result is
    // BaseNumber + BaseNumber * SumOfFactors, so any mix of the two commutes.
    private static bool IsAdditive(string type, string method) =>
        type == "ExplainedNumber" && method is "Add" or "AddFactor";

    // The by-ref parameters (by zero-based position) a body writes through. A load of the argument that
    // is followed straight by ldind or ldobj only reads it; any other use may store or mutate.
    private static HashSet<int> WrittenByRefParameters(
        PEReader peReader, MethodDefinition method, bool isStatic, IReadOnlyList<bool> byRef)
    {
        var all = byRef.Select((isRef, i) => (isRef, i)).Where(x => x.isRef).Select(x => x.i).ToHashSet();

        if (all.Count == 0 || method.RelativeVirtualAddress == 0)
            return all;

        List<Instruction>? code;

        try
        {
            code = Decode(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return all;
        }

        if (code is null)
            return all;

        var written = new HashSet<int>();

        for (var i = 0; i < code.Count; i++)
        {
            var value = (ushort)code[i].Code.Value;
            int? arg = value switch
            {
                >= 0x02 and <= 0x05 => value - 0x02,
                0x0E or 0xFE09 => (int)code[i].Operand,
                _ => null
            };

            if (arg is null)
                continue;

            var index = arg.Value - (isStatic ? 0 : 1);

            if (!all.Contains(index))
                continue;

            var next = i + 1 < code.Count ? (ushort)code[i + 1].Code.Value : 0;
            var reads = next is >= 0x46 and <= 0x4F or 0x50 or 0x71;

            if (!reads)
                written.Add(index);
        }

        return written;
    }

    private static string? AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

                return member.Parent.Kind == HandleKind.TypeReference
                    ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                    : null;

            case HandleKind.MethodDefinition:
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);

                return reader.GetString(reader.GetTypeDefinition(definition.GetDeclaringType()).Name);

            default:
                return null;
        }
    }

    private static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, params string[] names) =>
        attributes.Any(h => AttributeName(reader, reader.GetCustomAttribute(h)) is { } name && names.Contains(name));

    // [HarmonyPriority(int)]: the blob is the 0x0001 prolog and then the int32. A priority a mod sets
    // through a HarmonyMethod at run time is not in metadata, and reads as the default here.
    private static int? AttributeInt(MetadataReader reader, CustomAttributeHandleCollection attributes, string name)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);

            if (AttributeName(reader, attribute) != name)
                continue;

            var blob = reader.GetBlobBytes(attribute.Value);

            if (blob.Length >= 6 && blob[0] == 1 && blob[1] == 0)
                return BitConverter.ToInt32(blob, 2);
        }

        return null;
    }

    private readonly record struct Instruction(int Offset, OpCode Code, long Operand, int[]? Jumps = null);

    private static PrefixReturn ClassifyReturns(PEReader peReader, MetadataReader reader, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0)
            return PrefixReturn.Unknown;

        byte[] il;

        try
        {
            il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return PrefixReturn.Unknown;
        }

        var code = Decode(il);

        if (code is null)
            return PrefixReturn.Unknown;

        var targets = code
            .Where(i => i.Code.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget)
            .Select(i => (int)i.Operand)
            .Concat(code.SelectMany(i => i.Jumps ?? []))
            .ToHashSet();

        var values = new HashSet<bool?>();

        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].Code != OpCodes.Ret)
                continue;

            // A ret that two paths branch into returns whatever each path pushed.
            if (i == 0 || targets.Contains(code[i].Offset))
            {
                values.Add(null);
                continue;
            }

            if (ConstantOf(code[i - 1]) is { } direct)
            {
                values.Add(direct);
                continue;
            }

            if (LocalOf(code[i - 1]) is { } local && ValuesOfLocal(code, targets, local, 4) is { } spilled)
            {
                // Debug builds spill the constant through one or two locals before the ret.
                foreach (var value in spilled)
                    values.Add(value);

                continue;
            }

            values.Add(null);
        }

        if (values.Count == 0)
            return PrefixReturn.Unknown;

        if (values.Contains(null) || (values.Contains(true) && values.Contains(false)))
            return PrefixReturn.Conditional;

        return values.Contains(false) ? PrefixReturn.AlwaysFalse : PrefixReturn.AlwaysTrue;
    }

    // Every store to the local must be a constant or a copy of another such local, or the value is unknown.
    private static List<bool?>? ValuesOfLocal(List<Instruction> code, HashSet<int> targets, int local, int depth)
    {
        var found = new List<bool?>();

        for (var s = 0; s < code.Count; s++)
        {
            if (LocalStored(code[s]) != local)
                continue;

            if (s == 0 || targets.Contains(code[s].Offset))
                return null;

            if (ConstantOf(code[s - 1]) is { } constant)
            {
                found.Add(constant);
                continue;
            }

            if (depth > 0 && LocalOf(code[s - 1]) is { } source && ValuesOfLocal(code, targets, source, depth - 1) is { } inner)
            {
                found.AddRange(inner);
                continue;
            }

            return null;
        }

        return found.Count == 0 ? null : found;
    }

    private static bool? ConstantOf(Instruction i)
    {
        var value = (ushort)i.Code.Value switch
        {
            0x16 => 0L,
            0x17 => 1L,
            0x1F or 0x20 => i.Operand,
            _ => -1L
        };

        return value switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }

    private static int? LocalOf(Instruction i) => (ushort)i.Code.Value switch
    {
        0x06 => 0,
        0x07 => 1,
        0x08 => 2,
        0x09 => 3,
        0x11 or 0xFE0C => (int)i.Operand,
        _ => null
    };

    private static int? LocalStored(Instruction i) => (ushort)i.Code.Value switch
    {
        0x0A => 0,
        0x0B => 1,
        0x0C => 2,
        0x0D => 3,
        0x13 or 0xFE0E => (int)i.Operand,
        _ => null
    };

    private static List<Instruction>? Decode(byte[] il)
    {
        var result = new List<Instruction>();
        var at = 0;

        while (at < il.Length)
        {
            var start = at;
            short value = il[at++];

            if (value == 0xFE)
            {
                if (at >= il.Length)
                    return null;

                value = (short)(0xFE00 | il[at++]);
            }

            if (!OpCodesByValue.TryGetValue(value, out var code))
                return null;

            long operand = 0;
            int[]? jumps = null;
            int size;

            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    size = 0;
                    break;
                case OperandType.ShortInlineBrTarget:
                    size = 1;
                    if (at + size > il.Length) return null;
                    operand = start + (at - start) + size + (sbyte)il[at];
                    break;
                case OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    size = 1;
                    if (at + size > il.Length) return null;
                    operand = code.OperandType == OperandType.ShortInlineI && code.Value == 0x1F ? (sbyte)il[at] : il[at];
                    break;
                case OperandType.InlineVar:
                    size = 2;
                    if (at + size > il.Length) return null;
                    operand = BitConverter.ToUInt16(il, at);
                    break;
                case OperandType.InlineBrTarget:
                    size = 4;
                    if (at + size > il.Length) return null;
                    operand = at + size + BitConverter.ToInt32(il, at);
                    break;
                case OperandType.InlineI:
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig
                    or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                    or OperandType.ShortInlineR:
                    size = 4;
                    if (at + size > il.Length) return null;
                    operand = BitConverter.ToInt32(il, at);
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    size = 8;
                    break;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return null;
                    var cases = BitConverter.ToInt32(il, at);
                    if (cases < 0 || cases > (il.Length - at - 4) / 4) return null;
                    size = 4 + (4 * cases);
                    var next = at + size;
                    jumps = new int[cases];
                    for (var c = 0; c < cases; c++)
                        jumps[c] = next + BitConverter.ToInt32(il, at + 4 + (4 * c));
                    break;
                default:
                    return null;
            }

            at += size;
            result.Add(new Instruction(start, code, operand, jumps));
        }

        return result;
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

    private enum ShapeKind
    {
        Other,
        Void,
        Bool,
        Reference
    }

    private readonly record struct Shape(ShapeKind Kind, bool ByRef = false);

    // Void, bool, a reference type, or by-ref: the only questions Harmony's rules ask. A generic
    // parameter is Other because a value type may stand behind it, and a row must not rest on a guess.
    private sealed class ShapeProvider : ISignatureTypeProvider<Shape, object?>
    {
        private const byte ClassKind = 0x12;

        public static ShapeProvider Instance { get; } = new();

        public Shape GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => new Shape(ShapeKind.Void),
            PrimitiveTypeCode.Boolean => new Shape(ShapeKind.Bool),
            PrimitiveTypeCode.String or PrimitiveTypeCode.Object => new Shape(ShapeKind.Reference),
            _ => new Shape(ShapeKind.Other)
        };

        public Shape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(rawTypeKind == ClassKind ? ShapeKind.Reference : ShapeKind.Other);

        public Shape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new(rawTypeKind == ClassKind ? ShapeKind.Reference : ShapeKind.Other);

        public Shape GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            new(rawTypeKind == ClassKind ? ShapeKind.Reference : ShapeKind.Other);

        public Shape GetSZArrayType(Shape elementType) => new(ShapeKind.Reference);

        public Shape GetArrayType(Shape elementType, ArrayShape shape) => new(ShapeKind.Reference);

        public Shape GetByReferenceType(Shape elementType) => elementType with { ByRef = true };

        public Shape GetGenericInstantiation(Shape genericType, ImmutableArray<Shape> typeArguments) => genericType;

        public Shape GetPointerType(Shape elementType) => new(ShapeKind.Other);

        public Shape GetPinnedType(Shape elementType) => elementType;

        public Shape GetModifiedType(Shape modifier, Shape unmodifiedType, bool isRequired) => unmodifiedType;

        public Shape GetFunctionPointerType(MethodSignature<Shape> signature) => new(ShapeKind.Other);

        public Shape GetGenericMethodParameter(object? genericContext, int index) => new(ShapeKind.Other);

        public Shape GetGenericTypeParameter(object? genericContext, int index) => new(ShapeKind.Other);
    }
}
