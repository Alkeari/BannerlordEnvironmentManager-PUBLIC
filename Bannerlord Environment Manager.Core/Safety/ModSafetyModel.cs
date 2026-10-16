using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Safety;

// The capability reimplemented here comes from Calradia Warden by Rely1234 (MIT), a read-only
// PowerShell scanner written after its author was hit by a trojanized Steam Workshop mod that pulled
// a hidden PowerShell payload on every game launch. The rules below are BEM's own, but the idea, the
// verdict vocabulary and the blocklist shape are theirs. See THIRD-PARTY-NOTICES.md.
//
// Nothing in this namespace ever loads or runs what it inspects. Assemblies are read through
// System.Reflection.Metadata, metadata only, the same rule AssemblyIndex keeps.

// Ordered weakest to strongest: the worst finding in a target is the maximum over its files.
//
// Two grades the tool this came from has are deliberately absent, both because measuring them on the
// real install showed they said nothing true.
//
// POWERFUL, for code that reaches the network or loads code at runtime, landed on 70 of its 170
// modules. A grade 41 percent of an install wears is a fact about C# mods, not a warning, and a
// surface full of it trains the reader to ignore the row that matters. The capability is still
// measured per module and reported as detail on the module the user is already asking about.
//
// SIGNED, for a trusted publisher's Authenticode signature, was worse: it graded ButterLib and
// Harmony "signed by Microsoft Corporation", because each ships a Microsoft-signed dependency
// alongside its own unsigned assembly. The grade was making a claim about the mod from a fact about
// one file in its folder. Signatures are now reported per file, where they are true.
public enum SafetyVerdict
{
    NothingFound,
    Suspicious,
    KnownBad
}

// Each of these is rare-to-never in a mod that is not doing something it should not. Anything weaker
// lives in SafetyCapability, which never drives a verdict.
public enum SafetySignal
{
    BlocklistWorkshopId,
    BlocklistFileHash,
    EncodedPowerShellPayload,
    HiddenPowerShellDownload,
    StartupPersistence,
    PayloadUrl,
    RawIpUrl
}

// What the code is able to do, read off assembly metadata. Context, never an accusation.
public enum SafetyCapability
{
    Network,
    Process,
    Shell,
    DynamicCode,
    Registry
}

public enum SafetyTargetKind
{
    Module,
    WorkshopModule,
    Archive
}

public sealed record SafetyEvidence(SafetySignal Signal, string Matched, string Why)
{
    public string Headline => Signal switch
    {
        SafetySignal.BlocklistWorkshopId => Strings.Current["Core.Safety.Evidence.Blocklist.WorkshopId"],
        SafetySignal.BlocklistFileHash => Strings.Current["Core.Safety.Evidence.Blocklist.FileHash"],
        SafetySignal.EncodedPowerShellPayload => Strings.Current["Core.Safety.Evidence.EncodedPowerShellPayload"],
        SafetySignal.HiddenPowerShellDownload => Strings.Current["Core.Safety.Evidence.HiddenPowerShellDownload"],
        // Names a start-up location rather than sets one. The evidence is a string literal, and a
        // literal cannot say whether the code around it writes there, reads it, or only names it.
        SafetySignal.StartupPersistence => Strings.Current["Core.Safety.Evidence.StartupPersistence"],
        SafetySignal.PayloadUrl => Strings.Current["Core.Safety.Evidence.PayloadUrl"],
        SafetySignal.RawIpUrl => Strings.Current["Core.Safety.Evidence.RawIpUrl"],
        _ => Strings.Current["Core.Safety.Evidence.Fallback"]
    };

    public string Describe() => Strings.Current.Format("Core.Safety.Evidence.Describe", Headline, Matched, Why);
}

// One file, with everything the user needs to act on that file specifically.
public sealed record FlaggedFile(
    string FilePath,
    string? EntryPath,
    string? Sha256,
    IReadOnlyList<SafetyEvidence> Evidence)
{
    public string Location => EntryPath is null ? FilePath : $"{FilePath} -> {EntryPath}";

    // Looks the file up by hash rather than uploading it. Nothing leaves the machine: the hash is in
    // the URL the browser opens, and VirusTotal either already knows the file or says it does not.
    public string? VirusTotalUrl =>
        Sha256 is { Length: 64 } hash ? $"https://www.virustotal.com/gui/file/{hash}" : null;
}

// "Could not read this" is a different statement from "read this and found nothing", and the two must
// never be collapsed. A scan that could not open half a module has not cleared that module.
public sealed record UnreadableFile(string Path, string Reason);

public sealed record SafetyTarget(
    string Name,
    string Detail,
    string Path,
    SafetyTargetKind Kind,
    string? WorkshopId = null,
    string? PageUrl = null);

public sealed record SafetyScanResult(
    SafetyTarget Target,
    SafetyVerdict Verdict,
    IReadOnlyList<FlaggedFile> Flagged,
    IReadOnlyList<SafetyCapability> Capabilities,
    IReadOnlyList<string> UnrecognizedUrls,
    IReadOnlyList<UnreadableFile> Unreadable,
    IReadOnlyList<string> Signers,
    int SignedFileCount = 0,
    string? BlocklistReason = null,
    int FilesRead = 0)
{
    public bool NothingCouldBeRead => FilesRead == 0 && Unreadable.Count > 0;

    public bool IsAlarm => Verdict is SafetyVerdict.Suspicious or SafetyVerdict.KnownBad;

    public string VerdictText => NothingCouldBeRead
        ? Strings.Current["Core.Safety.Verdict.CouldNotLook"]
        : Verdict switch
        {
            SafetyVerdict.KnownBad => Strings.Current["Core.Safety.Verdict.KnownBad"],
            SafetyVerdict.Suspicious => Strings.Current["Core.Safety.Verdict.Suspicious"],
            _ => Strings.Current["Core.Safety.Verdict.NothingFound"]
        };

    // Never rolled into the verdict. A signature on a bundled dependency says who published that file
    // and nothing at all about the mod that ships it.
    public string? SignatureNote => Signers.Count == 0
        ? null
        : Strings.Current.Plural("Core.Safety.SignatureNote", FilesRead, SignedFileCount, string.Join(", ", Signers));

    // Never "safe" and never "clean". The strongest true statement about a target with no findings is
    // that the watched behaviors were not found in the files that could be read.
    public string VerdictExplanation => NothingCouldBeRead
        ? Strings.Current.Plural("Core.Safety.VerdictExplanation.NothingCouldBeRead", Unreadable.Count)
        : Verdict switch
        {
            SafetyVerdict.KnownBad => Strings.Current["Core.Safety.VerdictExplanation.KnownBad"],
            SafetyVerdict.Suspicious => Strings.Current["Core.Safety.VerdictExplanation.Suspicious"],
            _ => FilesRead == 0
                ? Strings.Current["Core.Safety.VerdictExplanation.NoCode"]
                : Strings.Current.Plural("Core.Safety.VerdictExplanation.Clean", FilesRead)
        };
}

public sealed record ModSafetyReport(
    IReadOnlyList<SafetyScanResult> Results,
    SafetyBlocklistState Blocklist,
    int OfficialModulesSkipped,
    int ArchivesScanned,
    string? Error = null,
    TimeSpan Elapsed = default)
{
    public IReadOnlyList<SafetyScanResult> Alarms => [.. Results.Where(r => r.IsAlarm)];

    // Folds in what a later, narrower scan found, so an install-time check and the full scan converge
    // on one picture instead of leaving two that disagree. A target that was scanned again replaces
    // its earlier row; anything new is added. An archive that is no longer on disk is dropped, because
    // an install that consumed it leaves nothing for the user to act on.
    public ModSafetyReport With(IReadOnlyList<SafetyScanResult> later)
    {
        ArgumentNullException.ThrowIfNull(later);

        var merged = Results.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

        foreach (var result in later)
            merged[Key(result)] = result;

        var kept = merged.Values
            .Where(result => result.Target.Kind is not SafetyTargetKind.Archive || File.Exists(result.Target.Path))
            .OrderByDescending(r => r.Verdict)
            .ThenByDescending(r => r.NothingCouldBeRead)
            .ThenBy(r => r.Target.Name, StringComparer.OrdinalIgnoreCase);

        return this with { Results = [.. kept] };
    }

    // The counterpart to With: a module or archive the user sent to the Recycle Bin is no
    // longer on disk to alarm about, and the report has to say so immediately rather than waiting for
    // the next full scan to notice it is gone.
    public ModSafetyReport Without(SafetyScanResult removed) =>
        this with { Results = [.. Results.Where(r => Key(r) != Key(removed))] };

    private static string Key(SafetyScanResult result) => $"{result.Target.Kind}|{result.Target.Path}";

    public int KnownBadCount => Results.Count(r => r.Verdict is SafetyVerdict.KnownBad);

    public int SuspiciousCount => Results.Count(r => r.Verdict is SafetyVerdict.Suspicious);

    public int NothingFoundCount => Results.Count(r => r.Verdict is SafetyVerdict.NothingFound && !r.NothingCouldBeRead);

    public int CouldNotLookCount => Results.Count(r => r.NothingCouldBeRead);

    public int CapableCount => Results.Count(r => r.Capabilities.Count > 0);

    // Said in this order on purpose: what was found, then what could not be looked at, then the
    // caveat. A headline that stops after "nothing found" has overstated the result.
    public string Headline
    {
        get
        {
            if (Error is not null)
                return Error;

            if (Results.Count == 0)
                return Strings.Current["Core.Safety.Headline.NoneScanned"];

            var scanned = Strings.Current.Plural("Core.Safety.Headline.ScannedCount", Results.Count);

            if (KnownBadCount > 0)
                return $"{scanned} {Strings.Current.Plural("Core.Safety.Headline.KnownBad", KnownBadCount)}";

            if (SuspiciousCount > 0)
                return $"{scanned} {Strings.Current.Plural("Core.Safety.Headline.Suspicious", SuspiciousCount)}";

            return CouldNotLookCount > 0
                ? $"{scanned} {Strings.Current.Plural("Core.Safety.Headline.CouldNotLook", CouldNotLookCount)}"
                : $"{scanned} {Strings.Current["Core.Safety.Headline.Clean"]}";
        }
    }
}
