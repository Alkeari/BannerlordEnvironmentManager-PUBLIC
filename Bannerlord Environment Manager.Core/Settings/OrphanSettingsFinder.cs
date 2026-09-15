using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Settings;

// How strong the claim is, strongest first. Stated is the user's own word, Content and Exact are
// evidence BEM can read right now, Remembered is evidence it read on an earlier scan and can no
// longer find, and Partial is a guess.
public enum SettingsMatchKind
{
    Stated,
    Content,
    Exact,
    Remembered,
    Partial,
    None
}

// Which evidence produced a match, so a guess is never shown as a fact. Ordered strongest first.
public enum SettingsMatchSource
{
    FileContent,
    FolderName,
    FolderNameContained,
    FolderNameInitials,
    FileName,
    None
}

public sealed record ModuleSuggestion(ModuleId Id, string Name, double Similarity);

public sealed record SettingsFolderReview(
    ModSettingsFolder Folder,
    SettingsMatchKind Kind,
    ModuleId MatchedModuleId,
    string? MatchedModuleName,
    IReadOnlyList<ModuleSuggestion> Suggestions,
    SettingsMatchSource Source = SettingsMatchSource.None,
    string Evidence = "",
    DateTimeOffset? RememberedUtc = null);

// MCM folder names are chosen by mod authors and follow neither the module id nor the display name
// consistently, so nothing here concludes that a folder is dead. It produces candidates the user
// confirms, with the nearest installed modules attached so the user can do the matching BEM cannot.
public static class OrphanSettingsFinder
{
    private const int MinimumContainmentLength = 4;

    // A file name is compared for equality rather than containment, so three characters is evidence
    // where three characters of a substring would not be.
    private const int MinimumFileEvidenceLength = 3;

    private const int MinimumAcronymLength = 3;

    private const double MinimumSuggestionSimilarity = 0.34;

    // Content evidence is corroborated by the folder name before it is believed, and that corroboration
    // is allowed to be weaker than a name match standing on its own would have to be.
    private const int MinimumAffinityAcronymLength = 2;

    private const int MinimumContentTokenLength = 4;

    private const int MaxContentFiles = 200;

    private const long MaxContentFileBytes = 2L * 1024 * 1024;

    private const long MaxContentBytes = 16L * 1024 * 1024;

    private static readonly HashSet<string> GenericFileNames = new(StringComparer.Ordinal)
    {
        "options", "settings", "config", "configuration", "global", "data", "default", "general"
    };

    private sealed class Candidate(ModuleManifest module)
    {
        public ModuleManifest Module { get; } = module;

        public string Id { get; } = Normalize(module.Id.Value);

        public string Name { get; } = Normalize(module.Name);

        public HashSet<string> Words { get; } =
            [.. Words(module.Id.Value), .. Words(module.Name)];
    }

    public static IReadOnlyList<SettingsFolderReview> Review(
        IReadOnlyList<ModSettingsFolder> folders,
        IReadOnlyList<ModuleManifest> modules,
        int maxSuggestions = 3)
    {
        // An empty module list means the scan found nothing or could not run, and calling every
        // settings folder orphaned on that basis would be the worst possible answer.
        if (folders.Count == 0 || modules.Count == 0)
            return [];

        var candidates = modules.Select(module => new Candidate(module)).ToList();
        var tokens = ContentTokens(candidates);

        var reviews = new List<SettingsFolderReview>(folders.Count);

        foreach (var folder in folders)
        {
            var normalized = Normalize(folder.Name);
            var acronym = Initials(folder.Name);

            var exact = candidates.FirstOrDefault(candidate =>
                normalized.Length > 0 && (candidate.Id == normalized || candidate.Name == normalized));

            var content = ContentMatch(folder, tokens, normalized, acronym);

            // Content never overrules a folder that names a different module outright: a settings file
            // routinely mentions the mods it integrates with, and the folder name is the mod's own claim.
            if (content is not null && (exact is null || ReferenceEquals(content.Value.Candidate, exact)))
            {
                reviews.Add(Match(folder, SettingsMatchKind.Content, content.Value.Candidate,
                    SettingsMatchSource.FileContent, content.Value.Evidence));
                continue;
            }

            if (exact is not null)
            {
                reviews.Add(Match(folder, SettingsMatchKind.Exact, exact,
                    SettingsMatchSource.FolderName, Strings.Current["Core.Settings.OrphanFinder.Evidence.FolderName"]));
                continue;
            }

            var contained = candidates.FirstOrDefault(candidate =>
                Contains(candidate.Id, normalized) || Contains(candidate.Name, normalized));

            if (contained is not null)
            {
                reviews.Add(Match(folder, SettingsMatchKind.Partial, contained,
                    SettingsMatchSource.FolderNameContained,
                    Strings.Current["Core.Settings.OrphanFinder.Evidence.FolderNameContained"]));
                continue;
            }

            var initials = normalized.Length == 0
                ? null
                : candidates.FirstOrDefault(candidate => Acronym(candidate.Module.Name) == normalized);

            if (initials is not null)
            {
                reviews.Add(Match(folder, SettingsMatchKind.Partial, initials,
                    SettingsMatchSource.FolderNameInitials,
                    Strings.Current["Core.Settings.OrphanFinder.Evidence.FolderNameInitials"]));
                continue;
            }

            var evidence = FileEvidence(folder);

            var named = candidates.FirstOrDefault(candidate =>
                evidence.ContainsKey(candidate.Id) || evidence.ContainsKey(candidate.Name));

            if (named is not null)
            {
                var file = evidence.TryGetValue(named.Id, out var byId) ? byId : evidence[named.Name];

                reviews.Add(Match(folder, SettingsMatchKind.Partial, named,
                    SettingsMatchSource.FileName,
                    Strings.Current.Format("Core.Settings.OrphanFinder.Evidence.FileName", file)));
                continue;
            }

            var suggestions = candidates
                .Select(candidate => new ModuleSuggestion(
                    candidate.Module.Id,
                    candidate.Module.Name,
                    Math.Max(Similarity(normalized, candidate.Id), Similarity(normalized, candidate.Name))))
                .Where(suggestion => suggestion.Similarity >= MinimumSuggestionSimilarity)
                .OrderByDescending(suggestion => suggestion.Similarity)
                .ThenBy(suggestion => suggestion.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxSuggestions)
                .ToList();

            reviews.Add(new SettingsFolderReview(folder, SettingsMatchKind.None, ModuleId.None, null, suggestions));
        }

        return reviews;
    }

    public static IReadOnlyList<SettingsFolderReview> Candidates(IReadOnlyList<SettingsFolderReview> reviews) =>
        [.. reviews.Where(review => review.Kind == SettingsMatchKind.None)];

    public static string Normalize(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : new string([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static SettingsFolderReview Match(
        ModSettingsFolder folder,
        SettingsMatchKind kind,
        Candidate candidate,
        SettingsMatchSource source,
        string evidence) =>
        new(folder, kind, candidate.Module.Id, candidate.Module.Name, [], source, evidence);

    // Only an identifier-shaped id or display name can be recognized inside a file: a module called
    // Titles or Horses is indistinguishable from the same word written in prose, and matching it would
    // hand a folder to whichever mod happened to have the most ordinary name.
    private static List<(string Token, Candidate Candidate)> ContentTokens(List<Candidate> candidates)
    {
        var tokens = new List<(string, Candidate)>(candidates.Count * 2);

        foreach (var candidate in candidates)
        {
            var id = candidate.Module.Id.Value;
            var name = candidate.Module.Name;

            if (IsIdentifierShaped(id))
                tokens.Add((id, candidate));

            if (!string.Equals(id, name, StringComparison.Ordinal) && IsIdentifierShaped(name))
                tokens.Add((name, candidate));
        }

        return tokens;
    }

    private static bool IsIdentifierShaped(string value)
    {
        if (value.Length < MinimumContentTokenLength)
            return false;

        return value.Any(character => character is '.' or '-' or '_') ||
            value.Skip(1).Any(char.IsUpper) ||
            value.Any(char.IsDigit);
    }

    // Measured on the reference install: Global\EditableEncyclopedia holds only Logs\debug-compat.log,
    // whose version line names EE-Core and CompanionLeadArmy. Both are installed, so content alone is
    // ambiguous and the folder name decides which of the two the folder belongs to.
    private static (Candidate Candidate, string Evidence)? ContentMatch(
        ModSettingsFolder folder,
        List<(string Token, Candidate Candidate)> tokens,
        string normalized,
        string acronym)
    {
        if (tokens.Count == 0)
            return null;

        var found = new Dictionary<Candidate, string>();
        var budget = MaxContentBytes;

        foreach (var file in ModSettingsScanner.EnumerateFiles(folder.FullPath).Take(MaxContentFiles))
        {
            string text;

            try
            {
                var length = new FileInfo(file).Length;

                if (length == 0 || length > MaxContentFileBytes)
                    continue;

                budget -= length;

                if (budget < 0)
                    break;

                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var (token, candidate) in tokens)
            {
                if (found.ContainsKey(candidate) || !text.Contains(token, StringComparison.Ordinal))
                    continue;

                found[candidate] = Strings.Current.Format(
                    "Core.Settings.OrphanFinder.Evidence.ContentToken", token, Path.GetRelativePath(folder.FullPath, file));
            }
        }

        var ranked = found
            .Select(entry => (Candidate: entry.Key, Evidence: entry.Value, Pull: Affinity(normalized, acronym, entry.Key)))
            .Where(entry => entry.Pull > 0)
            .OrderByDescending(entry => entry.Pull)
            .ToList();

        if (ranked.Count == 0 || (ranked.Count > 1 && ranked[0].Pull <= ranked[1].Pull))
            return null;

        return (ranked[0].Candidate, ranked[0].Evidence);
    }

    // How hard the folder name pulls toward one module. Zero means the name says nothing about it, and
    // a mention inside a file is then only a mention.
    private static double Affinity(string normalized, string acronym, Candidate candidate)
    {
        if (normalized.Length == 0)
            return 0;

        if (candidate.Id == normalized || candidate.Name == normalized)
            return 1;

        if (Contains(candidate.Id, normalized) || Contains(candidate.Name, normalized))
            return 0.8;

        if (acronym.Length >= MinimumAffinityAcronymLength &&
            (candidate.Words.Contains(acronym) || Acronym(candidate.Module.Name) == acronym))
        {
            return 0.6;
        }

        var similarity = Math.Max(Similarity(normalized, candidate.Id), Similarity(normalized, candidate.Name));

        return similarity >= MinimumSuggestionSimilarity ? similarity / 2 : 0;
    }

    // Measured on the reference install: the folder Global\MyModFolder holds RMS.json and the module
    // RMS is installed, so the file names carry evidence the folder name does not.
    private static Dictionary<string, string> FileEvidence(ModSettingsFolder folder)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in ModSettingsScanner.EnumerateFiles(folder.FullPath))
        {
            var name = Normalize(Path.GetFileNameWithoutExtension(file));

            if (name.Length >= MinimumFileEvidenceLength && !GenericFileNames.Contains(name))
                names.TryAdd(name, Path.GetRelativePath(folder.FullPath, file));
        }

        return names;
    }

    // Measured on the reference install: Global\YKWYK belongs to You Keep What You Kill.
    private static string Acronym(string name)
    {
        var initials = Initials(name);

        return initials.Length >= MinimumAcronymLength ? initials : string.Empty;
    }

    private static string Initials(string name) =>
        new([.. SplitOnWordStarts(name).Select(char.ToLowerInvariant)]);

    private static IEnumerable<string> Words(string value)
    {
        var word = new List<char>();

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (!char.IsLetterOrDigit(character))
            {
                if (word.Count > 0)
                    yield return new string([.. word]);

                word.Clear();
                continue;
            }

            if (char.IsUpper(character) && index > 0 && char.IsLower(value[index - 1]) && word.Count > 0)
            {
                yield return new string([.. word]);
                word.Clear();
            }

            word.Add(char.ToLowerInvariant(character));
        }

        if (word.Count > 0)
            yield return new string([.. word]);
    }

    private static IEnumerable<char> SplitOnWordStarts(string name)
    {
        var atWordStart = true;

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (!char.IsLetterOrDigit(character))
            {
                atWordStart = true;
                continue;
            }

            var startsWord = atWordStart ||
                (char.IsUpper(character) && index > 0 && !char.IsUpper(name[index - 1]));

            if (startsWord)
                yield return character;

            atWordStart = false;
        }
    }

    private static bool Contains(string module, string folder)
    {
        if (folder.Length < MinimumContainmentLength || module.Length < MinimumContainmentLength)
            return false;

        return module.Contains(folder, StringComparison.Ordinal) || folder.Contains(module, StringComparison.Ordinal);
    }

    private static double Similarity(string left, string right)
    {
        var leftPairs = Bigrams(left);
        var rightPairs = Bigrams(right);

        if (leftPairs.Count == 0 || rightPairs.Count == 0)
            return 0;

        var shared = 0;
        var remaining = new List<string>(rightPairs);

        foreach (var pair in leftPairs)
        {
            if (!remaining.Remove(pair))
                continue;

            shared++;
        }

        return 2.0 * shared / (leftPairs.Count + rightPairs.Count);
    }

    private static List<string> Bigrams(string value)
    {
        var pairs = new List<string>(Math.Max(0, value.Length - 1));

        for (var index = 0; index + 1 < value.Length; index++)
            pairs.Add(value.Substring(index, 2));

        return pairs;
    }
}
