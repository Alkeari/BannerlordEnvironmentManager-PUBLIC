namespace BannerlordEnvironmentManager.Core.Instances;

// JunctionsReleased is false when the run is over and the canonical paths are still being put back.
// The caller has to say so plainly: until it finishes, the game would read another version's saves.
//
// Releasing is that teardown, handed to the caller rather than left running unwatched. The activation
// lock is held for the whole of it, so a control re-enabled on this result alone is a control whose
// next press is refused; awaiting this is how the caller keeps the two together.
public sealed record ActivationResult(
    bool Activated,
    bool JunctionsUsed,
    string Message,
    bool JunctionsReleased = true,
    Task? Releasing = null);
