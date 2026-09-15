namespace BannerlordEnvironmentManager.Core.Instances;

// SteamAccountName is the name DepotDownloader signed in with last, so a later download can reuse the
// token it remembered instead of asking again. The password is never here, and never anywhere else in
// BEM: it goes from the dialog to that process and is dropped.
//
// ShareGameSettings is off by default and stays off for a settings file written before it existed,
// so an instance keeps using its own engine_config.txt, BannerlordConfig.txt and
// BannerlordGameKeys.xml exactly as it always has until the user asks for otherwise.
public sealed record InstanceSettings(
    string GamesRoot,
    string RestingInstanceId,
    string ActiveInstanceId,
    string SteamAccountName = "",
    bool ShareGameSettings = false)
{
    public static InstanceSettings Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    // A settings file written before this field existed deserializes it as null.
    public string SteamAccountName { get; init; } = SteamAccountName ?? string.Empty;
}
