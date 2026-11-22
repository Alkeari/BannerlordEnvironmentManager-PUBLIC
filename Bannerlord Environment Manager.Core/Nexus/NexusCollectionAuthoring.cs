using System.Globalization;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Nexus;

// One mod of a collection being authored: the Nexus page and, where BEM knows it, the exact file.
public sealed record CollectionDraftMod(int ModId, int? FileId, string Name, string? Version, bool Optional);

// A collection built from a load-order profile. Nexus has no public API for publishing one, so the
// draft is what BEM can honestly produce: the ordered shopping list of mod pages and files the profile
// enables, written as a file (and as links) for the curator to build the collection from, and read
// back by BEM as a revision it can plan against. Modules with no Nexus page are listed apart rather
// than dropped, so the curator sees what the collection will be missing.
public sealed record CollectionDraft(
    string Name,
    IReadOnlyList<CollectionDraftMod> Mods,
    IReadOnlyList<string> Unlinked,
    IReadOnlyList<string> LoadOrder)
{
    public const string FormatMarker = "bannerlord-environment-manager/collection-draft";

    public const int FormatVersion = 1;

    public const int MaxNameLength = 120;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    // Enabled modules only, in load order; a page that carries several enabled modules is one mod of the
    // collection. optionalModuleIds marks the curator's suggestions rather than requirements, and
    // isOfficial keeps the game's own modules, which ship with the game, out of the list altogether.
    public static CollectionDraft From(
        string name,
        LoadOrderSnapshot order,
        IEnumerable<NexusModuleLink> links,
        Func<string, bool>? isOfficial = null,
        IEnumerable<string>? optionalModuleIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(links);

        var byModule = new Dictionary<string, NexusModuleLink>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
            byModule.TryAdd(link.ModuleId, link);

        var optional = new HashSet<string>(optionalModuleIds ?? [], StringComparer.OrdinalIgnoreCase);
        var pages = new Dictionary<int, CollectionDraftMod>();
        var unlinked = new List<string>();
        var loadOrder = new List<string>();

        foreach (var entry in order.Entries.Where(entry => entry.IsEnabled))
        {
            if (isOfficial?.Invoke(entry.Id) == true)
                continue;

            loadOrder.Add(entry.Id);

            if (!byModule.TryGetValue(entry.Id, out var link) || link.NexusModId is not { } modId)
            {
                unlinked.Add(entry.Id);
                continue;
            }

            var mod = new CollectionDraftMod(
                modId, link.NexusFileId, link.ModuleName, link.NexusVersionAtInstall ?? link.InstalledVersion, optional.Contains(entry.Id));

            // A page already listed keeps its first entry, and is a requirement if any module on it is.
            pages[modId] = pages.TryGetValue(modId, out var seen)
                ? seen with { Optional = seen.Optional && mod.Optional }
                : mod;
        }

        var trimmed = name.Trim();

        return new CollectionDraft(
            trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed,
            [.. pages.Values],
            unlinked,
            loadOrder);
    }

    public int RequiredCount => Mods.Count(mod => !mod.Optional);

    public string PageUrl(CollectionDraftMod mod) =>
        $"https://www.nexusmods.com/{NexusEndpoints.GameDomain}/mods/{mod.ModId.ToString(CultureInfo.InvariantCulture)}";

    // The revision shape the planner reads, so a draft can be checked against an install the way an
    // imported collection is. It has no slug, since nothing has been published.
    public NexusCollectionRevision ToRevision() =>
        new(string.Empty, Name, 0,
            [.. Mods.Where(mod => mod.FileId is not null)
                .Select(mod => new NexusCollectionMod(mod.ModId, mod.FileId!.Value, mod.Name, null, mod.Version, mod.Optional))],
            Mods.Count(mod => mod.FileId is null));

    public string ToJson() =>
        JsonSerializer.Serialize(new
        {
            format = FormatMarker,
            version = FormatVersion,
            name = Name,
            game = NexusEndpoints.GameDomain,
            mods = Mods.Select(mod => new { mod.ModId, mod.FileId, mod.Name, mod.Version, mod.Optional }),
            unlinked = Unlinked,
            loadOrder = LoadOrder
        }, Format);

    public string ToMarkdown()
    {
        var text = new StringBuilder();

        text.AppendLine($"# {Name}").AppendLine();

        void Section(string heading, IEnumerable<CollectionDraftMod> mods)
        {
            var list = mods.ToList();

            if (list.Count == 0)
                return;

            text.AppendLine($"## {heading}").AppendLine();

            foreach (var mod in list)
                text.AppendLine($"- [{mod.Name}]({PageUrl(mod)}){(string.IsNullOrWhiteSpace(mod.Version) ? string.Empty : " " + mod.Version)}");

            text.AppendLine();
        }

        Section("Required", Mods.Where(mod => !mod.Optional));
        Section("Optional", Mods.Where(mod => mod.Optional));

        if (Unlinked.Count > 0)
        {
            text.AppendLine("## Not on Nexus Mods").AppendLine();

            foreach (var id in Unlinked)
                text.AppendLine($"- {id}");
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    // Null for anything that is not a draft this reads; nothing in it becomes a path or a command.
    public static CollectionDraft? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || NexusCollections.Text(root, "format") != FormatMarker
                || NexusCollections.Text(root, "name") is not { Length: > 0 } name
                || !root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
                return null;

            var read = new List<CollectionDraftMod>();

            foreach (var mod in mods.EnumerateArray())
            {
                if (!mod.TryGetProperty("ModId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var modId) || modId <= 0)
                    return null;

                read.Add(new CollectionDraftMod(
                    modId,
                    mod.TryGetProperty("FileId", out var file) && file.ValueKind == JsonValueKind.Number && file.TryGetInt32(out var fileId) ? fileId : null,
                    NexusCollections.Text(mod, "Name") ?? modId.ToString(CultureInfo.InvariantCulture),
                    NexusCollections.Text(mod, "Version"),
                    mod.TryGetProperty("Optional", out var optional) && optional.ValueKind == JsonValueKind.True));
            }

            return new CollectionDraft(name, read, Strings(root, "unlinked"), Strings(root, "loadOrder"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];
}
