using System.Xml.Linq;

namespace BannerlordEnvironmentManager.Core.DryRun;

// The contract with the companion assembly. SubModuleClassTypeName and AssemblyFileName have to
// match what the companion project actually produces, and MarkerPrefix has to match CompanionGate:
// change either side and the companion silently does nothing.
public static class CompanionManifest
{
    // The one module BEM ever writes into the game, used by both the dry run and a watched play
    // session. The folder it is installed into is named after the id, because the game resolves a
    // module list entry by folder name, so the two can never drift apart.
    //
    // Three characters, and short on purpose. The game copies the whole command line into a 4096-byte
    // buffer and fastfails over it (see GameCommandLine), and every character of this id is a
    // character a watched launch costs that an unwatched one does not. A real 242-module
    // order has nine characters of headroom, so the difference between this and the thirteen-character
    // name it replaced is the difference between watching that order and refusing to.
    public const string ModuleId = "BEM";

    public const string ModuleName = "BEM Companion";

    // What the same module was called under earlier builds, newest first. A folder or a saved load
    // order entry left over from any of them is still BEM's own, so it is still recognized, still
    // removable and still what goes on the command line while that is what is actually on disk.
    public static readonly IReadOnlyList<string> LegacyModuleIds = ["BEM.Companion", "BEM.DryRun"];

    public static bool IsCompanionId(string? id) =>
        string.Equals(id, ModuleId, StringComparison.OrdinalIgnoreCase)
        || LegacyModuleIds.Any(legacy => string.Equals(id, legacy, StringComparison.OrdinalIgnoreCase));

    public const string AssemblyFileName = "BannerlordEnvironmentManager.Companion.dll";

    public const string SubModuleClassTypeName = "BannerlordEnvironmentManager.Companion.SubModule";

    // The dry run's marker, and the only thing BEM ever adds to a command line besides the module id
    // itself. It stays an argument because a dry run is a one-shot launch BEM starts and waits on,
    // with its own run id decided per launch and no armed state behind it.
    //
    // A watched play session carries no marker at all. It is armed by watch-session.json, which BEM
    // writes when the user turns watching on and deletes when they turn it off, and which the
    // companion reads for itself. That is what makes a watched launch cost 4 characters rather than
    // 50: see GameCommandLine for why every one of them matters.
    public const string MarkerPrefix = "/bem-dryrun:";

    public const string HarmonyModuleId = "Bannerlord.Harmony";

    public const string Version = "v1.0.0";

    public static string Build() => new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("Module",
            new XElement("Id", new XAttribute("value", ModuleId)),
            new XElement("Name", new XAttribute("value", ModuleName)),
            new XElement("Version", new XAttribute("value", Version)),
            new XElement("DefaultModule", new XAttribute("value", "false")),
            new XElement("ModuleCategory", new XAttribute("value", "Singleplayer")),
            new XElement("ModuleType", new XAttribute("value", "Community")),
            new XElement("DependedModules",
                new XElement("DependedModule", new XAttribute("Id", HarmonyModuleId))),
            new XElement("SubModules",
                new XElement("SubModule",
                    new XElement("Name", new XAttribute("value", ModuleName)),
                    new XElement("DLLName", new XAttribute("value", AssemblyFileName)),
                    new XElement("SubModuleClassType", new XAttribute("value", SubModuleClassTypeName)),
                    // Deliberately empty. Shipping a 0Harmony of its own would win the assembly load
                    // race and the dry run would measure a configuration the user never runs.
                    new XElement("Assemblies"),
                    new XElement("Tags"))))).ToString();
}
