using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One family of assemblies a module ships whose file names differ only by a game version, the way
// Bannerlord.ButterLib.Implementation.1.4.7.dll differs from ...1.4.6.dll. The convention was read off
// a real 230-module install rather than assumed: five modules there ship one, three of them with a
// Bannerlord.ModuleLoader shim beside it, one with forty builds and one with a single build, and one
// whose file names carry a fourth build component the game version does not have.
public sealed record VersionedAssemblySet(
    ModuleId ModuleId,
    string DisplayName,
    string Stem,
    string FolderPath,
    IReadOnlyList<ModuleVersion> Versions)
{
    public ModuleVersion Newest => Versions.Count == 0 ? ModuleVersion.Empty : Versions[^1];

    public ModuleVersion Oldest => Versions.Count == 0 ? ModuleVersion.Empty : Versions[0];

    // Matching is on major.minor.revision, which is what ModuleVersion compares on and what the file
    // names actually encode. A fourth component in a file name is that mod's own build number and
    // never appears in the game's version, so comparing on it would report every set as a mismatch.
    public bool Supports(ModuleVersion gameVersion) => Versions.Any(v => v.CompareTo(gameVersion) == 0);

    // The build the loader opens for this game: the highest version not above the game's. ButterLib's own
    // log names it ("Implementation Bannerlord.ButterLib.Implementation.1.4.8.dll for game v1.4.8.0 is
    // loaded"). Null when every build is newer than the game, where nothing in the set fits.
    public ModuleVersion? LoaderPick(ModuleVersion gameVersion)
    {
        ModuleVersion? pick = null;

        foreach (var version in Versions)
        {
            if (version.CompareTo(gameVersion) <= 0)
                pick = version;
        }

        return pick;
    }

    // True when the build the loader opens belongs to the game's own major.minor line. A patch-only gap
    // inside one line (ArenaOverhaul 1.4.7, MarryAnyone 1.4.5 and MBFastDialogue 1.4.6 on game 1.4.8) runs;
    // only a missing build for the line is worth a finding.
    public bool CoversLineOf(ModuleVersion gameVersion) =>
        LoaderPick(gameVersion) is { } pick && GameLine.Same(pick, gameVersion);
}

// A game line is the major.minor pair (1.4, 1.5). Patches inside a line keep the API a mod binds to close
// enough that the owner's mods built for 1.4.5 to 1.4.7 all ran on 1.4.8; a new line is where they break.
public static class GameLine
{
    public static bool Same(ModuleVersion left, ModuleVersion right) =>
        left.Major == right.Major && left.Minor == right.Minor;

    public static string Of(ModuleVersion version) =>
        version.IsEmpty ? string.Empty : $"{version.Major}.{version.Minor}";
}

// A module whose versioned assemblies contain nothing built for the game that is installed.
public sealed record GameVersionGap(VersionedAssemblySet Set, ModuleVersion GameVersion)
{
    public bool GameIsNewer => GameVersion.CompareTo(Set.Newest) > 0;

    // Every gap that is still reported crosses a game line or leaves the loader nothing to open (a patch
    // gap inside the game's own line is not a gap), and both shapes crashed the owner's game: Cultured
    // Start Reloaded built for 1.4 on game 1.5.2. So the grade no longer depends on the direction.
    public string Grade => "Warning";

    public string Headline => Strings.Current.Format(
        "Core.Diagnostics.GameVersionSupport.Gap.Headline", Set.DisplayName, Set.Stem, GameVersion);

    public string Detail
    {
        get
        {
            var range = Set.Versions.Count == 1
                ? Set.Newest.ToString()
                : $"{Set.Oldest} through {Set.Newest}";

            return Strings.Current.Plural(
                "Core.Diagnostics.GameVersionSupport.Gap.Detail",
                Set.Versions.Count, range, GameVersion, Set.FolderPath, string.Join(", ", Set.Versions));
        }
    }

    public string Why => GameIsNewer
        ? Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Gap.Why.GameNewer", Set.Newest)
        : Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Gap.Why.GameOlder", Set.Newest);
}

public sealed record GameVersionSupportReport(
    ModuleVersion GameVersion,
    IReadOnlyList<VersionedAssemblySet> Sets,
    IReadOnlyList<GameVersionGap> Gaps)
{
    public static GameVersionSupportReport Empty { get; } = new(ModuleVersion.Empty, [], []);

    public VersionedAssemblySet? FindByStem(string? stem) => stem is null
        ? null
        : Sets.FirstOrDefault(s => string.Equals(s.Stem, stem, StringComparison.OrdinalIgnoreCase));

    // Warnings first, because the direction that breaks is the one worth reading.
    public IReadOnlyList<GameVersionGap> GapsWorstFirst => [.. Gaps.OrderBy(gap => gap.GameIsNewer)];

    // Kept for callers that count notes. Every gap still reported is a warning now, so this is zero.
    public int NoteCount => Gaps.Count(gap => gap.Grade == "Note");

    // Said once above the rows rather than repeated in each of them. Empty when there are no gaps, so
    // the section's own summary keeps that case.
    public string GapGrading
    {
        get
        {
            if (Gaps.Count == 0)
                return string.Empty;

            var otherLine = Gaps.Count(gap => gap.GameIsNewer);
            var newer = Gaps.Count - otherLine;

            if (otherLine == 0)
                return Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Grading.AllWarnings", newer);

            var line = otherLine == Gaps.Count
                ? Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Grading.AllOtherLine", otherLine)
                : Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Grading.SomeOtherLine", otherLine, Gaps.Count);

            return newer == 0
                ? line
                : line + Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Grading.PlusWarnings", newer);
        }
    }

    public string Summary
    {
        get
        {
            if (GameVersion.IsEmpty)
                return Strings.Current["Core.Diagnostics.GameVersionSupport.Summary.NoGameVersion"];

            if (Sets.Count == 0)
                return Strings.Current.Format("Core.Diagnostics.GameVersionSupport.Summary.NoSets", GameVersion);

            return Gaps.Count == 0
                ? Strings.Current.Plural(
                    "Core.Diagnostics.GameVersionSupport.Summary.NoGaps", Sets.Count, GameVersion)
                : Strings.Current.Plural(
                    "Core.Diagnostics.GameVersionSupport.Summary.SomeGaps", Sets.Count, Gaps.Count, GameVersion);
        }
    }
}

// A module shipping a set of versioned assemblies where none matches the running game is something BEM
// can see without launching anything, so it belongs beside the other install checks rather than only in
// a log after the fact.
public static partial class GameVersionSupport
{
    // Lazy stem so the version is the longest trailing run of dotted numbers, which is what puts
    // Bannerlord.ButterLib.Implementation and 1.4.7 on the right sides of the split. Three components
    // minimum: two would take Newtonsoft.Json.13.0 style names and every other library that happens to
    // end in a pair of numbers.
    //
    // A "v" in front of the version (Bannerlord.MBOptionScreen.v1.4.1.dll, TournamentsXPanded.v1.3.7.dll,
    // CustomFamily.v1.4.dll) marks it as a game version outright, so two components are enough there.
    [GeneratedRegex(@"^(?<stem>.+?)\.(?:v(?<version>\d+\.\d+(?:\.\d+){0,2})|(?<version>\d+\.\d+\.\d+(?:\.\d+)?))$")]
    private static partial Regex VersionedFileName { get; }

    // A single versioned assembly is only a set when its stem names the module itself, which is how
    // ArenaOverhaul.1.4.7.dll reads and how a lone third-party library beside it does not.
    private const int SetWithoutNameEvidence = 2;

    public static GameVersionSupportReport Inspect(
        IReadOnlyList<ModuleEntry> entries, ModuleVersion gameVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sets = new List<VersionedAssemblySet>();

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.Manifest is not { } manifest)
                continue;

            sets.AddRange(SetsIn(manifest));
        }

        var ordered = sets
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Stem, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One gap per module and set, not one per platform folder: a mod that ships the same set for
        // Steam and Xbox (TAOM Dependencies) raised every gap twice. The folder that stands for it is the
        // Steam one when the module has one there, since the gap is the same build list either way.
        var gaps = gameVersion.IsEmpty
            ? []
            : ordered.Where(s => !s.CoversLineOf(gameVersion))
                .GroupBy(s => (s.ModuleId, Stem: s.Stem.ToUpperInvariant()))
                .Select(g => g
                    .OrderBy(s => string.Equals(
                        Path.GetFileName(s.FolderPath), BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .First())
                .Select(s => new GameVersionGap(s, gameVersion))
                .ToList();

        return new GameVersionSupportReport(gameVersion, ordered, gaps);
    }

    // Public so a log that names a versioned assembly can be split the same way the folder scan splits
    // a file name, rather than by a second rule that could disagree with this one.
    public static (string Stem, ModuleVersion Version)? Split(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = VersionedFileName.Match(stem);

        if (!match.Success || !ModuleVersion.TryParse(match.Groups["version"].Value, out var version))
            return null;

        return (match.Groups["stem"].Value, version);
    }

    private static IEnumerable<VersionedAssemblySet> SetsIn(ModuleManifest manifest)
    {
        foreach (var binary in BinaryFolders(manifest.FolderPath))
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(binary, "*.dll");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var grouped = new Dictionary<string, List<ModuleVersion>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (Split(Path.GetFileName(file)) is not { } split)
                    continue;

                if (!grouped.TryGetValue(split.Stem, out var versions))
                    grouped[split.Stem] = versions = [];

                versions.Add(split.Version);
            }

            foreach (var (stem, versions) in grouped)
            {
                if (versions.Count < SetWithoutNameEvidence && !NamesTheModule(stem, manifest))
                    continue;

                yield return new VersionedAssemblySet(
                    manifest.Id,
                    manifest.Name.Length > 0 ? manifest.Name : manifest.Id.Value,
                    stem,
                    binary,
                    [.. versions.Order()]);
            }
        }
    }

    private static bool NamesTheModule(string stem, ModuleManifest manifest) =>
        string.Equals(stem, manifest.Id.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(stem, Path.GetFileName(manifest.FolderPath), StringComparison.OrdinalIgnoreCase);

    // Whatever the module actually ships under bin, rather than the platform folders the game install
    // has: a mod built only for Win64 in a Game Pass install still has its assemblies read here.
    private static IEnumerable<string> BinaryFolders(string moduleFolderPath)
    {
        if (string.IsNullOrWhiteSpace(moduleFolderPath))
            yield break;

        var bin = Path.Combine(moduleFolderPath, "bin");

        string[] folders;

        try
        {
            folders = Directory.Exists(bin) ? Directory.GetDirectories(bin) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var folder in folders)
            yield return folder;
    }
}
