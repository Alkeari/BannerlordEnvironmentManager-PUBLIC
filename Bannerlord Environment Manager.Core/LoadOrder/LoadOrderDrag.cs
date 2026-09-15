using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// What a hand reorder would break that was not already broken. The comparison matters: an imported load
// order is applied exactly as it came and may already violate something, and refusing every later drag
// because of a violation the user did not just cause would leave them unable to fix it by hand.
public static class LoadOrderDrag
{
    public static IReadOnlyList<ConstraintViolation> Introduced(
        IReadOnlyList<ModuleEntry> before,
        IReadOnlyList<ModuleEntry> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var already = LoadOrderSorter.Verify(before, []).ToHashSet();
        var loops = LoopsIn(after);

        return
        [
            .. LoadOrderSorter.Verify(after, [])
                .Where(violation => !already.Contains(violation) && !NoOrderSatisfies(loops, violation))
        ];
    }

    public static string Describe(IReadOnlyList<ConstraintViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        if (violations.Count == 0)
            return string.Empty;

        var first = violations[0].Describe();

        return violations.Count == 1
            ? Strings.Current.Format("Core.LoadOrder.Drag.Only", first)
            : Strings.Current.Plural("Core.LoadOrder.Drag.AndOthers", violations.Count - 1, first);
    }

    // Two modules that each declare they must load before the other cannot both be satisfied by any
    // order, so every order of them breaks one and every drag of either looks like it introduced a
    // violation the order before it did not have. Refusing on that leaves the user unable to move
    // either one, which is the only thing left to do about a loop.
    //
    // Only a violation whose two modules are in the same loop is dismissed. A module that merely
    // depends on something caught in one is still held to its own declaration, because that
    // declaration is satisfiable and breaking it is a real thing to warn about.
    private static bool NoOrderSatisfies(IReadOnlyList<HashSet<ModuleId>> loops, ConstraintViolation violation) =>
        loops.Any(loop => loop.Contains(violation.Earlier) && loop.Contains(violation.Later));

    private static IReadOnlyList<HashSet<ModuleId>> LoopsIn(IReadOnlyList<ModuleEntry> entries) =>
        [.. LoadOrderSorter.Sort(new ModuleEnvironment(entries, [])).Cycles.Select(cycle => cycle.ToHashSet())];
}
