namespace BannerlordEnvironmentManager.Core.Install;

public sealed record BinPayloadOverwrite(string PlatformFolder, string RelativePath, string DestinationPath);

public static class BinPayloadInstaller
{
    public static string DestinationFolder(string gameInstallPath, string platformFolder) =>
        Path.Combine(gameInstallPath, "bin", platformFolder);

    public static string DestinationPathFor(string gameInstallPath, string platformFolder, string relativePath) =>
        Path.Combine(
            DestinationFolder(gameInstallPath, platformFolder),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static IReadOnlyList<BinPayloadOverwrite> FindOverwrites(string gameInstallPath, IEnumerable<BinPayload> payloads)
    {
        var overwrites = new List<BinPayloadOverwrite>();

        foreach (var payload in payloads)
        {
            var folder = DestinationFolder(gameInstallPath, payload.PlatformFolder);

            foreach (var file in payload.Files)
            {
                var destination = Path.Combine(folder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(destination))
                    overwrites.Add(new BinPayloadOverwrite(payload.PlatformFolder, file.RelativePath, destination));
            }
        }

        return overwrites;
    }

    public static IReadOnlyList<string> FindInstalledInjectors(string gameInstallPath, string platformFolder)
    {
        var folder = DestinationFolder(gameInstallPath, platformFolder);

        if (!Directory.Exists(folder))
            return [];

        return Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(ArchiveLayout.IsInjectorProxy)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
