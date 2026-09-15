namespace BannerlordEnvironmentManager.Core.Toolkit;

// What BEM shells out to but does not ship. Capability is written as the thing BEM gains, not as a
// description of the program: what is being chosen is a capability, not a package.
//
// WinGetId is null for a tool BEM can find and use but will not install, and NotInstallableReason then
// says why in the user's own terms. Offering an install BEM cannot verify afterwards would be a button
// that promises something it does not deliver.
public sealed record ExternalTool(
    string Id,
    string Name,
    string Capability,
    string WithoutIt,
    string? WinGetId,
    string? NotInstallableReason,
    string HomePage)
{
    public bool CanInstall => WinGetId is not null;
}
