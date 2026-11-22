namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// Every undeclared conflict between the same mods, as one finding. A group is one attribute of one kind
// of element, which is the unit the merge works in and the wrong unit to show: two armour mods that
// disagree about weight, value and tier are one decision for the player, which of the two should win,
// and eighteen rows asking it eighteen times read as eighteen problems.
public sealed record XmlOverlapPair(IReadOnlyList<XmlOverlapGroup> Groups)
{
    public XmlOverlapGrade Grade => Groups[0].Grade;

    // Every group here has the same mods in the same order, so the first speaks for all of them.
    public IReadOnlyList<XmlDefiner> Definers => Groups[0].Definers;

    public XmlDefiner Winner => Groups[0].Winner;

    public IReadOnlyList<string> Datasets =>
        [.. Groups.Select(g => g.Dataset).Distinct(StringComparer.Ordinal)];

    public IReadOnlyList<string> EntryIds =>
        [.. Groups.SelectMany(g => g.EntryIds).Distinct(StringComparer.Ordinal)];

    // A group without recorded contests still covers its entries, so the larger of the two is counted.
    public int ContestedValues => Groups.Sum(g => Math.Max(g.Contests.Count, g.EntryIds.Count));
}

public static class XmlOverlapPairs
{
    // Only undeclared conflicts fold. A base game override row and a declared layering row are already
    // behind Show Hidden, one per attribute, where the detail is what someone who opens them wants.
    // Folded by the mods alone and in their order: the base game also having set the value does not
    // make it a different argument between the same two mods. A pair sits where its first group sat.
    public static IReadOnlyList<XmlOverlapPair> Fold(IReadOnlyList<XmlOverlapGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var order = new List<List<XmlOverlapGroup>>();
        var byMods = new Dictionary<string, List<XmlOverlapGroup>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (group.Grade != XmlOverlapGrade.UndeclaredConflict)
            {
                order.Add([group]);
                continue;
            }

            var key = string.Join("|", group.Definers.Where(d => !d.IsOfficial).Select(d => d.ModuleId.Value));

            if (!byMods.TryGetValue(key, out var members))
            {
                members = [];
                byMods[key] = members;
                order.Add(members);
            }

            members.Add(group);
        }

        return [.. order.Select(members => new XmlOverlapPair(members))];
    }
}
