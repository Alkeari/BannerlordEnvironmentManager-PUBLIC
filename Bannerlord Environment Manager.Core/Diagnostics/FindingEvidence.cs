using System.Security.Cryptography;
using System.Text;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum EvidenceAdjustment
{
    // Nothing recorded settles it; the finding keeps the grade its own check gave it.
    None,

    // A recorded crash settles it: the finding is an Error.
    RaiseToError,

    // A run that played fine settles it: the finding is never an Error.
    ForbidError
}

public sealed record FindingEvidenceVerdict(EvidenceAdjustment Adjustment, FindingProof Proof, string Reason)
{
    public static readonly FindingEvidenceVerdict NoEvidence = new(EvidenceAdjustment.None, FindingProof.None, string.Empty);

    // The grade after the evidence: an Error is only raised or taken away here.
    public IssueSeverity Apply(IssueSeverity graded) => Adjustment switch
    {
        EvidenceAdjustment.RaiseToError => IssueSeverity.Error,
        EvidenceAdjustment.ForbidError when graded == IssueSeverity.Error => IssueSeverity.Warning,
        _ => graded
    };
}

// What BEM has watched, set against any Health finding about a mod. Two proofs:
//   a recorded crash on the same game version and the same mod file that attributes the fault to the mod
//   raises the finding to an Error;
//   a run that carried the same finding on the same mod file and game version, ended normally and lasted
//   long enough to be past loading, forbids Error.
// When both exist the run wins: a crash and a played run of one file and one version mean the fault is
// not certain, and an Error is what WILL crash.
public static class FindingEvidence
{
    // The key a launch record keeps in FindingsPresent for a Health finding.
    public static string? KeyFor(HealthFindingKind kind, string? moduleId, string? subject, string moduleStamp, string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrEmpty(moduleStamp) || string.IsNullOrEmpty(gameVersion))
            return null;

        var text = $"health:{kind}|{moduleId}|{subject}|{moduleStamp}|{gameVersion}";

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
    }

    public static FindingEvidenceVerdict Judge(
        HealthFindingKind kind,
        string? moduleId,
        string? subject,
        string moduleStamp,
        string gameVersion,
        IReadOnlyList<LaunchRecord>? history,
        IReadOnlyList<CrashGuardRecord>? crashes,
        // A patch overlap names its method ("Namespace.Type.Method"). With it, only a crash that went
        // through a patch of this module on that method proves the row; a crash of the same mod file
        // elsewhere proves nothing about this method.
        string? patchTarget = null)
    {
        var key = KeyFor(kind, moduleId, subject, moduleStamp, gameVersion);

        if (key is null)
            return FindingEvidenceVerdict.NoEvidence;

        var played = history?.LastOrDefault(r =>
            LaunchEvidence.PlayedFine(r) && r.FindingsPresent?.Contains(key, StringComparer.Ordinal) == true);

        if (played is not null)
        {
            var when = DateTime.SpecifyKind(played.StartedUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            return new FindingEvidenceVerdict(EvidenceAdjustment.ForbidError, FindingProof.RefutedByRun,
                Strings.Current.Format("Core.Diagnostics.FindingEvidence.Refuted", when, (int)played.Ran!.Value.TotalMinutes, gameVersion));
        }

        foreach (var crash in (crashes ?? []).Reverse())
        {
            if (!string.Equals(crash.GameVersion, gameVersion, StringComparison.Ordinal)
                || crash.ModuleStamps is not { } stamps
                || !stamps.TryGetValue(moduleId!, out var crashedStamp)
                || !string.Equals(crashedStamp, moduleStamp, StringComparison.Ordinal))
                continue;

            var attribution = CrashAttributor.Attribute(crash.Stack, crash.PatchOwners);

            if (patchTarget is not null && !ThroughPatchOn(attribution, patchTarget, moduleId!))
                continue;

            var involved = attribution.Involved;

            if (involved.Count == 0)
                continue;

            var top = involved.Max(m => m.Score);

            if (!involved.Any(m => m.Score == top && string.Equals(m.Module, moduleId, StringComparison.OrdinalIgnoreCase)))
                continue;

            var when = DateTime.SpecifyKind(crash.Utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            return new FindingEvidenceVerdict(EvidenceAdjustment.RaiseToError, FindingProof.ObservedCrash,
                Strings.Current.Format("Core.Diagnostics.FindingEvidence.Observed", when, gameVersion, crash.ExceptionType));
        }

        return FindingEvidenceVerdict.NoEvidence;
    }

    // True when a stack frame is a patched call of the target method and the module owns a patch there.
    private static bool ThroughPatchOn(StackAttribution attribution, string target, string moduleId)
    {
        var cut = target.LastIndexOf('.');

        if (cut <= 0)
            return false;

        var type = target[..cut];
        var method = target[(cut + 1)..];
        var shortType = type[(type.LastIndexOfAny(['.', '+']) + 1)..];

        return attribution.Frames.Any(f =>
            f.IsPatchFrame
            && f.Type[(f.Type.LastIndexOfAny(['.', '+']) + 1)..] == shortType
            && f.Method.StartsWith(method + "_Patch", StringComparison.Ordinal)
            && (f.Modules.Contains(moduleId, StringComparer.OrdinalIgnoreCase)
                // No recorded owner and no scan for this frame: the module counts when the stack involves it at all.
                || (f.Modules.Count == 0 && attribution.Involved.Any(m => string.Equals(m.Module, moduleId, StringComparison.OrdinalIgnoreCase)))));
    }
}
