namespace BannerlordEnvironmentManager.Core.Instances;

// What changing which version rests actually did, so the page that asked for it has something true to
// show. Changed is false when the version already rested. UserDataNeedsMoving is the one condition that
// leaves the machine in a state the design calls illegal: a resting version whose own store still holds
// files, which means its data is not at the canonical paths where a launch of it will look.
public sealed record RestingChangeResult(bool Changed, bool UserDataNeedsMoving, string Message);
