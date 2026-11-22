namespace BannerlordEnvironmentManager.Core.Io;

// A wait on filesystem work that has to end whether or not the work does. Every move here is
// synchronous IO across volumes, so "it will finish eventually" is true and useless: the caller is a
// button, and a button held behind a copy that is taking minutes is read as broken.
//
// The work is never canceled. Abandoning a half-finished folder move is the one thing that could strand
// a campaign, so it keeps running and its owner keeps whatever it holds; only the waiting stops.
public static class BoundedWait
{
    public static async Task<bool> FinishedWithinAsync(Task work, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (work.IsCompleted)
            return true;

        var finished = await Task.WhenAny(work, Task.Delay(budget, CancellationToken.None)).ConfigureAwait(false);

        return ReferenceEquals(finished, work);
    }
}
