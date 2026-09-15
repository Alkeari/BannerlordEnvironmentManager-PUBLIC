using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

public readonly record struct RollbackOutcome(bool Removed, string? Problem);

// What an install leaves behind when it fails partway through a module.
//
// The extractor creates the destination folder before it writes the first file, so an archive that
// cannot be read at all still produced a module folder: empty, with no SubModule.xml, indistinguishable
// on disk from a module that was never installed except that it is in the way of the next attempt. A
// failed install has to leave the game folder as it found it.
//
// The one thing it must never do is delete a folder the install did not create. When an existing module
// was kept rather than replaced, the extract wrote into the user's own folder, and everything else in
// there is theirs.
public static class InstallRollback
{
    public static RollbackOutcome UndoFailedModuleInstall(string folderPath, bool wasAlreadyThere)
    {
        if (wasAlreadyThere || !Directory.Exists(folderPath))
            return new RollbackOutcome(false, null);

        // An install never targets a Workshop folder, so reaching one here means the target was
        // resolved wrongly, and undoing that by deleting Steam's folder would be the worse mistake.
        if (WorkshopContent.Owns(folderPath))
        {
            return new RollbackOutcome(false,
                $"The failed install left {folderPath} behind, which belongs to the Steam Workshop and was left alone.");
        }

        try
        {
            Directory.Delete(folderPath, recursive: true);
            return new RollbackOutcome(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RollbackOutcome(false,
                $"The failed install left {folderPath} behind and it could not be removed: {ex.Message}");
        }
    }
}
