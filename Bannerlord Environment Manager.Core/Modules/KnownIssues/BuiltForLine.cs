using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// The game assemblies of another game version on this computer (another instance), to test a mod's
// loaded build against.
public sealed record GameBuildOnDisk(ModuleVersion Version, AssemblyIndex Index);

public enum BuiltForLineEvidence
{
    // Every reference that fails on this game resolves against another game line's assemblies on disk.
    BindsOnOtherLine,

    // No other game line is on disk to test against; the module's own manifest requires an official
    // module of another line, and its loaded build fails to bind here.
    DeclaredLine
}

public sealed record BuiltForLineFinding(
    ModuleId ModuleId,
    string ModuleName,
    string ModuleVersion,
    string BuiltForLine,
    string GameLine,
    BuiltForLineEvidence Evidence,
    string FirstApi,
    int ReferenceCount);

// A module whose loaded build was made for another game line: the one MissingGameApi shape that predicted
// real crashes (DeathForAll 6.0.0 built for 1.5 calling PerkHelper.AddPerkBonusForParty on 1.4.8;
// MainQuestNoble's MapTrackerManager on 1.4.8, 5 of 5 launches). A missing reference alone is not that:
// most are in builds or paths that never run, and 86 of 86 watched boots succeeded with them. What makes
// a module "will likely crash" is that its loaded build binds cleanly on another line and not on this one.
//
// Metadata only. Nothing here loads or runs mod code.
public static class BuiltForLine
{
    private static readonly HashSet<string> OfficialIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Native", "SandBoxCore", "SandBox", "StoryMode", "CustomBattle", "Multiplayer", "BirthAndDeath", "NavalDLC"
    };

    public static IReadOnlyList<BuiltForLineFinding> Inspect(
        MissingApiReport current,
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        string? loadedPlatformFolder,
        ModuleVersion gameVersion,
        IReadOnlyList<GameBuildOnDisk>? otherBuilds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (gameVersion.IsEmpty || current.Findings.Count == 0 || index.Failed)
            return [];

        var gameLine = GameLine.Of(gameVersion);
        var failing = current.ByModule.ToDictionary(g => g.Key, g => g.ToList());
        var unreadable = current.Problems.Select(p => p.ModuleId).ToHashSet();
        var found = new List<BuiltForLineFinding>();
        var byId = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

        // Newest line first, so a module that would bind on two other lines is named for the newer one.
        foreach (var other in (otherBuilds ?? [])
                     .Where(b => !b.Version.IsEmpty && !b.Index.Failed && !GameLine.Same(b.Version, gameVersion))
                     .OrderByDescending(b => b.Version))
        {
            var remaining = failing.Keys.Where(id => !unreadable.Contains(id)).ToHashSet();

            if (remaining.Count == 0)
                break;

            // The selection stays pinned to this game's version: the question is about the build that
            // loads here, tested against the other line's game assemblies.
            var there = MissingGameApis.Inspect(
                index.WithGameFrom(other.Index), entries, loadedPlatformFolder, gameVersion, remaining);

            var stillFailing = there.Findings.Select(f => f.ModuleId).Concat(there.Problems.Select(p => p.ModuleId)).ToHashSet();

            foreach (var id in remaining.Where(id => !stillFailing.Contains(id)))
            {
                found.Add(Finding(id, failing[id], byId, GameLine.Of(other.Version), gameLine, BuiltForLineEvidence.BindsOnOtherLine));
                failing.Remove(id);
            }
        }

        foreach (var (id, references) in failing)
        {
            if (unreadable.Contains(id) || !byId.TryGetValue(id, out var entry))
                continue;

            if (DeclaredLine(entry, gameVersion) is { } declared)
                found.Add(Finding(id, references, byId, declared, gameLine, BuiltForLineEvidence.DeclaredLine));
        }

        return [.. found.OrderBy(f => f.ModuleName, StringComparer.OrdinalIgnoreCase)];
    }

    // The game line a module's manifest requires through an official module, when that is a different
    // line from the running game's and newer than it. "v1.5.*" and "v1.5.2" both read as line 1.5.
    public static string? DeclaredLine(ModuleEntry entry, ModuleVersion gameVersion)
    {
        ArgumentNullException.ThrowIfNull(entry);

        foreach (var dependency in entry.Dependencies)
        {
            if (dependency.IsIncompatible || dependency.VersionRange.IsAny || !OfficialIds.Contains(dependency.TargetId.Value))
                continue;

            if (!ModuleVersion.TryParse(dependency.VersionRange.Text.Replace('*', '0'), out var declared) || declared.IsEmpty)
                continue;

            if (!GameLine.Same(declared, gameVersion) && declared.CompareTo(gameVersion) > 0)
                return GameLine.Of(declared);
        }

        return null;
    }

    private static BuiltForLineFinding Finding(
        ModuleId id,
        List<MissingApiReference> references,
        IReadOnlyDictionary<ModuleId, ModuleEntry> byId,
        string builtFor,
        string gameLine,
        BuiltForLineEvidence evidence)
    {
        var entry = byId.TryGetValue(id, out var e) ? e : null;
        var first = references.FirstOrDefault(r => r.Kind == MissingApiKind.TypeMissing) ?? references[0];
        var version = entry?.Manifest?.VersionText is { Length: > 0 } text ? text : entry?.Version.ToString() ?? string.Empty;

        return new BuiltForLineFinding(id, first.ModuleName, version, builtFor, gameLine, evidence, first.Api, references.Count);
    }
}
