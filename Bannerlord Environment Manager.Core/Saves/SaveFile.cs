using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

// One module as the save recorded it. The version is kept as the raw text the save wrote, because the
// save carries four components and a manifest declares three: parsing it away here would lose the
// difference before anything got the chance to report it.
public sealed record SaveModuleRecord(ModuleId Id, string VersionText)
{
    public ModuleVersion Version => ModuleVersion.Parse(VersionText);
}

public sealed record SaveFile(
    string Path,
    string ApplicationVersion,
    string CharacterName,
    int MainHeroLevel,
    double DayLong,
    DateTime? Created,
    IReadOnlyList<SaveModuleRecord> Modules,
    string Error = "")
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool Failed => Error.Length > 0;

    // The game counts days as a fraction, and a campaign is talked about in whole days. Rounding is for
    // display only; DayLong keeps what the save said.
    public int Days => (int)Math.Round(DayLong, MidpointRounding.AwayFromZero);

    // A save BEM could not read is still a save the user has, so it gets a record with the reason
    // rather than being dropped out of the list.
    public static SaveFile Unreadable(string path, string error) =>
        new(path, string.Empty, string.Empty, 0, 0, null, [], error);
}
