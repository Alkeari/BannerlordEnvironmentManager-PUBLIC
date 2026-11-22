namespace BannerlordEnvironmentManager.Core.Io;

// The one step every route that puts a DLL (or exe) on disk ends with: Windows marks a file that came
// from an archive or the internet with a Zone.Identifier alternate data stream, and refuses to load a
// marked assembly into the game. Removing the stream is the same result as Unblock-File.
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    private static readonly string[] Extensions = [".dll", ".exe"];

    public static bool IsCode(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Removes the stream from one file of any kind. True when a stream was there and is now gone.
    public static bool Remove(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return false;

        try
        {
            var stream = path + ":" + StreamName;

            if (!File.Exists(stream))
                return false;

            File.Delete(stream);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // Removes it from the file when it is a DLL or exe, and leaves anything else as it was.
    public static bool RemoveFromCode(string path) => IsCode(path) && Remove(path);

    // Every DLL and exe under the folder, reparse points not followed. Returns how many were unblocked.
    public static int RemoveFromTree(string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return 0;

        var count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                if (RemoveFromCode(file))
                    count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return count;
    }

    public static bool HasStream(string path) =>
        OperatingSystem.IsWindows() && File.Exists(path) && File.Exists(path + ":" + StreamName);
}
