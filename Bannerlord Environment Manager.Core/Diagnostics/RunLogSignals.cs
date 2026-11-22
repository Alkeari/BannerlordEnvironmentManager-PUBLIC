using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Single engine log lines that are evidence on their own, kept by RecentRunErrors for the smaller launch
// checks.
public static partial class RunLogSignals
{
    // "rgl_post_warning_line: Shader cache version of the external module (OpenSourceArmory) is invalid."
    // In rgl_log_18080.txt it was followed by 0xC0000005 a few seconds later (findings catalog K06, G5).
    [GeneratedRegex(@"Shader cache version of the external module \((?<module>[^)]+)\) is invalid", RegexOptions.CultureInvariant)]
    private static partial Regex StaleShaderCache { get; }

    // BLSE issue #158 (Xbox PC, game v1.4.8, BLSE 1.7.5, Harmony 2.4.2): the engine loads Harmony's files from the
    // module's own bin\Gaming.Desktop.x64_Shipping_Client folder, then Harmony's own dependencies are looked up in
    // the game's main bin folder of the same name and the log says
    //   ERROR: Mono.Cecil.dll: Could not load file or assembly '<install>\Content\bin\Gaming.Desktop.x64_Shipping_Client\Mono.Cecil.dll'. The system cannot find the file specified.
    // for 0Harmony, Mono.Cecil, MonoMod.ILHelpers and MonoMod.Backports. A path under a Modules folder is the
    // module's own copy and not this failure.
    [GeneratedRegex(
        @"ERROR: (?<dll>0Harmony|Mono\.Cecil|MonoMod\.ILHelpers|MonoMod\.Backports)\.dll: Could not load file or assembly '(?<path>[^']*\\Gaming\.Desktop[^'\\]*\\\k<dll>\.dll)'",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex XboxHarmonyUnresolved { get; }

    public const string XboxHarmonyIssueUrl = "https://github.com/BUTR/Bannerlord.BLSE/issues/158";

    public static bool IsSignal(string line) => StaleShaderCache.IsMatch(line) || IsXboxHarmonyFailure(line);

    private static bool IsXboxHarmonyFailure(string line) =>
        XboxHarmonyUnresolved.Match(line) is { Success: true } m
        && !m.Groups["path"].Value.Contains("\\Modules\\", StringComparison.OrdinalIgnoreCase);

    // The Harmony dependencies a log says were looked for in the Gaming.Desktop main bin folder and not found,
    // each once, spelled as the file name, in the order first seen.
    public static IReadOnlyList<string> XboxHarmonyUnresolvedFiles(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var seen = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            if (!IsXboxHarmonyFailure(line))
                continue;

            var file = XboxHarmonyUnresolved.Match(line).Groups["dll"].Value + ".dll";

            if (!seen.Contains(file, StringComparer.OrdinalIgnoreCase))
                seen.Add(file);
        }

        return seen;
    }

    // The modules named by a stale-shader-cache warning, each once, in the order first seen.
    public static IReadOnlyList<string> StaleShaderCacheModules(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var seen = new List<string>();

        foreach (Match match in StaleShaderCache.Matches(text))
        {
            var module = match.Groups["module"].Value.Trim();

            if (module.Length > 0 && !seen.Contains(module, StringComparer.OrdinalIgnoreCase))
                seen.Add(module);
        }

        return seen;
    }
}
