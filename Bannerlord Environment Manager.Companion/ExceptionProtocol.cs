using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BannerlordEnvironmentManager.Companion;

internal enum ExceptionAnswer
{
    // No answer yet, or one that could not be read.
    None,
    Continue,
    SaveAndClose,
    Close
}

internal struct GuardSession
{
    public GuardSession(string runId, string bemPath)
    {
        RunId = runId;
        BemPath = bemPath;
    }

    public string RunId { get; }

    // The exe that shows the window. Empty: no window, and the guard acts by its own policy.
    public string BemPath { get; }

    public bool IsEmpty => string.IsNullOrEmpty(RunId);
}

// The two files BEM and the Companion pass each other for the Bannerlord Exception Manager, read by hand:
// the Companion is loaded into the owner's game and takes no JSON library with it. Nothing from the game
// is in this file, so the tests compile it directly against what Core writes.
internal static class ExceptionProtocol
{
    // A contract with Core.DryRun.ExceptionSession.FreshMinutes.
    public const int GuardFreshMinutes = 10;

    public const string SessionFileName = "exception-session.json";

    public const string DecisionSuffix = ".decision";

    // exception-session.json, written by BEM for a launch it starts. Honored only while fresh, so the file
    // never arms a launch BEM did not start: without that a Companion folder left in the game would stop
    // being inert.
    public static GuardSession ReadSession(string? json, DateTime utcNow)
    {
        try
        {
            var runId = ReadString(json, "RunId");
            var armed = ReadString(json, "ArmedUtc");

            if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(armed))
                return default;

            if (!DateTime.TryParse(
                    armed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var armedUtc))
                return default;

            var age = utcNow - armedUtc;

            if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(GuardFreshMinutes))
                return default;

            return new GuardSession(runId!, ReadString(json, "BemPath") ?? string.Empty);
        }
        catch
        {
            return default;
        }
    }

    public static ExceptionAnswer ReadDecision(string? json)
    {
        try
        {
            switch (ReadString(json, "decision"))
            {
                case "continue": return ExceptionAnswer.Continue;
                case "saveAndClose": return ExceptionAnswer.SaveAndClose;
                case "close": return ExceptionAnswer.Close;
                default: return ExceptionAnswer.None;
            }
        }
        catch
        {
            return ExceptionAnswer.None;
        }
    }

    // The load order this run was started with, in order, read from the game's own command line
    // (_MODULES_*Native*SandBox*_MODULES_). The same on every game version, and needs nothing of the game's.
    public static List<string> ModulesFrom(string commandLine)
    {
        var modules = new List<string>();

        if (string.IsNullOrEmpty(commandLine))
            return modules;

        const string marker = "_MODULES_";
        var start = commandLine.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
            return modules;

        start += marker.Length;
        var end = commandLine.IndexOf(marker, start, StringComparison.Ordinal);

        if (end < 0)
            return modules;

        foreach (var part in commandLine.Substring(start, end - start).Split('*'))
        {
            var id = part.Trim();

            if (id.Length > 0)
                modules.Add(id);
        }

        return modules;
    }

    public static string DecisionPathFor(string recordPath)
    {
        var at = recordPath.LastIndexOf('.');

        return (at < 0 ? recordPath : recordPath.Substring(0, at)) + DecisionSuffix;
    }

    // The value of one top-level string property, unescaped. Null when the property is absent or is not a
    // string. Enough for the flat objects BEM writes; it does not walk nested objects.
    public static string? ReadString(string? json, string name)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        var key = "\"" + name + "\"";
        var at = json!.IndexOf(key, StringComparison.Ordinal);

        if (at < 0)
            return null;

        var i = at + key.Length;

        while (i < json.Length && char.IsWhiteSpace(json[i]))
            i++;

        if (i >= json.Length || json[i] != ':')
            return null;

        i++;

        while (i < json.Length && char.IsWhiteSpace(json[i]))
            i++;

        if (i >= json.Length || json[i] != '"')
            return null;

        var builder = new StringBuilder();

        for (i++; i < json.Length; i++)
        {
            var c = json[i];

            if (c == '"')
                return builder.ToString();

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (++i >= json.Length)
                return null;

            switch (json[i])
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u':
                    if (i + 4 >= json.Length
                        || !int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        return null;

                    builder.Append((char)code);
                    i += 4;
                    break;
                default: builder.Append(json[i]); break;
            }
        }

        return null;
    }
}
