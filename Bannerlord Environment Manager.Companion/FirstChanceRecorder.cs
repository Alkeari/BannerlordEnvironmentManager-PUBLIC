using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace BannerlordEnvironmentManager.Companion;

// Watches every managed exception at the moment it is thrown, before the runtime looks for a handler.
//
// Three measured facts shape this and getting any of them wrong breaks it.
//
// 1. e.Exception.StackTrace holds ONE frame at first-chance time. The trace is accreted while the
//    exception unwinds, and at first chance nothing has unwound. So the stack is taken from the
//    CURRENT thread with new StackTrace(false), which at that instant is the throw site. This is the
//    capability no crash report has: it predates every catch-and-rethrow and every
//    TargetInvocationException wrapper. StackTrace.ToString() is never called: it roughly doubles
//    the cost of the capture.
//
// 2. An exception thrown inside the handler re-enters the handler. An unguarded handler that threw
//    took the host process down during research, exit code 6, no output. The [ThreadStatic] guard
//    and the swallow-all catch are not defensive style, they are the reason this can ship.
//
// 3. Cost is not the constraint. A throw already costs 30-77 us on this CLR and a full capture adds
//    14-43 us. The constraint is signal: about half of the load-time traffic on the reference
//    install is infrastructure noise with a stable, enumerable shape, so it is rejected on type and
//    message alone before anything is captured, which measured free.
//
// The buffer is a bounded ring, not a log. The reference tool wrote 1407 records containing 4
// distinct frames and then went deaf for the rest of each session at a hard 200 cap, which is
// exactly the wrong shape: it is silent during the ten seconds before the crash. This keeps the
// recent past and says plainly when it wrapped.
internal sealed class FirstChanceRecorder
{
    public const int Capacity = 512;

    private const int FramesPerRecord = 24;

    private const int PerTickBudget = 8;

    private const int RepeatCap = 3;

    private const int ConsecutiveFailureLimit = 3;

    private const int MaxCountedTypes = 256;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // Reproduced during research: without this an exception raised inside the handler re-enters it
    // and the process dies with no managed exception surfacing at all.
    [ThreadStatic]
    private static bool _inHandler;

    private static FirstChanceRecorder? _current;

    private readonly object _gate = new object();

    private readonly long[] _when = new long[Capacity];

    private readonly int[] _sequence = new int[Capacity];

    private readonly Type?[] _type = new Type?[Capacity];

    private readonly string?[] _message = new string?[Capacity];

    private readonly MethodBase?[][] _frames = new MethodBase?[Capacity][];

    private readonly int[] _frameCount = new int[Capacity];

    private readonly int[] _repeats = new int[Capacity];

    private readonly byte[] _class = new byte[Capacity];

    private readonly string?[] _module = new string?[Capacity];

    // What the throwing thread was doing to Harmony at the instant of the throw. This is the field
    // that answers the crash this whole feature exists for: a NullReferenceException raised inside
    // Harmony.PatchAll names Harmony and the game and nothing in between, and the stamp names the mod.
    private readonly string?[] _patchModule = new string?[Capacity];

    private readonly string?[] _patchActivity = new string?[Capacity];

    private readonly Dictionary<Type, int> _counts = new Dictionary<Type, int>();

    private readonly Dictionary<long, int> _seen = new Dictionary<long, int>();

    private readonly Dictionary<Assembly, byte> _assemblyClass = new Dictionary<Assembly, byte>();

    private readonly Dictionary<Assembly, string> _assemblyModule = new Dictionary<Assembly, string>();

    private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();

    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private readonly ModuleMap _map;

    private readonly string _runId;

    private readonly string _mode;

    private readonly string _path;

    private int _next;

    private int _written;

    private int _observed;

    private int _filtered;

    private int _captured;

    private int _dropped;

    private int _flushedCount = -1;

    private int _flushedObserved = -1;

    private int _thisTick;

    private int _consecutiveFailures;

    private bool _disabled;

    private string _disabledReason = string.Empty;

    private bool _attached;

    private FirstChanceRecorder(ModuleMap map, string runId, string mode, string path)
    {
        _map = map;
        _runId = runId;
        _mode = mode;
        _path = path;

        for (var i = 0; i < Capacity; i++)
            _frames[i] = new MethodBase?[FramesPerRecord];
    }

    private const byte ClassUnknown = 0;

    private const byte ClassInfrastructure = 1;

    private const byte ClassGameOnly = 2;

    private const byte ClassPatchedGame = 3;

    private const byte ClassModOrigin = 4;

    public static FirstChanceRecorder? Start(ModuleMap map, string runId, string mode)
    {
        try
        {
            var recorder = new FirstChanceRecorder(
                map, runId, mode, DryRunFiles.GetFirstChancePath(runId));

            AppDomain.CurrentDomain.FirstChanceException += recorder.OnFirstChance;
            AppDomain.CurrentDomain.UnhandledException += recorder.OnUnhandled;
            recorder._attached = true;
            _current = recorder;

            return recorder;
        }
        catch
        {
            // A watch that cannot attach is a watch that does not run. It is never a crash.
            return null;
        }
    }

    public void Stop()
    {
        try
        {
            if (!_attached)
                return;

            _attached = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;

            if (ReferenceEquals(_current, this))
                _current = null;
        }
        catch
        {
            // Nothing useful is left to do on the way out.
        }
    }

    // Called from OnApplicationTick. The per-frame budget resets here and the flush happens here,
    // never inside the handler: writing to disk during exception dispatch is how a diagnostic
    // becomes the fault.
    public void OnTick()
    {
        try
        {
            _thisTick = 0;

            if (_sinceFlush.Elapsed < FlushInterval)
                return;

            // Only when there is something new to say. A watched session runs for hours, and
            // re-rendering an unchanged ring buffer every five seconds for all of them is a hitch on
            // the main thread that buys nothing: the file on disk is already correct.
            if (_captured == _flushedCount && _observed == _flushedObserved)
            {
                _sinceFlush.Restart();
                return;
            }

            Flush();
        }
        catch
        {
            // A tick that cannot flush still has to return to the game.
        }
    }

    public void Flush()
    {
        try
        {
            _sinceFlush.Restart();
            _flushedCount = _captured;
            _flushedObserved = _observed;

            var json = Render();
            var folder = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // Written whole through a temporary so a kill part way through leaves the previous flush
            // intact rather than a half file. A native crash can end the process with no managed
            // handler running at all, which turns "lost the trace" into "lost the last five seconds".
            var temporary = _path + ".partial";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(_path))
                File.Delete(_path);

            File.Move(temporary, _path);
        }
        catch
        {
            // A trace that cannot be written must never become a crash inside the game.
        }
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Flush();
        }
        catch
        {
            // The process is already going down.
        }
    }

    private void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
    {
        if (_inHandler)
            return;

        _inHandler = true;

        try
        {
            if (_disabled)
                return;

            var exception = e.Exception;

            if (exception == null)
                return;

            _observed++;
            Count(exception.GetType());

            // Type and message only, before anything is captured. Measured to cost nothing, and it
            // rejects about half of the load-time traffic on the reference install.
            if (IsKnownNoise(exception))
            {
                _filtered++;
                return;
            }

            var repeat = NoteRepeat(exception);

            if (repeat > RepeatCap)
            {
                _filtered++;
                Bump(exception);
                return;
            }

            if (_thisTick >= PerTickBudget)
            {
                _dropped++;
                return;
            }

            _thisTick++;
            Capture(exception, repeat);
            _consecutiveFailures = 0;
        }
        catch
        {
            _consecutiveFailures++;

            if (_consecutiveFailures > ConsecutiveFailureLimit)
            {
                _disabled = true;
                _disabledReason = ConsecutiveFailureLimit + " consecutive internal failures";
            }
        }
        finally
        {
            _inHandler = false;
        }
    }

    private void Capture(Exception exception, int repeat)
    {
        // The whole reason this layer exists. fNeedFileInfo stays false: true loads PDBs.
        var stack = new StackTrace(false);
        var slot = _next;

        lock (_gate)
        {
            _next = (_next + 1) % Capacity;

            if (_written < Capacity)
                _written++;
        }

        _captured++;

        _when[slot] = DateTime.UtcNow.Ticks;
        _sequence[slot] = _observed;
        _type[slot] = exception.GetType();
        _message[slot] = exception.Message;
        _repeats[slot] = repeat;

        // Read from thread-static state that PatchWatch keeps up to date, so this costs two field
        // reads and no stack walk of its own.
        PatchWatch.ReadActive(out var patchModule, out var patchActivity);
        _patchModule[slot] = patchModule;
        _patchActivity[slot] = patchActivity;

        var frames = _frames[slot];
        var count = 0;
        var depth = stack.FrameCount;

        for (var i = 0; i < depth && count < FramesPerRecord; i++)
        {
            var frame = stack.GetFrame(i);

            if (frame == null)
                continue;

            var method = frame.GetMethod();

            if (method == null)
                continue;

            // Skip this handler's own frames: they are the same three every time and they crowd out
            // the frames worth keeping.
            if (count == 0 && method.DeclaringType == typeof(FirstChanceRecorder))
                continue;

            frames[count++] = method;
        }

        for (var i = count; i < FramesPerRecord; i++)
            frames[i] = null;

        _frameCount[slot] = count;

        // Resolving frames measured nearly free once the stack is captured, so it happens now rather
        // than being deferred to a flush that a native crash may never reach.
        Classify(frames, count, out var classification, out var module);
        _class[slot] = classification;
        _module[slot] = module;
    }

    private void Classify(MethodBase?[] frames, int count, out byte classification, out string module)
    {
        classification = ClassUnknown;
        module = string.Empty;

        for (var i = 0; i < count; i++)
        {
            var method = frames[i];
            var declaring = method?.DeclaringType;
            var assembly = declaring?.Assembly;

            if (assembly == null)
                continue;

            var assemblyClass = ClassOf(assembly);

            if (assemblyClass == ClassUnknown || assemblyClass == ClassInfrastructure)
                continue;

            if (assemblyClass == ClassModOrigin)
            {
                classification = ClassModOrigin;
                module = ModuleOf(assembly);
                return;
            }

            // A Harmony replacement is named X_PatchN, which is the same shape a crash frame carries.
            // Game code executing under that name has a module's code inside it.
            classification = method != null && method.Name != null && method.Name.Contains("_Patch")
                ? ClassPatchedGame
                : ClassGameOnly;

            return;
        }

        if (count > 0)
            classification = ClassInfrastructure;
    }

    private byte ClassOf(Assembly assembly)
    {
        byte known;

        if (_assemblyClass.TryGetValue(assembly, out known))
            return known;

        var value = Determine(assembly);

        if (_assemblyClass.Count < 1024)
            _assemblyClass[assembly] = value;

        return value;
    }

    private byte Determine(Assembly assembly)
    {
        string name;

        try
        {
            name = assembly.GetName().Name ?? string.Empty;
        }
        catch
        {
            return ClassUnknown;
        }

        if (StartsWith(name, "MonoMod") || StartsWith(name, "HarmonyLib") || StartsWith(name, "0Harmony")
            || StartsWith(name, "Mono.Cecil"))
        {
            return ClassInfrastructure;
        }

        if (StartsWith(name, "System") || StartsWith(name, "mscorlib") || StartsWith(name, "Microsoft")
            || StartsWith(name, "netstandard") || StartsWith(name, "WindowsBase"))
        {
            return ClassUnknown;
        }

        if (ModuleOf(assembly).Length > 0)
            return ClassModOrigin;

        return StartsWith(name, "TaleWorlds") || StartsWith(name, "SandBox") || StartsWith(name, "StoryMode")
            ? ClassGameOnly
            : ClassUnknown;
    }

    private string ModuleOf(Assembly assembly)
    {
        string known;

        if (_assemblyModule.TryGetValue(assembly, out known))
            return known;

        var module = string.Empty;

        try
        {
            module = assembly.IsDynamic ? string.Empty : _map.ResolveLocation(assembly.Location ?? string.Empty);
        }
        catch
        {
            module = string.Empty;
        }

        if (_assemblyModule.Count < 1024)
            _assemblyModule[assembly] = module;

        return module;
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private void Count(Type type)
    {
        int count;

        if (_counts.TryGetValue(type, out count))
        {
            _counts[type] = count + 1;
            return;
        }

        // Counting without capturing is the measurement the research could not take, so it runs
        // before every filter. The cap exists so a pathological session cannot grow it without end.
        if (_counts.Count < MaxCountedTypes)
            _counts[type] = 1;
    }

    private int NoteRepeat(Exception exception)
    {
        var key = KeyOf(exception);
        int seen;

        if (_seen.TryGetValue(key, out seen))
        {
            seen++;
            _seen[key] = seen;
            return seen;
        }

        if (_seen.Count < 4096)
            _seen[key] = 1;

        return 1;
    }

    private void Bump(Exception exception)
    {
        var key = KeyOf(exception);

        lock (_gate)
        {
            for (var i = 0; i < _written; i++)
            {
                if (_type[i] != null && KeyOf(_type[i]!, _message[i]) == key)
                {
                    _repeats[i]++;
                    return;
                }
            }
        }
    }

    // A composed hash rather than a built string: the handler runs inside the CLR's exception path
    // and must not allocate there.
    private static long KeyOf(Exception exception) =>
        KeyOf(exception.GetType(), exception.Message);

    private static long KeyOf(Type type, string? message) =>
        ((long)type.GetHashCode() << 32) ^ (message == null ? 0 : message.GetHashCode());

    private static bool IsKnownNoise(Exception exception)
    {
        try
        {
            var message = exception.Message ?? string.Empty;

            // Harmony and MonoMod probing abstract methods during PatchAll. 69 of 200 on the
            // reference install's first session.
            if (exception is ArgumentNullException
                && message.IndexOf("Abstract methods cannot be prepared", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            // The BCL XmlSerializer probes for a pre-generated serializer assembly and expects the
            // miss. 21 of 200 on the reference install.
            if (exception is FileNotFoundException
                && message.IndexOf(".XmlSerializers", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (exception is OperationCanceledException || exception is ThreadInterruptedException
                || exception is ThreadAbortException)
            {
                return true;
            }

            if (exception is NotSupportedException || exception is ArgumentException
                || exception is AmbiguousMatchException)
            {
                return IsInfrastructure(exception.TargetSite);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInfrastructure(MethodBase? targetSite)
    {
        try
        {
            var name = targetSite?.DeclaringType?.Assembly.GetName().Name;

            return name != null
                && (StartsWith(name, "MonoMod") || StartsWith(name, "HarmonyLib") || StartsWith(name, "0Harmony"));
        }
        catch
        {
            return false;
        }
    }

    private string Render()
    {
        var writer = new JsonWriter();

        lock (_gate)
        {
            writer.BeginObject()
                .Number("schema", 1)
                .Text("runId", _runId)
                .Text("mode", _mode)
                .Text("startedUtc", _startedUtc.ToString("O"))
                .Text("flushedUtc", DateTime.UtcNow.ToString("O"))
                .Number("observed", _observed)
                .Number("filtered", _filtered)
                .Number("captured", _captured)
                .Number("dropped", _dropped)
                .Number("capacity", Capacity)
                .Boolean("wrapped", _captured > Capacity)
                .Boolean("disabled", _disabled)
                .Text("disabledReason", _disabledReason);

            writer.BeginArray("counts");

            foreach (var pair in _counts)
            {
                writer.BeginObject()
                    .Text("type", SafeName(pair.Key))
                    .Number("count", pair.Value)
                    .EndObject();
            }

            writer.EndArray();

            writer.BeginArray("records");

            // Oldest first, so the file reads forward in time even after the ring wrapped.
            var start = _captured > Capacity ? _next : 0;

            for (var i = 0; i < _written; i++)
            {
                var slot = (start + i) % Capacity;

                if (_type[slot] == null)
                    continue;

                writer.BeginObject()
                    .Number("seq", _sequence[slot])
                    .Text("t", new DateTime(_when[slot], DateTimeKind.Utc).ToString("O"))
                    .Text("type", SafeName(_type[slot]!))
                    .Text("message", _message[slot] ?? string.Empty)
                    .Text("class", NameOf(_class[slot]))
                    .Text("module", _module[slot] ?? string.Empty)
                    .Text("patchModule", _patchModule[slot] ?? string.Empty)
                    .Text("patchActivity", _patchActivity[slot] ?? string.Empty)
                    .Number("repeats", _repeats[slot]);

                writer.BeginArray("frames");

                for (var f = 0; f < _frameCount[slot]; f++)
                {
                    var method = _frames[slot][f];

                    if (method == null)
                        continue;

                    writer.Value(Describe(method));
                }

                writer.EndArray().EndObject();
            }

            writer.EndArray();
        }

        return writer.EndObject().ToString();
    }

    private static string NameOf(byte classification)
    {
        switch (classification)
        {
            case ClassInfrastructure: return "Infrastructure";
            case ClassGameOnly: return "GameOnly";
            case ClassPatchedGame: return "PatchedGame";
            case ClassModOrigin: return "ModOrigin";
            default: return "Unknown";
        }
    }

    private static string SafeName(Type type)
    {
        try
        {
            return type.FullName ?? type.Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Rendered here, at flush time, and never in the handler: formatting a stack trace inside the
    // handler roughly doubles the measured cost of the capture.
    private static string Describe(MethodBase method)
    {
        try
        {
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

}
