using System.Text.Json;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Settings;

public enum SecretConfidence
{
    Possible,
    Probable,
    Certain
}

public sealed record SecretFinding(
    string RelativePath,
    string ValuePath,
    string Rule,
    SecretConfidence Confidence,
    int ValueLength,
    string Preview);

public enum SecretScanOutcome
{
    Scanned,
    TooLarge,
    Unreadable,
    NotText
}

// What the scan did, not only what it found. A file the scan never opened cannot be part of an
// all-clear, and the only way to keep that promise is to carry the skip out with the findings.
public sealed record SecretScan(
    IReadOnlyList<SecretFinding> Findings,
    SecretScanOutcome Outcome = SecretScanOutcome.Scanned,
    string Reason = "")
{
    public bool WasRead => Outcome is SecretScanOutcome.Scanned;
}

// Calibrated against the reference install: 3081 values, 1000 of them strings, one true positive and
// no false positives. Every threshold here exists to hold that down, because a warning that fires on
// healthy settings is a warning that gets clicked through.
public static class SecretScanner
{
    public static string KnownPrefixRule => Strings.Current["Core.Settings.SecretScanner.Rule.KnownPrefix"];

    public static string SecretShapedNameRule => Strings.Current["Core.Settings.SecretScanner.Rule.SecretShapedName"];

    public static string HighEntropyRule => Strings.Current["Core.Settings.SecretScanner.Rule.HighEntropy"];

    private const int MinimumPrefixedLength = 16;

    private const int MinimumNamedLength = 16;

    private const int MinimumEntropyLength = 24;

    private const double MinimumEntropy = 3.5;

    private const int MaximumLetterRun = 12;

    private const long MaximumScannedBytes = 8L * 1024 * 1024;

    private static readonly string[] KnownPrefixes =
    [
        "sk-", "sk_live_", "pk_live_", "rk_live_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_",
        "xoxb-", "xoxp-", "xoxa-", "xoxs-", "AIza", "AKIA", "ASIA", "hf_", "nvapi-", "glpat-", "npm_",
        "dop_v1_", "shpat_", "SG.", "eyJ", "-----BEGIN"
    ];

    private static readonly string[] SecretWords =
    [
        "key", "keys", "apikey", "apikeys", "token", "tokens", "secret", "secrets", "password",
        "passwords", "passwd", "pwd", "auth", "authorization", "credential", "credentials"
    ];

    public static IReadOnlyList<SecretFinding> ScanFolder(ModSettingsFolder folder) =>
        ScanDirectory(folder.FullPath, folder.RelativePath);

    public static IReadOnlyList<SecretFinding> ScanDirectory(string directory, string relativeRoot)
    {
        var findings = new List<SecretFinding>();

        foreach (var file in ModSettingsScanner.EnumerateFiles(directory))
        {
            var relativePath = Path.Combine(relativeRoot, Path.GetRelativePath(directory, file));

            findings.AddRange(ScanFile(file, relativePath));
        }

        return findings;
    }

    public static IReadOnlyList<SecretFinding> ScanFile(string path, string relativePath) =>
        Inspect(path, relativePath).Findings;

    // Three files never reach the rules: one too big to read, one the file system refuses, and one that
    // is not text. Each comes back saying so, because "no finding" and "never opened" are opposite
    // answers to whoever is deciding whether to share the bundle.
    public static SecretScan Inspect(string path, string relativePath)
    {
        try
        {
            var length = new FileInfo(path).Length;

            if (length > MaximumScannedBytes)
            {
                return new SecretScan(
                    [],
                    SecretScanOutcome.TooLarge,
                    Strings.Current.Plural("Core.Settings.SecretScanner.TooLarge", length, MaximumScannedBytes));
            }

            return InspectContent(relativePath, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SecretScan([], SecretScanOutcome.Unreadable, ex.Message);
        }
    }

    public static SecretScan InspectContent(string relativePath, string content)
    {
        if (content.Contains('\0'))
            return new SecretScan([], SecretScanOutcome.NotText, Strings.Current["Core.Settings.SecretScanner.NotText"]);

        return new SecretScan(ScanContent(relativePath, content));
    }

    public static IReadOnlyList<SecretFinding> ScanContent(string relativePath, string content)
    {
        if (string.IsNullOrEmpty(content) || content.Contains('\0'))
            return [];

        var findings = new List<SecretFinding>();

        foreach (var value in ReadValues(relativePath, content))
        {
            if (Judge(value) is not { } judgment)
                continue;

            findings.Add(new SecretFinding(
                relativePath,
                value.Name,
                judgment.Rule,
                judgment.Confidence,
                value.Text.Length,
                Preview(value.Text)));
        }

        return findings;
    }

    // Four characters name the vendor without carrying the credential: enough to tell an OpenRouter key
    // from a GitHub token, useless to anyone who reads it.
    public static string Preview(string value) =>
        value.Length <= 4 ? "..." : value[..4] + "...";

    private static (string Rule, SecretConfidence Confidence)? Judge(ScannedValue value)
    {
        var token = StripBearer(value.Text);

        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            return null;

        if (token.Length >= MinimumPrefixedLength &&
            KnownPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
            return (KnownPrefixRule, SecretConfidence.Certain);

        if (token.Length >= MinimumNamedLength && NameSuggestsSecret(value.Name))
            return (SecretShapedNameRule, SecretConfidence.Probable);

        if (LooksHighEntropy(token))
            return (HighEntropyRule, SecretConfidence.Possible);

        return null;
    }

    // "Hotkey", "PortAuthority" and "LeadershipAuthority" all contain a secret word as a substring. A
    // substring match flagged 14 values on the reference install and 13 of them were nonsense, so the
    // name is split into words and only a whole word counts.
    private static bool NameSuggestsSecret(string name) =>
        SplitWords(name).Any(word => SecretWords.Contains(word, StringComparer.Ordinal));

    public static IReadOnlyList<string> SplitWords(string name)
    {
        if (string.IsNullOrEmpty(name))
            return [];

        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                continue;
            }

            var startsWord = char.IsUpper(character) &&
                current.Length > 0 &&
                (!char.IsUpper(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1])));

            if (startsWord)
                Flush();

            current.Append(char.ToLowerInvariant(character));
        }

        Flush();

        return words;

        void Flush()
        {
            if (current.Length == 0)
                return;

            words.Add(current.ToString());
            current.Clear();
        }
    }

    // Mod settings are full of long CamelCase item ids, which score as high entropy on any character
    // measure. The letter-run cap is what separates them from a credential: an identifier is a run of
    // letters, a credential is not. Requiring mixed character classes instead was tried and rejected,
    // because it also rejects the lowercase-hex key this feature exists to catch.
    private static bool LooksHighEntropy(string token)
    {
        if (token.Length < MinimumEntropyLength)
            return false;

        // A base64 secret contains slashes, so only an actual URL or path is excluded here.
        if (token.Contains('\\') || (Uri.TryCreate(token, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1))
            return false;

        if (Guid.TryParse(token, out _))
            return false;

        return MaxLetterRun(token) <= MaximumLetterRun && Entropy(token) >= MinimumEntropy;
    }

    public static double Entropy(string value)
    {
        if (value.Length == 0)
            return 0;

        var counts = new Dictionary<char, int>();

        foreach (var character in value)
            counts[character] = counts.GetValueOrDefault(character) + 1;

        var entropy = 0.0;

        foreach (var count in counts.Values)
        {
            var probability = (double)count / value.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static int MaxLetterRun(string value)
    {
        var best = 0;
        var current = 0;

        foreach (var character in value)
        {
            if (char.IsLetter(character))
            {
                current++;
                best = Math.Max(best, current);
                continue;
            }

            current = 0;
        }

        return best;
    }

    private static string StripBearer(string value)
    {
        var trimmed = value.Trim();

        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Bearer ".Length..].Trim()
            : trimmed;
    }

    private readonly record struct ScannedValue(string Name, string Text);

    private static IEnumerable<ScannedValue> ReadValues(string relativePath, string content) =>
        Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".json" => ReadJson(content),
            ".xml" => ReadXml(content),
            ".txt" or ".ini" or ".cfg" or ".config" or ".properties" => ReadKeyValueLines(content),
            _ => ReadLooseTokens(content)
        };

    private static IEnumerable<ScannedValue> ReadJson(string content)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return ReadLooseTokens(content);
        }

        var values = new List<ScannedValue>();

        using (document)
            Walk(document.RootElement, string.Empty);

        return values;

        void Walk(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}");

                    break;

                case JsonValueKind.Array:
                    var index = 0;

                    foreach (var item in element.EnumerateArray())
                        Walk(item, $"{path}[{index++}]");

                    break;

                case JsonValueKind.String:
                    values.Add(new ScannedValue(LastSegment(path), element.GetString() ?? string.Empty));
                    break;
            }
        }
    }

    private static IEnumerable<ScannedValue> ReadXml(string content)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException)
        {
            return ReadLooseTokens(content);
        }

        var values = new List<ScannedValue>();

        foreach (var element in document.Descendants())
        {
            if (!element.HasElements)
                values.Add(new ScannedValue(element.Name.LocalName, element.Value));

            foreach (var attribute in element.Attributes())
                values.Add(new ScannedValue(attribute.Name.LocalName, attribute.Value));
        }

        return values;
    }

    private static IEnumerable<ScannedValue> ReadKeyValueLines(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('﻿');

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('['))
                continue;

            var separator = trimmed.IndexOf('=');

            if (separator <= 0)
                continue;

            yield return new ScannedValue(trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim());
        }
    }

    // A file BEM cannot parse still gets looked at, because a credential pasted into an unknown format
    // leaks exactly as badly. Only the prefix rule can fire here, since there is no key name to read
    // and no structure to trust.
    private static IEnumerable<ScannedValue> ReadLooseTokens(string content)
    {
        foreach (var token in content.Split([' ', '\t', '\r', '\n', '"', '\'', ',', '<', '>', '=', '(', ')'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= MinimumPrefixedLength &&
                KnownPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
                yield return new ScannedValue(string.Empty, token);
        }
    }

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOf('.');

        return index < 0 ? path : path[(index + 1)..];
    }
}
