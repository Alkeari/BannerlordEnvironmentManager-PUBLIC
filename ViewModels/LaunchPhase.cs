namespace BannerlordEnvironmentManager.ViewModels
{
    // What the Launch button is doing, so a command that is genuinely held reads as held. A launch on
    // a version that is not the resting one holds the canonical folders from the press until they are
    // back, and for that whole time the button is disabled; without a phase on it, that is
    // indistinguishable from a button that has broken.
    public enum LaunchPhase
    {
        Idle,
        Starting,
        Playing,
        Releasing
    }
}
