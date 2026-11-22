namespace BannerlordEnvironmentManager.Core.Instances;

// Makes a new managed instance from an existing one's game files, carrying only the base game, the
// DLC it was built with and the BUTR stack: every official module and the four stack
// modules are copied, and no other module is. BLSE lives in bin, which is copied whole. Unchanged files are
// hard-linked on one volume and copied otherwise (Io.InstanceFiles); every write into an instance breaks
// the link first, so nothing run in the new instance can change the source's files.
//
// It exists so automated tests run in an instance of their own, never in one the user
// plays or tests in.
public static class InstanceCloning
{
    public static readonly IReadOnlyList<string> ButrModules =
        ["Bannerlord.Harmony", "Bannerlord.ButterLib", "Bannerlord.UIExtenderEx", "Bannerlord.MBOptionScreen"];

    // A module is carried when it is official (its SubModule.xml says so) or a member of the stack.
    public static bool Carries(string moduleFolder)
    {
        var name = Path.GetFileName(moduleFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (ButrModules.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        var manifest = Path.Combine(moduleFolder, "SubModule.xml");

        try
        {
            // The game marks its own modules <ModuleType value="Official" /> or "OfficialOptional" (DLC).
            if (!File.Exists(manifest))
                return false;

            var type = System.Xml.Linq.XDocument.Load(manifest).Root?.Element("ModuleType")?.Attribute("value")?.Value?.Trim();
            return type is not null && type.StartsWith("Official", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    // Copies sourceGame into targetGame, which must not exist yet. Reports each top-level step and
    // every 500 files, and returns the number of files copied.
    public static int CopyGame(string sourceGame, string targetGame, Action<string>? progress = null, CancellationToken cancel = default, bool linkUnchanged = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceGame);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetGame);

        if (!Directory.Exists(sourceGame))
            throw new DirectoryNotFoundException($"'{sourceGame}' does not exist.");

        if (Directory.Exists(targetGame) && Directory.EnumerateFileSystemEntries(targetGame).Any())
            throw new IOException($"'{targetGame}' already holds files; a new instance starts in an empty folder.");

        var copied = 0;
        var linked = 0;
        var carried = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var modules = Path.Combine(sourceGame, "Modules");

        foreach (var file in Directory.EnumerateFiles(sourceGame, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceGame, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (parts.Length > 2
                && string.Equals(parts[0], "Modules", StringComparison.OrdinalIgnoreCase)
                && !CarriesCached(Path.Combine(modules, parts[1])))
                continue;

            var target = Path.Combine(targetGame, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Io.InstanceFiles.LinkOrCopy(file, target, linkUnchanged))
                linked++;

            if (++copied % 500 == 0)
                progress?.Invoke($"{copied} files copied, now {relative}");
        }

        progress?.Invoke($"{copied} files in all, {linked} hard-linked and {copied - linked} copied.");
        return copied;

        bool CarriesCached(string folder)
        {
            if (!carried.TryGetValue(folder, out var keep))
            {
                keep = Carries(folder);
                carried[folder] = keep;
                progress?.Invoke(keep ? $"Carrying module {Path.GetFileName(folder)}" : $"Leaving out module {Path.GetFileName(folder)}");
            }

            return keep;
        }
    }
}
