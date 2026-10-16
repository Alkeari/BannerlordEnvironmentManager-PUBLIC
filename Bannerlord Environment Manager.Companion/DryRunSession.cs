using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BannerlordEnvironmentManager.Companion;

internal enum LoadStatus
{
    NotObserved,
    NoOverride,
    Loading,
    Loaded,
    Threw
}

internal sealed class ObservedSubModule
{
    public ObservedSubModule(int index, string moduleId, Type type)
    {
        Index = index;
        ModuleId = moduleId;
        Type = type;
        TypeName = type.FullName ?? type.Name;
        AssemblyLocation = SafeLocation(type);
        Status = LoadStatus.NotObserved;
    }

    public int Index { get; }

    public Type Type { get; }

    public string ModuleId { get; }

    public string TypeName { get; }

    public string AssemblyLocation { get; }

    public LoadStatus Status { get; set; }

    public long? DurationMilliseconds { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public string? ExceptionStack { get; set; }

    public long StartedTicks { get; set; }

    public string Label => ModuleId.Length > 0 ? ModuleId : TypeName;

    private static string SafeLocation(Type type)
    {
        try
        {
            return type.Assembly.Location ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

// Everything in here is wrapped. A companion that destabilizes a real session is worse than no
// companion, so every entry point from the game swallows its own failures and records them in the
// result file instead of letting them reach the engine.
internal sealed class DryRunSession
{
    public const string HarmonyId = "com.alkeari.bem.dryrun";

    private const string GranularityLoadLoop = "LoadLoop";

    private const string GranularityBaseMethod = "BaseMethod";

    // A watched session runs for hours, and mods patch again when a campaign starts. Thirty seconds
    // between checks is often enough to see that happen and rare enough that the check itself, which
    // only counts what Harmony already holds, is not a cost anyone can feel.
    private const long RegistryRecheckMs = 30000;

    // Unconditional, unlike the registry recheck above: a session that never gets a new patch and
    // never throws used to leave nothing recorded between the first tick and however it ended, so a
    // silent hang and an uneventful hour of play looked identical to the trail. This is what lets
    // BEM say "still running as of here" instead of nothing at all. Sixty seconds bounds a multi-hour
    // session to a readable handful of lines while still narrowing a hang to within about a minute.
    private const long HeartbeatIntervalMs = 60000;

    private static DryRunSession? _current;

    private static Action<MBSubModuleBase>? _loadInvoker;

    private readonly object _gate = new object();

    private readonly Dictionary<Type, ObservedSubModule> _byType = new Dictionary<Type, ObservedSubModule>();

    private readonly List<ObservedSubModule> _observed = new List<ObservedSubModule>();

    private readonly List<string> _errors = new List<string>();

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly BreadcrumbWriter _breadcrumbs;

    private readonly string _runId;

    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private ModuleMap _map = ModuleMap.Empty;

    private string _granularity = GranularityLoadLoop;

    private string _degradedReason = string.Empty;

    private int _subModuleCount;

    private int _companionIndex = -1;

    private int _patchedOverrides;

    private int _patchFailures;

    private PatchCaptureResult _patches = PatchCaptureResult.Empty;

    private FirstChanceRecorder? _firstChance;

    private PatchWatch? _patchWatch;

    private readonly CompanionMode _mode;

    private bool _capturedFirstTick;

    private long _lastRegistryCheckMs;

    private long _lastHeartbeatMs;

    private int _lastRegistryMethodCount;

    private DryRunSession(string runId, CompanionMode mode)
    {
        _runId = runId;
        _mode = mode;
        _breadcrumbs = new BreadcrumbWriter(DryRunFiles.GetBreadcrumbPath(runId));
    }

    // One session type for both modes. A dry run and a watched play session observe exactly the same
    // things in exactly the same way; the only difference is that the dry run stops at the first
    // frame and the watch keeps going for as long as the player plays. Two implementations of the
    // same observation would mean the timeline a crash is read from is not the timeline the boot
    // check was proved against.
    public static DryRunSession? Start(string runId, CompanionMode mode)
    {
        var session = new DryRunSession(runId, mode);
        _current = session;

        session.Note(w => w
            .Text("kind", "run-start")
            .Text("runId", runId)
            .Text("mode", mode.ToString()));

        session._map = ModuleMap.Build();

        // Started before the patch so it sees the module loading window, which is where the
        // reference install threw 200 exceptions in 54 seconds.
        session._firstChance = FirstChanceRecorder.Start(session._map, runId, mode.ToString());
        session.InstallPatches();

        return session;
    }

    public void NotePhase(string phase) =>
        Note(w => w.Text("kind", "phase").Text("phase", phase));

    // One Harmony patch, on one TaleWorlds method, and none at all on any mod's code. The engine's
    // own load loop is
    //
    //     Managed.AddConstructorDelegateOfClass<SpawnedItemEntity>();
    //     foreach (var value in _subModuleBases.Values) value.OnSubModuleLoad();
    //
    // with no try/catch anywhere in it, so running that loop here instead gives the same order, the
    // same calls and the same propagation, and a begin, end or throw record around each call. The
    // earlier design put a prefix and a finalizer on all 142 derived overrides, which meant every
    // mod's own OnSubModuleLoad was executing as a Harmony-generated copy of itself rather than as
    // itself. Obfuscated and anti-tampered mods do not survive that, and the dry run then reported a
    // crash the real launch does not have.
    private void InstallPatches()
    {
        Harmony harmony;

        // Written before anything is patched, not after. Everything below rewrites live methods in
        // a live game, and the failure that costs the most is the one that ends the process
        // rather than throwing: no handler runs, no summary is written, and the trail would stop at
        // the last line before this. That line says the companion got as far as arming itself, which
        // is the difference between "watching kills the game" and knowing where it died.
        Note(w => w.Text("kind", "patch-install-begin"));

        try
        {
            harmony = new Harmony(HarmonyId);
        }
        catch (Exception ex)
        {
            Fail("Creating the Harmony instance failed: " + Describe(ex));
            return;
        }

        InstallLoadLoop(harmony);

        // Harmony's own patching entry points, watched so the trail says WHO was patching at the
        // moment anything went wrong. Nothing here touches a mod's code.
        _patchWatch = PatchWatch.Install(_map, _breadcrumbs, _clock, harmony, Fail);

        Note(w => w
            .Text("kind", "patch-watch")
            .Number("installed", _patchWatch?.Installed ?? 0)
            .Number("installFailures", _patchWatch?.InstallFailures ?? 0)
            .Text("degradedReason", _patchWatch?.DegradedReason ?? "The patch watch could not be created at all."));
    }

    private void InstallLoadLoop(Harmony harmony)
    {
        var loop = FindLoadLoop();

        if (loop is null || FindSubModuleBasesField() is null || BuildLoadInvoker() is null)
        {
            _granularity = GranularityBaseMethod;
            _degradedReason =
                "TaleWorlds.MountAndBlade.Module.InitializeSubModuleBases or its _subModuleBases field was not "
                + "found on this build, so the companion could not stand in for the engine's own load loop. "
                + "Breadcrumbs come from MBSubModuleBase.OnSubModuleLoad itself, which every derived override "
                + "calls first, so a module is named as it starts loading but a throw inside its own body is not "
                + "observed. Treat the failing module as inferred from the last module that began, not as observed.";

            PatchBaseMethod(harmony);
            return;
        }

        try
        {
            harmony.Patch(loop, new HarmonyMethod(GetOwnMethod(nameof(LoadLoopPrefix))));
            _patchedOverrides = 1;
        }
        catch (Exception ex)
        {
            _patchFailures++;
            Fail("Patching Module.InitializeSubModuleBases failed: " + Describe(ex));

            _granularity = GranularityBaseMethod;
            _degradedReason =
                "Patching TaleWorlds.MountAndBlade.Module.InitializeSubModuleBases failed, so the companion fell "
                + "back to MBSubModuleBase.OnSubModuleLoad, which every derived override calls first. A module is "
                + "named as it starts loading but a throw inside its own body is not observed.";

            PatchBaseMethod(harmony);
        }
    }

    private static MethodInfo? FindLoadLoop()
    {
        try
        {
            return typeof(TaleWorlds.MountAndBlade.Module).GetMethod(
                "InitializeSubModuleBases", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return null;
        }
    }

    // OnSubModuleLoad is protected, so the engine's `value.OnSubModuleLoad()` cannot be written here
    // in C#. An open instance delegate over the base declaration dispatches virtually, so this calls
    // exactly the override the engine would have called, with no reflection cost per module and no
    // TargetInvocationException wrapping to unpick around a throw.
    private static Action<MBSubModuleBase>? BuildLoadInvoker()
    {
        if (_loadInvoker != null)
            return _loadInvoker;

        try
        {
            var method = typeof(MBSubModuleBase).GetMethod(
                "OnSubModuleLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (method is null)
                return null;

            _loadInvoker = (Action<MBSubModuleBase>)Delegate.CreateDelegate(
                typeof(Action<MBSubModuleBase>), method, throwOnBindFailure: false);
        }
        catch
        {
            _loadInvoker = null;
        }

        return _loadInvoker;
    }

    private static FieldInfo? FindSubModuleBasesField()
    {
        try
        {
            return typeof(TaleWorlds.MountAndBlade.Module).GetField(
                "_subModuleBases", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return null;
        }
    }

    // Returning false skips the engine's own loop because this one has replaced it. Returning true
    // leaves the engine to load the modules exactly as it always would, which is what happens if
    // anything at all here is not as expected: a dry run that measures nothing is a great deal
    // better than one that changes what it is measuring.
    private static bool LoadLoopPrefix(object __instance) => _current?.RunLoadLoop(__instance) != true;

    private bool RunLoadLoop(object moduleInstance)
    {
        System.Collections.ICollection bases;
        var load = BuildLoadInvoker();

        if (load is null)
        {
            Fail("MBSubModuleBase.OnSubModuleLoad could not be bound, so the engine's own load loop was left alone.");
            return false;
        }

        try
        {
            if (FindSubModuleBasesField()?.GetValue(moduleInstance) is not System.Collections.IDictionary map)
            {
                Fail("Module._subModuleBases was not readable, so the engine's own load loop was left alone.");
                return false;
            }

            bases = map.Values;
        }
        catch (Exception ex)
        {
            Fail("Reading Module._subModuleBases failed, so the engine's own load loop was left alone: " + Describe(ex));
            return false;
        }

        Discover(bases);

        // The one statement the engine's loop does before loading anything. It registers a native
        // constructor delegate, and skipping it would break item spawning later in a way that would
        // look like a mod's fault.
        TaleWorlds.DotNet.Managed.AddConstructorDelegateOfClass<TaleWorlds.MountAndBlade.SpawnedItemEntity>();

        var index = 0;

        foreach (MBSubModuleBase subModule in bases)
        {
            var observed = index < _observed.Count ? _observed[index] : Observe(subModule);
            index++;

            OnBegin(observed);

            try
            {
                load(subModule);
            }
            catch (Exception ex)
            {
                OnEnd(observed, ex);

                // Rethrown, not wrapped and not swallowed: the engine has no catch here either, so
                // this has to reach the same place with the same stack it would have reached without
                // the companion in the process at all.
                throw;
            }

            OnEnd(observed, null);
        }

        return true;
    }

    // The count is the single most load-bearing measurement in the design, so it is recorded as its
    // own breadcrumb the moment it is known and again as a first-class field in the result.
    private void Discover(System.Collections.ICollection bases)
    {
        var index = 0;

        try
        {
            foreach (MBSubModuleBase subModule in bases)
            {
                var type = subModule.GetType();

                if (type == typeof(SubModule))
                    _companionIndex = index;

                var observed = new ObservedSubModule(index, _map.Resolve(type), type);
                _observed.Add(observed);

                if (!_byType.ContainsKey(type))
                    _byType.Add(type, observed);

                index++;
            }
        }
        catch (Exception ex)
        {
            Fail("Enumerating submodules failed: " + Describe(ex));
        }

        _subModuleCount = index;

        // Every submodule in the loop is watched directly, including the ones the old design had to
        // report as NotObserved because they loaded before the companion did.
        _patchedOverrides = _subModuleCount;

        Note(w => w
            .Text("kind", "discovery")
            .Number("subModuleCount", _subModuleCount)
            .Number("companionIndex", _companionIndex)
            .Text("granularity", _granularity)
            .Text("degradedReason", _degradedReason));

        Note(w => w
            .Text("kind", "patched")
            .Number("patchedOverrides", _patchedOverrides)
            .Number("skippedNoOverride", 0)
            .Number("patchFailures", _patchFailures));
    }

    private void PatchBaseMethod(Harmony harmony)
    {
        try
        {
            var method = typeof(MBSubModuleBase).GetMethod(
                "OnSubModuleLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (method is null)
            {
                Fail("MBSubModuleBase.OnSubModuleLoad was not found, so no breadcrumbs could be installed at all.");
                return;
            }

            harmony.Patch(method, Prefix(), null, null, Finalizer());
            _patchedOverrides = 1;
        }
        catch (Exception ex)
        {
            _patchFailures++;
            Fail("Patching MBSubModuleBase.OnSubModuleLoad failed: " + Describe(ex));
        }

        Note(w => w
            .Text("kind", "patched")
            .Number("patchedOverrides", _patchedOverrides)
            .Number("skippedNoOverride", 0)
            .Number("patchFailures", _patchFailures));
    }

    private static HarmonyMethod Prefix() => new HarmonyMethod(GetOwnMethod(nameof(BeginPatch)));

    private static HarmonyMethod Finalizer() => new HarmonyMethod(GetOwnMethod(nameof(EndPatch)));

    private static MethodInfo GetOwnMethod(string name) =>
        typeof(DryRunSession).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(DryRunSession).FullName, name);

    // Only the degraded fallback reaches these, where the patch is on MBSubModuleBase itself.
    private static void BeginPatch(object __instance)
    {
        var session = _current;

        if (session != null)
            session.OnBegin(session.Observe(__instance));
    }

    private static void EndPatch(object __instance, Exception? __exception)
    {
        var session = _current;

        if (session != null)
            session.OnEnd(session.Observe(__instance), __exception);
    }

    private void OnBegin(ObservedSubModule? observed)
    {
        try
        {
            if (observed is null)
                return;

            lock (_gate)
            {
                observed.Status = LoadStatus.Loading;
                observed.StartedTicks = _clock.ElapsedMilliseconds;
            }

            Note(w => w
                .Text("kind", "begin")
                .Text("module", observed.ModuleId)
                .Text("type", observed.TypeName)
                .Number("index", observed.Index));
        }
        catch
        {
            // Never throw into a module's own load.
        }
    }

    private void OnEnd(ObservedSubModule? observed, Exception? exception)
    {
        try
        {
            if (observed is null)
                return;

            lock (_gate)
            {
                observed.DurationMilliseconds = _clock.ElapsedMilliseconds - observed.StartedTicks;

                if (exception is null)
                {
                    observed.Status = LoadStatus.Loaded;
                }
                else
                {
                    observed.Status = LoadStatus.Threw;
                    observed.ExceptionType = exception.GetType().FullName;
                    observed.ExceptionMessage = exception.Message;
                    observed.ExceptionStack = DescribeChain(exception);
                }
            }

            Note(w =>
            {
                w.Text("kind", exception is null ? "end" : "throw")
                    .Text("module", observed.ModuleId)
                    .Text("type", observed.TypeName)
                    .Number("index", observed.Index)
                    .Number("durationMs", observed.DurationMilliseconds);

                if (exception != null)
                {
                    w.Text("exceptionType", observed.ExceptionType)
                        .Text("exceptionMessage", observed.ExceptionMessage);
                }
            });
        }
        catch
        {
            // A finalizer that throws would replace the module's own exception with ours, which
            // would destroy exactly the evidence this run exists to collect.
        }
    }

    private ObservedSubModule? Observe(object instance)
    {
        if (instance is null)
            return null;

        var type = instance.GetType();

        lock (_gate)
        {
            if (_byType.TryGetValue(type, out var known))
                return known;

            // Base-method granularity sees instances that were never in the discovery pass, and so
            // does any module that constructs a submodule base later.
            var observed = new ObservedSubModule(_observed.Count, _map.Resolve(type), type);
            _byType.Add(type, observed);
            _observed.Add(observed);
            return observed;
        }
    }

    // The watch equivalent of Complete, called on every frame and doing work on almost none of them.
    // The first tick is the first moment every module has finished every initialization callback, so
    // it is where the registry is taken; after that this only lets the first-chance recorder reset
    // its per-frame budget and flush, and re-takes the registry when Harmony's own method count has
    // grown, which is what a mod patching at campaign start looks like from here.
    public void WatchTick()
    {
        _firstChance?.OnTick();

        if (!_capturedFirstTick)
        {
            _capturedFirstTick = true;
            _lastRegistryCheckMs = _clock.ElapsedMilliseconds;
            _lastHeartbeatMs = _clock.ElapsedMilliseconds;
            NotePhase("first-tick");
            CapturePatches();
            _lastRegistryMethodCount = _patches.MethodCount;
            WriteResult();

            return;
        }

        if (_clock.ElapsedMilliseconds - _lastHeartbeatMs >= HeartbeatIntervalMs)
        {
            _lastHeartbeatMs = _clock.ElapsedMilliseconds;
            Note(w => w.Text("kind", "heartbeat").Number("atMs", _clock.ElapsedMilliseconds));
        }

        if (_clock.ElapsedMilliseconds - _lastRegistryCheckMs < RegistryRecheckMs)
            return;

        _lastRegistryCheckMs = _clock.ElapsedMilliseconds;

        if (CountPatchedMethods() <= _lastRegistryMethodCount)
            return;

        CapturePatches();
        _lastRegistryMethodCount = _patches.MethodCount;
        WriteResult();
    }

    // Flushed when the game shuts down cleanly, so a session that ends normally is not left with
    // only what the last five-second flush happened to catch.
    public void CompleteWatch()
    {
        NotePhase("shutdown");
        FlushFirstChance();
        Note(w => w.Text("kind", "exit"));
        _breadcrumbs.Dispose();
    }

    private int CountPatchedMethods()
    {
        try
        {
            var count = 0;

            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                if (method != null)
                    count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    public string Complete()
    {
        var path = DryRunFiles.GetResultPath(_runId);

        try
        {
            NotePhase("first-tick");
            CapturePatches();
            WriteResult();
        }
        catch (Exception ex)
        {
            Fail("Writing the result file failed: " + Describe(ex));
        }

        FlushFirstChance();

        Note(w => w.Text("kind", "exit"));
        _breadcrumbs.Dispose();

        return path;
    }

    private void WriteResult()
    {
        var path = DryRunFiles.GetResultPath(_runId);

        try
        {
            var json = BuildResult();
            var folder = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // Written whole through a temporary file so BEM can never read a half written result.
            var temporary = path + ".partial";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temporary, path);

            Note(w => w.Text("kind", "capture").Text("result", path));
        }
        catch (Exception ex)
        {
            Fail("Writing the result file failed: " + Describe(ex));
            Note(w => w.Text("kind", "capture-failed").Text("error", Describe(ex)));
        }
    }

    // At the first tick, and only here: every module has finished every initialization callback by
    // now, so this is the first moment Harmony's own answer is the whole answer.
    private void CapturePatches()
    {
        try
        {
            _patches = PatchCapture.Capture(_map, Fail);

            Note(w => w
                .Text("kind", "patch-registry")
                .Number("patchedMethodCount", _patches.MethodCount)
                .Number("patchCount", _patches.Patches.Count)
                .Number("unattributedPatches", _patches.Unattributed));
        }
        catch (Exception ex)
        {
            _patches = PatchCaptureResult.Empty;
            Fail("Capturing the patch registry failed: " + Describe(ex));
        }
    }

    private void FlushFirstChance()
    {
        var recorder = _firstChance;

        if (recorder == null)
            return;

        _firstChance = null;

        try
        {
            recorder.Flush();
            recorder.Stop();
        }
        catch (Exception ex)
        {
            Fail("Writing the first-chance trace failed: " + Describe(ex));
        }
    }

    private string BuildResult()
    {
        var writer = new JsonWriter();
        var failed = 0;

        lock (_gate)
        {
            foreach (var observed in _observed)
            {
                if (observed.Status == LoadStatus.Threw)
                    failed++;
            }

            writer.BeginObject()
                .Number("schema", 1)
                .Text("runId", _runId)
                .Text("mode", _mode.ToString())
                .Text("companionVersion", CompanionVersion())
                .Text("startedUtc", _startedUtc.ToString("O"))
                .Text("completedUtc", DateTime.UtcNow.ToString("O"))
                .Number("elapsedMs", _clock.ElapsedMilliseconds)
                .Text("gameVersion", GameVersion())
                .Number("subModuleCount", _subModuleCount)
                .Number("companionIndex", _companionIndex)
                .Text("granularity", _granularity)
                .Boolean("isDegraded", _granularity != GranularityLoadLoop)
                .Text("degradedReason", _degradedReason)
                .Number("patchedOverrides", _patchedOverrides)
                .Number("skippedNoOverride", 0)
                .Number("patchFailures", _patchFailures)
                .Boolean("reachedFirstTick", true)
                .Number("failedModuleCount", failed)
                .Number("patchedMethodCount", _patches.MethodCount)
                .Number("unattributedPatches", _patches.Unattributed)
                .Number("patchWatchInstalled", _patchWatch?.Installed ?? 0)
                .Number("patchWatchFailures", _patchWatch?.InstallFailures ?? 0)
                .Number("patchScans", _patchWatch?.Scans ?? 0)
                .Number("patchScanThrows", _patchWatch?.ScanThrows ?? 0)
                .Number("patchClassesConsidered", _patchWatch?.PatchClassesConsidered ?? 0)
                .Number("patchClassThrows", _patchWatch?.ClassThrows ?? 0)
                .Number("manualPatchCalls", _patchWatch?.ManualPatches ?? 0)
                .Number("manualPatchThrows", _patchWatch?.ManualThrows ?? 0)
                .Text("patchWatchDegradedReason", _patchWatch?.DegradedReason ?? string.Empty)
                .Boolean("booted", failed == 0);

            writer.BeginArray("modules");

            foreach (var observed in _observed)
            {
                writer.BeginObject()
                    .Text("id", observed.ModuleId)
                    .Text("type", observed.TypeName)
                    .Number("index", observed.Index)
                    .Text("status", observed.Status.ToString())
                    .Number("durationMs", observed.DurationMilliseconds)
                    .Text("assembly", observed.AssemblyLocation)
                    .Text("exceptionType", observed.ExceptionType)
                    .Text("exceptionMessage", observed.ExceptionMessage)
                    .Text("exceptionStack", observed.ExceptionStack)
                    .EndObject();
            }

            writer.EndArray();

            writer.BeginArray("patches");

            foreach (var patch in _patches.Patches)
            {
                writer.BeginObject()
                    .Text("type", patch.TargetType)
                    .Text("method", patch.TargetMethod)
                    .Text("kind", patch.Kind)
                    .Text("module", patch.ModuleId)
                    .Text("owner", patch.Owner)
                    .Text("attributedBy", patch.AttributedBy)
                    .Text("patchMethod", patch.PatchMethod)
                    .EndObject();
            }

            writer.EndArray();
            writer.Strings("companionErrors", _errors.ToArray());
        }

        return writer.EndObject().ToString();
    }

    private static string CompanionVersion()
    {
        try
        {
            return typeof(DryRunSession).Assembly.GetName().Version?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GameVersion()
    {
        try
        {
            return TaleWorlds.Engine.Utilities.GetApplicationVersionWithBuildNumber().ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void Note(Action<JsonWriter> fill) => _breadcrumbs.Write(fill);

    private void Fail(string message)
    {
        lock (_gate)
        {
            if (_errors.Count < 64)
                _errors.Add(message);
        }
    }

    private static string Describe(Exception ex) => ex.GetType().FullName + ": " + ex.Message;

    private static string DescribeChain(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var depth = 0;

        while (current != null && depth < 8)
        {
            builder.Append(current.GetType().FullName).Append(": ").Append(current.Message).Append('\n');

            if (current.StackTrace != null)
                builder.Append(current.StackTrace).Append('\n');

            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }
}
