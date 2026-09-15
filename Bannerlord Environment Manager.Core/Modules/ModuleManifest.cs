namespace BannerlordEnvironmentManager.Core.Modules;

public enum ModuleType
{
    Community,
    Official,
    OfficialOptional
}

public enum ModuleCategory
{
    Singleplayer,
    Multiplayer,
    Both
}

public enum ModuleSource
{
    Modules,
    Workshop
}

// Where a module came from, as one value rather than the type-and-source pair, so a list of these
// covers every installed module exactly once. Official is tested first because the game's own
// modules also ship through the Workshop, and they are never "installed by the user" either way.
public enum ModuleOrigin
{
    Official,
    Manual,
    Workshop
}

public sealed record ModuleManifest(
    ModuleId Id,
    string Name,
    ModuleVersion Version,
    ModuleType Type,
    ModuleCategory Category,
    string FolderPath,
    string ManifestPath,
    IReadOnlyList<ModuleDependency> Dependencies,
    ModuleSource Source = ModuleSource.Modules,
    string? Url = null,
    // ModuleVersion drops a zero fourth component, so a manifest declaring v1.2.3.0 comes back out of
    // it as v1.2.3. Anything written to a file another tool reads echoes this instead of the parsed
    // value, so a shared load order says what SubModule.xml says.
    string? VersionText = null,
    // Type is already rewritten to Community by the time a demoting basis is set, so every protection
    // and every tier keeps following from Type alone. This records only why, so a user whose mod
    // loses the Official badge can be told it was taken away rather than left to wonder, and so a
    // module accepted on its name alone can be told apart from one BEM proved with a signature.
    OfficialClaimBasis OfficialClaimBasis = OfficialClaimBasis.NotAudited,
    // Every assembly the SubModules block names, in declaration order. Null is not empty: it means
    // this manifest came from something that never read that block, and only SubModuleXmlParser fills
    // it. Nothing may read an absent list as "this module declares nothing", because that is exactly
    // the assumption that got a declared assembly deleted.
    IReadOnlyList<DeclaredAssembly>? DeclaredAssemblies = null)
{
    public bool IsOfficial => Type is ModuleType.Official or ModuleType.OfficialOptional;

    public bool OfficialClaimDemoted =>
        OfficialClaimBasis is OfficialClaimBasis.DemotedForeignSignature
            or OfficialClaimBasis.DemotedUnrecognized
            or OfficialClaimBasis.DemotedUnrecognizedCertificate
            or OfficialClaimBasis.DemotedBorrowedOfficialId
            or OfficialClaimBasis.DemotedWithoutGameCode;

    public ModuleOrigin Origin => this switch
    {
        { IsOfficial: true } => ModuleOrigin.Official,
        { Source: ModuleSource.Workshop } => ModuleOrigin.Workshop,
        _ => ModuleOrigin.Manual
    };

    // A list item with no automation name of its own is announced by its ToString, and a record's own
    // ToString reads the type name and every member aloud, dependencies included.
    public override string ToString() => Name;

    // Written out rather than synthesized because Dependencies and DeclaredAssemblies are typed as
    // interfaces, and the compiler compares those by reference: a re-read of an untouched file builds
    // new lists, so an unchanged manifest compared unequal to itself. Every member is listed here on
    // purpose, and ModuleManifestShapeTests fails if one is added without being added here too.
    public bool Equals(ModuleManifest? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Version == other.Version
        && Type == other.Type
        && Category == other.Category
        && FolderPath == other.FolderPath
        && ManifestPath == other.ManifestPath
        && Source == other.Source
        && Url == other.Url
        && VersionText == other.VersionText
        && OfficialClaimBasis == other.OfficialClaimBasis
        && ValueList.Equal(Dependencies, other.Dependencies)
        && ValueList.Equal(DeclaredAssemblies, other.DeclaredAssemblies);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(Version);
        hash.Add(Type);
        hash.Add(Category);
        hash.Add(FolderPath);
        hash.Add(ManifestPath);
        hash.Add(Source);
        hash.Add(Url);
        hash.Add(VersionText);
        hash.Add(OfficialClaimBasis);
        hash.Add(ValueList.HashOf(Dependencies));
        hash.Add(ValueList.HashOf(DeclaredAssemblies));

        return hash.ToHashCode();
    }
}
