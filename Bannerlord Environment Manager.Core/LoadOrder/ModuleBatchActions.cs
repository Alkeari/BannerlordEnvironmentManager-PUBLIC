using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The actions the Play page's context menu can run against more than one selected module at once.
// The list is closed and matches the menu exactly: an action absent from here is single-module
// business and the menu hides it while a multi-selection stands.
public enum ModuleBatchAction
{
    Disable,
    EnableWithDependencies,
    DisableWithDependents,
    MoveToTop,
    MoveToBottom,
    Pin,
    OpenModPage,
    Update,
    MarkNotOnNexus,
    MarkBuiltOnThisMachine,
    ForgetModId,
    Prune,
    Uninstall
}

// Why one selected module was left out. Every reason is a whole sentence in the catalog rather than
// a word dropped into a template, because the count and the noun have to agree in eleven languages.
public enum ModuleBatchSkipReason
{
    Official,
    NotInstalled,
    AlreadySettled,
    NoModPage,
    NoUpdate,
    NoModId,
    StillListed,
    AboveTheCap,
    Subscribed,
    SteamPublished
}

// Everything about one selected row that decides whether an action may touch it. The view model
// reads these off its own row, so the decision itself stays here and stays testable.
public sealed record ModuleBatchCandidate(
    ModuleId Id,
    string DisplayName,
    bool IsOfficial,
    bool IsOrphan,
    bool IsEnabled,
    bool HasFolder,
    bool HasModPage,
    bool HasUpdate,
    bool HasModId,
    bool IsMarkedNotOnNexus,
    bool IsMarkedBuiltLocally,
    bool IsPinned,
    bool IsWorkshopSubscription);

public sealed record ModuleBatchSkip(ModuleBatchCandidate Module, ModuleBatchSkipReason Reason);

public sealed record ModuleBatchPlan(
    ModuleBatchAction Action,
    IReadOnlyList<ModuleBatchCandidate> Eligible,
    IReadOnlyList<ModuleBatchSkip> Skipped)
{
    private const int NamesShown = 12;

    // An action is offered when it is legal for at least one of the selection, not for all of it.
    // A mixed selection of official modules and mods still gets the action, acts on the mods, and
    // says which modules it left alone and why.
    public bool CanRun => Eligible.Count > 0;

    public string Describe(string outcome) =>
        Skipped.Count == 0 ? outcome : $"{outcome} {DescribeSkips()}";

    // One sentence per reason, naming the modules it covers. A count on its own answers "how many"
    // and never "which of mine", which is the only part the user can act on.
    public string DescribeSkips()
    {
        if (Skipped.Count == 0)
            return string.Empty;

        ModuleBatchSkipReason[] order =
        [
            ModuleBatchSkipReason.Official,
            ModuleBatchSkipReason.Subscribed,
            ModuleBatchSkipReason.SteamPublished,
            ModuleBatchSkipReason.NotInstalled,
            ModuleBatchSkipReason.AlreadySettled,
            ModuleBatchSkipReason.NoModPage,
            ModuleBatchSkipReason.NoUpdate,
            ModuleBatchSkipReason.NoModId,
            ModuleBatchSkipReason.StillListed,
            ModuleBatchSkipReason.AboveTheCap,
        ];

        var sentences = order
            .Select(reason => Skipped.Where(skip => skip.Reason == reason).ToList())
            .Where(group => group.Count > 0)
            .Select(group => Strings.Current.Plural(KeyFor(group[0].Reason), group.Count, Names(group)));

        return string.Join(" ", sentences);
    }

    private static string KeyFor(ModuleBatchSkipReason reason) => reason switch
    {
        ModuleBatchSkipReason.Official => "Core.LoadOrder.Batch.Skipped.Official",
        ModuleBatchSkipReason.NotInstalled => "Core.LoadOrder.Batch.Skipped.NotInstalled",
        ModuleBatchSkipReason.AlreadySettled => "Core.LoadOrder.Batch.Skipped.AlreadySettled",
        ModuleBatchSkipReason.NoModPage => "Core.LoadOrder.Batch.Skipped.NoModPage",
        ModuleBatchSkipReason.NoUpdate => "Core.LoadOrder.Batch.Skipped.NoUpdate",
        ModuleBatchSkipReason.NoModId => "Core.LoadOrder.Batch.Skipped.NoModId",
        ModuleBatchSkipReason.StillListed => "Core.LoadOrder.Batch.Skipped.StillListed",
        ModuleBatchSkipReason.Subscribed => "Core.LoadOrder.Batch.Skipped.Subscribed",
        ModuleBatchSkipReason.SteamPublished => "Core.LoadOrder.Batch.Skipped.SteamPublished",
        _ => "Core.LoadOrder.Batch.Skipped.AboveTheCap"
    };

    private static string Names(IReadOnlyList<ModuleBatchSkip> skips) =>
        skips.Count <= NamesShown
            ? string.Join(", ", skips.Select(skip => skip.Module.DisplayName))
            : Strings.Current.Format(
                "Core.LoadOrder.Batch.AndMore",
                string.Join(", ", skips.Take(NamesShown).Select(skip => skip.Module.DisplayName)),
                skips.Count - NamesShown);
}

public static class ModuleBatchActions
{
    // Opening a browser tab per selected module turns a fifty-module selection into fifty tabs and
    // a wedged machine. Everything past the tenth is reported as a skip with the cap named, rather
    // than silently dropped or silently opened.
    public const int ModPageCap = 10;

    public static ModuleBatchPlan Plan(ModuleBatchAction action, IEnumerable<ModuleBatchCandidate> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var eligible = new List<ModuleBatchCandidate>();
        var skipped = new List<ModuleBatchSkip>();

        foreach (var module in selection)
        {
            if (Refusal(action, module) is { } reason)
                skipped.Add(new ModuleBatchSkip(module, reason));
            else if (action == ModuleBatchAction.OpenModPage && eligible.Count >= ModPageCap)
                skipped.Add(new ModuleBatchSkip(module, ModuleBatchSkipReason.AboveTheCap));
            else
                eligible.Add(module);
        }

        return new ModuleBatchPlan(action, eligible, skipped);
    }

    // Order matters: an official module that is also already disabled is reported as official,
    // because that is the fact the user cannot change and the other one they could.
    //
    // The three marking actions only ever mark. A batch toggle over a mixed set has no honest
    // meaning - half the selection would come back the other way - so unmarking stays the
    // single-row menu's job, and anything already marked is a named skip.
    private static ModuleBatchSkipReason? Refusal(ModuleBatchAction action, ModuleBatchCandidate module) => action switch
    {
        ModuleBatchAction.Disable =>
            module.IsOfficial ? ModuleBatchSkipReason.Official
            : module.IsOrphan ? ModuleBatchSkipReason.NotInstalled
            : !module.IsEnabled ? ModuleBatchSkipReason.AlreadySettled
            : null,

        // Every selected module is a legal root for a closure and for a move: what a closure refuses
        // is decided by the closure itself and previewed before anything is written, and a row that
        // is already at the top still has a position in the batch's own relative order.
        ModuleBatchAction.EnableWithDependencies
            or ModuleBatchAction.DisableWithDependents
            or ModuleBatchAction.MoveToTop
            or ModuleBatchAction.MoveToBottom => null,

        ModuleBatchAction.Pin => module.IsPinned ? ModuleBatchSkipReason.AlreadySettled : null,
        ModuleBatchAction.OpenModPage => module.HasModPage ? null : ModuleBatchSkipReason.NoModPage,
        ModuleBatchAction.Update => module.HasUpdate ? null : ModuleBatchSkipReason.NoUpdate,
        // Both statements are about a mod's place on Nexus, and a subscribed item has none: Steam
        // published it, and it was never built here. Reported before the already-marked case, because
        // the subscription is the fact the owner cannot change.
        ModuleBatchAction.MarkNotOnNexus =>
            module.IsWorkshopSubscription ? ModuleBatchSkipReason.SteamPublished
            : module.IsMarkedNotOnNexus ? ModuleBatchSkipReason.AlreadySettled
            : null,

        ModuleBatchAction.MarkBuiltOnThisMachine =>
            module.IsWorkshopSubscription ? ModuleBatchSkipReason.SteamPublished
            : module.IsMarkedBuiltLocally ? ModuleBatchSkipReason.AlreadySettled
            : null,
        ModuleBatchAction.ForgetModId => module.HasModId ? null : ModuleBatchSkipReason.NoModId,
        ModuleBatchAction.Prune => module.IsOrphan ? null : ModuleBatchSkipReason.StillListed,

        // A Workshop module is reported before the missing-folder case: its folder is there, and what
        // the owner can act on is the subscription rather than anything on disk.
        ModuleBatchAction.Uninstall =>
            module.IsOfficial ? ModuleBatchSkipReason.Official
            : module.IsWorkshopSubscription ? ModuleBatchSkipReason.Subscribed
            : !module.HasFolder ? ModuleBatchSkipReason.NotInstalled
            : null,

        _ => null
    };
}

// Moving a selection is not the same as moving each of its members in turn: nine rows sent to the
// top one at a time arrive in reverse, and a hand-tuned order inside the selection is destroyed by
// a move that was meant to keep it. Both directions carry the selection as a block and leave the
// relative order inside it, and among everything else, exactly as it was.
public static class ModuleBatchOrder
{
    public static IReadOnlyList<ModuleId> ToTop(IReadOnlyList<ModuleId> order, IReadOnlyCollection<ModuleId> moving)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(moving);

        var set = moving.ToHashSet();

        return [.. order.Where(set.Contains), .. order.Where(id => !set.Contains(id))];
    }

    public static IReadOnlyList<ModuleId> ToBottom(IReadOnlyList<ModuleId> order, IReadOnlyCollection<ModuleId> moving)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(moving);

        var set = moving.ToHashSet();

        return [.. order.Where(id => !set.Contains(id)), .. order.Where(set.Contains)];
    }
}
