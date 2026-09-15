namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record CanonicalPathPair(string StoreFolderName, string LivePath);

// The four folders Bannerlord writes to that carry no version in their name. Documents and
// ProgramData are the ones the game itself owns; the two AppData folders are where mods put their
// logs. The engine resolves the first two before any managed code runs, which is why isolation is a
// filesystem concern rather than something a module could do from inside the process.
public sealed record CanonicalPathSet(
    string Documents,
    string ProgramData,
    string AppDataLocal,
    string AppDataRoaming)
{
    public const string GameDataFolderName = "Mount and Blade II Bannerlord";

    public static CanonicalPathSet ForMachine() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), GameDataFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), GameDataFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), GameDataFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), GameDataFolderName));

    // Tests drive the real code against a temporary root rather than a stand-in for the filesystem.
    public static CanonicalPathSet Rooted(string root) => new(
        Path.Combine(root, "Documents", GameDataFolderName),
        Path.Combine(root, "ProgramData", GameDataFolderName),
        Path.Combine(root, "AppDataLocal", GameDataFolderName),
        Path.Combine(root, "AppDataRoaming", GameDataFolderName));

    public IReadOnlyList<CanonicalPathPair> Pairs =>
    [
        new("Documents", Documents),
        new("ProgramData", ProgramData),
        new("AppDataLocal", AppDataLocal),
        new("AppDataRoaming", AppDataRoaming)
    ];
}
