using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record ButrStackOutcome(string Id, string Name, bool Installed, string Message);

// The guided route, for an account Nexus will not hand a download link to. Nexus mints a single-use
// grant only when a person clicks Mod Manager Download on the website, so the stack cannot be fetched
// unattended by a free account - but it can be walked, one mod at a time, and that is what this
// sequences.
//
// One page at a time, never five at once: the whole point is that the user always knows which mod the
// browser is showing and which one BEM is waiting for. Advancing is driven by the nxm:// link arriving
// rather than by a timer, so a slow page, a login prompt or a coffee break costs nothing, and a link
// for a mod this is not waiting on is not consumed by it.
//
// This object is the only thing that answers "is a guided walk waiting for this link", and that answer
// is what lets an arriving download skip the destination confirmation. So the answer lives with the
// walk's own lifetime rather than beside it: End is terminal and idempotent, and once it has been
// called every question this can be asked answers no, whatever still holds a reference. A flag kept
// next to the walk in a view model is one a closed InfoBar, a thrown exception or a page the user
// navigated away from can leave armed; this cannot be left armed by anything, because there is nothing
// separate to leave armed.
//
// Nothing here is persisted. Abandoning the walk leaves whatever already installed installed, and the
// next press re-plans against what is on disk, so the button finishes what is missing rather than
// starting the stack over.
public sealed class ButrStackFlow(IReadOnlyList<ButrStackItem> steps)
{
    private readonly List<ButrStackOutcome> outcomes = [];

    private int index;

    private bool ended;

    public IReadOnlyList<ButrStackItem> Steps { get; } = steps ?? throw new ArgumentNullException(nameof(steps));

    public IReadOnlyList<ButrStackOutcome> Outcomes => outcomes;

    public int Total => Steps.Count;

    // One-based, for a person reading "Step 2 of 5". It stays on the last step once the walk is over
    // rather than running past the total.
    public int Position => Math.Min(index + 1, Math.Max(Total, 1));

    public bool Finished => ended || index >= Total;

    public ButrStackItem? Current => Finished ? null : Steps[index];

    // Both halves of the plan, never the mod id alone. The mod page lists every file the author
    // publishes, so a click on a localization pack or a source archive carries the same mod id as the
    // main file BEM planned; matching on the id alone installs whichever one was clicked and records
    // the stack member as done. A link with no file id on it never matches, and falls to the ordinary
    // confirmation the user would have had if no walk were running.
    public bool IsWaitingFor(int nexusModId, int? fileId) =>
        Current is { } item && item.NexusModId == nexusModId && fileId is { } id && item.File?.FileId == id;

    public string Describe() => Current is { } item
        ? Strings.Current.Format("Core.Nexus.ButrStack.Flow.Step", Position, Total, item.Name)
        : Strings.Current["Core.Nexus.ButrStack.Flow.Done"];

    // The step being recorded is named rather than assumed, and the record is refused when the walk has
    // already moved past it. A download runs for minutes with Skip live the whole time: without this,
    // an arrival that lost the race is credited to whatever step Skip advanced to, and that mod is
    // never fetched while the line on screen names the one that was.
    public bool Record(ButrStackItem step, bool installed, string message)
    {
        ArgumentNullException.ThrowIfNull(step);

        // Reference identity, because two steps for the same mod would compare equal by value and the
        // question here is which step this result was started for, not which mod it names.
        if (Current is not { } item || !ReferenceEquals(item, step))
            return false;

        outcomes.Add(new ButrStackOutcome(item.Prerequisite.Id, item.Name, installed, message));
        index++;

        return true;
    }

    public void Skip()
    {
        if (Current is { } item)
            Record(item, false, Strings.Current["Core.Nexus.ButrStack.Flow.Skipped"]);
    }

    // Ending names every step that never ran, so a stopped walk reports the same shape a finished one
    // does rather than a short list the reader has to notice is short. Terminal and idempotent: after
    // this the walk waits for nothing and records nothing, however many times it is called.
    public void End()
    {
        while (Current is { } item)
        {
            outcomes.Add(new ButrStackOutcome(item.Prerequisite.Id, item.Name, false,
                Strings.Current["Core.Nexus.ButrStack.Flow.NotReached"]));
            index++;
        }

        ended = true;
    }
}
