using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum CrashPhase
{
    Unknown,
    Startup,
    GameData,
    SaveOrLoad,
    OpeningMission,
    Mission,
    Conversation,
    Campaign,
    Interface
}

// What the game was doing when it crashed, read from the game's own methods on the stack. It tells the
// player when the crash happens and where to look; it names no culprit.
//
// The innermost match wins, since that is the activity closest to the fault. Types match by simple name
// inside TaleWorlds or SandBox, because the game has moved these classes between namespaces across
// versions (ScreenManager among them), so a full name would hold for only some of them.
//
// Disproof condition: a report whose stack holds none of these reads Unknown and adds no line, so a wrong
// line can only come from a rule here naming a method the game also runs during some other activity.
public static class CrashPhases
{
    private static readonly string[] GameNamespaces = ["TaleWorlds.", "SandBox."];

    // Modules override these, so the frame's type is a mod's and only the method name identifies it.
    private static readonly Dictionary<string, CrashPhase> AnyTypeMethods = new(StringComparer.Ordinal)
    {
        ["OnSubModuleLoad"] = CrashPhase.Startup,
        ["OnBeforeInitialModuleScreenSetAsRoot"] = CrashPhase.Startup,
        ["OnMissionTick"] = CrashPhase.Mission,
        ["OnMissionScreenTick"] = CrashPhase.Mission
    };

    private static readonly (string Type, string? Method, CrashPhase Phase)[] GameMethods =
    [
        ("Module", "LoadSubModules", CrashPhase.Startup),
        ("Module", "Initialize", CrashPhase.Startup),
        ("MBObjectManager", "LoadXML", CrashPhase.GameData),
        ("MBObjectManager", "LoadOneXmlFromFile", CrashPhase.GameData),
        ("MBObjectManager", "LoadXml", CrashPhase.GameData),
        ("SaveManager", null, CrashPhase.SaveOrLoad),
        ("MBSaveLoad", null, CrashPhase.SaveOrLoad),
        ("CampaignMissionManager", null, CrashPhase.OpeningMission),
        ("MissionState", "HandleOpenNew", CrashPhase.OpeningMission),
        ("MissionState", "LoadMission", CrashPhase.OpeningMission),
        ("MissionState", "FinishMissionLoading", CrashPhase.OpeningMission),
        ("Mission", "AfterStart", CrashPhase.OpeningMission),
        ("Mission", "Tick", CrashPhase.Mission),
        ("Mission", "OnTick", CrashPhase.Mission),
        ("MissionState", "OnTick", CrashPhase.Mission),
        ("MissionState", "TickMission", CrashPhase.Mission),
        ("ConversationManager", null, CrashPhase.Conversation),
        ("Campaign", "RealTick", CrashPhase.Campaign),
        ("Campaign", "Tick", CrashPhase.Campaign),
        ("MapState", "OnTick", CrashPhase.Campaign),
        ("ScreenManager", "Tick", CrashPhase.Interface),
        ("ScreenManager", "Update", CrashPhase.Interface)
    ];

    public static CrashPhase Classify(IEnumerable<CrashFrame> innermostFirst)
    {
        ArgumentNullException.ThrowIfNull(innermostFirst);

        foreach (var frame in innermostFirst)
        {
            var method = frame.IsHarmonyReplacement && frame.OriginalMethodName.Length > 0
                ? frame.OriginalMethodName
                : frame.MethodName;

            if (AnyTypeMethods.TryGetValue(method, out var overridden))
                return overridden;

            var type = frame.DeclaringTypeFullName;

            if (!GameNamespaces.Any(space => type.StartsWith(space, StringComparison.Ordinal)))
                continue;

            var simple = type[(type.LastIndexOf('.') + 1)..];

            foreach (var rule in GameMethods)
            {
                if (rule.Type == simple && (rule.Method is null || rule.Method == method))
                    return rule.Phase;
            }
        }

        return CrashPhase.Unknown;
    }

    public static string? Describe(CrashPhase phase) =>
        phase is CrashPhase.Unknown ? null : Strings.Current[$"Core.Diagnostics.CrashPhase.{phase}"];
}
