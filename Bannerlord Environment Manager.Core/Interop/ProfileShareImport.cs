using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Interop;

// What importing a shared profile would produce, shown before anything is written. The vocabulary is
// LoadOrderImport's: a module the sender has and the recipient does not is a MissingModule, one they
// both have is an ImportedModule, and one they both have at different versions is a VersionDelta.
// What differs is the verb, because this import saves a profile rather than rewriting the load order,
// so a module that is not installed here is kept in the profile instead of being left out of anything.
public sealed record ProfileImportPreview(
    SharedProfile Profile,
    string TargetName,
    string InstalledGameVersion,
    IReadOnlyList<ImportedModule> Present,
    IReadOnlyList<MissingModule> Missing,
    // Both are subsets of Present, because a module has to be installed here before its version can be
    // compared at all. Deltas is the finding the whole exchange turns on; UnknownVersions is what BEM
    // is not entitled to call either way.
    IReadOnlyList<VersionDelta> Deltas,
    IReadOnlyList<VersionDelta> UnknownVersions)
{
    public bool Renamed => !string.Equals(TargetName.Trim(), Profile.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool NothingInstalled => Present.Count == 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Strings.Current.Plural("Core.Interop.ProfileShareImport.Summary.Present", Present.Count)
            };

            if (Deltas.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Interop.ProfileShareImport.Summary.Deltas", Deltas.Count));

            if (Missing.Count > 0)
                parts.Add(Strings.Current.Plural("Core.Interop.ProfileShareImport.Summary.Missing", Missing.Count));

            return string.Join(", ", parts) + ".";
        }
    }

    // A load order is version-locked, so a shared one that says nothing about the version it was built
    // against is a trap. Nothing here refuses the import: a 1.4.7 order is a reasonable starting point
    // on 1.4.8, and only the player knows whether it is.
    public string VersionNote
    {
        get
        {
            var stated = Profile.GameVersion.Trim();
            var here = InstalledGameVersion?.Trim() ?? string.Empty;

            if (stated.Length == 0 && here.Length == 0)
                return Strings.Current["Core.Interop.ProfileShareImport.Version.NeitherKnown"];

            if (stated.Length == 0)
                return Strings.Current.Format("Core.Interop.ProfileShareImport.Version.Unstated", here);

            if (here.Length == 0)
                return Strings.Current.Format("Core.Interop.ProfileShareImport.Version.UnknownHere", stated);

            return ProfileShareImport.SameGameVersion(stated, here)
                ? Strings.Current.Format("Core.Interop.ProfileShareImport.Version.Match", stated)
                : Strings.Current.Format("Core.Interop.ProfileShareImport.Version.Mismatch", stated, here);
        }
    }

    public bool VersionMatches =>
        Profile.GameVersion.Trim().Length > 0
        && !string.IsNullOrWhiteSpace(InstalledGameVersion)
        && ProfileShareImport.SameGameVersion(Profile.GameVersion, InstalledGameVersion);

    // The game version above says whether the order was built for this build of Bannerlord; this says
    // whether it was built with the mods actually sitting here. Both are advisory and neither refuses
    // the import, but a shared order that comes apart usually comes apart on this one.
    public string ModuleVersionNote
    {
        get
        {
            if (!Profile.RecordsModuleVersions)
                return Strings.Current["Core.Interop.ProfileShareImport.ModuleVersions.NotRecorded"];

            if (Present.Count == 0)
                return string.Empty;

            var parts = new List<string>();

            if (Deltas.Count > 0)
            {
                parts.Add(Strings.Current.Plural(
                    "Core.Interop.ProfileShareImport.ModuleVersions.Differ", Deltas.Count));
            }

            if (UnknownVersions.Count > 0)
            {
                parts.Add(Strings.Current.Plural(
                    "Core.Interop.ProfileShareImport.ModuleVersions.NotKnown", UnknownVersions.Count));
            }

            if (parts.Count == 0)
                parts.Add(Strings.Current["Core.Interop.ProfileShareImport.ModuleVersions.AllMatch"]);

            return string.Join(" ", parts);
        }
    }
}

public static class ProfileShareImport
{
    public static ProfileImportPreview Preview(
        ModuleEnvironment installed,
        SharedProfile profile,
        string installedGameVersion,
        string targetName)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(profile);

        var byId = installed.ById;
        var present = new List<ImportedModule>();
        var missing = new List<MissingModule>();
        var deltas = new List<VersionDelta>();
        var unknown = new List<VersionDelta>();

        // Every official module carries the game's build number as its fourth component, so the same
        // normalization the load order import uses applies here: without it a profile made on another
        // hotfix reports a difference for every module the game itself ships.
        var profileChangeSet = LoadOrderImport.ChangeSet(profile.Entries.Select(e => (e.Id, e.Version)));
        var installedChangeSet = LoadOrderImport.ChangeSet(
            installed.Entries.Select(e => (e.Id.Value, LoadOrderExporter.Version(e))));

        foreach (var entry in profile.Entries)
        {
            if (byId.TryGetValue(new ModuleId(entry.Id), out var module))
            {
                var installedVersion = LoadOrderExporter.Version(module);

                present.Add(new ImportedModule(
                    module.Id, module.DisplayName, entry.Version, installedVersion));

                if (!profile.RecordsModuleVersions)
                    continue;

                switch (LoadOrderImport.Agreement(
                    entry.Version, installedVersion, profileChangeSet, installedChangeSet))
                {
                    case VersionAgreement.Different:
                        deltas.Add(new VersionDelta(
                            module.Id, module.DisplayName, entry.Version, installedVersion));
                        break;

                    case VersionAgreement.NotKnown:
                        unknown.Add(new VersionDelta(
                            module.Id, module.DisplayName, entry.Version, installedVersion));
                        break;
                }
            }
            else
            {
                missing.Add(new MissingModule(
                    entry.Id, entry.Version, null, entry.Name.Length == 0 ? null : entry.Name));
            }
        }

        return new ProfileImportPreview(
            profile,
            string.IsNullOrWhiteSpace(targetName) ? profile.Name : targetName.Trim(),
            installedGameVersion ?? string.Empty,
            present,
            missing,
            deltas,
            unknown);
    }

    // The build number every game version carries changes with a hotfix that no load order cares
    // about, so v1.4.8.30000 and v1.4.8.31000 are the same version here. A version neither side can
    // parse is compared as the text it is rather than declared a mismatch on a formatting difference.
    public static bool SameGameVersion(string left, string right)
    {
        var one = left?.Trim() ?? string.Empty;
        var other = right?.Trim() ?? string.Empty;

        return ModuleVersion.TryParse(one, out var parsedLeft) && ModuleVersion.TryParse(other, out var parsedRight)
            ? parsedLeft.CompareTo(parsedRight) == 0
            : string.Equals(one, other, StringComparison.OrdinalIgnoreCase);
    }
}
