using System.Globalization;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum FrameSource
{
    // The frame is not a patch and no mod assembly defines its type: the game's own code.
    None,

    // The frame's type is defined in a mod's assembly.
    ModAssembly,

    // BEM's own scan of the enabled mods for Harmony patches on this method, read from their assemblies.
    StaticPatchScan,

    // The Companion recorded which mods patched this method at the moment the exception was caught.
    RecordedPatchOwner,

    // No mod is on the stack; the mod's XML or XSLT touches data the crashing game method reads.
    DataFeed,

    // The engine's own log shows this mod's data file failed the schema while the data the crashing method reads was loading.
    EngineLog,
}

public sealed record AttributedFrame(
    string Raw,
    string Type,
    string Method,
    int? PatchNumber,
    IReadOnlyList<string> Modules,
    FrameSource Source)
{
    public bool IsPatchFrame => PatchNumber is not null;
}

public sealed record InvolvedModule(string Module, int Frames, int Score, FrameSource Best)
{
    // The module files behind a DataFeed suspect, relative to the module folder.
    public IReadOnlyList<string> Files { get; init; } = [];

    // What the engine's log says about that file, when it does.
    public EngineEvidence? Engine { get; init; }
}

// The schema failure behind an EngineLog suspect: the file, its error count, the kind of data it was loaded as,
// the missing ids the crashing method reads (or empty) and how many objects in all the game printed as missing.
public sealed record EngineEvidence(string File, int ErrorCount, string Element, IReadOnlyList<string> NamedIds, int NullCount);

public sealed record StackAttribution(IReadOnlyList<AttributedFrame> Frames, IReadOnlyList<InvolvedModule> Involved);

// Which mods a managed crash went through, read from the exception's stack trace text. A method a mod
// patches runs as a Harmony replacement named Method_PatchN, so the frame names the method but not the
// mod; the owner comes from what the Companion recorded when it caught the exception, else from BEM's
// static scan of the mods' patches, and a frame in a mod's own assembly maps through the type's
// assembly. This reports who was on the stack, never a verdict on who is wrong.
public static partial class CrashAttributor
{
    private const int RecordedWeight = 3;

    private const int StaticWeight = 2;

    private const int AssemblyWeight = 1;

    [GeneratedRegex(@"^\s*at\s+(?:\(wrapper[^)]*\)\s*)?(?<sig>[^\r\n]*?)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"^DMD<.*?>\.(?=[A-Za-z_])", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicPrefix();

    [GeneratedRegex(@"_Patch(?<n>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchSuffix();

    public static StackAttribution Attribute(
        string stack,
        IReadOnlyList<CrashPatchOwners>? recorded = null,
        IReadOnlyList<HarmonyPatchOverlap>? overlaps = null,
        Func<string, string?>? moduleOfType = null)
    {
        var frames = new List<AttributedFrame>();

        foreach (var line in (stack ?? string.Empty).Split('\n'))
        {
            if (FrameLine().Match(line) is not { Success: true } match)
                continue;

            var signature = DynamicPrefix().Replace(match.Groups["sig"].Value.Trim(), string.Empty);
            var dot = signature.LastIndexOf('.');

            if (dot <= 0 || dot == signature.Length - 1)
                continue;

            var type = signature[..dot];
            var method = signature[(dot + 1)..];
            var suffix = PatchSuffix().Match(method);
            var baseMethod = suffix.Success ? method[..suffix.Index] : method;
            int? number = suffix.Success ? int.Parse(suffix.Groups["n"].Value, CultureInfo.InvariantCulture) : null;
            var shortType = Short(type);

            var owners = recorded?
                .Where(r => Short(r.Type) == shortType && r.Method == baseMethod)
                .SelectMany(r => r.Modules)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var scanned = number is null || overlaps is null
                ? null
                : overlaps
                    .Where(o => Short(o.TargetType) == shortType && o.TargetMember == baseMethod)
                    .SelectMany(o => o.Modules.Select(m => m.Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            IReadOnlyList<string> modules = [];
            var source = FrameSource.None;

            if (owners is { Count: > 0 })
            {
                modules = owners;
                source = FrameSource.RecordedPatchOwner;
            }
            else if (scanned is { Count: > 0 })
            {
                modules = scanned;
                source = FrameSource.StaticPatchScan;
            }
            else if (moduleOfType?.Invoke(type) is { Length: > 0 } owner)
            {
                modules = [owner];
                source = FrameSource.ModAssembly;
            }

            frames.Add(new AttributedFrame(line.Trim(), type, method, number, modules, source));
        }

        return new StackAttribution(frames, Rank(frames));
    }

    // A crash known only by its code, from a dump or the engine's error log, has no frames. The mods that
    // patch one method together are still the ones worth looking at, transpilers first.
    public static IReadOnlyList<InvolvedModule> FromOverlaps(IReadOnlyList<HarmonyPatchOverlap> overlaps)
    {
        var scores = new Dictionary<string, (int Frames, int Score)>(StringComparer.OrdinalIgnoreCase);

        foreach (var overlap in overlaps)
        {
            foreach (var module in overlap.Modules)
            {
                var weight = overlap.HasTranspiler ? StaticWeight : AssemblyWeight;
                scores[module.Value] = scores.TryGetValue(module.Value, out var have)
                    ? (have.Frames + 1, have.Score + weight)
                    : (1, weight);
            }
        }

        return [.. scores
            .Select(p => new InvolvedModule(p.Key, p.Value.Frames, p.Value.Score, FrameSource.StaticPatchScan))
            .OrderByDescending(m => m.Score).ThenBy(m => m.Module, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<InvolvedModule> Rank(List<AttributedFrame> frames)
    {
        var order = new List<string>();
        var tally = new Dictionary<string, (int Frames, int Score, FrameSource Best)>(StringComparer.OrdinalIgnoreCase);

        foreach (var frame in frames)
        {
            var weight = frame.Source switch
            {
                FrameSource.RecordedPatchOwner => RecordedWeight,
                FrameSource.StaticPatchScan => StaticWeight,
                FrameSource.ModAssembly => AssemblyWeight,
                _ => 0,
            };

            foreach (var module in frame.Modules)
            {
                if (!tally.TryGetValue(module, out var have))
                {
                    order.Add(module);
                    have = (0, 0, frame.Source);
                }

                tally[module] = (have.Frames + 1, have.Score + weight, (FrameSource)Math.Max((int)have.Best, (int)frame.Source));
            }
        }

        // Ties keep stack order, so the module nearest the throw comes first.
        return [.. order
            .Select((m, i) => (Module: m, Index: i, Tally: tally[m]))
            .OrderByDescending(x => x.Tally.Score).ThenBy(x => x.Index)
            .Select(x => new InvolvedModule(x.Module, x.Tally.Frames, x.Tally.Score, x.Tally.Best))];
    }

    private static string Short(string type)
    {
        var cut = type.LastIndexOfAny(['.', '+', '/']);

        return cut < 0 ? type : type[(cut + 1)..];
    }
}
