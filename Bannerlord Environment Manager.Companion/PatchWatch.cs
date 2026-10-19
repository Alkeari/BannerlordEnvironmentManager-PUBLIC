using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace BannerlordEnvironmentManager.Companion;

// Records WHO is patching, AS they patch.
//
// This exists because of a real crash that could not be answered. Four minidumps carried the same
// three-frame stack: HarmonyLib.Harmony.PatchAll threw a NullReferenceException, two frames in
// between were JIT code the dump had no memory for, and below them was
// TaleWorlds.MountAndBlade.Module.Initialize. Harmony was named, the game was named, and the mod
// that called PatchAll was in exactly the two frames nobody could resolve. Hours of forensics after
// the fact could not name it. A single line written at the moment of the call would have.
//
// THE SAFETY RULE THIS OBEYS. An earlier companion put a Harmony patch on OnSubModuleLoad in 142
// mods and caused crashes: Harmony runs a patched method as an IL copy in a dynamic method with a
// different identity, and AIValuesLife, obfuscated with ConfuserEx, derives its Harmony id from
// AppDomain.GetAssemblies() and did not survive that. So: this patches TaleWorlds' own methods and
// Harmony's OWN methods, and no mod's code, ever. Harmony patching Harmony changes the identity of
// HarmonyLib.Harmony.PatchAll and of nothing else.
//
// WHY THESE ENTRY POINTS AND NOT THE OTHERS. Harmony 2.4.2 exposes three parameterless overloads,
// PatchAll(), PatchAllUncategorized() and PatchCategory(string), and every one of them works out
// which assembly to scan with `new StackTrace().GetFrame(1).GetMethod()`. Patching a method that
// reads its own call stack to decide what to do is how a watcher becomes the fault, so those three
// are deliberately left alone. Each delegates to an overload that takes the Assembly explicitly, and
// those are what is patched here: the assembly arrives as an argument, which is better evidence than
// any stack walk and costs nothing to obtain.
//
// TWO TIERS, AND WHY.
//
//   Durable, coarse. A breadcrumb when a scan of an assembly starts and another when it ends. Those
//   lines are flushed to disk as they happen, so a scan that begins and never ends survives a hard
//   native crash that runs no managed handler at all. That is the line tonight's crash was missing.
//
//   In memory, fine. The patch class currently being processed, held in a thread-static field and
//   never written on its own. Every first-chance exception is stamped with it, so a managed throw
//   anywhere inside patching names the exact HarmonyPatch class. Writing that to disk would mean
//   tens of thousands of flushes during load, which is a cost the watcher has no right to impose.
internal sealed class PatchWatch
{
    // Scan records only. The reference install runs about 180 modules, so this is roughly twenty
    // times what a normal session produces and exists so a pathological session cannot grow the
    // trail without end.
    private const int MaxScanRecords = 4096;

    private static PatchWatch? _current;

    // The throwing thread is the thread that runs the first-chance handler, so thread-static is
    // exactly the right scope: the stamp on an exception describes what THAT thread was doing.
    [ThreadStatic]
    private static string? _scanModule;

    [ThreadStatic]
    private static string? _scanAssembly;

    [ThreadStatic]
    private static string? _scanOwner;

    [ThreadStatic]
    private static string? _classType;

    [ThreadStatic]
    private static string? _classModule;

    // Never cleared, only overwritten. Harmony runs the class finalizer before the scan finalizer, so
    // by the time a failed scan is recorded the active class is already gone; this is what names the
    // class the scan died on.
    [ThreadStatic]
    private static string? _lastClass;

    [ThreadStatic]
    private static string? _manualTarget;

    [ThreadStatic]
    private static string? _manualOwner;

    private readonly object _gate = new object();

    private readonly Dictionary<Assembly, string> _moduleByAssembly = new Dictionary<Assembly, string>();

    private readonly HashSet<string> _announcedManual = new HashSet<string>(StringComparer.Ordinal);

    private readonly ModuleMap _map;

    private readonly BreadcrumbWriter _breadcrumbs;

    private readonly Stopwatch _clock;

    private readonly string _ownHarmonyId;

    private FieldInfo? _containerTypeField;

    private int _scans;

    private int _scanRecords;

    private int _scanThrows;

    private int _patchClasses;

    private int _classThrows;

    private int _manualPatches;

    private int _manualThrows;

    private PatchWatch(ModuleMap map, BreadcrumbWriter breadcrumbs, Stopwatch clock, string ownHarmonyId)
    {
        _map = map;
        _breadcrumbs = breadcrumbs;
        _clock = clock;
        _ownHarmonyId = ownHarmonyId;
    }

    public int Scans => _scans;

    public int ScanThrows => _scanThrows;

    // Every type Harmony was asked to consider, not every type that turned out to carry patches:
    // PatchClassProcessor is constructed for each type in a scanned assembly and returns immediately
    // for the ones with no [HarmonyPatch] on them. Naming it for what it counts rather than for what
    // a reader would hope it counts.
    public int PatchClassesConsidered => _patchClasses;

    public int ClassThrows => _classThrows;

    public int ManualPatches => _manualPatches;

    public int ManualThrows => _manualThrows;

    public int Installed { get; private set; }

    public int InstallFailures { get; private set; }

    public string DegradedReason { get; private set; } = string.Empty;

    // What the thread calling this is doing to Harmony right now, in the order the reader wants it:
    // the innermost thing first. Empty when the thread is not patching, which is the normal case and
    // is why an empty stamp on an exception means "not during patching" rather than "not known".
    public static void ReadActive(out string module, out string detail)
    {
        module = string.Empty;
        detail = string.Empty;

        try
        {
            var patchClass = _classType;

            if (patchClass != null)
            {
                module = _classModule ?? _scanModule ?? string.Empty;
                detail = "applying the patch class " + patchClass;
                return;
            }

            var manual = _manualTarget;

            if (manual != null)
            {
                module = _scanModule ?? string.Empty;
                detail = "patching " + manual + " directly (Harmony id " + (_manualOwner ?? string.Empty) + ")";
                return;
            }

            var assembly = _scanAssembly;

            if (assembly != null)
            {
                module = _scanModule ?? string.Empty;
                detail = "scanning " + assembly + " for patches (Harmony id " + (_scanOwner ?? string.Empty) + ")";
            }
        }
        catch
        {
            // A stamp that cannot be read is an empty stamp, never an exception raised inside the
            // handler that is trying to record another exception.
        }
    }

    public static PatchWatch? Install(
        ModuleMap map,
        BreadcrumbWriter breadcrumbs,
        Stopwatch clock,
        Harmony harmony,
        Action<string> fail)
    {
        PatchWatch watch;

        try
        {
            watch = new PatchWatch(map, breadcrumbs, clock, harmony.Id ?? string.Empty);
            _current = watch;
        }
        catch (Exception ex)
        {
            fail("The patch watch could not be created, so nothing recorded who was patching: " + Describe(ex));
            return null;
        }

        watch.InstallScanWatch(harmony, fail);
        watch.InstallClassWatch(harmony, fail);
        watch.InstallManualWatch(harmony, fail);

        return watch;
    }

    // PatchAll(Assembly), PatchAllUncategorized(Assembly) and PatchCategory(Assembly, string) all
    // name the assembly in a parameter called "assembly", so one prefix and one finalizer serve all
    // three. Anything not found on this Harmony build is skipped and said out loud rather than
    // guessed at.
    private void InstallScanWatch(Harmony harmony, Action<string> fail)
    {
        var scan = new MethodInfo?[]
        {
            FindHarmonyMethod("PatchAll", typeof(Assembly)),
            FindHarmonyMethod("PatchAllUncategorized", typeof(Assembly)),
            FindHarmonyMethod("PatchCategory", typeof(Assembly), typeof(string))
        };

        foreach (var method in scan)
            Patch(harmony, method, nameof(ScanPrefix), nameof(ScanFinalizer), fail);

        if (scan[0] is null)
        {
            Degrade(
                "HarmonyLib.Harmony.PatchAll(Assembly) was not found on this Harmony build, so nothing records "
                + "which mod was applying its patches. This is the line a crash inside PatchAll is answered from, "
                + "and without it BEM can only say which module was initializing.");
        }
    }

    // PatchClassProcessor.Patch() is the single funnel every attribute-driven patch passes through,
    // whichever entry point started it. The class it is working on lives in a private field, so this
    // is the one place here that depends on Harmony's internals: if the field is not there, the
    // class tier is not installed at all and the reason is recorded, because a watch that silently
    // reports nothing is indistinguishable from a session where nothing happened.
    private void InstallClassWatch(Harmony harmony, Action<string> fail)
    {
        MethodInfo? method = null;

        try
        {
            var processor = typeof(PatchClassProcessor);
            _containerTypeField = processor.GetField("containerType", BindingFlags.Instance | BindingFlags.NonPublic);
            method = processor.GetMethod("Patch", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        }
        catch (Exception ex)
        {
            fail("Looking up HarmonyLib.PatchClassProcessor failed: " + Describe(ex));
        }

        if (_containerTypeField is null || method is null)
        {
            _containerTypeField = null;
            Degrade(
                "HarmonyLib.PatchClassProcessor.Patch() or its containerType field was not found on this Harmony "
                + "build, so BEM records which assembly was being scanned but not which patch class was being "
                + "applied. A crash inside patching is attributed to the mod, not to the class.");

            return;
        }

        Patch(harmony, method, nameof(ClassPrefix), nameof(ClassFinalizer), fail);
    }

    private void InstallManualWatch(Harmony harmony, Action<string> fail)
    {
        var method = FindHarmonyMethod(
            "Patch",
            typeof(MethodBase),
            typeof(HarmonyMethod),
            typeof(HarmonyMethod),
            typeof(HarmonyMethod),
            typeof(HarmonyMethod));

        if (method is null)
        {
            Degrade(
                "HarmonyLib.Harmony.Patch(MethodBase, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod) "
                + "was not found on this Harmony build, so a mod that patches methods one at a time rather than "
                + "through PatchAll is not recorded as it patches. Its patches still appear in the registry.");

            return;
        }

        Patch(harmony, method, nameof(ManualPrefix), nameof(ManualFinalizer), fail);
    }

    private static MethodInfo? FindHarmonyMethod(string name, params Type[] parameters)
    {
        try
        {
            return typeof(Harmony).GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameters, null);
        }
        catch
        {
            return null;
        }
    }

    // PatchProcessor rather than Harmony.Patch, because Harmony.Patch is one of the methods being
    // watched: calling it here would mean detouring the very method whose frame is on the stack, and
    // the first line of every install would be BEM recording itself.
    private void Patch(Harmony harmony, MethodInfo? target, string prefix, string finalizer, Action<string> fail)
    {
        if (target is null)
            return;

        try
        {
            new PatchProcessor(harmony, target)
                .AddPrefix(new HarmonyMethod(Own(prefix)))
                .AddFinalizer(new HarmonyMethod(Own(finalizer)))
                .Patch();

            Installed++;
        }
        catch (Exception ex)
        {
            InstallFailures++;
            fail("Watching " + target.Name + " failed, so calls to it are not recorded: " + Describe(ex));
        }
    }

    private static MethodInfo Own(string name) =>
        typeof(PatchWatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(PatchWatch).FullName, name);

    private void Degrade(string reason)
    {
        DegradedReason = DegradedReason.Length == 0 ? reason : DegradedReason + " " + reason;
    }

    // Every one of these four is the boundary between the game and this assembly. None of them may
    // throw: an exception raised here would replace the mod's own exception and destroy exactly the
    // evidence the watch exists to collect.
    private static void ScanPrefix(Harmony __instance, Assembly assembly) =>
        _current?.BeginScan(__instance, assembly);

    private static void ScanFinalizer(Harmony __instance, Assembly assembly, Exception? __exception) =>
        _current?.EndScan(assembly, __exception);

    private static void ClassPrefix(object __instance) => _current?.BeginClass(__instance);

    private static void ClassFinalizer(Exception? __exception) => _current?.EndClass(__exception);

    private static void ManualPrefix(Harmony __instance, MethodBase original) =>
        _current?.BeginManual(__instance, original);

    private static void ManualFinalizer(Exception? __exception) => _current?.EndManual(__exception);

    private void BeginScan(Harmony instance, Assembly assembly)
    {
        try
        {
            var owner = SafeId(instance);
            var name = SafeName(assembly);
            var module = ModuleOf(assembly);

            _scanAssembly = name;
            _scanModule = module;
            _scanOwner = owner;
            _scans++;

            Note(w => w
                .Text("kind", "patch-scan-begin")
                .Text("module", module)
                .Text("assembly", name)
                .Text("harmonyId", owner));
        }
        catch
        {
            // A scan that could not be announced is still a scan the game has to finish.
        }
    }

    private void EndScan(Assembly assembly, Exception? exception)
    {
        try
        {
            var module = _scanModule ?? string.Empty;
            var name = _scanAssembly ?? SafeName(assembly);
            var owner = _scanOwner ?? string.Empty;
            var patchClass = _classType ?? _lastClass ?? string.Empty;

            _scanAssembly = null;
            _scanModule = null;
            _scanOwner = null;

            if (exception != null)
                _scanThrows++;

            Note(w =>
            {
                w.Text("kind", exception is null ? "patch-scan-end" : "patch-scan-throw")
                    .Text("module", module)
                    .Text("assembly", name)
                    .Text("harmonyId", owner);

                if (exception != null)
                {
                    w.Text("patchClass", patchClass)
                        .Text("exceptionType", exception.GetType().FullName)
                        .Text("exceptionMessage", exception.Message);
                }
            });
        }
        catch
        {
            // Never throw out of a finalizer: it would replace the mod's own exception with ours.
        }
    }

    private void BeginClass(object instance)
    {
        try
        {
            var field = _containerTypeField;

            if (field is null)
                return;

            if (field.GetValue(instance) is not Type container)
                return;

            _classType = container.FullName ?? container.Name;
            _lastClass = _classType;
            _classModule = ModuleOf(container.Assembly);
            _patchClasses++;
        }
        catch
        {
            _classType = null;
        }
    }

    private void EndClass(Exception? exception)
    {
        try
        {
            var patchClass = _classType;
            var module = _classModule ?? string.Empty;

            _classType = null;
            _classModule = null;

            if (exception is null || patchClass is null)
                return;

            _classThrows++;

            Note(w => w
                .Text("kind", "patch-class-throw")
                .Text("module", module)
                .Text("patchClass", patchClass)
                .Text("exceptionType", exception.GetType().FullName)
                .Text("exceptionMessage", exception.Message));
        }
        catch
        {
            // As above: a throwing finalizer destroys the evidence.
        }
    }

    private void BeginManual(Harmony instance, MethodBase original)
    {
        try
        {
            var owner = SafeId(instance);

            // BEM arming its own watch is not a mod patching the game, and recording it would put
            // BEM at the top of its own report.
            if (string.Equals(owner, _ownHarmonyId, StringComparison.Ordinal))
                return;

            _manualTarget = SafeTarget(original);
            _manualOwner = owner;
            _manualPatches++;

            // One line the first time each Harmony id patches something by hand, and never again.
            // Per-call lines would be thousands of disk flushes during load; this is the durable
            // record that the id was patching at all, and the registry holds what it patched.
            var announce = false;

            lock (_gate)
            {
                if (_announcedManual.Count < 512 && _announcedManual.Add(owner))
                    announce = true;
            }

            if (!announce)
                return;

            Note(w => w
                .Text("kind", "patch-manual-begin")
                .Text("module", _scanModule ?? string.Empty)
                .Text("harmonyId", owner)
                .Text("target", _manualTarget));
        }
        catch
        {
            _manualTarget = null;
        }
    }

    private void EndManual(Exception? exception)
    {
        try
        {
            var target = _manualTarget;
            var owner = _manualOwner ?? string.Empty;

            _manualTarget = null;
            _manualOwner = null;

            if (exception is null || target is null)
                return;

            _manualThrows++;

            Note(w => w
                .Text("kind", "patch-manual-throw")
                .Text("module", _scanModule ?? string.Empty)
                .Text("harmonyId", owner)
                .Text("target", target)
                .Text("exceptionType", exception.GetType().FullName)
                .Text("exceptionMessage", exception.Message));
        }
        catch
        {
            // As above.
        }
    }

    private string ModuleOf(Assembly? assembly)
    {
        if (assembly is null)
            return string.Empty;

        lock (_gate)
        {
            if (_moduleByAssembly.TryGetValue(assembly, out var known))
                return known;
        }

        var module = string.Empty;

        try
        {
            module = assembly.IsDynamic ? string.Empty : _map.ResolveLocation(assembly.Location ?? string.Empty);
        }
        catch
        {
            module = string.Empty;
        }

        lock (_gate)
        {
            if (_moduleByAssembly.Count < 1024)
                _moduleByAssembly[assembly] = module;
        }

        return module;
    }

    private void Note(Action<JsonWriter> fill)
    {
        if (_scanRecords >= MaxScanRecords)
            return;

        _scanRecords++;

        if (_scanRecords == MaxScanRecords)
        {
            _breadcrumbs.Write(w => w
                .Text("kind", "patch-watch-capped")
                .Number("records", MaxScanRecords));

            return;
        }

        _breadcrumbs.Write(w =>
        {
            fill(w);
            w.Number("atMs", _clock.ElapsedMilliseconds);
        });
    }

    private static string SafeId(Harmony? harmony)
    {
        try
        {
            return harmony?.Id ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeName(Assembly? assembly)
    {
        try
        {
            return assembly?.GetName().Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeTarget(MethodBase? method)
    {
        try
        {
            if (method is null)
                return string.Empty;

            var declaring = method.DeclaringType;

            return declaring == null || declaring.FullName == null
                ? method.Name ?? string.Empty
                : declaring.FullName + "." + method.Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Describe(Exception ex) => ex.GetType().FullName + ": " + ex.Message;
}
