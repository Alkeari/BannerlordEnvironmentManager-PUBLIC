using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record ClanWarsArmyTargetRiskFinding(ModuleId ClanWarsId, string ClanWarsName);

// Clan Wars can leave an AI army besieging nothing, and the game's hourly army tick then crashes in
// Army.IsAnotherEnemyBesiegingTarget, which casts the army's empty target and reads it. ClanWarsBehavior.Detach
// rebuilds a rebelling clan's army with new Army(null, leader, oldType) and never calls Gather, so the army has no
// target. ChangeKingdomAction resets each party to Hold, except one in a battle, so a leader fighting at that moment
// keeps its besiege order. Read from the code of Clan Wars v0.9.2 and the game; the crash report it answers has no
// line number and its mini dump holds no heap objects, so this is the one route found in that mod list, not a replayed crash.
//
// Versions after the one read are not flagged, since a later one may have fixed it.
//
// Disproof condition: decompile ClanWars.ClanWarsBehavior.Detach. If it gives the rebuilt army a target or clears the
// leader's order, retire this check.
public static class ClanWarsArmyTargetRisk
{
    private const string ClanWars = "ClanWars";

    private static readonly ModuleVersion LastVersionRead = ModuleVersion.Parse("v0.9.2");

    public static ClanWarsArmyTargetRiskFinding? Inspect(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var clanWars = entries.FirstOrDefault(entry => entry.IsEnabled && entry.Id == new ModuleId(ClanWars));

        return clanWars?.Manifest is { } manifest && manifest.Version <= LastVersionRead
            ? new ClanWarsArmyTargetRiskFinding(clanWars.Id, clanWars.DisplayName)
            : null;
    }
}
