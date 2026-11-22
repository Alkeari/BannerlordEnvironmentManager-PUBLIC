using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Services
{
    // What the install and repair flows say before they change files another mod manager owns: Vortex
    // files it deployed and has not seen changed, and anything inside a Mod Organizer 2 mod folder.
    // Read-only; the decision to go on stays with the player.
    internal static class DeploymentOwnershipNote
    {
        private const int Examples = 3;

        private const int MaxFilesChecked = 20000;

        // The game folder a file under Modules\ belongs to, or null when it is not under one.
        public static string? GameRootOf(string path)
        {
            try
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty);

                while (directory is not null)
                {
                    if (directory.Name.Equals("Modules", StringComparison.OrdinalIgnoreCase))
                        return directory.Parent?.FullName;

                    directory = directory.Parent;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }

            return null;
        }

        // The files among these that Vortex or Mod Organizer 2 owns, whichever game folder each is in.
        public static IReadOnlyList<(string Path, DeploymentCheck Check)> Find(IEnumerable<string> files)
        {
            var mo2Roots = ModManagerDetector.DefaultMo2Roots();
            var found = new List<(string Path, DeploymentCheck Check)>();

            foreach (var group in files.Take(MaxFilesChecked).GroupBy(file => GameRootOf(file) ?? string.Empty))
                found.AddRange(DeploymentOwnership.Warnings(group.Key, group, mo2Roots));

            return found;
        }

        // Every file under these module folders, which is what replacing the modules would touch.
        public static IEnumerable<string> FilesUnder(IEnumerable<string> moduleFolders)
        {
            foreach (var folder in moduleFolders.Where(Directory.Exists))
            {
                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                    yield return file;
            }
        }

        // Null when nothing is owned by another manager.
        public static string? Describe(IReadOnlyList<(string Path, DeploymentCheck Check)> warnings)
        {
            if (warnings.Count == 0)
                return null;

            var strings = Strings.Current;
            var vortex = warnings.Count(item => item.Check.Ownership == FileOwnership.VortexOwned);
            var mo2 = warnings.Count(item => item.Check.Ownership == FileOwnership.Mo2Mod);
            var lines = new List<string>();

            if (vortex > 0)
                lines.Add(strings.Plural("Environment.Ownership.Vortex", vortex));

            if (mo2 > 0)
                lines.Add(strings.Plural("Environment.Ownership.Mo2", mo2));

            lines.Add(strings.Format("Environment.Ownership.Examples",
                string.Join(", ", warnings.Take(Examples).Select(item => Path.GetFileName(item.Path)))));

            return string.Join(" ", lines);
        }
    }
}
