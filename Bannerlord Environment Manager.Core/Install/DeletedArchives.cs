using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

public enum DeletedArchiveAvailability
{
    Read,
    NotSupported,
    NotReadable
}

// "Found nothing" and "could not look" mean opposite things to whoever reads the result, so the
// listing carries which of the two happened rather than an empty list that could be either.
public sealed record DeletedArchiveListing(
    IReadOnlyList<string> FileNames,
    DeletedArchiveAvailability Availability = DeletedArchiveAvailability.Read)
{
    public static DeletedArchiveListing Unavailable(DeletedArchiveAvailability why) => new([], why);

    public bool CouldRead => Availability == DeletedArchiveAvailability.Read;
}

// Core never learns what a Recycle Bin is. It is handed the names, and a platform that has no such
// thing hands it a listing that says so.
public interface IDeletedArchiveNames
{
    DeletedArchiveListing ReadNames();
}

public sealed record DeletedArchiveClaims(
    IReadOnlyList<ArchiveModuleCandidate> Candidates,
    int Archives,
    int NoModIdInFileName,
    int NoModNameInFileName,
    int NoInstalledModuleOfThatName,
    int NamedMoreThanOneModule)
{
    public static DeletedArchiveClaims Nothing { get; } = new([], 0, 0, 0, 0, 0);
}

// A deleted archive's filename is the last thing left saying which mod page a module came from, and it
// says the mod's name rather than the module id the game uses. Turning one into the other is a guess,
// so it is made only where the guess cannot be wrong in more than one way: the name has to land on
// exactly one installed module, spelled exactly as that module spells its own id or its own name.
public static class DeletedArchiveMatching
{
    public static DeletedArchiveClaims Match(
        DeletedArchiveListing listing,
        IEnumerable<ModuleManifest> installed)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(installed);

        if (!listing.CouldRead)
            return DeletedArchiveClaims.Nothing;

        var byName = ModulesByName(installed);

        var candidates = new List<ArchiveModuleCandidate>();

        var archives = 0;
        var noModId = 0;
        var noModName = 0;
        var noModule = 0;
        var manyModules = 0;

        foreach (var fileName in listing.FileNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            archives++;

            if (NexusArchiveName.TryRead(fileName) is not { } parts)
            {
                noModId++;
                continue;
            }

            if (parts.ModName.Length == 0)
            {
                noModName++;
                continue;
            }

            var matches = TitleCandidates(parts.ModName)
                .Select(title => byName.TryGetValue(Normalize(title), out var ids) ? ids : null)
                .FirstOrDefault(ids => ids is not null);

            if (matches is null)
            {
                noModule++;
                continue;
            }

            if (matches.Count > 1)
            {
                manyModules++;
                continue;
            }

            candidates.Add(new ArchiveModuleCandidate(fileName, [matches.Single()]));
        }

        return new DeletedArchiveClaims(candidates, archives, noModId, noModName, noModule, manyModules);
    }

    public static string Describe(DeletedArchiveClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (claims.Archives == 0)
            return Strings.Current["Core.Install.DeletedArchives.NoneRead"];

        var refused = new List<string>();

        if (claims.NoModIdInFileName > 0)
            refused.Add(Strings.Current.Plural("Core.Install.DeletedArchives.NoModId", claims.NoModIdInFileName));

        if (claims.NoModNameInFileName > 0)
            refused.Add(Strings.Current.Plural("Core.Install.DeletedArchives.NoModName", claims.NoModNameInFileName));

        if (claims.NoInstalledModuleOfThatName > 0)
            refused.Add(Strings.Current.Plural("Core.Install.DeletedArchives.NoModule", claims.NoInstalledModuleOfThatName));

        if (claims.NamedMoreThanOneModule > 0)
            refused.Add(Strings.Current.Plural("Core.Install.DeletedArchives.ManyModules", claims.NamedMoreThanOneModule));

        var head = Strings.Current.Plural(
            "Core.Install.DeletedArchives.Head", claims.Archives, claims.Candidates.Count);

        return refused.Count == 0
            ? head
            : Strings.Current.Format("Core.Install.DeletedArchives.LeftAlone", head, string.Join(", ", refused));
    }

    // Both spellings are indexed because a download is named after the mod, and a module calls itself
    // by an id in one place and a display name in another. A spelling two modules share is kept and
    // counted rather than dropped, so it can be refused for being ambiguous instead of silently missed.
    private static Dictionary<string, List<string>> ModulesByName(IEnumerable<ModuleManifest> installed)
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var module in installed)
        {
            foreach (var spelling in new[] { module.Id.Value, module.Name })
            {
                var key = Normalize(spelling);

                if (key.Length == 0)
                    continue;

                if (!byName.TryGetValue(key, out var ids))
                    byName[key] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                ids.Add(module.Id.Value);
            }
        }

        return byName.ToDictionary(entry => entry.Key, entry => entry.Value.ToList(), StringComparer.Ordinal);
    }

    // Uploaders routinely put the version in the download's name, so "Retinues - v1.3.14.31" and
    // "Retinues" are the same mod. Only trailing tokens that are a version or a bare separator are
    // dropped, and the untrimmed name is always tried first, so a mod genuinely called "Grass x8"
    // still matches itself.
    private static IEnumerable<string> TitleCandidates(string modName)
    {
        var tokens = modName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (tokens.Count > 0)
        {
            yield return string.Join(' ', tokens);

            if (!IsVersionOrSeparator(tokens[^1]))
                yield break;

            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    private static bool IsVersionOrSeparator(string token)
    {
        if (!token.Any(char.IsAsciiLetterOrDigit))
            return true;

        return token.Any(char.IsAsciiDigit)
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
    }

    private static string Normalize(string? text) =>
        text is null
            ? string.Empty
            : new string([.. text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);
}
