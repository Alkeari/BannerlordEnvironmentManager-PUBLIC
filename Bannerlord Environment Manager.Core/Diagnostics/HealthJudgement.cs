using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Safety;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// What a Health check's reading amounts to, by the owner's three questions (2026-11-11), asked in order:
// "Will this crash the game? Yes = Error. No = Warning. Will this make the game or mods behave
// unpredictably or not like the user or author intended? Yes = Warning. No = Note. Is this even worth
// showing the user? Does it even matter? Yes = Note. No = Nothing."
public enum HealthVerdict
{
    // Not raised at all: no row, no count.
    Nothing,
    Note,
    Warning,
    Error
}

// One place that turns what a check read into what the owner is told. A check reads files and reports
// everything of a shape; this decides whether that shape was shown to do harm on THIS install. Each rule
// here rests on a run of a real play install (170 modules, played without crashing, 47 rows raised)
// and says which of those rows it removes and why. The aim is no warning or error without a shown
// cause, and no false positives.
public static class HealthJudgement
{
    // A patch whose target is not on the install. The launch check already grades this by what is
    // proven (LaunchPreflight.GradeHarmonyTarget) and Health showed every one as a Warning regardless:
    // Character Reload's three BannerEditor patches name types of Better Banner Editor, which is not
    // installed, sit in a Harmony category the mod applies only when that mod is there, and the game
    // ran. A patch like that is the mod's own compatibility code and is nothing to raise.
    public static HealthVerdict Of(HarmonyPatchTarget target, string? recentRunErrors, PatchApplicationSite site)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (grade, relevance) = LaunchPreflight.GradeHarmonyTarget(target, recentRunErrors, site);

        if (relevance == FindingRelevance.Hidden)
            return HealthVerdict.Nothing;

        return grade switch
        {
            PreflightGrade.Critical => HealthVerdict.Error,
            PreflightGrade.Warning => HealthVerdict.Warning,
            _ => HealthVerdict.Note
        };
    }

    // A mod shipping a file named like one of the game's. On its own that changes nothing: the game
    // resolves an assembly from its own folder first, so the mod's copy of a file the game loads is never
    // the one bound, and a copy of a file the game does not load (its crash uploader's Newtonsoft.Json)
    // shadows nothing. It matters only when some enabled mod calls into that assembly for something the
    // copy that binds does not have, which the missing-API scan reads from the metadata. Training
    // Battles' Newtonsoft.Json and ButterLib's System.Numerics.Vectors were rows on an install where no
    // mod does.
    public static HealthVerdict OfGameAssemblyCopies(
        IEnumerable<ShadowedGameAssembly> copies, string? loadedPlatformFolder, MissingApiReport? missingApis)
    {
        ArgumentNullException.ThrowIfNull(copies);

        if (missingApis is null)
            return HealthVerdict.Nothing;

        foreach (var copy in copies.Where(c => !c.Identical && IsLoadedByTheGame(c, loadedPlatformFolder)))
        {
            var name = Path.GetFileNameWithoutExtension(copy.Name);

            if (missingApis.Findings.Any(r => string.Equals(r.ReferencedAssembly, name, StringComparison.OrdinalIgnoreCase)))
                return HealthVerdict.Warning;
        }

        return HealthVerdict.Nothing;
    }

    // The references behind a Warning from the rule above, for the row to name.
    public static IReadOnlyList<MissingApiReference> BrokenBy(
        IEnumerable<ShadowedGameAssembly> copies, string? loadedPlatformFolder, MissingApiReport? missingApis)
    {
        ArgumentNullException.ThrowIfNull(copies);

        if (missingApis is null)
            return [];

        var names = copies
            .Where(c => !c.Identical && IsLoadedByTheGame(c, loadedPlatformFolder))
            .Select(c => Path.GetFileNameWithoutExtension(c.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. missingApis.Findings.Where(r => names.Contains(r.ReferencedAssembly))];
    }

    private static bool IsLoadedByTheGame(ShadowedGameAssembly copy, string? loadedPlatformFolder)
    {
        if (string.IsNullOrEmpty(loadedPlatformFolder))
            return true;

        var folder = Path.GetFileName(Path.GetDirectoryName(copy.GamePath) ?? string.Empty);

        return string.Equals(
            folder,
            Path.GetFileName(loadedPlatformFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);
    }

    // A definition-only animation: a tiny file that only names a face-generation clip. Played on a
    // character in the world it has no body motion to give, which is the crash this check was written
    // for. Nothing can play it unless an action of some enabled module is mapped to it, so one that no
    // action names is inert. ArtemsBetterUIVisuals ships five for its menu portraits and no action on the
    // install names any of them.
    public static IReadOnlyList<StubAnimation> DrivenStubs(RigReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var driven = report.Patchers
            .SelectMany(patcher => patcher.Actions)
            .Select(action => action.AnimationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. report.StubAnimations.Where(stub => driven.Contains(stub.AnimationName))];
    }

    // Several mods on one game method. Stacking is how Harmony is meant to be used and is nothing. One
    // mod's prefix that always returns false, proven from its code, means the game's method and the other
    // mods' prefixes never run there: a mod is not doing what its author wrote, which is a Warning (his
    // RBM and Realistic Combat Mod case). A prefix that may return false, or two mods writing one result,
    // depends on values no file shows, so it is told as a Note and never counted.
    public static HealthVerdict Of(PatchOrderFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return finding.Grade switch
        {
            PatchOrderGrade.High => HealthVerdict.Warning,
            PatchOrderGrade.Medium => HealthVerdict.Note,
            _ => HealthVerdict.Nothing
        };
    }

    // One assembly shipped by several mods: the first in the load order is the one every mod gets. An
    // older copy further down never binds and changes nothing (Limitless ships Harmony 2.2.2 under the
    // Harmony module's 2.4.2). A copy further down that is a newer major or minor version is a mod that
    // may call what the bound copy lacks, which is a Warning; a newer patch version adds no API.
    //
    // A file that is one game version's build of a versioned set (ButterLib's
    // Bannerlord.ButterLib.Implementation.1.4.0.dll) is opened only when the set's loader picks it for
    // this game, so a newer copy of a build the loader passes over is never loaded by anyone and is
    // nothing: six such rows for ButterLib's 1.4.x builds stood on game v1.5.7 in the popular-mods
    // calibration.
    public static HealthVerdict Of(ShadowedAssembly shadowed, GameVersionSupportReport? versions = null)
    {
        ArgumentNullException.ThrowIfNull(shadowed);

        if (IsDormantBuild(shadowed.FileName, versions))
            return HealthVerdict.Nothing;

        return shadowed.Shadowed.Any(copy =>
            copy.Difference == AssemblyCopyDifference.NewerThanTheOneThatBinds
            && (copy.Copy.Version.Major != shadowed.Winner.Version.Major
                || copy.Copy.Version.Minor != shadowed.Winner.Version.Minor))
            ? HealthVerdict.Warning
            : HealthVerdict.Nothing;
    }

    // True only when the file is a versioned build whose set is known and the loader of every set with
    // that stem picks another build (or none) for this game. Unknown sets, an unread game version or a
    // file that is not a versioned build answer false, so the row is judged as before.
    public static bool IsDormantBuild(string fileName, GameVersionSupportReport? versions)
    {
        if (versions is null || versions.GameVersion.IsEmpty || GameVersionSupport.Split(fileName) is not { } split)
            return false;

        var sets = versions.Sets
            .Where(s => string.Equals(s.Stem, split.Stem, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sets.Count > 0
            && sets.All(s => s.LoaderPick(versions.GameVersion) is not { } pick || pick.CompareTo(split.Version) != 0);
    }

    // A mod with no build for this game's version line. The harm such a gap does is one thing: its code
    // calls something this game no longer has. When every reference of the module was read against this
    // install and all of them resolve, that harm is disproven and the gap is nothing. It stays a Warning
    // when the calls were not read, when the module could not be read, or when one of them is missing.
    public static HealthVerdict OfVersionGap(ModuleId module, MissingApiReport? missingApis) =>
        missingApis is not null
        && missingApis.Findings.All(reference => reference.ModuleId != module)
        && missingApis.Problems.All(problem => problem.ModuleId != module)
            ? HealthVerdict.Nothing
            : HealthVerdict.Warning;

    // Two mods give one XML value of one entry and neither declares the other. The game takes the value of
    // the one later in the load order, every time, so nothing crashes and nothing is unpredictable; whether
    // the loser's value was wanted is the owner's choice and no file shows it. That is a Note, with the
    // move that makes the other mod win offered on the row. A mod over the base game, or over a mod it
    // declares, is how the game is modded and is nothing.
    public static HealthVerdict Of(XmlOverlapGrade grade) =>
        grade == XmlOverlapGrade.UndeclaredConflict ? HealthVerdict.Note : HealthVerdict.Nothing;

    // The module a test boot was loading when it stopped. A boot names that module whenever it did not
    // reach the menu, and why it did not is the whole of what the row is worth. The game ending during
    // the load is a boot that failed there: a Warning. Time running out says where the boot was and not
    // that anything is wrong with that module, so it is a Note. A boot the player cancelled, one BEM
    // refused or could not start, and one with nothing captured say nothing about the module at all.
    public static HealthVerdict OfLoadingWhenStopped(DryRunStatus status) => status switch
    {
        DryRunStatus.StoppedDuringLoad => HealthVerdict.Warning,
        DryRunStatus.TimedOut => HealthVerdict.Note,
        _ => HealthVerdict.Nothing
    };

    // What the Mod Safety scan found in one mod. A file or Workshop item on the blocklist, and PowerShell
    // that is encoded or hidden and downloads, are what the scan was written to find: a Warning. The other
    // three signals are one piece of text each (the name of a start-up location, a link to a program, a
    // link to a bare address) and text cannot say whether the code writes there or fetches that; a mod
    // with one of them is told as a Note. Two or more different ones in the same mod are the pieces of a
    // downloader that stays, and that is a Warning again.
    public static HealthVerdict Of(SafetyScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsAlarm)
            return HealthVerdict.Nothing;

        if (result.Verdict == SafetyVerdict.KnownBad)
            return HealthVerdict.Warning;

        var signals = result.Flagged
            .SelectMany(file => file.Evidence)
            .Select(evidence => evidence.Signal)
            .ToHashSet();

        if (signals.Any(signal => !IsTextAlone(signal)))
            return HealthVerdict.Warning;

        // An alarm with no signal recorded was raised by something this rule does not know: it stays as it was.
        return signals.Count == 1 ? HealthVerdict.Note : HealthVerdict.Warning;
    }

    private static bool IsTextAlone(SafetySignal signal) =>
        signal is SafetySignal.StartupPersistence or SafetySignal.PayloadUrl or SafetySignal.RawIpUrl;

    // Several mods patching one game method, read from the patch registry. Mods that only stack there
    // are using Harmony as it is meant to be used, which is nothing. A method they contend for (two
    // transpilers, a transpiler beside another mod's patch, several prefixes) may not run the way one
    // of them wrote it, and the registry cannot show whether it does: a Note.
    public static HealthVerdict Of(PatchCollision collision)
    {
        ArgumentNullException.ThrowIfNull(collision);

        return collision.IsContended ? HealthVerdict.Note : HealthVerdict.Nothing;
    }

    // A place where the game's merge of this install's XML stops, found by running that merge over the
    // real files in the real load order. The game's own code says a new campaign ends there, and that
    // is read from the code of one game version and replayed outside the game, not seen in a run: a
    // Warning. It becomes an Error the way everything else does, when a launch shows the crash.
    public static HealthVerdict Of(XmlMergeFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        return HealthVerdict.Warning;
    }

    public static LoadOrder.IssueSeverity? SeverityOf(HealthVerdict verdict) => verdict switch
    {
        HealthVerdict.Error => LoadOrder.IssueSeverity.Error,
        HealthVerdict.Warning => LoadOrder.IssueSeverity.Warning,
        HealthVerdict.Note => LoadOrder.IssueSeverity.Information,
        _ => null
    };
}
