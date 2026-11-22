using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

internal enum Lineage
{
    // Every base up to System.Object was read and none is a target.
    No,

    // A base in the chain is a target.
    Yes,

    // A base lives in a file that was not found or not readable, so nothing is proven either way.
    Unknown
}

// Whether a type of a mod's file inherits, through any number of bases in any number of files, from one of
// the named types. Built for what the game asks with typeof(X).IsAssignableFrom(type) while it loads a
// SubModule's file (TaleWorlds.MountAndBlade Module.cs AddSubModule and CollectModuleAssemblyTypes).
//
// Answers from metadata alone, so a base BEM cannot read is Unknown and never No: a finding may rest on Yes,
// or on No for a chain read to System.Object, and never on a guess.
internal sealed class TypeAncestry(IReadOnlyList<string> folders) : IDisposable
{
    private const int MaxDepth = 64;

    private readonly Dictionary<string, Image?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _open = [];

    internal sealed class Image(string path, MetadataReader reader, Dictionary<string, TypeDefinitionHandle> types)
    {
        public string Path { get; } = path;

        public MetadataReader Reader { get; } = reader;

        public IReadOnlyDictionary<string, TypeDefinitionHandle> Types { get; } = types;
    }

    // Where the game and the runtime look for a type a mod's file builds on: the game's folders, then each
    // enabled module's own.
    public static List<string> Folders(IReadOnlyList<BannerlordEnvironmentManager.Core.LoadOrder.ModuleEntry> loadOrder, string? gameInstallPath, string platformFolder)
    {
        var folders = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            folders.Add(Path.Combine(gameInstallPath, "bin", platformFolder));
            folders.Add(Path.Combine(gameInstallPath, "bin"));
        }

        folders.AddRange(loadOrder
            .Where(e => e.IsEnabled && e.Manifest is { FolderPath.Length: > 0 })
            .Select(e => Path.Combine(e.Manifest!.FolderPath, "bin", platformFolder)));

        return folders;
    }

    // The file's types by full name, or null when it cannot be read as a .NET assembly.
    public Image? Open(string path)
    {
        if (_byPath.TryGetValue(path, out var known))
            return known;

        Image? image = null;

        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);

            _open.Add(pe);

            if (pe.HasMetadata)
            {
                var reader = pe.GetMetadataReader();
                var types = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);

                foreach (var handle in reader.TypeDefinitions)
                    types.TryAdd(MissingGameApis.NameOf(reader, reader.GetTypeDefinition(handle)), handle);

                image = new Image(path, reader, types);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            image = null;
        }

        _byPath[path] = image;

        return image;
    }

    public Lineage Derives(Image image, TypeDefinitionHandle start, IReadOnlySet<string> targets)
    {
        var current = image;
        var handle = start;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var reader = current.Reader;
            var baseType = reader.GetTypeDefinition(handle).BaseType;

            if (baseType.Kind == HandleKind.TypeSpecification)
                baseType = Underlying(reader, (TypeSpecificationHandle)baseType);

            if (baseType.IsNil)
            {
                // System.Object itself, an interface or a module-level entry: the chain ends here.
                return reader.GetTypeDefinition(handle).BaseType.IsNil ? Lineage.No : Lineage.Unknown;
            }

            if (baseType.Kind == HandleKind.TypeDefinition)
            {
                var next = (TypeDefinitionHandle)baseType;

                if (targets.Contains(MissingGameApis.NameOf(reader, reader.GetTypeDefinition(next))))
                    return Lineage.Yes;

                handle = next;
                continue;
            }

            if (baseType.Kind != HandleKind.TypeReference)
                return Lineage.Unknown;

            var reference = (TypeReferenceHandle)baseType;
            var name = MissingGameApis.NameOf(reader, reference);

            if (targets.Contains(name))
                return Lineage.Yes;

            if (name == "System.Object")
                return Lineage.No;

            if (MissingGameApis.AssemblyNameOf(reader, reference) is not { } assembly)
                return Lineage.Unknown;

            if (IsFramework(assembly))
                return Lineage.No;

            if (Locate(assembly) is not { } found || !found.Types.TryGetValue(name, out var declared))
                return Lineage.Unknown;

            current = found;
            handle = declared;
        }

        return Lineage.Unknown;
    }

    // The type a generic instantiation such as Base<T> is an instance of.
    private static EntityHandle Underlying(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);

        if (blob.ReadByte() != 0x15)
            return default;

        blob.ReadByte();

        return blob.ReadTypeHandle() is { IsNil: false } entity ? entity : default;
    }

    // The runtime's own libraries cannot derive from a game type, so a chain that reaches one has left the
    // game's hierarchy.
    // "System" alone or as a dotted prefix: a mod's own assembly may begin with the word ("SystemicMod").
    private static bool IsFramework(string assembly) =>
        assembly is "mscorlib" or "netstandard" or "WindowsBase" or "System"
        || assembly.StartsWith("System.", StringComparison.Ordinal)
        || assembly.StartsWith("Microsoft.", StringComparison.Ordinal);

    private Image? Locate(string assemblyName)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, assemblyName + ".dll");

            if (File.Exists(path))
                return Open(path);
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var open in _open)
            open.Dispose();

        _open.Clear();
        _byPath.Clear();
    }
}
