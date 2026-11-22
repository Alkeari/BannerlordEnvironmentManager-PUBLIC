namespace BannerlordEnvironmentManager.Core.Instances;

// One Steam app a download flow is going to ask Steam for, and whether the flow has anything left
// to do if the user closes that app's sign-in. The base game is required, because a variant
// download with no base game is not a download; a DLC is not, because a base game that landed is a
// working instance whatever its DLC did.
public sealed record SteamSignInTarget(int AppId, bool Required);

// What one authenticating run did. Failed and Declined are deliberately different: Failed is Steam
// or the tool ending the run, which the download that follows can still recover from by signing in
// on its own, and Declined is the user closing the prompt, which is a decision.
public enum SteamSignInStatus
{
    Authenticated,
    Failed,
    Declined
}

// What the whole flow's sign-in did. Declined is the app ids whose sign-in the user closed, which
// the caller drops from the download rather than attempting them and opening the same prompt again;
// Aborted means one of those was a required app, so there is no flow left to run.
public sealed record FlowSignInOutcome(
    bool Aborted,
    IReadOnlyList<int> Authenticated,
    IReadOnlyList<int> Declined,
    IReadOnlyList<int> Failed);

// Which Steam apps a flow touches, worked out before anything downloads.
//
// This exists because the app set was previously implicit in the order two blocks of a view model
// method happened to be written in, and it was wrong: a variant download signed in for the base
// game only, and the DLC's own DepotDownloader run started an hour later and authenticated by
// itself. A Steam Guard code rotates roughly every 30 seconds, so a run an hour later cannot share
// the code the user typed at the start; runs seconds apart can.
//
// No network call is needed to answer this. The variant is already in hand when the flow starts and
// GameDlc.Known names every DLC BEM can build, so the set is known before Steam is contacted at all.
// Only the per-app depot hint, which makes each authenticating run cheaper, comes from a fetch that
// can fail, and a missing hint costs manifests rather than the answer.
public static class FlowSignInPlan
{
    // Base game first, then one target per DLC in the variant's own stable app id order. Base first
    // because it is the app whose sign-in decides whether the flow runs at all, and because it is
    // the one carrying the token every later run reuses.
    public static IReadOnlyList<SteamSignInTarget> ForVariantDownload(GameDlcSet variant) =>
        [
            new SteamSignInTarget(GameDlc.BaseAppId, Required: true),
            .. variant.AppIds.Select(appId => new SteamSignInTarget(appId, Required: false))
        ];

    // Adding a DLC to an instance that already has its base game touches that one app, and it is
    // the whole job: closing its sign-in leaves nothing to download.
    public static IReadOnlyList<SteamSignInTarget> ForDlcAddition(int dlcAppId) =>
        [new SteamSignInTarget(dlcAppId, Required: true)];
}

// Runs a flow's authenticating runs back to back at the start of the flow, so every one of them
// happens inside the life of the single code the user typed.
//
// signInOne is the seam: production hands in a delegate that runs a real -manifest-only
// DepotDownloader invocation, a test hands in one that records which app ids it was asked for and
// in what order. Nothing here starts a process or knows DepotDownloader exists.
public static class FlowSignInSequence
{
    public static async Task<FlowSignInOutcome> RunAsync(
        IReadOnlyList<SteamSignInTarget> targets,
        Func<int, CancellationToken, Task<SteamSignInStatus>> signInOne,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(signInOne);

        var authenticated = new List<int>();
        var declined = new List<int>();
        var failed = new List<int>();

        foreach (var target in targets)
        {
            switch (await signInOne(target.AppId, cancellationToken))
            {
                case SteamSignInStatus.Declined when target.Required:
                    declined.Add(target.AppId);

                    // Nothing after a required app's refusal is worth running: the flow it was
                    // signing in for is over.
                    return new FlowSignInOutcome(true, authenticated, declined, failed);

                case SteamSignInStatus.Declined:
                    declined.Add(target.AppId);
                    break;

                case SteamSignInStatus.Failed:
                    // Not fatal for any target. A sign-in run that ended for a reason other than the
                    // user closing it leaves the download to sign in on its own, exactly as it did
                    // before an up-front sign-in existed.
                    failed.Add(target.AppId);
                    break;

                default:
                    authenticated.Add(target.AppId);
                    break;
            }
        }

        return new FlowSignInOutcome(false, authenticated, declined, failed);
    }
}
