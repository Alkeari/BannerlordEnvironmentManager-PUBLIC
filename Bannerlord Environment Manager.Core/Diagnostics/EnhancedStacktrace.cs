using System.Globalization;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One patch BUTR found sitting on a frame. The plugin id is the only part that names an installed
// thing; the method is the patch's own name and the patch type is when it ran relative to the body.
public sealed record EnhancedPatch(
    string Method,
    string PluginId,
    string Provider,
    string PatchType)
{
    public string Describe()
    {
        var parts = new List<string>();

        if (Method.Length > 0)
            parts.Add(Method);

        if (PluginId.Length > 0)
            parts.Add(Strings.Current.Format("Core.Diagnostics.EnhancedStacktrace.Plugin", PluginId));

        if (PatchType.Length > 0)
            parts.Add(PatchType.ToLowerInvariant());

        if (Provider.Length > 0)
            parts.Add(Strings.Current.Format("Core.Diagnostics.EnhancedStacktrace.Via", Provider));

        return string.Join(", ", parts);
    }
}

// One frame of a BUTR Enhanced Stacktrace. The raw frame text is the part a plain stack trace also
// carries; everything else on this record exists only in the enhanced report, and the parameter list
// is the field that turns "an exception in PatchAll" into "an exception in the overload that has to
// work out its own caller".
public sealed record EnhancedFrame(
    int Number,
    string Text,
    string ExecutingMethod,
    string DeclaringTypeFullName,
    string MethodName,
    string Parameters,
    int ParameterCount,
    string ModuleId,
    string IlOffset,
    IReadOnlyList<EnhancedPatch> Patches)
{
    public string Signature => DeclaringTypeFullName.Length == 0
        ? $"{MethodName}({Parameters})"
        : $"{DeclaringTypeFullName}.{MethodName}({Parameters})";

    public bool IsParameterless => ParameterCount == 0;

    public string Describe()
    {
        var lines = new List<string> { $"{Number}. {Signature}" };

        if (ModuleId.Length > 0)
            lines.Add(Strings.Current.Format("Core.Diagnostics.EnhancedStacktrace.ThrownInside", ModuleId));

        if (IlOffset.Length > 0)
            lines.Add(Strings.Current.Format("Core.Diagnostics.EnhancedStacktrace.IlOffset", IlOffset));

        lines.AddRange(Patches.Select(
            patch => Strings.Current.Format("Core.Diagnostics.EnhancedStacktrace.PatchedBy", patch.Describe())));

        return string.Join("\n", lines);
    }
}

// The Enhanced Stacktrace out of a BUTR crash report.
//
// A plain stack trace names a method. This names the method, its parameter list, the module the
// executing code came out of, the approximate IL offset it stopped at, and every Harmony patch
// sitting on the frame. The parameter list is the field that mattered on the crash this was built
// for: HarmonyLib.Harmony.PatchAll() and HarmonyLib.Harmony.PatchAll(Assembly) are different methods,
// only one of them has to work out which assembly called it, and a plain trace shows the same name
// for both.
//
// Nothing here loads or runs anything. It reads text a crash report already contains.
public sealed partial record EnhancedStacktrace(IReadOnlyList<EnhancedFrame> Frames)
{
    // The keys BUTR writes. Several of them share one physical line, so they are matched as a set
    // rather than assumed to start at the beginning of a line.
    private const string Keys =
        "Executing Method|Original Method|Module Id|Approximate IL Offset|Patch Methods|Plugin Id|"
        + "Patch Type|Assembly|Method|Type";

    [GeneratedRegex(@"Enhanced\s+Stacktrace", RegexOptions.IgnoreCase)]
    private static partial Regex Header { get; }

    [GeneratedRegex(@"^\s*(?<number>\d{1,4})\.\s+(?:at\s+)?(?<frame>\S.*?)\s*$")]
    private static partial Regex FrameLine { get; }

    [GeneratedRegex($@"\b(?<key>{Keys})\s*:\s*")]
    private static partial Regex KeyMarker { get; }

    public static EnhancedStacktrace None { get; } = new([]);

    public bool IsEmpty => Frames.Count == 0;

    public EnhancedFrame? Fault => Frames.Count > 0 ? Frames[0] : null;

    public string Describe() => IsEmpty
        ? Strings.Current["Core.Diagnostics.EnhancedStacktrace.NoTrace"]
        : string.Join("\n", Frames.Select(frame => frame.Describe()));

    public static EnhancedStacktrace Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, Header.IsMatch);
        var frames = new List<EnhancedFrame>();

        Builder? current = null;

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Trim().Length == 0)
                continue;

            if (FrameLine.Match(line) is { Success: true } frame)
            {
                Close(current, frames);

                current = new Builder(
                    int.TryParse(frame.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                        ? number
                        : frames.Count + 1,
                    frame.Groups["frame"].Value);

                continue;
            }

            if (current is null)
                continue;

            // A line that carries none of the enhanced keys ends the section. Without this the parse
            // walks on into whatever the report writes next and attaches it to the last frame.
            if (!Apply(current, line))
            {
                Close(current, frames);
                current = null;

                if (start < 0)
                    continue;

                break;
            }
        }

        Close(current, frames);

        // With no header to anchor on, a numbered list is not evidence of an enhanced trace by itself,
        // so only frames that carry at least one enhanced field are kept. Otherwise an ordinary
        // numbered list somewhere in a report parses as a stack.
        if (start < 0)
            frames.RemoveAll(f => f.ExecutingMethod.Length == 0 && f.ModuleId.Length == 0 && f.IlOffset.Length == 0);

        return frames.Count == 0 ? None : new EnhancedStacktrace(frames);
    }

    private static void Close(Builder? builder, List<EnhancedFrame> frames)
    {
        if (builder is not null)
            frames.Add(builder.Build());
    }

    // True when the line carried at least one recognized key. Several keys on one physical line is
    // the shape BUTR actually writes for a patch, so the line is split on the keys rather than on
    // its first colon.
    private static bool Apply(Builder builder, string line)
    {
        var matches = KeyMarker.Matches(line);

        if (matches.Count == 0)
            return false;

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var from = match.Index + match.Length;
            var to = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;

            builder.Set(match.Groups["key"].Value, line[from..to].Trim());
        }

        return true;
    }

    private sealed class Builder(int number, string text)
    {
        private readonly List<EnhancedPatch> patches = [];
        private string method = string.Empty;
        private string pluginId = string.Empty;
        private string provider = string.Empty;
        private string patchType = string.Empty;
        private bool started;

        public string ExecutingMethod { get; private set; } = string.Empty;

        public string ModuleId { get; private set; } = string.Empty;

        public string IlOffset { get; private set; } = string.Empty;

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "Executing Method":
                    ExecutingMethod = value;
                    break;
                case "Module Id":
                    ModuleId = value;
                    break;
                case "Approximate IL Offset":
                    IlOffset = value;
                    break;
                case "Patch Methods":
                case "Method":
                    Flush();
                    method = value;
                    started = true;
                    break;
                case "Plugin Id":
                    if (pluginId.Length > 0)
                        Flush();

                    pluginId = value;
                    started = true;
                    break;
                case "Patch Type":
                    patchType = value;
                    started = true;
                    break;
                case "Type":
                    provider = value;
                    started = true;
                    break;
            }
        }

        public EnhancedFrame Build()
        {
            Flush();

            var (type, name, parameters) = Split(ExecutingMethod, text);

            return new EnhancedFrame(
                number,
                text,
                ExecutingMethod,
                type,
                name,
                parameters,
                CountParameters(parameters),
                ModuleId,
                IlOffset,
                patches);
        }

        private void Flush()
        {
            if (!started)
                return;

            patches.Add(new EnhancedPatch(method, pluginId, provider, patchType));

            method = string.Empty;
            pluginId = string.Empty;
            provider = string.Empty;
            patchType = string.Empty;
            started = false;
        }
    }

    // The executing method is preferred because it is written in a form with no ambiguity in it:
    // Ret Namespace.Type::Method(parameters). The frame text is the fallback, and it puts the return
    // type in front of the type name, which is exactly what makes a plain frame parser read
    // "void HarmonyLib.Harmony" as a type.
    public static (string Type, string Method, string Parameters) Split(string executing, string frameText)
    {
        var separator = executing.IndexOf("::", StringComparison.Ordinal);

        if (separator > 0)
        {
            var owner = executing[..separator];
            var member = executing[(separator + 2)..];
            var (name, parameters) = NameAndParameters(member);

            return (AfterLastSpace(owner), name, parameters);
        }

        var (qualified, fallback) = NameAndParameters(frameText);
        var whole = AfterLastSpace(qualified);

        // A constructor renders as Type..ctor, so the separating dot is the one before the leading dot.
        var lastDot = whole.LastIndexOf('.');

        if (lastDot <= 0)
            return (string.Empty, whole, fallback);

        return whole[lastDot - 1] == '.'
            ? (whole[..(lastDot - 1)], whole[lastDot..], fallback)
            : (whole[..lastDot], whole[(lastDot + 1)..], fallback);
    }

    private static (string Name, string Parameters) NameAndParameters(string member)
    {
        var open = member.IndexOf('(', StringComparison.Ordinal);

        if (open < 0)
            return (member.Trim(), string.Empty);

        var close = member.LastIndexOf(')');

        return (
            member[..open].Trim(),
            (close > open ? member[(open + 1)..close] : member[(open + 1)..]).Trim());
    }

    private static string AfterLastSpace(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.LastIndexOf(' ');

        return space < 0 ? trimmed : trimmed[(space + 1)..].Trim();
    }

    // Commas inside a generic argument list or an array rank are not parameter separators, so the
    // count is taken at nesting depth zero. Getting this wrong would separate two overloads that are
    // the same method, or merge two that are not.
    public static int CountParameters(string parameters)
    {
        if (parameters.Trim().Length == 0)
            return 0;

        var count = 1;
        var depth = 0;

        foreach (var character in parameters)
        {
            switch (character)
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' or ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    count++;
                    break;
            }
        }

        return count;
    }
}
