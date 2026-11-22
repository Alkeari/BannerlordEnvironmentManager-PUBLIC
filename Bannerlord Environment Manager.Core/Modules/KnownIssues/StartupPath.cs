using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum StartupClassFault
{
    None,

    // The class SubModule.xml names is not in the file it names. TaleWorlds.MountAndBlade Module.cs
    // AddSubModule checks (GetType returns null) and returns CriticalError; LoadSubModules collects it, shows
    // "Error while loading <module>" and throws after the loop, so the game stops.
    ClassMissing,

    // The class is there with no constructor taking no arguments: AddSubModule's GetConstructor returns null
    // and it returns CriticalError, the same stop.
    NoParameterlessConstructor,

    // The class is there and every base up to System.Object was read, and none is MBSubModuleBase:
    // AddSubModule's typeof(MBSubModuleBase).IsAssignableFrom is false and it returns CriticalError.
    NotASubModuleBase,

    // The class's constructor (or its static constructor) certainly throws: AddSubModule catches the
    // exception from constructor.Invoke and returns CriticalError, the same stop.
    ConstructorThrows,

    // The class is abstract: AddSubModule finds its constructor, but constructor.Invoke on an abstract
    // class throws MemberAccessException, which the same catch turns into CriticalError.
    AbstractClass
}

// One use of something outside the assembly that the game is certain to reach while it starts.
// Chain is how it gets there, the method the game calls first and every call after it.
public sealed record StartupReference(
    string Method,
    string TypeName,
    string? MemberName,
    string? Signature,
    IReadOnlyList<string> Chain);

// A type of the assembly and a type outside it that it inherits from or implements. The loader lists
// every type of a SubModule's file (Assembly.GetTypes in Module.CollectModuleAssemblyTypes), and one
// whose base or interface cannot be loaded makes that call throw, which the loader turns into an
// exception of its own that nothing catches.
public sealed record StartupTypeLink(string Type, string DependsOn, bool IsInterface);

public sealed record StartupReport(
    StartupClassFault ClassFault,
    IReadOnlyList<string> CertainMethods,
    IReadOnlyList<StartupReference> References,
    IReadOnlyList<StartupTypeLink> Hierarchy)
{
    public static StartupReport Nothing { get; } = new(StartupClassFault.None, [], [], []);

    // What the game is certain to run when a campaign behavior of this file is added to a campaign: the
    // behavior's RegisterEvents, for each behavior class the file itself constructs. The method the game
    // runs first is the first link of each reference's chain.
    public IReadOnlyList<StartupReference> CampaignReferences { get; init; } = [];
}

// What the game is certain to run in a mod's SubModule file on every launch, read from the file alone.
//
// The game constructs the class SubModule.xml names, then calls its OnSubModuleLoad, then its
// OnBeforeInitialModuleScreenSetAsRoot, each in a plain loop with nothing catching
// (Module.AddSubModule, InitializeSubModuleBases and SetInitialModuleScreenAsRootScreen in 1.4.8). From
// each of those this follows only what cannot be skipped: an instruction every path through the method
// passes, outside any try block that has a catch, and from such an instruction a call that binds to one
// method of the same file. Whatever that reaches runs on every launch or the launch has already failed.
//
// A reference found this way that the install does not have is a crash, not a risk. Run on two
// runtimes (.NET Framework 4.8 and .NET 10, 2026-11-11): a method whose code uses a
// method, field or type that is not there throws MissingMethodException, MissingFieldException or
// TypeLoadException. A use behind a branch is left out on purpose: the runtime refuses the whole method
// in the common case, but it may drop a branch it can prove dead, and a proof may not rest on "usually".
public static class StartupPath
{
    private const int MaxDepth = 12;
    private const int MaxBlocks = 2000;

    private static readonly IReadOnlySet<string> SubModuleBases = new HashSet<string>(StringComparer.Ordinal) { SubModuleBase };

    private static readonly string[] EntryMethods = ["OnSubModuleLoad", "OnBeforeInitialModuleScreenSetAsRoot"];

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .GroupBy(op => (ushort)op.Value)
        .ToDictionary(g => g.Key, g => g.First());

    private const string SubModuleBase = "TaleWorlds.MountAndBlade.MBSubModuleBase";

    // baseFolders is where a base type outside the file is looked for (the game's folder and the modules').
    // Absent, whether the class derives from MBSubModuleBase is not judged.
    public static StartupReport Inspect(string assemblyPath, string subModuleClass, IReadOnlyList<string>? baseFolders = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);
        ArgumentNullException.ThrowIfNull(subModuleClass);

        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);

            return pe.HasMetadata ? Inspect(pe, pe.GetMetadataReader(), subModuleClass, assemblyPath, baseFolders) : StartupReport.Nothing;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A file BEM cannot read proves nothing either way.
            return StartupReport.Nothing;
        }
    }

    private static StartupReport Inspect(
        PEReader pe, MetadataReader reader, string subModuleClass, string assemblyPath, IReadOnlyList<string>? baseFolders)
    {
        // A name the runtime would parse rather than look up (an assembly-qualified or generic one) is
        // not judged.
        if (subModuleClass.IndexOfAny([',', '[', '`', '\\', '*', '&']) >= 0)
            return StartupReport.Nothing;

        var types = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);

        foreach (var handle in reader.TypeDefinitions)
            types.TryAdd(MissingGameApis.NameOf(reader, reader.GetTypeDefinition(handle)), handle);

        var hierarchy = Hierarchy(reader);

        if (!types.TryGetValue(subModuleClass, out var classHandle))
        {
            // Assembly.GetType follows a forwarder to the assembly that has the type.
            return new StartupReport(
                IsForwarded(reader, subModuleClass) ? StartupClassFault.None : StartupClassFault.ClassMissing, [], [], hierarchy);
        }

        var type = reader.GetTypeDefinition(classHandle);

        // Module.AddSubModule checks the base before it looks for the constructor.
        if (baseFolders is not null)
        {
            using var ancestry = new TypeAncestry(baseFolders);

            if (ancestry.Open(assemblyPath) is { } image
                && image.Types.TryGetValue(subModuleClass, out var declared)
                && ancestry.Derives(image, declared, SubModuleBases) == Lineage.No)
            {
                return new StartupReport(StartupClassFault.NotASubModuleBase, [], [], hierarchy);
            }
        }

        var constructor = Find(reader, type, ".ctor", isStatic: false);

        if (constructor.IsNil)
            return new StartupReport(StartupClassFault.NoParameterlessConstructor, [], [], hierarchy);

        if ((type.Attributes & TypeAttributes.Abstract) != 0)
            return new StartupReport(StartupClassFault.AbstractClass, [], [], hierarchy);

        // constructor.Invoke is inside a try that returns CriticalError, so an exception the constructor
        // is certain to throw is a stop, not a crash report. Only a throw every path passes counts.
        var creation = new Walk(pe, reader);

        if ((type.Attributes & TypeAttributes.BeforeFieldInit) == 0 && Find(reader, type, ".cctor", isStatic: true) is { IsNil: false } staticConstructor)
            creation.From(staticConstructor, []);

        creation.From(constructor, []);

        if (creation.Throws.Count > 0)
            return new StartupReport(StartupClassFault.ConstructorThrows, [], [], hierarchy);

        var walk = new Walk(pe, reader);

        // A type with a static constructor of its own runs it before the first instance exists. One the
        // compiler wrote for field initializers alone (beforefieldinit) runs when the runtime chooses.
        if ((type.Attributes & TypeAttributes.BeforeFieldInit) == 0 && Find(reader, type, ".cctor", isStatic: true) is { IsNil: false } initializer)
            walk.From(initializer, []);

        walk.From(constructor, []);

        foreach (var name in EntryMethods)
        {
            if (Override(reader, type, name) is { IsNil: false } entry)
                walk.From(entry, []);
        }

        return new StartupReport(StartupClassFault.None, walk.Methods, walk.References, hierarchy)
        {
            CampaignReferences = CampaignStart(pe, reader, types)
        };
    }

    private const string CampaignBehaviorBase = "TaleWorlds.CampaignSystem.CampaignBehaviorBase";

    // A campaign behavior is added to a campaign by constructing it, and the game calls its RegisterEvents
    // (abstract on CampaignBehaviorBase) when it is added, whenever a campaign starts or a save loads. A
    // behavior class the file never constructs is not added by this file, so its RegisterEvents is not
    // followed. The same rule as the launch path applies inside: only what every path through the method
    // passes, outside any catch, and a call that binds to one method of the file.
    private static List<StartupReference> CampaignStart(
        PEReader pe, MetadataReader reader, Dictionary<string, TypeDefinitionHandle> types)
    {
        var behaviors = new HashSet<TypeDefinitionHandle>();

        foreach (var handle in types.Values)
        {
            var current = reader.GetTypeDefinition(handle);

            for (var depth = 0; depth < 12; depth++)
            {
                if (current.BaseType.IsNil)
                    break;

                if (current.BaseType.Kind == HandleKind.TypeDefinition)
                {
                    current = reader.GetTypeDefinition((TypeDefinitionHandle)current.BaseType);
                    continue;
                }

                if (current.BaseType.Kind == HandleKind.TypeReference
                    && MissingGameApis.AssemblyNameOf(reader, (TypeReferenceHandle)current.BaseType) is not null
                    && MissingGameApis.NameOf(reader, (TypeReferenceHandle)current.BaseType) == CampaignBehaviorBase)
                {
                    behaviors.Add(handle);
                }

                break;
            }
        }

        if (behaviors.Count == 0)
            return [];

        var constructed = new HashSet<TypeDefinitionHandle>();

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);

            if (method.RelativeVirtualAddress == 0 || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
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

            if (Decode(body.GetILReader()) is not { } instructions)
                continue;

            foreach (var instruction in instructions.Where(i => i.Code == OpCodes.Newobj && i.Token != 0))
            {
                try
                {
                    if (MetadataTokens.EntityHandle(instruction.Token) is { Kind: HandleKind.MethodDefinition } target)
                        constructed.Add(reader.GetMethodDefinition((MethodDefinitionHandle)target).GetDeclaringType());
                }
                catch (ArgumentException)
                {
                }
            }
        }

        var walk = new Walk(pe, reader);

        foreach (var handle in behaviors.Where(constructed.Contains))
        {
            if (Override(reader, reader.GetTypeDefinition(handle), "RegisterEvents") is { IsNil: false } entry)
                walk.From(entry, []);
        }

        return walk.References;
    }

    private static bool IsForwarded(MetadataReader reader, string typeName)
    {
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            var @namespace = reader.GetString(exported.Namespace);
            var name = reader.GetString(exported.Name);

            if ((string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name) == typeName)
                return true;
        }

        return false;
    }

    private static List<StartupTypeLink> Hierarchy(MetadataReader reader)
    {
        var links = new List<StartupTypeLink>();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = MissingGameApis.NameOf(reader, type);

            if (type.BaseType.Kind == HandleKind.TypeReference && Outside(reader, (TypeReferenceHandle)type.BaseType) is { } baseName)
                links.Add(new StartupTypeLink(name, baseName, false));

            foreach (var implemented in type.GetInterfaceImplementations())
            {
                var target = reader.GetInterfaceImplementation(implemented).Interface;

                if (target.Kind == HandleKind.TypeReference && Outside(reader, (TypeReferenceHandle)target) is { } interfaceName)
                    links.Add(new StartupTypeLink(name, interfaceName, true));
            }
        }

        return links;
    }

    private static string? Outside(MetadataReader reader, TypeReferenceHandle handle) =>
        MissingGameApis.AssemblyNameOf(reader, handle) is null ? null : MissingGameApis.NameOf(reader, handle);

    private static MethodDefinitionHandle Find(MetadataReader reader, TypeDefinition type, string name, bool isStatic)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);

            if (reader.GetString(method.Name) != name || ((method.Attributes & MethodAttributes.Static) != 0) != isStatic)
                continue;

            if (ParameterCount(reader, method) == 0)
                return handle;
        }

        return default;
    }

    private static int ParameterCount(MetadataReader reader, MethodDefinition method)
    {
        var blob = reader.GetBlobReader(method.Signature);
        var header = blob.ReadSignatureHeader();

        if (header.IsGeneric)
            blob.ReadCompressedInteger();

        return blob.ReadCompressedInteger();
    }

    // The override the game's virtual call lands on: the class's own, or the nearest one in a base class
    // this same file defines. A base in another file is the game's own empty method or another mod's.
    private static MethodDefinitionHandle Override(MetadataReader reader, TypeDefinition type, string name)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (Find(reader, type, name, isStatic: false) is { IsNil: false } found)
                return (reader.GetMethodDefinition(found).Attributes & MethodAttributes.Virtual) != 0 ? found : default;

            if (type.BaseType.Kind != HandleKind.TypeDefinition)
                return default;

            type = reader.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
        }

        return default;
    }

    private sealed record Instruction(int Offset, OpCode Code, int Token, int[] Targets, int Next);

    private sealed class Walk(PEReader pe, MetadataReader reader)
    {
        private readonly HashSet<MethodDefinitionHandle> _seen = [];

        public List<string> Methods { get; } = [];

        public List<StartupReference> References { get; } = [];

        // Methods that certainly throw: a throw instruction every path through the method passes.
        public List<string> Throws { get; } = [];

        public void From(MethodDefinitionHandle handle, IReadOnlyList<string> chain)
        {
            if (chain.Count >= MaxDepth || !_seen.Add(handle))
                return;

            var method = reader.GetMethodDefinition(handle);

            if (method.RelativeVirtualAddress == 0 || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
                return;

            MethodBodyBlock body;

            try
            {
                body = pe.GetMethodBody(method.RelativeVirtualAddress);
            }
            catch (BadImageFormatException)
            {
                return;
            }

            var name = MissingGameApis.NameOf(reader, reader.GetTypeDefinition(method.GetDeclaringType())) + "." + reader.GetString(method.Name);
            IReadOnlyList<string> here = [.. chain, name];

            Methods.Add(name);

            if (Decode(body.GetILReader()) is not { Count: > 0 } instructions)
                return;

            foreach (var instruction in Certain(instructions, body))
            {
                if (instruction.Code == OpCodes.Throw)
                    Throws.Add(name);

                if (instruction.Token == 0)
                    continue;

                if (MissingGameApis.Describe(reader, instruction.Token) is { } outside)
                    References.Add(new StartupReference(name, outside.TypeName, outside.MemberName, outside.Signature, here));

                if (Callee(instruction) is { IsNil: false } callee)
                {
                    // Reaching a member of a type with a static constructor of its own runs that first.
                    var owner = reader.GetTypeDefinition(reader.GetMethodDefinition(callee).GetDeclaringType());

                    if ((owner.Attributes & TypeAttributes.BeforeFieldInit) == 0
                        && Find(reader, owner, ".cctor", isStatic: true) is { IsNil: false } initializer)
                    {
                        From(initializer, here);
                    }

                    From(callee, here);
                }
            }
        }

        // The one method of this file a call is bound to. A virtual call picks its method at run time, so
        // it is followed only where the method called is not virtual.
        private MethodDefinitionHandle Callee(Instruction instruction)
        {
            var code = instruction.Code;

            if (code != OpCodes.Call && code != OpCodes.Newobj && code != OpCodes.Callvirt)
                return default;

            EntityHandle handle;

            try
            {
                handle = MetadataTokens.EntityHandle(instruction.Token);
            }
            catch (ArgumentException)
            {
                return default;
            }

            if (handle.Kind == HandleKind.MethodSpecification)
                handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

            if (handle.Kind != HandleKind.MethodDefinition)
                return default;

            var target = (MethodDefinitionHandle)handle;

            return code == OpCodes.Callvirt && (reader.GetMethodDefinition(target).Attributes & MethodAttributes.Virtual) != 0
                ? default
                : target;
        }
    }

    private static List<Instruction>? Decode(BlobReader il)
    {
        var instructions = new List<Instruction>();

        while (il.RemainingBytes > 0)
        {
            var offset = il.Offset;
            ushort value = il.ReadByte();

            if (value == 0xFE && il.RemainingBytes > 0)
                value = (ushort)(0xFE00 | il.ReadByte());

            // Code this cannot read is code nothing may be claimed about.
            if (!OpCodesByValue.TryGetValue(value, out var code))
                return null;

            var token = 0;
            int[] targets = [];

            switch (code.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                    token = il.ReadInt32();
                    break;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    var relative = new int[count];

                    for (var i = 0; i < count; i++)
                        relative[i] = il.ReadInt32();

                    targets = [.. relative.Select(r => il.Offset + r)];
                    break;
                case OperandType.InlineBrTarget:
                    var far = il.ReadInt32();
                    targets = [il.Offset + far];
                    break;
                case OperandType.ShortInlineBrTarget:
                    var near = il.ReadSByte();
                    targets = [il.Offset + near];
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.ShortInlineR:
                    il.Offset += 4;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
            }

            instructions.Add(new Instruction(offset, code, token, targets, il.Offset));
        }

        return instructions;
    }

    private static bool Ends(OpCode code) =>
        code.FlowControl is FlowControl.Return or FlowControl.Throw || code == OpCodes.Jmp;

    private static bool Jumps(OpCode code) => code.FlowControl == FlowControl.Branch;

    // The instructions every run of the method executes: those in a block that every path from the first
    // instruction passes through, and that no catch of the method's own covers. A method too large to
    // reason about cheaply, or one whose jumps do not land on instructions, gives up its first block only.
    private static IEnumerable<Instruction> Certain(List<Instruction> instructions, MethodBodyBlock body)
    {
        var index = new Dictionary<int, int>();

        for (var i = 0; i < instructions.Count; i++)
            index[instructions[i].Offset] = i;

        var leaders = new SortedSet<int> { 0 };
        var sound = true;

        foreach (var instruction in instructions)
        {
            foreach (var target in instruction.Targets)
            {
                if (index.ContainsKey(target))
                    leaders.Add(target);
                else
                    sound = false;
            }

            if ((instruction.Targets.Length > 0 || Ends(instruction.Code)) && index.ContainsKey(instruction.Next))
                leaders.Add(instruction.Next);
        }

        foreach (var region in body.ExceptionRegions)
        {
            foreach (var edge in new[] { region.TryOffset, region.TryOffset + region.TryLength, region.HandlerOffset, region.HandlerOffset + region.HandlerLength })
            {
                if (index.ContainsKey(edge))
                    leaders.Add(edge);
            }
        }

        var starts = leaders.ToList();
        var blockOf = new Dictionary<int, int>();

        for (var b = 0; b < starts.Count; b++)
            blockOf[starts[b]] = b;

        var successors = new List<int>[starts.Count];
        var exits = new bool[starts.Count];

        for (var b = 0; b < starts.Count; b++)
        {
            var end = b + 1 < starts.Count ? index[starts[b + 1]] : instructions.Count;
            var last = instructions[end - 1];
            var next = new List<int>();

            foreach (var target in last.Targets)
            {
                if (blockOf.TryGetValue(target, out var to))
                    next.Add(to);
            }

            if (Ends(last.Code))
                exits[b] = true;
            else if (!Jumps(last.Code))
            {
                if (blockOf.TryGetValue(last.Next, out var fall))
                    next.Add(fall);
                else
                    sound = false;
            }

            successors[b] = next;
        }

        IEnumerable<Instruction> Block(int b)
        {
            var end = b + 1 < starts.Count ? index[starts[b + 1]] : instructions.Count;

            for (var i = index[starts[b]]; i < end; i++)
                yield return instructions[i];
        }

        bool Caught(int offset) => body.ExceptionRegions.Any(region =>
            region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter
            && offset >= region.TryOffset
            && offset < region.TryOffset + region.TryLength);

        for (var b = 0; b < starts.Count; b++)
        {
            if (b > 0 && (!sound || starts.Count > MaxBlocks || !OnEveryPath(b, successors, exits)))
                continue;

            foreach (var instruction in Block(b))
            {
                if (!Caught(instruction.Offset))
                    yield return instruction;
            }
        }
    }

    // True when, with this block taken out, nothing that starts at the first block can finish or go round
    // for ever: every way through the method then runs into the block.
    private static bool OnEveryPath(int block, List<int>[] successors, bool[] exits)
    {
        // 0 unseen, 1 on the current path, 2 done.
        var state = new byte[successors.Length];
        var stack = new Stack<(int Node, int Next)>();

        state[0] = 1;
        stack.Push((0, 0));

        if (exits[0])
            return false;

        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();

            if (next >= successors[node].Count)
            {
                state[node] = 2;
                continue;
            }

            stack.Push((node, next + 1));

            var to = successors[node][next];

            if (to == block || state[to] == 2)
                continue;

            if (state[to] == 1 || exits[to])
                return false;

            state[to] = 1;
            stack.Push((to, 0));
        }

        return true;
    }
}
