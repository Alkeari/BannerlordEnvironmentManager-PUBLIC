using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Interop;

// ModdingTools.com's private JSON, read only. It is a full scan of the Modules folder rather than a
// list of enabled modules, so unlike every other format here, presence does not mean enabled: the
// "selected" flag is the answer and it is honored.
public static class MtOrderFile
{
    public const string Game = "bannerlord";

    public static LoadOrderFileRead Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return LoadOrderFileRead.Unreadable(Strings.Current.Format("Core.Interop.MtOrderFile.UnreadableJson", ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.MtOrderFile.NotModdingToolsFile"]);

            if (root.TryGetProperty("game", out var game)
                && game.ValueKind == JsonValueKind.String
                && !string.Equals(game.GetString(), Game, StringComparison.OrdinalIgnoreCase))
            {
                return LoadOrderFileRead.Unreadable(
                    Strings.Current.Format("Core.Interop.MtOrderFile.WrongGame", game.GetString()));
            }

            if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.MtOrderFile.NoModulesArray"]);

            var entries = new List<LoadOrderFileEntry>();

            foreach (var module in modules.EnumerateArray())
            {
                if (module.ValueKind != JsonValueKind.Object)
                    continue;

                var id = Text(module, "id");

                if (string.IsNullOrEmpty(id))
                    continue;

                entries.Add(new LoadOrderFileEntry(
                    id,
                    Text(module, "version") ?? string.Empty,
                    IsEnabled: Selected(module),
                    Name: Text(module, "name"),
                    Url: Text(module, "url")));

                if (entries.Count == LoadOrderInterop.MaxEntries)
                    break;
            }

            return LoadOrderInterop.Collect(entries);
        }
    }

    // A missing flag means enabled: the field is not guaranteed to be written, and treating its
    // absence as disabled would import an empty load order from a file that lists everything.
    private static bool Selected(JsonElement module) =>
        !module.TryGetProperty("selected", out var selected)
        || selected.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            _ => true
        };

    private static string? Text(JsonElement module, string name) =>
        module.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}
