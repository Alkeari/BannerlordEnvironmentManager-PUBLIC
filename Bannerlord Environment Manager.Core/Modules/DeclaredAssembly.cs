using System.IO.Enumeration;
using BannerlordEnvironmentManager.Core.Game;

namespace BannerlordEnvironmentManager.Core.Modules;

// The <Tags> of the SubModule that declared an assembly. They decide whether this client process ever
// loads that SubModule, and without them a healthy install reads as broken: vanilla Multiplayer
// declares six dedicated-server assemblies and one GDK one, and Native declares the PlayStation and
// GDK platform ones. None of those nine ship on a PC install and none of them is a fault.
public sealed record SubModuleGate(
    string? DedicatedServerType,
    IReadOnlyList<string> RejectedPlatforms,
    IReadOnlyList<string> ExclusivePlatforms)
{
    public static SubModuleGate Unrestricted { get; } = new(null, [], []);

    // Two parses of one unchanged file build separate lists, so the synthesized comparison of these
    // two members would report every re-read as a different gate. See ValueList.
    public bool Equals(SubModuleGate? other) =>
        other is not null
        && DedicatedServerType == other.DedicatedServerType
        && ValueList.Equal(RejectedPlatforms, other.RejectedPlatforms)
        && ValueList.Equal(ExclusivePlatforms, other.ExclusivePlatforms);

    public override int GetHashCode() => HashCode.Combine(
        DedicatedServerType,
        ValueList.HashOf(RejectedPlatforms),
        ValueList.HashOf(ExclusivePlatforms));

    // Null when this client loads the SubModule, and otherwise the reason it does not, in the words of
    // the tag that said so.
    public string? SkippedIn(string binaryFolder)
    {
        if (!string.IsNullOrEmpty(DedicatedServerType)
            && !DedicatedServerType.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return $"it only loads in a {DedicatedServerType} dedicated server";
        }

        var running = PlatformsIn(binaryFolder);

        if (ExclusivePlatforms.Count > 0 && !ExclusivePlatforms.Any(p => Contains(running, p)))
            return $"it is exclusive to {string.Join(", ", ExclusivePlatforms)}";

        if (RejectedPlatforms.Count > 0 && running.All(p => Contains(RejectedPlatforms, p)))
            return $"it rejects every platform this install can be running as: {string.Join(", ", running)}";

        return null;
    }

    // The platform names a build running out of this shipping client folder can report. BEM cannot
    // tell a Steam install from an Epic or a GOG one, so a PC install is all of them at once and a
    // SubModule is ruled out only when its tags reject every one of them.
    private static IReadOnlyList<string> PlatformsIn(string binaryFolder) =>
        string.Equals(binaryFolder, GameInstallLocator.GamePassBinaryFolder, StringComparison.OrdinalIgnoreCase)
            ? ["GDKDesktop"]
            : ["WindowsSteam", "WindowsEpic", "WindowsGOG", "WindowsNoPlatform"];

    private static bool Contains(IEnumerable<string> names, string name) =>
        names.Contains(name, StringComparer.OrdinalIgnoreCase);
}

// One assembly a module's own SubModule.xml names, and the whole reason a shadowed copy is not always
// a spare copy.
//
// TaleWorlds.ModuleManager builds <module>\bin\<platform>\<name> out of <SubModule><DLLName> and
// <SubModule><Assemblies><Assembly>, opens that exact path, and stops with "Couldn't find .dll:" when
// the file is not there. No assembly binding is involved, so a copy that loses the binding race to an
// identically named file in an earlier module is still a file the game is required to open, and
// deleting it stops the game starting.
//
// Name is a file name for DLLName and Assembly, and a Win32 wildcard for the module loader's
// LoaderFilter tag, which is how BUTR's loader names a whole shelf of per-game-version builds in one
// line without any of them appearing in the manifest.
public sealed record DeclaredAssembly(string Name, SubModuleGate Gate)
{
    public bool IsPattern => Name.Contains('*') || Name.Contains('?');

    public bool Matches(string fileName) =>
        FileSystemName.MatchesSimpleExpression(Name, fileName, ignoreCase: true);
}
