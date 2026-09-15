using System.Xml;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

// One target and the file replacements resolved against that target's own Modules folder, as one
// thing rather than as two lists a caller lines up by position.
//
// FileReplacementResolution holds absolute TargetPath values, so a set resolved against one install
// and reused against the next writes every later version's loose files into the first version's
// module folders. Every path in such a set is a real file, the installer's own backup is taken over
// the wrong original, and nothing downstream can tell: the destruction is silent and the call graph
// looks correct. Pairing is what removes the mistake rather than warning about it, because a
// target's replacements are reachable only through the target they were resolved for.
public sealed record InstallTargetPlan(InstallTarget Target, IReadOnlyList<FileReplacementResolution> FileReplacements)
{
    public bool HasFileReplacements => FileReplacements.Any(resolution => resolution.Candidates.Count > 0);
}

// One archive against every install it is going to. The archive is read once and each install is
// indexed on its own, because two versions rarely hold the same modules: a file that has one home on
// a bare install has three on one carrying an overhaul, and which module an archive means is decided
// per install or not at all.
public sealed record ArchiveInstallPlan(IReadOnlyList<InstallTargetPlan> Targets)
{
    public static ArchiveInstallPlan For(IEnumerable<string> entryPaths, IEnumerable<InstallTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(entryPaths);
        ArgumentNullException.ThrowIfNull(targets);

        var paths = entryPaths as IReadOnlyList<string> ?? [.. entryPaths];

        // An archive that ships a module or a bin payload is unpacked, never dropped over installed
        // files, and asking for replacements would answer with whatever else happens to carry a file
        // of the same name.
        var looseFiles = FileReplacementArchive.IsLooseFileArchive(paths);

        return new ArchiveInstallPlan(
        [
            .. targets.Select(target => new InstallTargetPlan(
                target,
                looseFiles
                    ? FileReplacementArchive.Resolve(paths, target.ModulesFolder, OfficialModuleFoldersIn(target.ModulesFolder))
                    : []))
        ]);
    }

    // One instance that cannot be written does not stop the rest. A user installing a mod into four
    // versions keeps the three that took it and is told which one did not and why, rather than a run
    // that stops at the first and leaves them working out how far it got.
    //
    // The step reports how many files it wrote, and a step that throws reports nothing: a count from
    // a write that did not finish is not one that can be stood behind, so the error is what the
    // failed result carries.
    public MultiInstanceInstallReport Install(Func<InstallTargetPlan, int> installInto)
    {
        ArgumentNullException.ThrowIfNull(installInto);

        var results = new List<InstallTargetResult>(Targets.Count);

        foreach (var plan in Targets)
        {
            try
            {
                var written = installInto(plan);

                results.Add(new InstallTargetResult(
                    plan.Target,
                    written > 0 ? InstallTargetOutcome.Installed : InstallTargetOutcome.NothingToDo,
                    written));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or XmlException)
            {
                results.Add(new InstallTargetResult(plan.Target, InstallTargetOutcome.Failed, Error: ex.Message));
            }
        }

        return new MultiInstanceInstallReport(results);
    }

    private static IReadOnlyCollection<string> OfficialModuleFoldersIn(string modulesFolder)
    {
        if (!Directory.Exists(modulesFolder))
            return [];

        return ModuleScanner.Scan(modulesFolder).Modules
            .Where(manifest => manifest.IsOfficial)
            .Select(manifest => Path.GetFileName(manifest.FolderPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
