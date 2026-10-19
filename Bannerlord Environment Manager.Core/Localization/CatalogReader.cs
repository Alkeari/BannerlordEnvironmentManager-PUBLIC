using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Localization;

public sealed record CatalogReadResult(LanguageCatalog? Catalog, string? Error);

// A catalog can arrive from the Languages folder, which is to say from a stranger on the internet.
// Every failure here is reported rather than thrown: one bad community file must not stop BEM from
// starting.
public static class CatalogReader
{
    public const string HeaderKey = "$language";

    public static CatalogReadResult Read(string json) => Read(json, null);

    // A correction of three lines has no business declaring a language it is not defining, so the
    // header is optional and the file name names the language when it is absent. The header still
    // wins when it is there, because a translator who copies en.json to start a new language edits
    // the header rather than the file name.
    public static CatalogReadResult Read(string json, string? fallbackCode)
    {
        if (json == null)
            return new CatalogReadResult(null, "The catalog JSON is null.");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new CatalogReadResult(null, ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return new CatalogReadResult(null, "The catalog is not a JSON object.");

            document.RootElement.TryGetProperty(HeaderKey, out var header);

            var code = Text(header, "code");

            if (string.IsNullOrWhiteSpace(code))
                code = fallbackCode?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(code))
            {
                return new CatalogReadResult(
                    null, $"The catalog has no {HeaderKey} header naming a code.");
            }

            var entries = new Dictionary<string, LanguageEntry>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(HeaderKey))
                    continue;

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        entries[property.Name] = new LanguageEntry.Single(property.Value.GetString()!);
                        break;

                    case JsonValueKind.Object:
                        var forms = new Dictionary<PluralCategory, string>();
                        foreach (var form in property.Value.EnumerateObject())
                        {
                            if (!Enum.TryParse<PluralCategory>(form.Name, ignoreCase: true, out var category))
                                return new CatalogReadResult(
                                    null, $"'{property.Name}' has an unknown plural form '{form.Name}'.");

                            if (form.Value.ValueKind is not JsonValueKind.String)
                                return new CatalogReadResult(
                                    null, $"'{property.Name}' has a non-text value for '{form.Name}'.");

                            forms[category] = form.Value.GetString()!;
                        }

                        entries[property.Name] = new LanguageEntry.Plural(forms);
                        break;

                    default:
                        return new CatalogReadResult(
                            null, $"'{property.Name}' is neither text nor a set of plural forms.");
                }
            }

            // The names are left exactly as declared, empty when they were not, so a caller can tell
            // "this file names the language" from "this file only corrects some lines of it" and fall
            // back to the catalog it overrides rather than putting a bare language code in the picker.
            var catalog = new LanguageCatalog(code, Text(header, "native"), Text(header, "english"), entries);

            return new CatalogReadResult(catalog, null);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()!
            : string.Empty;
}
