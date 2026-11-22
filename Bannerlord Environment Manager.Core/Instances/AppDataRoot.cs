namespace BannerlordEnvironmentManager.Core.Instances;

// Where BEM keeps its own files under Local AppData. One answer, because the stores used to each
// build the same path from the same folder name and a run that wanted them somewhere else had no
// single place to say so.
//
// The override exists for a driven run. The two builds share one folder on purpose, and
// Environment.GetFolderPath reads the Windows known folder rather than LOCALAPPDATA, so a run that
// presses Accept or Forget writes the owner's own ledgers unless the process is pointed elsewhere
// first.
public static class AppDataRoot
{
    private static string? overridden;

    public static string Folder => overridden ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        InstanceStateFolder.AppFolderName);

    // Once, before anything reads a store. A root set after the first read would split one run
    // across two folders. A relative path, an empty path or a missing folder is refused rather than
    // created, because a typo must not grow a new store beside the real one.
    public static void Use(string? absolutePath)
    {
        if (overridden is not null)
            throw new InvalidOperationException("The data root is already set for this run.");

        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
            throw new ArgumentException("The data root must be an absolute path.", nameof(absolutePath));

        if (!Directory.Exists(absolutePath))
            throw new DirectoryNotFoundException($"The data root does not exist: {absolutePath}");

        overridden = Path.GetFullPath(absolutePath);
    }

#if DEV_BEM
    public const string Switch = "--bem-data-root";

    // The developer build's only way in, and the reason the switch is compiled out of the published
    // exe rather than ignored by it: an argument that redirects every store is a capability, and a
    // capability that ships gets found. Absent, malformed or pointing nowhere, it leaves the real
    // folder in place and says why, so a run that meant to be isolated and was not is visible in the
    // log rather than silent.
    public static string? TryUseFromCommandLine(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var at = -1;

        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
                continue;

            if (at >= 0)
                return $"{Switch} was given twice; the real data folder is in use.";

            at = i;
        }

        if (at < 0)
            return null;

        if (at + 1 >= args.Count || args[at + 1].StartsWith('-'))
            return $"{Switch} needs the absolute path of an existing folder; the real data folder is in use.";

        try
        {
            Use(args[at + 1]);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            return $"{ex.Message} The real data folder is in use.";
        }
    }
#endif
}
