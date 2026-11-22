using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Launcher;

// How a finding came to its grade, said on the row of every Error and of every finding a run refuted.
public enum FindingProof
{
    None,

    // The game's own code and the runtime's own behavior say this stops the game on every launch.
    ReadFromTheGame,

    // A launch on this computer crashed, with this same mod file on this same game version, and the
    // crash named this fault.
    ObservedCrash,

    // A launch on this computer carried this finding, with the same file on the same game version, and
    // played. Whatever the reading says, the game did not stop, so the finding is not an Error.
    RefutedByRun
}

// What BEM has watched happen, set against what it reads from files. The owner's rule: an Error is a
// crash that WILL happen, and his own runs are evidence. So a launch remembers which findings it carried,
// and a crash remembers which of them it named. From those two facts:
//
//   a finding a crash named, on the same mod file and game version, is an Error however it was graded;
//   a finding a launch carried and played through is never an Error however it was graded.
//
// Both are tied to the mod's files and the game version through the key, so an update to either starts
// the evidence again.
public static class LaunchEvidence
{
    // Long enough that the game is past loading every mod and into the menu or a campaign. A run that
    // ends sooner proves nothing about a fault at startup, even when it ends with exit code 0.
    public static readonly TimeSpan PlayedFor = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<(string Path, long Length, long Written), string> FileHashes = new();

    private static readonly Regex TypeLoad = new(
        @"Could not load type '(?<name>[^']+)'", RegexOptions.CultureInvariant);

    private static readonly Regex MemberLoad = new(
        @"(?:Method|Field) not found: '?(?<name>[^'\r\n]+)", RegexOptions.CultureInvariant);

    public static bool PlayedFine(LaunchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.Outcome == LaunchOutcome.ExitedCleanly && record.Ran is { } ran && ran >= PlayedFor;
    }

    // The fault a crash log has to name for a missing reference: the type for one that is gone, the
    // member for one that is gone or changed. A member of a type that is gone fails on the type, which is
    // what the runtime names. Nested types are written with dots, as Named compares them.
    public static string FaultOf(MissingApiReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return reference.MemberName is null || reference.Kind == MissingApiKind.TypeMissing
            ? "type:" + Dotted(reference.TypeName)
            : "member:" + Dotted(reference.TypeName) + "." + reference.MemberName;
    }

    // The files the game loads for a module, as one value: every file directly in its bin folder for
    // this platform. Empty when there is none, which is no identity and so no evidence.
    public static string StampOf(string? moduleFolder, string platformFolder)
    {
        if (string.IsNullOrWhiteSpace(moduleFolder))
            return string.Empty;

        try
        {
            var bin = new DirectoryInfo(Path.Combine(moduleFolder, "bin", platformFolder));

            if (!bin.Exists)
                return string.Empty;

            var text = new StringBuilder();

            foreach (var file in bin.EnumerateFiles("*.dll").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var hash = FileHashes.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc.Ticks), static key =>
                {
                    using var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return Convert.ToHexStringLower(SHA256.HashData(stream));
                });

                text.Append(file.Name.ToUpperInvariant()).Append('=').Append(hash).Append('\n');
            }

            return text.Length == 0 ? string.Empty : Short(text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static string? KeyFor(PreflightFinding finding, string moduleStamp, string gameVersion)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.ModuleId is not { } module || string.IsNullOrEmpty(moduleStamp) || string.IsNullOrEmpty(gameVersion))
            return null;

        return Short($"{finding.ClassId ?? finding.Check.ToString()}|{module.Value}|{finding.Subject ?? finding.Headline}|{moduleStamp}|{gameVersion}");
    }

    // What a launch record keeps: the findings that are an Error by reading, which a run can refute, and
    // the ones a crash could name, which a run can prove.
    public static IReadOnlyList<string> Present(IEnumerable<PreflightFinding>? findings) =>
    [
        .. (findings ?? [])
            .Where(f => f.EvidenceKey is not null && (f.Grade == PreflightGrade.Critical || f.Faults.Count > 0 || f.Proof != FindingProof.None))
            .Select(f => f.EvidenceKey!)
            .Distinct(StringComparer.Ordinal)
    ];

    // The findings a crashed run's own log names, each as "key|what it named".
    public static IReadOnlyList<string> NamedBy(string? crashText, IEnumerable<PreflightFinding>? findings)
    {
        if (string.IsNullOrEmpty(crashText) || findings is null)
            return [];

        var types = TypeLoad.Matches(crashText).Select(m => Dotted(m.Groups["name"].Value)).ToHashSet(StringComparer.Ordinal);
        var members = MemberLoad.Matches(crashText).Select(m => Dotted(m.Groups["name"].Value)).ToList();
        var named = new List<string>();

        foreach (var finding in findings.Where(f => f.EvidenceKey is not null))
        {
            var fault = finding.Faults.FirstOrDefault(f => Named(f, types, members));

            if (fault is not null)
                named.Add(finding.EvidenceKey + "|" + fault[(fault.IndexOf(':') + 1)..]);
        }

        return [.. named.Distinct(StringComparer.Ordinal)];
    }

    private static readonly Regex LoadingAssembly = new(
        @"Loading assembly: [^\r\n]*?[\\/]Modules[\\/](?<module>[^\\/\r\n]+)[\\/]bin[\\/]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The module whose assembly the engine loaded last, read from rgl_log_<pid>.txt. A mod that throws in
    // OnSubModuleLoad before the Exception Manager is armed leaves no message anywhere ("Unhandled
    // Exception Code 0xE0434352" only), but the engine loaded its assembly last, so it is where the start died.
    public static string? LastModuleLoaded(string? rglLog)
    {
        if (string.IsNullOrEmpty(rglLog))
            return null;

        var last = LoadingAssembly.Matches(rglLog).LastOrDefault();

        return last is null ? null : last.Groups["module"].Value;
    }

    // For a startup death no log names: the findings of the last module loaded that a crash could name,
    // each as "key|what it would name". A weaker attribution than NamedBy, so it is used only when NamedBy
    // found nothing, and only for findings that carry a fault.
    public static IReadOnlyList<string> NamedByLastModule(string? rglLog, IEnumerable<PreflightFinding>? findings)
    {
        var module = LastModuleLoaded(rglLog);

        if (module is null || findings is null)
            return [];

        return
        [
            .. findings
                .Where(f => f.EvidenceKey is not null && f.Faults.Count > 0
                    && string.Equals(f.ModuleId?.Value, module, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.EvidenceKey + "|" + f.Faults[0][(f.Faults[0].IndexOf(':') + 1)..])
                .Distinct(StringComparer.Ordinal)
        ];
    }

    // The engine's own line for a module whose types it could not load; the game then shows its error and
    // exits with a small code that is not a crash code.
    public static bool EndedOnModuleLoadError(string? rglLog) =>
        rglLog is not null && rglLog.Contains("Error while getting types and loading", StringComparison.Ordinal);

    private static bool Named(string fault, HashSet<string> types, List<string> members)
    {
        if (fault.StartsWith("type:", StringComparison.Ordinal))
            return types.Contains(fault[5..]);

        if (!fault.StartsWith("member:", StringComparison.Ordinal))
            return false;

        var name = fault[7..];

        // The runtime writes the member with its return type before it and its parameters after.
        return members.Any(text =>
        {
            var at = text.IndexOf(name, StringComparison.Ordinal);

            return at >= 0
                && (at == 0 || text[at - 1] is ' ' or '.')
                && (at + name.Length == text.Length || !char.IsLetterOrDigit(text[at + name.Length]) && text[at + name.Length] != '_');
        });
    }

    // Applies what was watched to what was read. Called once per finding, after its grade by reading is
    // final.
    public static PreflightFinding Judge(PreflightFinding finding, IReadOnlyList<LaunchRecord>? history)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var key = finding.EvidenceKey;
        var carried = key is null || history is null
            ? []
            : history.Where(r => r.FindingsPresent?.Contains(key, StringComparer.Ordinal) == true).ToList();

        if (carried.LastOrDefault(PlayedFine) is { } played)
        {
            if (finding.Grade != PreflightGrade.Critical)
                return finding;

            return finding with
            {
                Grade = PreflightGrade.Warning,
                Proof = FindingProof.RefutedByRun,
                ProofUtc = Utc(played.EndedUtc ?? played.StartedUtc),
                Detail = finding.Detail + "\n\n" + Strings.Current.Format(
                    "Core.Launcher.Preflight.Proof.Refuted", Local(played.StartedUtc), (int)played.Ran!.Value.TotalMinutes)
            };
        }

        var prefix = key + "|";
        var crash = key is null
            ? null
            : carried.LastOrDefault(r => r.Outcome == LaunchOutcome.Crashed && r.CrashedOn?.Any(c => c.StartsWith(prefix, StringComparison.Ordinal)) == true);

        if (crash is not null)
        {
            var named = crash.CrashedOn!.First(c => c.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
            var at = Utc(crash.EndedUtc ?? crash.StartedUtc);

            return finding with
            {
                Grade = PreflightGrade.Critical,
                Relevance = FindingRelevance.Relevant,
                Proof = FindingProof.ObservedCrash,
                ProofUtc = at,
                CrashEvidenceUtc = finding.CrashEvidenceUtc ?? at,
                Detail = finding.Detail + "\n\n" + Strings.Current.Format(
                    "Core.Launcher.Preflight.Proof.Observed", Local(crash.StartedUtc), named)
            };
        }

        if (finding.Grade != PreflightGrade.Critical)
            return finding;

        return finding with
        {
            Proof = FindingProof.ReadFromTheGame,
            Detail = finding.Detail + "\n\n" + Strings.Current.Format(
                "Core.Launcher.Preflight.Proof.Read", Strings.Current[PreflightGrades.ProofKey(finding.Check)])
        };
    }

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string Dotted(string name) => name.Replace('+', '.').Replace('/', '.').Replace("::", ".");

    private static string Short(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
}
