using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Interop;

// Vortex's per-profile load order JSON: an array of {id, name, isSelected, isDisabled, index}. The
// shape has varied (a wrapping object, an object keyed by id), so the reader accepts those too, and
// an entry is enabled unless it says otherwise. Read only: Vortex owns that file.
public static class VortexLoadOrderFile
{
    private static readonly JsonDocumentOptions Options =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static LoadOrderFileRead Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text.TrimStart('﻿'), Options);
        }
        catch (JsonException ex)
        {
            return LoadOrderFileRead.Unreadable(Strings.Current.Format("Core.Interop.VortexLoadOrderFile.UnreadableJson", ex.Message));
        }

        using (document)
        {
            var rows = new List<(int Index, int Position, LoadOrderFileEntry Entry)>();

            if (!Collect(document.RootElement, rows, 0) || rows.Count == 0)
                return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.VortexLoadOrderFile.NotALoadOrder"]);

            // A missing index keeps file order; a present one decides it.
            var ordered = rows.OrderBy(r => r.Index).ThenBy(r => r.Position).Select(r => r.Entry).ToList();

            return LoadOrderInterop.Collect(ordered);
        }
    }

    // True when the text is a Vortex load order, so a ".json" the user picks can be told from other formats.
    public static bool Looks(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿'), Options);
            var rows = new List<(int Index, int Position, LoadOrderFileEntry Entry)>();

            return Collect(document.RootElement, rows, 0) && rows.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Where Vortex keeps one: in the game folder, beside the deployment manifests (which are not a load
    // order and are skipped), and under Vortex's own folder for the game.
    public static IReadOnlyList<string> FindFiles(string gameRoot, string? roamingAppData = null)
    {
        var found = new List<string>();
        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var places = new List<(string Folder, SearchOption Option)>();

        if (!string.IsNullOrWhiteSpace(gameRoot))
            places.Add((gameRoot, SearchOption.TopDirectoryOnly));

        if (!string.IsNullOrEmpty(roaming))
            places.Add((Path.Combine(roaming, "Vortex", "mountandblade2bannerlord"), SearchOption.AllDirectories));

        foreach (var (folder, option) in places)
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (var file in Directory.EnumerateFiles(folder, "*.json", option))
                {
                    if (Path.GetFileName(file).StartsWith("vortex.deployment", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var info = new FileInfo(file);

                        if (info.Length is > 0 and < 4_000_000 && Looks(File.ReadAllText(file)))
                            found.Add(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Collect(JsonElement element, List<(int Index, int Position, LoadOrderFileEntry Entry)> rows, int depth)
    {
        if (depth > 4)
            return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                var any = false;

                foreach (var item in element.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        any |= Row(item, rows, null);

                return any;

            case JsonValueKind.Object:
                // A wrapper such as {"loadOrder": [...]}.
                foreach (var name in new[] { "loadOrder", "loadorder", "modules", "mods", "order" })
                    if (element.TryGetProperty(name, out var inner)
                        && inner.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        return Collect(inner, rows, depth + 1);

                // An object keyed by module id, each value an entry.
                var keyed = false;

                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object)
                        keyed |= Row(property.Value, rows, property.Name);

                return keyed;
        }

        return false;
    }

    private static bool Row(JsonElement item, List<(int Index, int Position, LoadOrderFileEntry Entry)> rows, string? keyId)
    {
        var id = Text(item, "id") ?? keyId;

        // A state flag or an index marks a load order row; a bare {"id"} is not enough to claim a file.
        var marked = item.TryGetProperty("index", out _) || item.TryGetProperty("isSelected", out _)
            || item.TryGetProperty("isDisabled", out _);

        if (string.IsNullOrWhiteSpace(id) || !marked)
            return false;

        var enabled = true;

        if (item.TryGetProperty("isSelected", out var selected) && selected.ValueKind == JsonValueKind.False)
            enabled = false;

        if (item.TryGetProperty("isDisabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
            enabled = false;

        var index = item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n)
            ? n
            : int.MaxValue;

        rows.Add((index, rows.Count, new LoadOrderFileEntry(id.Trim(), Text(item, "version") ?? string.Empty, enabled, Text(item, "name"))));

        return true;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
