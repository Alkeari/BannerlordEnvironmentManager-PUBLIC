namespace BannerlordEnvironmentManager.Core.Instances;

// Whether a run of DepotDownloader that did not work is worth running once more on its own.
//
// The one case that is: the run ended with a non-zero exit code after BEM answered a Steam Guard
// prompt in it. Steam either accepted the code, and -remember-password stored a token that makes the
// next run sign in silently, or the run ended before the code reached it. Both are fixed by running
// again, and running again is what the user would otherwise have to do by hand, which for a 58 GB
// download is the whole complaint.
//
// Everything else is left alone deliberately:
//
// - The run exited zero: it worked, whatever happened to an answer along the way. Retrying a run
//   that worked is how the user came to be asked for three Steam Guard codes in a row.
// - No Steam Guard code was answered: whatever failed had nothing to do with signing in, and a
//   second run would fail the same way.
// - UnansweredPrompt is set: the user closed a prompt rather than answering it, which is a decision
//   and not a fault to retry over.
// - A canceled run never reaches here at all: cancellation throws out of the run, so this is not
//   the guard against retrying one.
// - alreadyRetried: exactly one retry, so a failure that keeps happening stops rather than loops.
public static class DownloadRetryPolicy
{
    public static bool ShouldRetry(DownloadRunResult result, bool alreadyRetried)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !alreadyRetried
            && !result.Succeeded
            && result.GuardCodeAnswered
            && result.UnansweredPrompt is null;
    }

    // The overload every caller inside a download uses. A stop is a decision, and starting a second
    // 58 GB run on top of one the user just stopped is the opposite of what was asked for. A
    // cancellation normally throws out of the run before a result exists at all, so this guards the
    // gap between two runs rather than the runs themselves: the stop can land after the first run
    // has returned its result and before the retry would start it again.
    public static bool ShouldRetry(DownloadRunResult result, bool alreadyRetried, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && ShouldRetry(result, alreadyRetried);
}
