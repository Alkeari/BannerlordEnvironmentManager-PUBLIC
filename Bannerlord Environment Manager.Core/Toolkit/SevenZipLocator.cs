namespace BannerlordEnvironmentManager.Core.Toolkit;

// The one probe order for 7-Zip. The installer, the safety scanner and the Toolkit all have to agree
// on whether it is there, and two copies of this list disagreeing is how a tab says "installed" over
// an install that then fails.
public static class SevenZipLocator
{
    public const string EnvironmentVariable = "7ZIP_EXE";

    public static string? Locate() => Locate(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string? Locate(string? programFiles, string? programFilesX86, string? environmentOverride)
    {
        string?[] candidates =
        [
            Combine(programFiles),
            Combine(programFilesX86),
            environmentOverride
        ];

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable candidate is not a find, and the next one still deserves a look.
            }
        }

        return null;
    }

    private static string? Combine(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return Path.Combine(root, "7-Zip", "7z.exe");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
