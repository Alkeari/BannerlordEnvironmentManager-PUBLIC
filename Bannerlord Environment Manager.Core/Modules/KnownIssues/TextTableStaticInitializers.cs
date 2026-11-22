using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// A game type whose static constructor reads the text table, and the call in it that does.
public sealed record TextTableStaticInitializer(string TypeFullName, string AssemblyPath, string Call);

// One enabled module that patches such a type, and how.
public sealed record TextTablePatch(
    ModuleId ModuleId, string ModuleName, string AssemblyPath, string TypeFullName, bool ByAttribute);

public sealed record TextTableStaticInitializerReport(
    bool Looked,
    int GameAssembliesRead,
    long ElapsedMilliseconds,
    IReadOnlyList<TextTableStaticInitializer> AtRisk,
    IReadOnlyList<TextTablePatch> Patches)
{
    public static TextTableStaticInitializerReport CouldNotLook { get; } = new(false, 0, 0, [], []);
}

// A mod that Harmony-patches a game type whose static constructor reads GameTexts crashes the game later,
// in a place that has nothing to do with the mod.
//
// Applying a patch runs the patched type's static constructor, and the mod applies its patches while the
// module loads, before the game has handed GameTexts its text manager. GameTexts.FindText and its
// siblings dereference that manager (a static field that is still null), so the constructor throws. The
// runtime keeps that TypeInitializationException for the life of the process: the first time the game
// really uses the type it throws again, which is how Character Creation died on "Next" with More Trait
// Info (CampaignUIHelper.GetTraitEffectTooltip, patched by hand) and Better Smithing Continued
// (CampaignUIHelper through a HarmonyPatch attribute) both enabled.
//
// Why applying a patch runs the constructor: the game runs on Mono (bin\Win64_Shipping_Client\mono-2.0-sgen.dll,
// Mono 6.4.0). Harmony hands the detour to MonoMod.Core, whose MonoRuntime.Compile asks for the original's
// RuntimeMethodHandle.GetFunctionPointer, and Mono runs the declaring type's class constructor while compiling
// it, beforefieldinit or not. Proven by patching CampaignUIHelper.GetItemFlagDetails with the set's own
// 0Harmony.dll inside the game's own Mono: the stack went Harmony.Patch, PatchTools.DetourMethod,
// MonoRuntime.Compile, GetFunctionPointer, CampaignUIHelper..cctor, GameTexts.FindText("str_party_morale").
// The same probe on .NET Framework's CLR does not run it, so the premise is Mono-specific. The crash on his set
// agrees: with More Trait Info, Be Governor and Better Smithing Continued enabled, all three patching
// CampaignUIHelper at load, Character Creation died on the kept TypeInitializationException.
//
// Timing decides it. Only a patch applied before any Game exists (OnSubModuleLoad, the SubModule constructors,
// OnBeforeInitialModuleScreenSetAsRoot, or a PatchAll or manual Patch call reachable from them by direct calls)
// runs the constructor before the text manager exists: Game.Initialize is the only caller of
// GameTexts.Initialize, and OnBeforeInitialModuleScreenSetAsRoot runs first at startup, from
// Module.SetInitialModuleScreenAsRootScreen, with no game created yet. PatchAll(Assembly) given another file of
// the same module (Better Smithing Continued: Main.dll patches MainFrame.dll) counts that file's patch classes. A patch applied at OnGameStart, OnGameInitializationFinished or on campaign start
// finds the manager set and is harmless, and so is not reported (ServeAsSoldier applies its patches at
// OnGameInitializationFinished). A patch applied through a delegate is not followed, which is a miss.
//
// Nothing is hardcoded about which types are at risk. The members that need the manager are the static
// methods of TaleWorlds.Core.GameTexts that load its _gameTextManager field, and the at-risk types are
// the ones whose .cctor calls one of them outside a try that catches. Both are read from the installed
// game's own assemblies, so a game update that moves a constructor or adds a type is followed. Only a
// call made directly in the .cctor is seen; one made through a helper the constructor calls is a miss.
//
// What no reader can decide is whether the load order is safe anyway: a module that seeds the text table
// before the patching mods load would stop it. That cannot be told generically, so the finding offers no
// reorder and names the mods instead.
//
// Disproof condition: open the type named in a row in the game's assembly. If its .cctor makes no call
// into GameTexts, or wraps it in a catch, the row is wrong.
public static class TextTableStaticInitializers
{
    public const string GameTexts = "TaleWorlds.Core.GameTexts";

    private const string ManagerField = "_gameTextManager";

    public static TextTableStaticInitializerReport Inspect(AssemblyIndex index, IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
            return TextTableStaticInitializerReport.CouldNotLook;

        var timer = Stopwatch.StartNew();
        var game = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Game
                && (a.DefinedTypeFullNames.Contains(GameTexts) || a.ReferencedTypeFullNames.Contains(GameTexts)))
            .ToList();

        var owner = game.FirstOrDefault(a => a.DefinedTypeFullNames.Contains(GameTexts));

        if (owner is null)
            return TextTableStaticInitializerReport.CouldNotLook;

        var atRisk = new List<TextTableStaticInitializer>();

        try
        {
            var readers = ManagerReaders(owner);

            if (readers.Count == 0)
                return TextTableStaticInitializerReport.CouldNotLook;

            foreach (var assembly in game)
                atRisk.AddRange(ReadStaticConstructors(assembly, readers));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return TextTableStaticInitializerReport.CouldNotLook;
        }

        var risky = atRisk.Select(a => a.TypeFullName).ToHashSet(StringComparer.Ordinal);
        var names = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().DisplayName);
        var enabled = entries.Where(e => e.IsEnabled).Select(e => e.Id).ToHashSet();

        var modules = HarmonyPatchTargets.OnlyLoaderPicks(
            [.. index.Assemblies.Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is { } id && enabled.Contains(id))],
            HarmonyPatchTargets.GameVersionOf(entries));

        var patches = new List<TextTablePatch>();

        // Assemblies another file of the same module hands to PatchAll(Assembly) while loading.
        var patchedBySibling = new HashSet<IndexedAssembly>();

        foreach (var group in modules.GroupBy(a => a.ModuleId!.Value))
        {
            foreach (var assembly in group)
            {
                IReadOnlySet<string> others;

                try
                {
                    others = HarmonyCallSites.PatchesAllOfOthersAtLoad(assembly);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var sibling in group.Where(s => s != assembly
                    && (others.Contains(s.Name) || others.Contains(Path.GetFileNameWithoutExtension(s.Path)))))
                {
                    patchedBySibling.Add(sibling);
                }
            }
        }

        foreach (var assembly in modules)
        {
            var id = assembly.ModuleId!.Value;
            var name = names.GetValueOrDefault(id, id.Value);

            try
            {
                var bySibling = patchedBySibling.Contains(assembly);

                // [HarmonyPatch] classes are applied by PatchAll, so they count only when it runs while loading,
                // from this assembly or from another file of the module that names this one.
                foreach (var type in (bySibling || HarmonyCallSites.PatchesAllAtLoad(assembly) ? HarmonyPatchTargets.DeclaredPatches(assembly) : Array.Empty<HarmonyPatchTargets.DeclaredPatch>())
                    .Select(p => p.TargetType.Replace('/', '+'))
                    .Where(risky.Contains)
                    .Distinct(StringComparer.Ordinal))
                {
                    patches.Add(new TextTablePatch(id, name, assembly.Path, type, ByAttribute: true));
                }

                foreach (var type in HarmonyCallSites.ManualPatchTargets(assembly, risky, bySibling))
                    patches.Add(new TextTablePatch(id, name, assembly.Path, type, ByAttribute: false));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                // An unreadable mod file is reported by the checks that read it for their own sake; here it
                // only means this one has nothing to say.
            }
        }

        timer.Stop();

        return new TextTableStaticInitializerReport(
            true,
            game.Count,
            timer.ElapsedMilliseconds,
            [.. atRisk.OrderBy(a => a.TypeFullName, StringComparer.Ordinal)],
            [
                .. patches
                    .DistinctBy(p => (p.ModuleId, p.TypeFullName))
                    .OrderBy(p => p.ModuleName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.TypeFullName, StringComparer.Ordinal)
            ]);
    }

    // The static methods of GameTexts that load its text manager, read from its own IL.
    private static HashSet<string> ManagerReaders(IndexedAssembly owner)
    {
        using var stream = new FileStream(owner.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);

        var readers = new HashSet<string>(StringComparer.Ordinal);

        if (!pe.HasMetadata)
            return readers;

        var reader = pe.GetMetadataReader();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            if (IlInstructions.TypeName(reader, typeHandle) != GameTexts)
                continue;

            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var name = reader.GetString(method.Name);

                if (name == ".cctor" || method.RelativeVirtualAddress == 0
                    || !method.Attributes.HasFlag(System.Reflection.MethodAttributes.Static))
                {
                    continue;
                }

                if (Body(pe, method) is { } body
                    && IlInstructions.Read(body.GetILReader()).Any(i =>
                        i.Code == IlInstruction.Ldsfld
                        && IlInstructions.Member(reader, i.Token) is { } field
                        && field.Type == GameTexts
                        && field.Member == ManagerField))
                {
                    readers.Add(name);
                }
            }
        }

        return readers;
    }

    private static List<TextTableStaticInitializer> ReadStaticConstructors(IndexedAssembly assembly, HashSet<string> readers)
    {
        using var stream = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);

        var found = new List<TextTableStaticInitializer>();

        if (!pe.HasMetadata)
            return found;

        var reader = pe.GetMetadataReader();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);

                if (method.RelativeVirtualAddress == 0 || reader.GetString(method.Name) != ".cctor")
                    continue;

                if (Body(pe, method) is not { } body)
                    continue;

                foreach (var instruction in IlInstructions.Read(body.GetILReader()))
                {
                    if (!instruction.IsCall
                        || IlInstructions.Member(reader, instruction.Token) is not { } call
                        || call.Type != GameTexts
                        || !readers.Contains(call.Member))
                    {
                        continue;
                    }

                    // A call inside a try with a catch is handled by the constructor itself.
                    if (body.ExceptionRegions.Any(region =>
                            region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter
                            && instruction.Offset >= region.TryOffset
                            && instruction.Offset < region.TryOffset + region.TryLength))
                    {
                        continue;
                    }

                    if (IlInstructions.TypeName(reader, typeHandle) is { } type)
                        found.Add(new TextTableStaticInitializer(type, assembly.Path, GameTexts + "." + call.Member));

                    break;
                }
            }
        }

        return found;
    }

    private static MethodBodyBlock? Body(PEReader pe, MethodDefinition method)
    {
        try
        {
            return pe.GetMethodBody(method.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
