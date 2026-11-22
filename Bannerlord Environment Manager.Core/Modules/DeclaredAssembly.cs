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
    IReadOnlyList<string> ExclusivePlatforms,
    string? NoRenderModeElement = null,
    string? RuntimeLibrary = null,
    string? PlayerHostedDedicatedServer = null)
{
    public static SubModuleGate Unrestricted { get; } = new(null, [], []);

    // Two parses of one unchanged file build separate lists, so the synthesized comparison of these
    // two members would report every re-read as a different gate. See ValueList.
    public bool Equals(SubModuleGate? other) =>
        other is not null
        && DedicatedServerType == other.DedicatedServerType
        && ValueList.Equal(RejectedPlatforms, other.RejectedPlatforms)
        && ValueList.Equal(ExclusivePlatforms, other.ExclusivePlatforms)
        && NoRenderModeElement == other.NoRenderModeElement
        && RuntimeLibrary == other.RuntimeLibrary
        && PlayerHostedDedicatedServer == other.PlayerHostedDedicatedServer;

    public override int GetHashCode() => HashCode.Combine(
        DedicatedServerType,
        ValueList.HashOf(RejectedPlatforms),
        ValueList.HashOf(ExclusivePlatforms),
        NoRenderModeElement,
        RuntimeLibrary,
        PlayerHostedDedicatedServer);

    // Null when this client loads the SubModule, and otherwise the reason it does not, in the words of
    // the tag that said so.
    public string? SkippedIn(string binaryFolder)
    {
        if (!string.IsNullOrEmpty(DedicatedServerType)
            && !DedicatedServerType.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return $"it only loads in a {DedicatedServerType} dedicated server";
        }

        // Module.GetSubModuleValiditiy: IsNoRenderModeElement and (on a client) PlayerHostedDedicatedServer load
        // only on the exact text "false"; DependantRuntimeLibrary loads only on the running Runtime, which is
        // DotNetCore for the Game Pass build and DotNet for the others, and a name Enum.TryParse rejects loads.
        if (NoRenderModeElement is not null && NoRenderModeElement != "false")
            return "it only loads in no-render mode";

        if (PlayerHostedDedicatedServer is not null && PlayerHostedDedicatedServer.ToLowerInvariant() != "false")
            return "it only loads in a player-hosted dedicated server";

        if (RuntimeLibrary == "Unloadable")
            return "its runtime tags name no runtime this game runs on";

        if (RuntimeLibrary is "Mono" or "DotNet" or "DotNetCore")
        {
            var runtime = string.Equals(binaryFolder, GameInstallLocator.GamePassBinaryFolder, StringComparison.OrdinalIgnoreCase)
                ? "DotNetCore"
                : "DotNet";

            if (RuntimeLibrary != runtime)
                return $"it only loads on the {RuntimeLibrary} runtime";
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
//
// SubModuleClass is set on the DLLName entry alone: the class the loader looks up in that file and
// constructs (Module.AddSubModule), which is what ties a startup failure to one file.
//
// IsSubModuleDll is set on the DLLName entry alone, whether or not the SubModule names a class. It is the
// file Module.LoadSubModules loads as the module's code: one that exists and will not load there is a
// CriticalError that stops the game, where a plain Assemblies entry that will not load is a message box.
public sealed record DeclaredAssembly(string Name, SubModuleGate Gate, string? SubModuleClass = null, bool IsSubModuleDll = false)
{
    public bool IsPattern => Name.Contains('*') || Name.Contains('?');

    public bool Matches(string fileName) =>
        FileSystemName.MatchesSimpleExpression(Name, fileName, ignoreCase: true);
}
