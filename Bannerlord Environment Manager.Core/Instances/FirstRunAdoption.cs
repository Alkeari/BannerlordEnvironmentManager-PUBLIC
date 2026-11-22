namespace BannerlordEnvironmentManager.Core.Instances;

// A first-time player should never have to find the Versions page before anything works. This is the
// whole decision of whether BEM records the install it detected by itself: only when BEM manages no
// version yet, an install was detected, and no recorded version already owns that folder.
public static class FirstRunAdoption
{
    // profileIsFresh: the data root's instance settings are absent or empty. A data root already set up
    // (an agent's --bem-data-root, a profile another BEM is rewriting) never records the machine's install.
    public static bool ShouldAdopt(bool hasRestingInstance, int recordedInstanceCount, bool installDetected, bool detectedInstallAlreadyOwned, bool profileIsFresh = true)
        => profileIsFresh && !hasRestingInstance && recordedInstanceCount == 0 && installDetected && !detectedInstallAlreadyOwned;
}
