using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum PatchApplicationSite
{
    // No call that applies Harmony patches was found, or the only unprotected one sits in a class the
    // game does not call itself, so whether something above it catches the failure cannot be read.
    Unknown,

    // Every call that applies Harmony patches sits inside a try block with a catch. A patch that cannot
    // be applied is then skipped or logged by the mod, and the game goes on without it.
    Caught,

    // A call that applies Harmony patches sits in the mod's own MBSubModuleBase class with no catch around
    // it. The game calls those methods in a plain loop with nothing catching either
    // (Module.InitializeSubModuleBases in 1.4.8), so an exception Harmony throws there ends the process.
    UncaughtInSubModule
}

// Answers one question about a mod's assembly: when Harmony fails to apply one of its patches, does
// anything catch it? It is the difference between a mod that loses a feature and a game that does not
// start, and it is what makes a missing patch target a proven crash rather than a likely one.
//
// Surrender Tweaks is the case this was written for. Its TargetMethods named a campaign behavior 1.4.8
// does not have, its OnSubModuleLoad called PatchAll bare, and the owner's game died before the menu on
// every launch with nothing in BEM saying so.
public static class PatchApplicationGuard
{
    private static readonly HashSet<string> ApplyingTypes =
        new(["Harmony", "HarmonyInstance", "PatchClassProcessor", "PatchProcessor"], StringComparer.Ordinal);

    private static readonly HashSet<string> ApplyingMembers =
        new(["PatchAll", "PatchAllUncategorized", "PatchCategory", "CreateClassProcessor", "Patch"], StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .GroupBy(op => (ushort)op.Value)
        .ToDictionary(g => g.Key, g => g.First().OperandType);

    public static PatchApplicationSite Inspect(string assemblyPath)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return PatchApplicationSite.Unknown;

            return Inspect(pe, pe.GetMetadataReader());
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PatchApplicationSite.Unknown;
        }
    }

    private static PatchApplicationSite Inspect(PEReader pe, MetadataReader reader)
    {
        var sites = 0;
        var caught = 0;
        var uncaughtInSubModule = false;

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var isSubModule = IsSubModule(reader, type);

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

                foreach (var offset in ApplyingCalls(reader, body.GetILReader()))
                {
                    sites++;

                    if (body.ExceptionRegions.Any(region =>
                            region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter
                            && offset >= region.TryOffset
                            && offset < region.TryOffset + region.TryLength))
                    {
                        caught++;
                    }
                    else if (isSubModule)
                    {
                        uncaughtInSubModule = true;
                    }
                }
            }
        }

        if (uncaughtInSubModule)
            return PatchApplicationSite.UncaughtInSubModule;

        return sites > 0 && caught == sites ? PatchApplicationSite.Caught : PatchApplicationSite.Unknown;
    }

    // One level of inheritance is what every mod writes; a class behind a base class of the mod's own is
    // left as Unknown rather than guessed.
    private static bool IsSubModule(MetadataReader reader, TypeDefinition type)
    {
        if (type.BaseType.Kind != HandleKind.TypeReference)
            return false;

        var parent = reader.GetTypeReference((TypeReferenceHandle)type.BaseType);

        return reader.GetString(parent.Name) == "MBSubModuleBase";
    }

    private static IEnumerable<int> ApplyingCalls(MetadataReader reader, BlobReader il)
    {
        var offsets = new List<int>();

        while (il.RemainingBytes > 0)
        {
            var at = il.Offset;
            ushort code = il.ReadByte();

            if (code == 0xFE && il.RemainingBytes > 0)
                code = (ushort)(0xFE00 | il.ReadByte());

            if (!OperandTypes.TryGetValue(code, out var operand))
                break;

            switch (operand)
            {
                case OperandType.InlineMethod:
                    if (IsApplyingCall(reader, il.ReadInt32()))
                        offsets.Add(at);
                    break;
                case OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                    il.Offset += 4;
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

        return offsets;
    }

    private static bool IsApplyingCall(MetadataReader reader, int token)
    {
        EntityHandle handle;

        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (handle.Kind == HandleKind.MethodSpecification)
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

        if (handle.Kind != HandleKind.MemberReference)
            return false;

        var member = reader.GetMemberReference((MemberReferenceHandle)handle);

        if (member.Parent.Kind != HandleKind.TypeReference)
            return false;

        var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
        var typeName = reader.GetString(parent.Name);

        if (!ApplyingTypes.Contains(typeName))
            return false;

        var name = reader.GetString(member.Name);

        // Building a class processor reads the patch class's attributes, which is where a missing type
        // named in one is thrown.
        return ApplyingMembers.Contains(name) || (name == ".ctor" && typeName is "PatchClassProcessor");
    }
}
