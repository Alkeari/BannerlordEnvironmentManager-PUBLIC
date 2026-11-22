using System;
using System.IO;
using System.Text;

namespace BannerlordEnvironmentManager.Companion;

// Output goes to LOCALAPPDATA and nowhere else. The companion never writes into the game folder and
// never touches LauncherData.xml: BEM knows where to look without the companion knowing anything
// about BEM.
internal static class DryRunFiles
{
    public const string FolderName = "Bannerlord Environment Manager";

    // Set by BEM on the game it starts, and only when that BEM runs on a data folder of its own (a driven
    // run): GameLauncher.DataRootVariable. Unset, missing or unreadable, the real folder is used.
    public const string DataRootVariable = "BEM_DATA_ROOT";

    private static string Root()
    {
        try
        {
            var given = Environment.GetEnvironmentVariable(DataRootVariable);

            if (!string.IsNullOrEmpty(given) && Path.IsPathRooted(given) && Directory.Exists(given))
                return given;
        }
        catch
        {
            // The real folder.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
    }

    // Named for what it holds rather than for the mode that used to be the only thing that wrote
    // here: a boot check and a watched play session are both a LaunchRun, and this folder has held
    // both since watching was added. A folder still called "dry-run" would say a real play session's
    // own trail was never anything but a throwaway boot check.
    public const string RunFolderName = "runs";

    public const string BreadcrumbSuffix = ".breadcrumbs.jsonl";

    public const string ResultSuffix = ".json";

    public const string FirstChanceFolderName = "first-chance";

    public const string FirstChanceSuffix = ".firstchance.json";

    // Written by BEM when the user arms a watch and deleted when they stop. Its presence is what
    // lets a launch BEM did not start still be watched, which is the launch the crash happened on.
    public const string WatchStateFileName = "watch-session.json";

    public static string GetWatchStatePath()
    {
        return Path.Combine(Root(), WatchStateFileName);
    }

    // Crash catching is on unless this file says {"enabled":false}. A bare file from an earlier BEM, which
    // armed the guard by writing it, still means on.
    public const string CrashGuardStateFileName = "crash-guard.json";

    public const string CrashGuardFolderName = "crash-guard";

    public static string GetCrashGuardStatePath()
    {
        return Path.Combine(Root(), CrashGuardStateFileName);
    }

    public static bool CrashGuardEnabled()
    {
        try
        {
            var path = GetCrashGuardStatePath();

            if (!File.Exists(path))
                return true;

            var text = File.ReadAllText(path);
            var key = text.IndexOf("\"enabled\"", StringComparison.Ordinal);

            if (key < 0)
                return true;

            var colon = text.IndexOf(':', key);

            return colon < 0 || !text.Substring(colon + 1).TrimStart().StartsWith("false", StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    public static string GetCrashGuardRecordPath(string runId)
    {
        return Path.Combine(Root(), CrashGuardFolderName, runId + ".json");
    }

    public static string GetExceptionSessionPath()
    {
        return Path.Combine(Root(), ExceptionProtocol.SessionFileName);
    }

    public static string GetRunFolder()
    {
        return Path.Combine(Root(), RunFolderName);
    }

    public static string GetFirstChancePath(string runId)
    {
        return Path.Combine(Root(), FirstChanceFolderName, runId + FirstChanceSuffix);
    }

    // One file per game process, named by its id, because BEM knows the id of the process it started
    // and nothing else about a run that died before the companion's first tick.
    public const string StartupExceptionFolderName = "startup-exceptions";

    public const string StartupExceptionSuffix = ".jsonl";

    public static string GetStartupExceptionPath(int processId)
    {
        return Path.Combine(Root(), StartupExceptionFolderName, processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + StartupExceptionSuffix);
    }

    public static string GetBreadcrumbPath(string runId) =>
        Path.Combine(GetRunFolder(), runId + BreadcrumbSuffix);

    public static string GetResultPath(string runId) =>
        Path.Combine(GetRunFolder(), runId + ResultSuffix);
}

// One JSON object per line, appended and flushed to disk as it happens. The game dying mid load is
// the normal case this feature exists for, so a partially written last line has to be survivable
// rather than exceptional: BEM discards the incomplete line and still names the module that was
// loading when everything stopped.
internal sealed class BreadcrumbWriter : IDisposable
{
    private readonly object _gate = new object();

    private readonly FileStream? _stream;

    private int _sequence;

    public BreadcrumbWriter(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // FileShare.ReadWrite so BEM can follow the file while the game is still writing it.
            _stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1024, FileOptions.WriteThrough);
        }
        catch
        {
            _stream = null;
        }
    }

    public bool IsOpen => _stream != null;

    public void Write(Action<JsonWriter> fill)
    {
        var stream = _stream;

        if (stream is null)
            return;

        try
        {
            lock (_gate)
            {
                var writer = new JsonWriter();
                writer.BeginObject();
                writer.Number("seq", ++_sequence);
                writer.Text("t", DateTime.UtcNow.ToString("O"));
                fill(writer);
                writer.EndObject();

                var bytes = Encoding.UTF8.GetBytes(writer + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch
        {
            // A breadcrumb that cannot be written must never become a crash inside the game.
        }
    }

    public void Dispose()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Nothing useful is left to do on the way out.
        }
    }
}
