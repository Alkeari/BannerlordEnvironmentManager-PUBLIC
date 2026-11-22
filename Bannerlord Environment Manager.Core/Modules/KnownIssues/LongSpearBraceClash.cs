using BannerlordEnvironmentManager.Core.LoadOrder;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record LongSpearBraceClashFinding(
    ModuleId HoldTheLineId,
    string HoldTheLineName,
    ModuleId RbmId,
    string RbmName,
    string SettingsFile);

// Two mods that are each fine alone and crash the game while a battle loads. Hold the Line's "Long spears
// can brace" adds a usage to the spear's battle copy (MissionWeapon._weapons) and not to the item, and RBM's
// Agent.WeaponEquipped prefix counts usages on the copy and looks each one up on the item, so the last lookup
// is out of range and nothing catches it. Read from a real crash dump and both mods' code, Hold the Line
// b0.2.3 and RBM 4.5.4.
//
// Hold the Line defaults the setting on, so a missing file means on.
//
// Disproof condition: decompile RBMCombat.RangedRework.OverrideWeaponEquipped. If it bounds its loop by the
// item's usages, or HoldTheLine.BraceUsage.AddToLongSpears no longer adds to the copy, retire this check.
public static class LongSpearBraceClash
{
    private const string HoldTheLine = "HoldTheLine";

    private const string Rbm = "RBM";

    private const string Setting = "LongSpearBrace";

    public static string SettingsFile(string modSettingsRoot) =>
        Path.Combine(modSettingsRoot, "Global", HoldTheLine, HoldTheLine + ".json");

    // rbmCombatIsOn is false when this install has RBM Combat switched off (RbmSettings): the code that
    // crashes is RBMCombat's, RBM does not apply it then, and so there is nothing to raise.
    public static LongSpearBraceClashFinding? Inspect(
        IReadOnlyList<ModuleEntry> entries,
        string modSettingsRoot,
        bool rbmCombatIsOn = true)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (!rbmCombatIsOn)
            return null;

        var holdTheLine = entries.FirstOrDefault(entry => entry.IsEnabled && entry.Id == new ModuleId(HoldTheLine));
        var rbm = entries.FirstOrDefault(entry => entry.IsEnabled && entry.Id == new ModuleId(Rbm));

        if (holdTheLine is null || rbm is null)
            return null;

        var file = SettingsFile(modSettingsRoot);

        return IsTurnedOff(file)
            ? null
            : new LongSpearBraceClashFinding(holdTheLine.Id, holdTheLine.DisplayName, rbm.Id, rbm.DisplayName, file);
    }

    // Every other setting in the file is kept. A file that will not parse throws rather than being overwritten.
    public static void TurnOff(string settingsFile)
    {
        var settings = File.Exists(settingsFile)
            ? JsonNode.Parse(File.ReadAllText(settingsFile))?.AsObject() ?? []
            : [];

        settings[Setting] = false;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        Io.InstanceFiles.WriteAllText(settingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // A file that cannot be read says nothing about the setting, and the game would use the default, on.
    private static bool IsTurnedOff(string file)
    {
        try
        {
            return File.Exists(file)
                && JsonNode.Parse(File.ReadAllText(file)) is JsonObject settings
                && settings[Setting] is JsonValue value
                && value.TryGetValue<bool>(out var on)
                && !on;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
