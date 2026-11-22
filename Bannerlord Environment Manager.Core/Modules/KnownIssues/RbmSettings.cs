using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum RbmPart
{
    Tournament,
    Ai,
    Combat,
    Campaign
}

// Which parts of (RBM) Realistic Battle Mod patch the game on this install. RBM is four mods in one
// module, and its own code (RBM.SubModule.ApplyHarmonyPatches, read in v4.5.4) applies the patches of
// RBMTournament, RBMAI, RBMCombat and RBMCampaign only while that part's Enabled setting in
// Documents\Configs\RBM\config.xml is 1, and removes them when it is 0. So a patch of a part that is
// switched off is not in the game, and a finding built on it is a finding about nothing; and switching a
// part off is a fix for a clash that needs no change to any mod's files. The owner ended his own RBM
// crashes this way: his config holds RBMCampaign off, beside the backup he took before doing it.
//
// A part with no setting in the file is on, which is the default RBM itself reads.
public sealed record RbmSettings(string Path, bool Found, bool Tournament, bool Ai, bool Combat, bool Campaign)
{
    public static readonly ModuleId Module = new("RBM");

    public static string PathIn(string configsFolder) => System.IO.Path.Combine(configsFolder, "RBM", "config.xml");

    public static RbmSettings Read(string configsFolder)
    {
        var path = PathIn(configsFolder);

        try
        {
            if (!File.Exists(path))
                return new RbmSettings(path, false, true, true, true, true);

            var root = XDocument.Load(path).Root;

            bool On(RbmPart part) => root?.Element(ElementOf(part))?.Element("Enabled")?.Value.Trim() is not "0";

            return new RbmSettings(path, root is not null, On(RbmPart.Tournament), On(RbmPart.Ai), On(RbmPart.Combat), On(RbmPart.Campaign));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Unreadable is not the same as switched off: every part is taken to be patching.
            return new RbmSettings(path, false, true, true, true, true);
        }
    }

    public bool IsOn(RbmPart part) => part switch
    {
        RbmPart.Tournament => Tournament,
        RbmPart.Ai => Ai,
        RbmPart.Combat => Combat,
        _ => Campaign
    };

    // The part a patch method belongs to, by the namespace its class sits in. Null for RBM's own always-on
    // patches and for every other mod.
    public static RbmPart? PartOf(PatchRecord patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.ModuleId != Module)
            return null;

        return PartOf(patch.PatchMethod);
    }

    public static RbmPart? PartOf(string patchMethod)
    {
        if (patchMethod.StartsWith("RBMTournament.", StringComparison.Ordinal))
            return RbmPart.Tournament;

        if (patchMethod.StartsWith("RBMAI.", StringComparison.Ordinal))
            return RbmPart.Ai;

        if (patchMethod.StartsWith("RBMCombat.", StringComparison.Ordinal))
            return RbmPart.Combat;

        if (patchMethod.StartsWith("RBMCampaign.", StringComparison.Ordinal))
            return RbmPart.Campaign;

        return null;
    }

    // False only for a patch of a part this install has switched off.
    public bool Applies(PatchRecord patch) => PartOf(patch) is not { } part || IsOn(part);

    // Switches one part off the way RBM's own settings screen does, by its Enabled value, keeping the file
    // as it was beside it first. Returns the backup's path, or null when nothing was written because the
    // file is missing or has no such setting.
    public static string? TurnOff(string configsFolder, RbmPart part, DateTimeOffset now)
    {
        var path = PathIn(configsFolder);

        if (!File.Exists(path))
            return null;

        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);

        if (document.Root?.Element(ElementOf(part))?.Element("Enabled") is not { } enabled)
            return null;

        var backup = $"{path}.before-{ElementOf(part)}-off.{now.LocalDateTime:yyyyMMdd-HHmmss}.bak";

        File.Copy(path, backup, overwrite: false);
        enabled.Value = "0";
        Io.InstanceFiles.BreakLink(path);
        document.Save(path, SaveOptions.DisableFormatting);

        return backup;
    }

    // The one part of RBM a finding's RBM patches all belong to, when RBM is in it with another mod: the
    // part whose switch ends the clash. Null when RBM is not involved, when its always-on patches are, or
    // when two parts are, since then no one switch does it.
    public static RbmPart? PartToTurnOff(PatchOrderFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var parts = finding.Flow
            .Where(step => step.ModuleId == Module)
            .Select(step => PartOf(step.PatchMethod))
            .Distinct()
            .ToList();

        return parts.Count == 1 && finding.Modules.Count > 1 ? parts[0] : null;
    }

    // A proper name, the same in every language: it is what RBM's settings screen calls the part.
    public static string NameOf(RbmPart part) => part switch
    {
        RbmPart.Tournament => "RBM Tournament",
        RbmPart.Ai => "RBM AI",
        RbmPart.Combat => "RBM Combat",
        _ => "RBM Campaign"
    };

    public static string ElementOf(RbmPart part) => part switch
    {
        RbmPart.Tournament => "RBMTournament",
        RbmPart.Ai => "RBMAI",
        RbmPart.Combat => "RBMCombat",
        _ => "RBMCampaign"
    };
}
