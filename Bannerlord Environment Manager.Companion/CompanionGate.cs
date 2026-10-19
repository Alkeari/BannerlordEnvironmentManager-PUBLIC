using System;

namespace BannerlordEnvironmentManager.Companion;

internal enum CompanionMode
{
    // Nothing on the command line asked for the companion, so it does nothing at all.
    Off,

    // Watch every module load, capture the patch registry at the first tick, exit.
    DryRun,

    // Watch first-chance exceptions during a normal session and never exit or patch anything.
    Watch
}

internal struct CompanionRequest
{
    public CompanionRequest(CompanionMode mode, string runId)
    {
        Mode = mode;
        RunId = runId;
    }

    public static CompanionRequest None => new CompanionRequest(CompanionMode.Off, string.Empty);

    public CompanionMode Mode { get; }

    public string RunId { get; }
}

// The safety property that matters most: a companion folder left behind after a failed run must be
// completely inert during a normal play session. Nothing in this assembly does anything at all
// unless the dry run's marker argument is on the command line or BEM's watch state file exists on
// disk, and BEM only ever produces either of those deliberately.
//
// Watching carries nothing on the command line on purpose. The game copies its whole argument string
// into a 4096-byte buffer and fastfails over it, killing a large load order two seconds in with
// nothing written anywhere, so a marker argument for watching would be roughly 35 characters charged
// against that budget for information that is already in a file this assembly can read for itself.
internal static class CompanionGate
{
    public const string MarkerPrefix = "/bem-dryrun:";

    public static string? ReadRunId()
    {
        var request = Read();

        return request.Mode == CompanionMode.DryRun ? request.RunId : null;
    }

    public static CompanionRequest Read()
    {
        var fromProcess = FromProcessArguments();

        if (fromProcess.Mode != CompanionMode.Off)
            return fromProcess;

        var fromEngine = FromEngineCommandLine();

        if (fromEngine.Mode != CompanionMode.Off)
            return fromEngine;

        return FromWatchState();
    }

    // The whole of watch mode's arming. BEM writes watch-session.json when the user asks it to watch
    // and deletes it when they stop, and while it is there every launch that carries this module is
    // watched, exactly as the Watch page has always said. That covers the launch BEM starts and the
    // launch the user starts from Steam or a shortcut alike, which matters because the second one is
    // the launch the crash happened on.
    //
    // The inert-by-default property is unchanged. No dry run marker and no state file means the
    // companion does nothing at all, and a companion folder left behind by a failed dry run writes no
    // state file, so it stays inert. A state file cannot be left behind by a failed run either: it is
    // written only when the user turns watching on, and stopping deletes it.
    private static CompanionRequest FromWatchState()
    {
        try
        {
            var path = DryRunFiles.GetWatchStatePath();

            if (!System.IO.File.Exists(path))
                return CompanionRequest.None;

            var armed = ReadArmedRunId(System.IO.File.ReadAllText(path));

            if (armed.Length == 0)
                return CompanionRequest.None;

            // Every launch under one armed watch is its own run, so one session's trace never
            // overwrites another's. The armed id stays the prefix, which is how BEM finds them all.
            return Build(
                CompanionMode.Watch,
                armed + "-" + CurrentProcessId() + "-" + DateTime.UtcNow.ToString("HHmmssfff"));
        }
        catch
        {
            // A gate that throws is a gate that could take the game down.
            return CompanionRequest.None;
        }
    }

    private static string CurrentProcessId()
    {
        try
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "0";
        }
    }

    // Hand read rather than deserialized: the companion is loaded into a live game, and every
    // assembly it drags in is one more chance to change what it is measuring. The file is BEM's own
    // and its shape is a contract with Core.DryRun.WatchState.
    internal static string ReadArmedRunId(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        const string Name = "\"RunId\"";
        var at = json!.IndexOf(Name, StringComparison.Ordinal);

        if (at < 0)
            return string.Empty;

        var colon = json.IndexOf(':', at + Name.Length);

        if (colon < 0)
            return string.Empty;

        var open = json.IndexOf('"', colon + 1);

        if (open < 0)
            return string.Empty;

        var close = json.IndexOf('"', open + 1);

        return close < 0 ? string.Empty : Sanitize(json.Substring(open + 1, close - open - 1));
    }

    private static CompanionRequest FromProcessArguments()
    {
        try
        {
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                var request = ReadFromToken(argument);

                if (request.Mode != CompanionMode.Off)
                    return request;
            }
        }
        catch
        {
            // A gate that throws is a gate that could take the game down. Silence is the only
            // acceptable failure here, and it fails closed: no marker means the companion is inert.
        }

        return CompanionRequest.None;
    }

    // Utilities.GetFullCommandLineString is a native call, so it is the fallback rather than the
    // primary: it sees the line the engine was actually given even if a launcher rewrote it.
    private static CompanionRequest FromEngineCommandLine()
    {
        string line;

        try
        {
            line = TaleWorlds.Engine.Utilities.GetFullCommandLineString() ?? string.Empty;
        }
        catch
        {
            return CompanionRequest.None;
        }

        foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var request = ReadFromToken(token);

            if (request.Mode != CompanionMode.Off)
                return request;
        }

        return CompanionRequest.None;
    }

    private static CompanionRequest ReadFromToken(string? token)
    {
        if (token is null)
            return CompanionRequest.None;

        if (token.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
            return Build(CompanionMode.DryRun, token.Substring(MarkerPrefix.Length));

        return CompanionRequest.None;
    }

    private static CompanionRequest Build(CompanionMode mode, string raw)
    {
        var sanitized = Sanitize(raw.Trim().Trim('"'));

        return sanitized.Length == 0 ? CompanionRequest.None : new CompanionRequest(mode, sanitized);
    }

    // The run id becomes a file name, so a hostile or accidental path separator must not escape the
    // output folder.
    private static string Sanitize(string id)
    {
        var buffer = new char[id.Length];
        var length = 0;

        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                buffer[length++] = c;
        }

        return length == 0 ? string.Empty : new string(buffer, 0, length);
    }
}
