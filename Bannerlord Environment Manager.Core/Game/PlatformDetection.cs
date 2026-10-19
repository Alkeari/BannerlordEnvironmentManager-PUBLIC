using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Game;

public enum PlatformConfidence
{
    None,
    Confident,
    Ambiguous
}

// Detection has to fail OPEN. Guessing wrong and then deleting the binaries the game actually loads
// leaves every mod installed and inert, which is far worse than keeping a folder that wastes space.
// Only Confident ever permits a removal, and Confident means exactly one shipping client folder was
// found on disk. Nothing here is inferred from an install path or a default.
public sealed record PlatformDetection(
    string? PlatformFolder,
    IReadOnlyList<string> PresentFolders,
    PlatformConfidence Confidence,
    string Reason)
{
    public static PlatformDetection Unknown => new(
        null,
        [],
        PlatformConfidence.None,
        Strings.Current["Core.Game.PlatformDetection.Unknown"]);

    public bool IsGamePass =>
        PlatformFolder is not null
        && PlatformFolder.Equals(GameInstallLocator.GamePassBinaryFolder, StringComparison.OrdinalIgnoreCase);

    public bool IsConfident => Confidence == PlatformConfidence.Confident;

    // The only value the foreign-binary filter is allowed to act on. Null means skip nothing.
    public string? FilterPlatformFolder => IsConfident ? PlatformFolder : null;

    public string PlatformName => DescribePlatform(PlatformFolder);

    public static string DescribePlatform(string? platformFolder) => platformFolder switch
    {
        null => Strings.Current["Core.Game.PlatformName.Unknown"],
        _ when platformFolder.Equals(GameInstallLocator.GamePassBinaryFolder, StringComparison.OrdinalIgnoreCase) =>
            Strings.Current["Core.Game.PlatformName.GamePass"],
        _ when platformFolder.Equals(GameInstallLocator.StandardBinaryFolder, StringComparison.OrdinalIgnoreCase) =>
            Strings.Current["Core.Game.PlatformName.Standard"],
        _ => platformFolder
    };
}
