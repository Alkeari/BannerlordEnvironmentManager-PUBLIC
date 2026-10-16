using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// How one shadowed copy differs from the copy the game actually binds. The runtime binds a simple
// assembly name once, so every module that references that name executes against the winner whatever
// it shipped itself, and each of these is a different way for that to go wrong.
public enum AssemblyCopyDifference
{
    // The same assembly, so which file wins cannot matter. Never reported.
    None,

    // Older than the copy that binds. The module runs against a build that arrived after the one it
    // was compiled against, which a library's compatibility promise is supposed to cover and, for a
    // library that rewrites other people's methods, repeatedly has not.
    OlderThanTheOneThatBinds,

    // Newer than the copy that binds. Anything the module calls that arrived after the loaded version
    // is simply not there.
    NewerThanTheOneThatBinds,

    // The same version and a different build. This is the one nothing reveals: the version string
    // matches, so a repacked or merged assembly binds in place of the original and every tool that
    // compares versions agrees they are the same file.
    SameVersionDifferentBuild
}

public sealed record AssemblyCopy(
    ModuleId ModuleId,
    string DisplayName,
    string Path,
    string AssemblyName,
    Version Version,
    // Null when the file would not open, which is a different statement from a file of no length.
    // Printing an unreadable size as 0 made every copy look empty and made the one number that
    // separates two builds of the same version useless.
    long? SizeBytes,
    Guid BuildId,
    int LoadOrderIndex);

// IsDeclaredByItsModule is the difference between a spare copy and a file the game is required to
// open. The runtime binds a simple assembly name once, but the engine does not reach a declared
// assembly through binding at all: TaleWorlds.ModuleManager builds <module>\bin\<platform>\<DLLName>
// and opens that exact path. A copy that is shadowed for binding purposes is therefore still loaded
// by name from its own folder, and deleting it stops the game starting.
//
// BEM learned this by doing it. Its own remedy removed BerserkerKingCloak.dll from "Empires at War",
// whose SubModule.xml declares that file in a SubModule of its own, because "Jarl's Grafted Armory"
// ships a copy that binds first. The game refused to launch with
// "cannot find: ..\..\Modules\Empires at War\bin\Win64_Shipping_Client\BerserkerKingCloak.dll" until
// the file came back out of the Recycle Bin.
public sealed record ShadowedCopy(
    AssemblyCopy Copy,
    AssemblyCopyDifference Difference,
    bool IsDeclaredByItsModule);

// One assembly name, once, however many copies of it are installed. Twenty-six copies of 0Harmony are
// a single problem with twenty-six pieces of evidence, and reporting them as twenty-six rows is how a
// real finding gets buried.
public sealed record ShadowedAssembly(
    string FileName,
    string AssemblyName,
    AssemblyCopy Winner,
    IReadOnlyList<ShadowedCopy> Shadowed,
    int IdenticalCopyCount,
    IReadOnlyList<ModuleId> DependentModules)
{
    public int ShippingModuleCount =>
        1 + IdenticalCopyCount + Shadowed.Select(s => s.Copy.ModuleId).Distinct().Count();

    public int TotalCopyCount => 1 + IdenticalCopyCount + Shadowed.Count;

    // The copies BEM may send to the Recycle Bin, which is never all of them. A copy its own module
    // declares is off limits however thoroughly it is shadowed, because the game opens it by path out
    // of that module's folder rather than binding it by name.
    public IReadOnlyList<ShadowedCopy> Removable => [.. Shadowed.Where(s => !s.IsDeclaredByItsModule)];

    // Still reported, never removed. Two different builds of one assembly is worth knowing about even
    // when the answer is not a deletion.
    public IReadOnlyList<ShadowedCopy> Declared => [.. Shadowed.Where(s => s.IsDeclaredByItsModule)];

    public bool CanRemoveSafely => Removable.Count > 0;

    public string Summarize()
    {
        var kinds = new List<string>();

        Count(AssemblyCopyDifference.NewerThanTheOneThatBinds, Strings.Current["Core.Modules.Duplicate.Kind.Newer"]);
        Count(AssemblyCopyDifference.OlderThanTheOneThatBinds, Strings.Current["Core.Modules.Duplicate.Kind.Older"]);
        Count(AssemblyCopyDifference.SameVersionDifferentBuild, Strings.Current["Core.Modules.Duplicate.Kind.DifferentBuild"]);

        return Strings.Current.Format(
            "Core.Modules.Duplicate.Summarize", FileName, TotalCopyCount, Winner.Version, Winner.DisplayName, string.Join(", ", kinds));

        void Count(AssemblyCopyDifference difference, string noun)
        {
            var count = Shadowed.Count(s => s.Difference == difference);

            if (count > 0)
                kinds.Add(Strings.Current.Format("Core.Modules.Duplicate.CountNoun", count, noun));
        }
    }

    // Grouped by the build rather than listed one per module, because the same file in eleven mods is
    // one fact. Every path stays reachable through DescribePaths, which the removal question shows.
    //
    // The removal refusal rides here rather than in a member of its own because it has to reach the
    // reader before the button does, and this is the sentence the row prints under the heading.
    public string DescribeCopies()
    {
        var copies = string.Join("; ", Shadowed
            .GroupBy(s => (s.Copy.Version, s.Copy.SizeBytes, s.Copy.BuildId))
            .OrderByDescending(g => g.Count())
            .Select(g => Strings.Current.Format(
                "Core.Modules.Duplicate.CopyGroup",
                g.Key.Version,
                DescribeSize(g.Key.SizeBytes),
                string.Join(", ", g.Select(s => s.Copy.DisplayName).Order(StringComparer.OrdinalIgnoreCase)))));

        return Declared.Count == 0 ? copies : $"{copies}. {DescribeRemovalRefusal()}";
    }

    // Why a copy stays where it is, in the shape the reorder refusal set: what BEM will not do, the
    // mechanism that makes it wrong, and what actually settles it.
    public string DescribeRemovalRefusal()
    {
        var declared = Declared;

        if (declared.Count == 0)
            return string.Empty;

        var names = declared
            .Select(s => $"'{s.Copy.DisplayName}'")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var declares = Strings.Current.Plural("Core.Modules.Duplicate.Declares", names.Count, FileName);

        return Strings.Current.Plural(
            "Core.Modules.Duplicate.RemovalRefusal", names.Count, string.Join(", ", names), declares);
    }

    // A size BEM could not read says so. It used to print as "0 bytes", which read as an empty file and
    // hid the fact that the path was gone or held open by something else.
    private static string DescribeSize(long? sizeBytes) => sizeBytes is { } bytes
        ? $"{bytes:N0} bytes"
        : Strings.Current["Core.Modules.Duplicate.SizeUnreadable"];

    public string DescribePaths() =>
        string.Join(Environment.NewLine, Shadowed.Select(s => s.Copy.Path));

    // Why one mismatched assembly matters more than another, read off what the assembly is rather than
    // off a list of library names BEM would have to keep current. An assembly only its own shippers
    // reference is their business; one that other modules bind without carrying is a shared library,
    // and every one of those modules runs against whichever copy happened to win.
    // Whether a reorder is a remedy BEM may offer for this finding at all.
    //
    // Making a different copy bind means changing which module loads first, and there are only two ways
    // to do that: move the winner down, or move the shadowing module up past it. When the winner is
    // Harmony, ButterLib, UIExtenderEx or MBOptionScreen, both are wrong. Those have to load before
    // Native, and a content module has to load after it, so there is no position that satisfies both.
    // BEM used to offer the first of the two anyway.
    public bool CanReorderSafely => !ModuleTiers.LoadsBeforeContent(Winner.ModuleId);

    // Naming a problem is not solving it, and neither is a button that would break the install. When
    // there is no safe move this says why, and what would actually fix it.
    public string DescribeReorderRefusal() =>
        Strings.Current.Format("Core.Modules.Duplicate.ReorderRefusal", Winner.DisplayName, Winner.Version, AssemblyName);

    public string DescribeReach() => DependentModules.Count == 0
        ? Strings.Current.Plural("Core.Modules.Duplicate.Reach.None", ShippingModuleCount, AssemblyName)
        : Strings.Current.Plural("Core.Modules.Duplicate.Reach.Some", DependentModules.Count, AssemblyName);
}

public sealed record ShadowedCopyRefusal(string Path, string Reason);

public sealed record ShadowedCopyRemoval(
    string FileName,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ShadowedCopyRefusal> Refused)
{
    // Reports what reached the Recycle Bin, not what was asked for. A message that says twenty-four
    // files went when the game had one of them open sends the user looking for a file that is exactly
    // where it was.
    public string Describe()
    {
        if (Removed.Count == 0 && Refused.Count == 0)
            return Strings.Current.Format("Core.Modules.Duplicate.Removal.NothingListed", FileName);

        var moved = Removed.Count == 0
            ? Strings.Current["Core.Modules.Duplicate.Removal.NothingMoved"]
            : Strings.Current.Plural("Core.Modules.Duplicate.Removal.Moved", Removed.Count, FileName);

        if (Refused.Count == 0)
            return moved;

        var left = string.Join("; ", Refused.Select(r => $"{r.Path}: {r.Reason}"));

        return $"{moved} {Strings.Current.Plural("Core.Modules.Duplicate.Removal.Refused", Refused.Count, left)}";
    }
}

public static class DuplicateAssemblies
{
    // The folder the running build loads module assemblies from. Every mod that also targets the Xbox
    // runtime ships a second copy of each of its DLLs beside this one, and counting those as duplicates
    // turns almost every multi-target mod into a conflict with itself. A Game Pass install loads the
    // other folder, so the caller passes whichever one its install actually uses.
    private const string DefaultPlatformFolder = "Win64_Shipping_Client";

    public static IReadOnlyList<ShadowedAssembly> Find(
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> loadOrder,
        string? loadedPlatformFolder = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(loadOrder);

        var positions = new Dictionary<ModuleId, int>();
        var names = new Dictionary<ModuleId, string>();
        var declares = new Dictionary<ModuleId, IReadOnlyList<DeclaredAssembly>>();

        for (var i = 0; i < loadOrder.Count; i++)
        {
            if (!loadOrder[i].IsEnabled || loadOrder[i].IsOfficial)
                continue;

            if (!positions.TryAdd(loadOrder[i].Id, i))
                continue;

            names[loadOrder[i].Id] = loadOrder[i].DisplayName;
            declares[loadOrder[i].Id] = loadOrder[i].Manifest?.DeclaredAssemblies ?? [];
        }

        var shipped = index.Assemblies
            .Where(a => a.ModuleId is not null && positions.ContainsKey(a.ModuleId.Value))
            .ToList();

        var referencedBy = ReferencedBy(shipped);
        var platformFolder = loadedPlatformFolder ?? DefaultPlatformFolder;
        var findings = new List<ShadowedAssembly>();

        var loaded = shipped
            .Where(a => a.AssemblyVersion is not null && IsLoadedByTheGame(a.Path, platformFolder))
            .GroupBy(a => Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var group in loaded)
        {
            // Reading the module version id costs a second open of the file, so it is paid only where
            // one name has copies in more than one module. Everything else cannot shadow anything.
            if (group.Select(a => a.ModuleId!.Value).Distinct().Count() < 2)
                continue;

            // The runtime binds a simple assembly name once. Modules load in load order, so the
            // earliest module holding a copy is the one every later reference resolves to, whatever the
            // later modules ship. Ordering by path second only keeps the result stable.
            var copies = group
                .OrderBy(a => positions[a.ModuleId!.Value])
                .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .Select(a => Describe(a, positions, names))
                .ToList();

            var winner = copies[0];
            var shadowed = new List<ShadowedCopy>();
            var identical = 0;

            foreach (var copy in copies.Skip(1))
            {
                if (copy.ModuleId == winner.ModuleId)
                    continue;

                var difference = Compare(winner, copy);

                if (difference == AssemblyCopyDifference.None)
                    identical++;
                else
                    shadowed.Add(new ShadowedCopy(copy, difference, IsDeclaredByItsModule(copy, declares)));
            }

            if (shadowed.Count == 0)
                continue;

            var shippers = copies.Select(c => c.ModuleId).ToHashSet();

            findings.Add(new ShadowedAssembly(
                group.Key,
                winner.AssemblyName,
                winner,
                shadowed,
                identical,
                [
                    .. (referencedBy.TryGetValue(winner.AssemblyName, out var referencing)
                            ? referencing
                            : [])
                        .Where(id => !shippers.Contains(id))
                        .OrderBy(id => id.Value, StringComparer.OrdinalIgnoreCase)
                ]));
        }

        // No invented severity scale. The two numbers that decide how far a mismatch reaches are how
        // many modules bind the name without shipping it and how many copies disagree, so the list is
        // ordered by those and says so.
        return
        [
            .. findings
                .OrderByDescending(f => f.DependentModules.Count)
                .ThenByDescending(f => f.Shadowed.Count)
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
        ];
    }

    // Sends every removable shadowed copy this finding lists to the Recycle Bin, and never the copy
    // that binds nor a copy its own module declares. Removing them changes nothing about which
    // assembly loads today, which is exactly the point: it makes that stay true when the load order
    // changes. The caller hands in the Recycle Bin call so Core stays free of Windows, and nothing is
    // reported gone that is still on disk afterwards.
    public static ShadowedCopyRemoval RemoveShadowedCopies(ShadowedAssembly finding, Action<string> recycle)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(recycle);

        var removed = new List<string>();
        var refused = new List<ShadowedCopyRefusal>();

        foreach (var copy in finding.Shadowed)
        {
            var path = copy.Copy.Path;

            if (string.Equals(path, finding.Winner.Path, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add(new ShadowedCopyRefusal(path, "this is the copy the game loads"));
                continue;
            }

            // Stated again at the moment of the deletion rather than left to whoever built the finding.
            // This is the check whose absence deleted a file the game then could not find, and it costs
            // one comparison to make the mistake unreachable from any caller.
            if (copy.IsDeclaredByItsModule)
            {
                refused.Add(new ShadowedCopyRefusal(
                    path,
                    $"'{copy.Copy.DisplayName}' declares {finding.FileName} in its own SubModule.xml, so the "
                    + "game opens this exact path and deleting it stops the game starting"));
                continue;
            }

            if (!File.Exists(path))
            {
                refused.Add(new ShadowedCopyRefusal(path, "it is not on disk"));
                continue;
            }

            // A shadowed copy can sit inside a subscribed Workshop item, whose files Steam owns and
            // replaces. Deleting one there is undone by the next Steam check and breaks every install
            // on the machine until it is.
            if (WorkshopContent.Owns(path))
            {
                refused.Add(new ShadowedCopyRefusal(
                    path,
                    "it belongs to a Steam Workshop item, whose files Steam owns and puts back, so it is "
                    + "removed by unsubscribing on its Workshop page"));
                continue;
            }

            try
            {
                recycle(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused.Add(new ShadowedCopyRefusal(path, ex.Message));
                continue;
            }

            if (File.Exists(path))
            {
                refused.Add(new ShadowedCopyRefusal(path, "it is still on disk"));
                continue;
            }

            removed.Add(path);
        }

        return new ShadowedCopyRemoval(finding.FileName, removed, refused);
    }

    // Every declaration form the engine and its loaders actually read, checked against the module the
    // file sits in and no other. The tags are not consulted: a SubModule this client never loads still
    // declares the file, and a dedicated-server assembly is no more deletable for being unused today.
    private static bool IsDeclaredByItsModule(
        AssemblyCopy copy, Dictionary<ModuleId, IReadOnlyList<DeclaredAssembly>> declares)
    {
        var fileName = Path.GetFileName(copy.Path);

        return declares.TryGetValue(copy.ModuleId, out var declared)
            && declared.Any(d => d.Matches(fileName));
    }

    private static Dictionary<string, HashSet<ModuleId>> ReferencedBy(IEnumerable<IndexedAssembly> shipped)
    {
        var referencedBy = new Dictionary<string, HashSet<ModuleId>>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in shipped)
        {
            foreach (var name in assembly.ReferencedAssemblyNames)
            {
                if (!referencedBy.TryGetValue(name, out var modules))
                    referencedBy[name] = modules = [];

                modules.Add(assembly.ModuleId!.Value);
            }
        }

        return referencedBy;
    }

    private static AssemblyCopy Describe(
        IndexedAssembly assembly,
        Dictionary<ModuleId, int> positions,
        Dictionary<ModuleId, string> names)
    {
        var (buildId, sizeBytes) = ReadIdentity(assembly.Path);

        return new AssemblyCopy(
            assembly.ModuleId!.Value,
            names[assembly.ModuleId.Value],
            assembly.Path,
            assembly.Name,
            assembly.AssemblyVersion!,
            sizeBytes,
            buildId,
            positions[assembly.ModuleId.Value]);
    }

    private static AssemblyCopyDifference Compare(AssemblyCopy binds, AssemblyCopy copy)
    {
        if (copy.Version != binds.Version)
        {
            return copy.Version > binds.Version
                ? AssemblyCopyDifference.NewerThanTheOneThatBinds
                : AssemblyCopyDifference.OlderThanTheOneThatBinds;
        }

        // Same version, so the version string settles nothing. The module version id is the compiler's
        // own identity for one build of one assembly: two files carrying the same id and the same
        // length are the same assembly whichever one the game binds.
        //
        // An id BEM could not read comes back empty and matches on purpose, because a finding it cannot
        // evidence is worse than none. That used to be defeated by the size: an unreadable file came
        // back at zero bytes, zero never equals the winner's length, and one file the game had open for
        // a moment became an accusation that the mod shadows a different build.
        if (copy.BuildId == Guid.Empty || binds.BuildId == Guid.Empty)
            return AssemblyCopyDifference.None;

        // A size neither side could read settles nothing either way, so it is not allowed to be the
        // thing that makes two files look different. Two unknowns are not a match.
        if (copy.SizeBytes is not { } copySize || binds.SizeBytes is not { } bindsSize)
            return AssemblyCopyDifference.None;

        return copySize == bindsSize && copy.BuildId == binds.BuildId
            ? AssemblyCopyDifference.None
            : AssemblyCopyDifference.SameVersionDifferentBuild;
    }

    // The size and the id are read separately because they fail separately: a file that opens but holds
    // no readable metadata still has a length worth reporting, and only a file that would not open at
    // all leaves both unknown.
    private static (Guid BuildId, long? SizeBytes) ReadIdentity(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var sizeBytes = stream.Length;

            try
            {
                using var peReader = new PEReader(stream);

                if (!peReader.HasMetadata)
                    return (Guid.Empty, sizeBytes);

                var reader = peReader.GetMetadataReader();

                return (reader.GetGuid(reader.GetModuleDefinition().Mvid), sizeBytes);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                return (Guid.Empty, sizeBytes);
            }
        }
        // The file would not open at all, most often because it is no longer there. Neither number is
        // known, and reporting the size as zero claimed one of them.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Guid.Empty, null);
        }
    }

    private static bool IsLoadedByTheGame(string path, string platformFolder) =>
        string.Equals(
            Path.GetFileName(Path.GetDirectoryName(path)),
            platformFolder,
            StringComparison.OrdinalIgnoreCase);
}
