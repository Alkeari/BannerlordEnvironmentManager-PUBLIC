using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Whether a finding is shown by default or only when the user asks to see everything. Hidden is not
// deleted: the finding is still computed, counted and reachable, it just does not compete for attention
// with the ones that predicted a real stop. The split comes from the 2026-10-30 deep dive over 100
// launches, 86 watched boots and 73 engine logs (findings catalog K18 to K32): every class graded Hidden
// here fired on clean sessions and correlated with no crash.
public enum FindingRelevance
{
    Relevant,
    Hidden
}

public static partial class RelevanceRules
{
    // The exception types that mean a mod called into the game and the call did not bind, which is the
    // first-chance shape that matched real defects (K02, K03). Everything else thrown during a boot
    // (1,558,034 NullReferenceException across 86 clean boots, FormatException, XmlException from BOM
    // probing, a crash reporter probing dynamic assemblies, HTTP 404s) ran on sessions that played fine.
    private static readonly HashSet<string> BindingFailures = new(StringComparer.Ordinal)
    {
        "System.MissingMethodException",
        "System.MissingFieldException",
        "System.MissingMemberException",
        "System.TypeLoadException",
        "HarmonyLib.HarmonyException",
        "MissingMethodException",
        "MissingFieldException",
        "MissingMemberException",
        "TypeLoadException",
        "HarmonyException"
    };

    // Engine schema validation (62 of 73 logs, every clean session among them) and the save-time
    // "SAVE ERROR. Cant find" lines (three sessions that all closed cleanly).
    [GeneratedRegex(
        @"attribute is not declared|element is not declared|duplicate key sequence|cannot contain text|has incomplete content|has invalid child element|SAVE ERROR\. Cant find",
        RegexOptions.CultureInvariant)]
    private static partial Regex NoiseLine { get; }

    public static bool IsBindingFailure(string? exceptionType) =>
        exceptionType is not null && BindingFailures.Contains(exceptionType.Trim());

    // A binding failure attributed to a module is the only first-chance record shown by default.
    public static FindingRelevance OfFirstChance(string? exceptionType, ModuleId module) =>
        IsBindingFailure(exceptionType) && !module.IsEmpty ? FindingRelevance.Relevant : FindingRelevance.Hidden;

    public static FindingRelevance OfLogLine(string? line) =>
        line is not null && NoiseLine.IsMatch(line) ? FindingRelevance.Hidden : FindingRelevance.Relevant;
}
