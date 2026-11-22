using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace BannerlordEnvironmentManager.Companion;

// The managed exceptions thrown before the game's first application tick, each written to disk the
// moment it is thrown. Measured on v1.4.8: a mod whose OnSubModuleLoad throws kills the game with only
// "Unhandled Exception Code 0xE0434352" in rgl_log_errors, no message and no stack, and
// AppDomain.UnhandledException never runs because the engine's native filter takes the exception first.
// So nothing written at the end survives, and nothing buffered for a later flush does either: the
// exception text has to be on disk before the runtime even looks for a handler.
//
// Writing during exception dispatch is what FirstChanceRecorder refuses to do, and it is allowed here
// only because the window is short and bounded: until the first tick, at most MaxRecords lines, each
// distinct type and message once. After the first tick this detaches and costs nothing.
//
// Pure BCL, no TaleWorlds and no Harmony type, so it runs in every mode the companion has and the
// tests compile the same source.
internal sealed class StartupExceptionLog
{
    public const int MaxRecords = 512;

    public const int FramesPerRecord = 16;

    private const int MessageLimit = 2000;

    [ThreadStatic]
    private static bool _inHandler;

    private readonly object _gate = new object();

    private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

    private readonly string _path;

    private readonly string _ownAssembly;

    private FileStream? _stream;

    private int _written;

    private bool _attached;

    private bool _failed;

    internal StartupExceptionLog(string path)
    {
        _path = path;
        _ownAssembly = typeof(StartupExceptionLog).Assembly.GetName().Name ?? string.Empty;
    }

    public string FilePath => _path;

    public int Written => _written;

    // Never throws: a log that cannot attach is a log that does not run.
    public static StartupExceptionLog? Arm()
    {
        try
        {
            int processId;

            using (var process = Process.GetCurrentProcess())
                processId = process.Id;

            var log = new StartupExceptionLog(DryRunFiles.GetStartupExceptionPath(processId));
            AppDomain.CurrentDomain.FirstChanceException += log.OnFirstChance;
            log._attached = true;

            return log;
        }
        catch
        {
            return null;
        }
    }

    // Called at the first tick, after which the game is past module loading and the ordinary watch and
    // guard take over.
    public void Disarm()
    {
        try
        {
            if (_attached)
            {
                _attached = false;
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            }

            lock (_gate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }

    private void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
    {
        if (_inHandler)
            return;

        _inHandler = true;

        try
        {
            // Skips this handler and the runtime's dispatch frame, so the first frame is the throw site.
            Record(e.Exception, new StackTrace(1, false));
        }
        catch
        {
            // Never a crash inside the game.
        }
        finally
        {
            _inHandler = false;
        }
    }

    // The whole record path, apart from the event so tests drive it with a real exception and stack.
    internal void Record(Exception? exception, StackTrace? stack)
    {
        if (exception is null || _failed || _written >= MaxRecords || IsNoise(exception))
            return;

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = Clip((exception.Message ?? string.Empty) + LoaderDetail(exception));

        lock (_gate)
        {
            if (!_seen.Add(type + "\n" + message))
                return;
        }

        var frames = new List<string>(FramesPerRecord);
        var module = string.Empty;

        if (stack != null)
        {
            for (var i = 0; i < stack.FrameCount && frames.Count < FramesPerRecord; i++)
            {
                var method = stack.GetFrame(i)?.GetMethod();
                var declaring = method?.DeclaringType;

                if (method is null || declaring is null)
                    continue;

                var assembly = declaring.Assembly;
                var assemblyName = assembly.GetName().Name ?? string.Empty;

                if (string.Equals(assemblyName, _ownAssembly, StringComparison.Ordinal) && frames.Count == 0)
                    continue;

                frames.Add((declaring.FullName ?? declaring.Name) + "." + method.Name + " [" + assemblyName + "]");

                if (module.Length == 0 && !string.Equals(assemblyName, _ownAssembly, StringComparison.Ordinal))
                    module = ModuleFolderOf(assembly);
            }
        }

        var writer = new JsonWriter();
        writer.BeginObject()
            .Text("t", DateTime.UtcNow.ToString("O"))
            .Text("type", type)
            .Text("message", message)
            .Text("module", module)
            .Strings("frames", frames)
            .EndObject();

        Append(writer + "\n");
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                if (_stream is null)
                {
                    var folder = Path.GetDirectoryName(_path);

                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);

                    // Create, not append: a process id is reused, and a file left by an earlier process
                    // with the same id is not evidence about this one.
                    _stream = new FileStream(
                        _path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
                }

                var bytes = Encoding.UTF8.GetBytes(line);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush(true);
                _written++;
            }
            catch
            {
                // One failed write ends the log rather than retrying inside every later throw.
                _failed = true;
            }
        }
    }

    // The module folder an assembly was loaded from: "...\Modules\<folder>\bin\...". Empty for the
    // game's own bin folder, the runtime and anything loaded from bytes.
    internal static string ModuleFolderOf(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic)
                return string.Empty;

            return ModuleFolderOfPath(assembly.Location);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string ModuleFolderOfPath(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return string.Empty;

        var parts = location!.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        for (var i = parts.Length - 3; i >= 0; i--)
        {
            if (string.Equals(parts[i], "Modules", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[i + 2], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return string.Empty;
    }

    // The same expected, caught-by-design traffic FirstChanceRecorder rejects by type and message.
    private static bool IsNoise(Exception exception)
    {
        if (exception is OperationCanceledException || exception is ThreadInterruptedException
            || exception is ThreadAbortException)
        {
            return true;
        }

        var message = exception.Message ?? string.Empty;

        return (exception is ArgumentNullException && message.IndexOf("Abstract methods cannot be prepared", StringComparison.Ordinal) >= 0)
            || (exception is FileNotFoundException && message.IndexOf(".XmlSerializers", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // What ReflectionTypeLoadException hides behind "Retrieve the LoaderExceptions property": the first few
    // reasons a type would not load ("Method X in type Y does not have an implementation").
    internal static string LoaderDetail(Exception exception)
    {
        try
        {
            var load = exception as ReflectionTypeLoadException;

            if (load?.LoaderExceptions == null)
                return string.Empty;

            var text = new StringBuilder();
            var shown = 0;

            foreach (var inner in load.LoaderExceptions)
            {
                if (inner == null)
                    continue;

                if (shown == 3)
                    break;

                text.Append(" | ").Append(inner.Message);
                shown++;
            }

            return text.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Clip(string text) =>
        text.Length <= MessageLimit ? text : text.Substring(0, MessageLimit);
}
