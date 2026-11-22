using System.Reflection.Metadata;
using System.Security.Cryptography;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// One type that registers under a name: the module and file it is in, and the type's full name there.
public sealed record ManagedTypeSite(ModuleId Module, string DisplayName, bool IsOfficial, string Assembly, string FullName);

// Two types of the enabled SubModule files, each a ManagedObject or a DotNetObject, with the same simple name.
//
// The game keys such types by simple name, with no namespace: TaleWorlds.MountAndBlade Module.cs
// CollectModuleAssemblyTypes does types.Add(type.Name, type) for every type of a SubModule's file that
// derives from ManagedObject or DotNetObject, and a second type of the same name throws inside the try,
// which turns it into a CriticalError that stops the load. Across files it is TaleWorlds.DotNet Managed.cs
// AddTypes, which unions the new types into the known ones and calls ToDictionary on the result, so a
// name already registered by an earlier SubModule file throws an ArgumentException, and Module.AddSubModule
// calls it with nothing around it.
//
// Left out on purpose: a clash with a type of the game's own engine assemblies (CoreManaged.OnStart
// registers those at start, but which assemblies are loaded by then is not provable from the files), and
// two files with one assembly identity (Assembly.LoadFrom returns the assembly already loaded under an
// identity, which is name, version, culture and public key token, so a later copy never loads and its
// types never register). Copies whose identity differs both load, even under one file name.
public sealed record DuplicateManagedTypeName(string SimpleName, IReadOnlyList<ManagedTypeSite> Sites)
{
    // The site the game reaches last, which is where AddTypes or types.Add throws.
    public ManagedTypeSite Last => Sites[^1];

    public bool WithinOneFile => Sites.Select(s => (s.Module, s.Assembly)).Distinct().Count() == 1;

    // The module a person can turn off to clear it: the last site that is not the game's own.
    public ManagedTypeSite? Culprit => Sites.LastOrDefault(s => !s.IsOfficial);
}

public static class DuplicateManagedTypeNames
{
    private static readonly IReadOnlySet<string> Roots = new HashSet<string>(StringComparer.Ordinal)
    {
        "TaleWorlds.DotNet.ManagedObject",
        "TaleWorlds.DotNet.DotNetObject"
    };

    public static IReadOnlyList<DuplicateManagedTypeName> Find(
        IReadOnlyList<ModuleEntry> loadOrder, string? gameInstallPath, string platformFolder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);
        ArgumentNullException.ThrowIfNull(platformFolder);

        var enabled = loadOrder.Where(e => e.IsEnabled && e.Manifest is { FolderPath.Length: > 0 }).ToList();

        var shared = TypeAncestry.Folders(loadOrder, gameInstallPath, platformFolder);

        var sites = new List<(string Name, string Identity, ManagedTypeSite Site)>();

        using var ancestry = new TypeAncestry(shared);

        // Assembly.LoadFrom returns the assembly already loaded under an identity, so only the first module (in
        // load order) to ship an identity contributes its types; a later copy's own types never register.
        var owner = new Dictionary<string, ModuleId>(StringComparer.Ordinal);

        foreach (var entry in enabled)
        {
            if (entry.Manifest?.DeclaredAssemblies is not { } declared)
                continue;

            foreach (var assembly in declared.Where(d => d.IsSubModuleDll && !d.IsPattern && d.Gate.SkippedIn(platformFolder) is null))
            {
                var path = Path.Combine(entry.Manifest.FolderPath, "bin", platformFolder, assembly.Name);

                if (!File.Exists(path) || ancestry.Open(path) is not { } image)
                    continue;

                var identity = IdentityOf(image.Reader);

                if (identity is null)
                    continue;

                if (owner.TryGetValue(identity, out var first) && first != entry.Id)
                    continue;

                owner.TryAdd(identity, entry.Id);

                foreach (var (handle, name) in image.Types.Select(t => (t.Value, t.Key)))
                {
                    if (ancestry.Derives(image, handle, Roots) != Lineage.Yes)
                        continue;

                    var simple = image.Reader.GetString(image.Reader.GetTypeDefinition(handle).Name);

                    sites.Add((simple, identity, new ManagedTypeSite(entry.Id, entry.DisplayName, entry.IsOfficial, assembly.Name, name)));
                }
            }
        }

        var found = new List<DuplicateManagedTypeName>();

        foreach (var group in sites.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            // The same identity in two modules is one assembly to the runtime and registers its types once.
            var distinct = group
                .GroupBy(s => (s.Identity, s.Site.FullName))
                .Select(g => g.First().Site)
                .ToList();

            if (distinct.Count < 2)
                continue;

            var duplicate = new DuplicateManagedTypeName(group.Key, distinct);

            if (duplicate.Culprit is not null)
                found.Add(duplicate);
        }

        return found;
    }

    // What Assembly.LoadFrom compares: name (case-insensitive), version, culture and public key token, read
    // from the AssemblyDef row. Null when the file has no assembly row to read.
    private static string? IdentityOf(MetadataReader reader)
    {
        if (!reader.IsAssembly)
            return null;

        var definition = reader.GetAssemblyDefinition();
        var key = reader.GetBlobBytes(definition.PublicKey);
        var token = key.Length == 0 ? "null" : Convert.ToHexString(SHA1.HashData(key)[^8..].Reverse().ToArray());

        return string.Join("|",
            reader.GetString(definition.Name).ToLowerInvariant(),
            definition.Version,
            reader.GetString(definition.Culture).ToLowerInvariant(),
            token);
    }

    public static string Describe(DuplicateManagedTypeName duplicate) => Strings.Current.Format(
        duplicate.WithinOneFile
            ? "Core.Modules.DuplicateManagedTypeName.Within"
            : "Core.Modules.DuplicateManagedTypeName.Across",
        duplicate.SimpleName,
        string.Join(", ", duplicate.Sites.Select(s => s.FullName + " (" + s.DisplayName + ")")));
}
