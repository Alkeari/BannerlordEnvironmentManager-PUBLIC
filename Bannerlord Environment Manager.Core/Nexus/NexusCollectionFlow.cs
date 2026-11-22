namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record NexusCollectionOutcome(NexusCollectionItem Item, bool Installed, string Message);

// The guided route through a collection, for an account Nexus will not hand a download link to: one
// file page at a time, advanced by the nxm:// link the click produces, exactly as ButrStackFlow walks
// the stack. It is its own walk rather than that one's, because a collection's steps are pinned files
// on arbitrary pages and the stack's are the five members Prerequisites names.
//
// Ended is terminal and idempotent for the same reason as there: whether a walk is waiting for a link
// is what lets that download skip the destination confirmation, so once ended it answers no forever,
// whatever still holds a reference.
public sealed class NexusCollectionFlow(IReadOnlyList<NexusCollectionItem> steps)
{
    private readonly List<NexusCollectionOutcome> outcomes = [];

    private int index;

    private bool ended;

    public IReadOnlyList<NexusCollectionItem> Steps { get; } = steps ?? throw new ArgumentNullException(nameof(steps));

    public IReadOnlyList<NexusCollectionOutcome> Outcomes => outcomes;

    public int Total => Steps.Count;

    public int Position => Math.Min(index + 1, Math.Max(Total, 1));

    public bool Finished => ended || index >= Total;

    public NexusCollectionItem? Current => Finished ? null : Steps[index];

    // The pinned file, never the mod page alone: a click on any other file of the same page carries the
    // same mod id, and installing that one would credit the collection with a file it does not pin.
    public bool IsWaitingFor(int nexusModId, int? fileId) =>
        Current is { } item && item.Mod.ModId == nexusModId && fileId == item.Mod.FileId;

    public bool Record(NexusCollectionItem step, bool installed, string message)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (Current is not { } item || !ReferenceEquals(item, step))
            return false;

        outcomes.Add(new NexusCollectionOutcome(item, installed, message));
        index++;

        return true;
    }

    public void Skip(string message)
    {
        if (Current is { } item)
            Record(item, false, message);
    }

    public void End(string notReachedMessage)
    {
        while (Current is { } item)
        {
            outcomes.Add(new NexusCollectionOutcome(item, false, notReachedMessage));
            index++;
        }

        ended = true;
    }
}
