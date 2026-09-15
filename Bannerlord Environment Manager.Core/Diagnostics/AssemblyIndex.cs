using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum AssemblyOrigin
{
    Game,
    Module
}

public sealed record IndexedAssembly(
    string Path,
    string Name,
    AssemblyOrigin Origin,
    IReadOnlyList<string> DefinedTypeFullNames,
    IReadOnlyList<string> ReferencedTypeFullNames,
    IReadOnlyList<string> ReferencedAssemblyNames,
    ModuleId? ModuleId = null,
    Version? AssemblyVersion = null);

public sealed record UnreadableAssembly(string Path, string Error, ModuleId? ModuleId = null);

public sealed class AssemblyIndex
{
    private readonly ILookup<string, IndexedAssembly> _byDefinedType;
    private readonly ILookup<string, IndexedAssembly> _byReferencedType;

    private AssemblyIndex(
        IReadOnlyList<IndexedAssembly> assemblies,
        IReadOnlyList<UnreadableAssembly> unreadable,
        string? error)
    {
        Assemblies = assemblies;
        Unreadable = unreadable;
        Error = error;

        _byDefinedType = assemblies
            .SelectMany(a => a.DefinedTypeFullNames, (a, t) => (Type: t, Assembly: a))
            .ToLookup(x => x.Type, x => x.Assembly, StringComparer.Ordinal);

        _byReferencedType = assemblies
            .SelectMany(a => a.ReferencedTypeFullNames, (a, t) => (Type: t, Assembly: a))
            .ToLookup(x => x.Type, x => x.Assembly, StringComparer.Ordinal);
    }

    public static AssemblyIndex Empty { get; } = new([], [], null);

    public IReadOnlyList<IndexedAssembly> Assemblies { get; }

    public IReadOnlyList<UnreadableAssembly> Unreadable { get; }

    // Set when the Modules folder itself could not be enumerated. Without it an index built over
    // nothing would silently exonerate every mod, which is the one lie this engine must not tell.
    public string? Error { get; }

    public bool Failed => Error is not null;

    public IReadOnlyList<IndexedAssembly> FindDeclaring(string typeFullName) =>
        [.. _byDefinedType[typeFullName]];

    public IReadOnlyList<IndexedAssembly> FindReferencing(string typeFullName) =>
        [.. _byReferencedType[typeFullName]];

    public static AssemblyIndex Build(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return Empty;

        var scan = ModuleScanner.ScanAll(gameInstallPath);
        var assemblies = new List<IndexedAssembly>();
        var unreadable = new List<UnreadableAssembly>();

        foreach (var path in EnumerateGameDlls(Path.Combine(gameInstallPath, "bin")))
            Read(path, AssemblyOrigin.Game, moduleId: null, assemblies, unreadable);

        foreach (var module in scan.Modules)
        {
            // SandBox, Native and StoryMode ship inside Modules\, but their assemblies are the game's
            // own code. Counting them as mods would let the static sweep name a module the user never
            // installed and cannot disable.
            var official = module.IsOfficial;

            foreach (var path in EnumerateDlls(Path.Combine(module.FolderPath, "bin"), SearchOption.AllDirectories))
            {
                Read(
                    path,
                    official ? AssemblyOrigin.Game : AssemblyOrigin.Module,
                    official ? null : module.Id,
                    assemblies,
                    unreadable);
            }
        }

        return new AssemblyIndex(assemblies, unreadable, scan.Error);
    }

    // The game ships its own assemblies in bin\ and one level below it, in the platform folder.
    // Deeper are the bundled runtimes (mono, Microsoft.NETCore.App): thousands of DLLs whose types
    // are framework code, not game code. Indexing them costs about two minutes on a real install
    // and would let a pure framework frame resolve as a game frame.
    private static IEnumerable<string> EnumerateGameDlls(string binFolder) =>
        EnumerateDlls(binFolder, SearchOption.TopDirectoryOnly)
            .Concat(EnumerateSubfolders(binFolder)
                .SelectMany(folder => EnumerateDlls(folder, SearchOption.TopDirectoryOnly)));

    private static IReadOnlyList<string> EnumerateSubfolders(string folder) =>
        Enumerate(() => Directory.EnumerateDirectories(folder));

    private static IReadOnlyList<string> EnumerateDlls(string folder, SearchOption option) =>
        Enumerate(() => Directory.EnumerateFiles(folder, "*.dll", option));

    private static IReadOnlyList<string> Enumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return [.. enumerate().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Read(
        string path,
        AssemblyOrigin origin,
        ModuleId? moduleId,
        List<IndexedAssembly> assemblies,
        List<UnreadableAssembly> unreadable)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                unreadable.Add(new UnreadableAssembly(path, "Not a managed assembly.", moduleId));
                return;
            }

            var reader = peReader.GetMetadataReader();

            var name = reader.IsAssembly
                ? reader.GetString(reader.GetAssemblyDefinition().Name)
                : Path.GetFileNameWithoutExtension(path);

            assemblies.Add(new IndexedAssembly(
                path,
                name,
                origin,
                ReadDefinedTypes(reader),
                ReadReferencedTypes(reader),
                [.. reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))],
                moduleId,
                reader.IsAssembly ? reader.GetAssemblyDefinition().Version : null));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            unreadable.Add(new UnreadableAssembly(path, ex.Message, moduleId));
        }
    }

    private static IReadOnlyList<string> ReadDefinedTypes(MetadataReader reader)
    {
        var names = new List<string>(reader.TypeDefinitions.Count);

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);

            if (name is "<Module>")
                continue;

            names.Add(FullName(reader, definition));
        }

        return names;
    }

    private static IReadOnlyList<string> ReadReferencedTypes(MetadataReader reader)
    {
        var names = new List<string>(reader.TypeReferences.Count);

        foreach (var handle in reader.TypeReferences)
            names.Add(FullName(reader, reader.GetTypeReference(handle)));

        return names;
    }

    private static string FullName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();

        return declaring.IsNil
            ? Qualify(reader.GetString(definition.Namespace), name)
            : FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
    }

    private static string FullName(MetadataReader reader, TypeReference reference)
    {
        var name = reader.GetString(reference.Name);

        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? FullName(reader, reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope)) + "+" + name
            : Qualify(reader.GetString(reference.Namespace), name);
    }

    private static string Qualify(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
}
