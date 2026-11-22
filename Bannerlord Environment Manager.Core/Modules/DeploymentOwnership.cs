using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum FileOwnership
{
    // Nothing else manages the file: BEM may change it.
    Unmanaged,

    // Vortex deployed it and it still has the modification time Vortex recorded. Changing it makes
    // Vortex ask the owner about the file on its next deploy, and a purge removes it.
    VortexOwned,

    // Vortex deployed it and something has changed it since; Vortex already treats it as edited, so
    // another edit adds no new conflict but the file is still Vortex's to restore.
    VortexEdited,

    // Inside a Mod Organizer 2 mod folder, which MO2 owns and shows to the game only through its virtual
    // file system. A change there is a change to an MO2 mod.
    Mo2Mod
}

public sealed record DeploymentCheck(FileOwnership Ownership, string? Source = null)
{
    // Whether BEM should stop and say so before it writes the file. A Vortex file already edited is
    // not blocked: the conflict exists already and the edit is the owner's own.
    public bool NeedsWarning => Ownership is FileOwnership.VortexOwned or FileOwnership.Mo2Mod;
}

// What Vortex and Mod Organizer 2 own, read from their own records and never written. A file listed in
// a Vortex manifest is Vortex's while its modification time still matches the one Vortex wrote; MO2's
// mods are owned by folder. Every read is a hint that may be missing or malformed, and none throws.
public static class DeploymentOwnership
{
    // Vortex counts a file as untouched within this many milliseconds of the time it recorded.
    public const double VortexToleranceMs = 1000;

    private sealed record Deployed(string Source, double TimeMs);

    public static DeploymentCheck Check(string gameRoot, string filePath, IEnumerable<string>? mo2Roots = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new DeploymentCheck(FileOwnership.Unmanaged);

        if (IsInsideMo2Mods(filePath, mo2Roots ?? []))
            return new DeploymentCheck(FileOwnership.Mo2Mod);

        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return new DeploymentCheck(FileOwnership.Unmanaged);

        var relative = RelativeTo(gameRoot, filePath);

        if (relative is null || !ReadVortexManifests(gameRoot).TryGetValue(relative, out var deployed))
            return new DeploymentCheck(FileOwnership.Unmanaged);

        try
        {
            var modified = (File.GetLastWriteTimeUtc(filePath) - DateTime.UnixEpoch).TotalMilliseconds;

            return new DeploymentCheck(
                Math.Abs(modified - deployed.TimeMs) <= VortexToleranceMs ? FileOwnership.VortexOwned : FileOwnership.VortexEdited,
                deployed.Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeploymentCheck(FileOwnership.VortexOwned, deployed.Source);
        }
    }

    // The files among these that BEM should warn about, with who owns them.
    public static IReadOnlyList<(string Path, DeploymentCheck Check)> Warnings(
        string gameRoot, IEnumerable<string> filePaths, IEnumerable<string>? mo2Roots = null)
    {
        var roots = mo2Roots?.ToList();

        return [.. filePaths
            .Select(path => (Path: path, Check: Check(gameRoot, path, roots)))
            .Where(item => item.Check.NeedsWarning)];
    }

    private static bool IsInsideMo2Mods(string filePath, IEnumerable<string> mo2Roots)
    {
        var full = Normalize(filePath);

        foreach (var root in mo2Roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            // An instance's mods sit in <instance>\mods\<mod>\, one or two levels under the configured root.
            var prefix = Normalize(root);

            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = full[prefix.Length..].Split(['\\'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 3 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
                || parts.Length >= 4 && parts[1].Equals("mods", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\') + "\\";

    private static string? RelativeTo(string gameRoot, string filePath)
    {
        try
        {
            var root = Normalize(gameRoot);
            var full = Path.GetFullPath(filePath).Replace('/', '\\');

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // files[] entries with a relPath, a source and a time, as Vortex writes them. The first manifest to
    // list a path wins, which only matters for a path two mod types both claim.
    private static Dictionary<string, Deployed> ReadVortexManifests(string gameRoot)
    {
        var found = new Dictionary<string, Deployed>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var manifest in Directory.EnumerateFiles(gameRoot, "vortex.deployment*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(
                        File.ReadAllText(manifest),
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("files", out var files)
                        || files.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var entry in files.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object
                            || !entry.TryGetProperty("relPath", out var path) || path.ValueKind != JsonValueKind.String
                            || path.GetString() is not { Length: > 0 } relative)
                            continue;

                        var source = entry.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
                        var time = entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var ms) ? ms : double.NaN;

                        if (!double.IsNaN(time))
                            found.TryAdd(relative.Replace('/', '\\').TrimStart('\\'), new Deployed(source, time));
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }
}
