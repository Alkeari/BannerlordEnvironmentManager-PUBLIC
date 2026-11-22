using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Modules;

// Thrown by a route that moves or copies a folder rather than reporting on one. It derives from
// IOException because every caller of those routes already treats an IOException as "the folder was
// left as it was", which is exactly what happened.
public sealed class WorkshopContentException(string message) : IOException(message);

// Two questions read off the Steam Workshop folder, kept apart because they have different answers.
//
// Where a module came from is settled by where it sits. Anything under steamapps\workshop was put there
// by Steam, so it has no Nexus identity and its page is its Workshop page, and that stays true of the
// folder after the subscription ends.
//
// Whether Steam still holds it is not settled by the path. A subscribed item is removed by unsubscribing,
// not by deleting its folder: the subscription outlives the files, Steam reports the item installed while
// the folder is gone, and the game refuses to load it on every install, since they all share that folder.
// So BEM never deletes, moves or copies-then-removes an item Steam still lists in its own manifest. Once
// Steam has let an item go, what it left behind is ordinary leftovers and is cleared like any other.
// Anything under the workshop folder that is not an item folder, and any item whose manifest cannot be
// read, is still Steam's. The decision lives here rather than at each route because a manifest is not
// always in hand: a residue entry that is only a path and a quarantine asked to set a folder aside both
// arrive with nothing but a string.
public static class WorkshopContent
{
    // The app id is deliberately not part of the origin test. No Steam Workshop folder is BEM's whichever
    // app it belongs to, and a test that matched only Bannerlord's would pass anything reached through a
    // differently named library folder.
    public static bool IsWorkshopPath(string? path) => Locate(path) is not null;

    public static bool CameFromWorkshop(ModuleManifest? module) =>
        module is not null && (module.Source == ModuleSource.Workshop || IsWorkshopPath(module.FolderPath));

    public static bool Owns(string? path) => Locate(path) is { } location && StillHeld(location);

    // A manifest the Workshop scan tagged but whose path names no steamapps folder leaves no manifest of
    // Steam's that BEM can find, so it is treated as still held.
    public static bool Owns(ModuleManifest? module) =>
        module is not null
        && (IsWorkshopPath(module.FolderPath) ? Owns(module.FolderPath) : module.Source == ModuleSource.Workshop);

    // Whether anything stated in Nexus terms is about this module at all. That is a question of origin,
    // not of the subscription: Steam published the item, so it has no Nexus mod id to record or forget,
    // "not published on Nexus" is not a statement anyone can make about it, and none of that changes when
    // the subscription ends and leaves a folder behind.
    public static bool NexusSurfaceApplies(ModuleManifest? module) => !CameFromWorkshop(module);

    // Where the install is known, the same question with the library folder asked for by name as well.
    public static bool NexusSurfaceApplies(ModuleManifest? module, string? gameInstallPath) =>
        !CameFromWorkshop(module)
        && !(!string.IsNullOrWhiteSpace(gameInstallPath)
            && ModuleScanner.GetWorkshopFolder(gameInstallPath) is { } workshop
            && QuarantineScope.IsUnder(module?.FolderPath, workshop));

    public static void Refuse(string? path)
    {
        if (Owns(path))
            throw new WorkshopContentException($"{path} belongs to the Steam Workshop and is removed by unsubscribing.");
    }

    private sealed record Location(string SteamappsFolder, string? AppId, string? ItemId);

    private static bool StillHeld(Location location)
    {
        if (location.AppId is null || location.ItemId is null)
            return true;

        var held = WorkshopManifest.HeldItemIds(WorkshopManifest.PathFor(location.SteamappsFolder, location.AppId));

        return held is null || held.Contains(location.ItemId);
    }

    private static Location? Locate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (LocateIn(path) is { } location)
            return location;

        try
        {
            return LocateIn(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    // An item folder is content\<app id>\<item id> below the workshop folder, both ids all digits because
    // Steam names them and nothing else does.
    private static Location? LocateIn(string path)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (!segments[index].Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                || !segments[index + 1].Equals("workshop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = normalized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\"
                : normalized.StartsWith('\\') ? @"\"
                : string.Empty;

            var steamapps = prefix + string.Join('\\', segments[..(index + 1)]);

            var isItem = segments.Length > index + 4
                && segments[index + 2].Equals("content", StringComparison.OrdinalIgnoreCase)
                && IsId(segments[index + 3])
                && IsId(segments[index + 4]);

            return new Location(steamapps, isItem ? segments[index + 3] : null, isItem ? segments[index + 4] : null);
        }

        return null;
    }

    private static bool IsId(string segment) => segment.Length > 0 && segment.All(char.IsAsciiDigit);
}
