using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Modules;

// The one place the game's own module ids are written down, the way GameDlc is the one place a DLC's
// identity is written down. Before this, "Native", "SandBoxCore" and their siblings were string
// literals scattered through the readers, the validators and the overlap tables, so nothing could
// answer "is this one of the game's own modules" without a caller retyping the list.
//
// The DLC ids are taken from GameDlc.Known rather than retyped, so the next DLC stays one row in one
// table.
public static class OfficialModules
{
    // Every module a stock Bannerlord install ships under Modules. Verified against a real
    // 1.4.7 + War Sails install and a Steam 1.4.8 install on 2026-09-07: both carry exactly these
    // eight plus NavalDLC, and nothing else on either install claims to be official.
    //
    // These are ids, not folder names, and the two are not always spelled alike: the SandBox folder
    // declares the id "Sandbox". Every comparison here is case-insensitive for that reason.
    public static readonly IReadOnlyList<string> BaseGame =
    [
        "Native",
        "SandBoxCore",
        "SandBox",
        "StoryMode",
        "CustomBattle",
        "Multiplayer",
        "BirthAndDeath",
        "FastMode"
    ];

    // The game's own modules that ship no assembly of their own, counted across real
    // v1.4.7 + War Sails, v1.5.2 + War Sails and Steam v1.4.8 installs on 2026-09-08: SandBoxCore is
    // the only one, on all three, and it carries an empty bin\Win64_Shipping_Client. Every other
    // official module ships between one and fourteen assemblies on every install that has it.
    //
    // The list exists because "this module ships no assemblies" is otherwise an answer any folder can
    // give by holding nothing but a SubModule.xml, and the official-claim ladder accepts it in place
    // of the game's own signed code. Naming the modules that earn the allowance keeps SandBoxCore
    // while refusing an XML-only folder called Native.
    public static readonly IReadOnlyList<string> WithoutAssemblies =
    [
        "SandBoxCore"
    ];

    private static readonly HashSet<string> Ids =
        new(BaseGame.Concat(GameDlc.Known.Select(dlc => dlc.ModuleFolder)), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AssemblyLess =
        new(WithoutAssemblies, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> All => Ids;

    public static bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Ids.Contains(id);

    public static bool Contains(ModuleId id) => Contains(id.Value);

    public static bool ShipsNoAssemblies(string? id) =>
        !string.IsNullOrWhiteSpace(id) && AssemblyLess.Contains(id);

    public static bool ShipsNoAssemblies(ModuleId id) => ShipsNoAssemblies(id.Value);
}
