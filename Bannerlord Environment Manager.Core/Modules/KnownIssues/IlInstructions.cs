using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// One IL instruction with its metadata token, for the checks that read what a method body calls or names
// without running it.
internal readonly record struct IlInstruction(int Offset, ushort Code, int Token)
{
    public const ushort Call = 0x28;
    public const ushort Callvirt = 0x6F;
    public const ushort Newobj = 0x73;
    public const ushort Ldstr = 0x72;
    public const ushort Ldsfld = 0x7E;
    public const ushort Ldtoken = 0xD0;

    public bool IsCall => Code is Call or Callvirt or Newobj;
}

internal static class IlInstructions
{
    private static readonly IReadOnlyDictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .GroupBy(op => (ushort)op.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    // Every instruction of the body up to the first byte that is not an opcode. Token is the four-byte
    // operand of a method, field, type, token or string instruction, and zero for every other kind.
    public static List<IlInstruction> Read(BlobReader il)
    {
        var all = new List<IlInstruction>();

        while (il.RemainingBytes > 0)
        {
            var at = il.Offset;
            ushort code = il.ReadByte();

            if (code == 0xFE && il.RemainingBytes > 0)
                code = (ushort)(0xFE00 | il.ReadByte());

            if (!OperandTypes.TryGetValue(code, out var operand))
                break;

            var token = 0;

            switch (operand)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType
                    or OperandType.InlineTok or OperandType.InlineString:
                    token = il.ReadInt32();
                    break;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    il.Offset += count * 4;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.ShortInlineR:
                    il.Offset += 4;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
            }

            all.Add(new IlInstruction(at, code, token));
        }

        return all;
    }

    public static EntityHandle? Handle(int token)
    {
        try
        {
            return MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Namespace.Outer+Inner for a type definition or reference, which is how a nested type is written
    // everywhere else in BEM.
    public static string? TypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var outer = definition.GetDeclaringType();

                return outer.IsNil
                    ? Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name))
                    : TypeName(reader, outer) + "+" + reader.GetString(definition.Name);

            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)handle);

                return reference.ResolutionScope.Kind == HandleKind.TypeReference
                    ? TypeName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + reader.GetString(reference.Name)
                    : Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));

            default:
                return null;
        }
    }

    // The type and member a call, field or token operand names, whether it is defined in this file or
    // reached through a reference.
    public static (string Type, string Member)? Member(MetadataReader reader, int token)
    {
        if (Handle(token) is not { } handle)
            return null;

        if (handle.Kind == HandleKind.MethodSpecification)
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);

                return TypeName(reader, method.GetDeclaringType()) is { } owner
                    ? (owner, reader.GetString(method.Name))
                    : null;

            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);

                return TypeName(reader, field.GetDeclaringType()) is { } fieldOwner
                    ? (fieldOwner, reader.GetString(field.Name))
                    : null;

            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);

                return member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition
                    && TypeName(reader, member.Parent) is { } parent
                    ? (parent, reader.GetString(member.Name))
                    : null;

            default:
                return null;
        }
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}
