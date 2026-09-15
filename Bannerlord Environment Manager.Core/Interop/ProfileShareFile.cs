using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Interop;

// The name and version decorate the shopping list a recipient is shown; only Id and IsEnabled are
// load-bearing, and only those reach the stored profile.
public sealed record SharedProfileEntry(string Id, bool IsEnabled, string Version = "", string Name = "");

public sealed record SharedProfile(
    string Name,
    DateTime SavedUtc,
    string GameVersion,
    IReadOnlyList<SharedProfileEntry> Entries,
    IReadOnlyList<LoadOrderDividerSnapshotEntry> Sections,
    // Whether the writer stated module versions at all, which is not the same question as whether a
    // given module has one. False means the file predates format 2 and said nothing either way, so
    // every comparison against it is unanswerable rather than a mismatch; true means a blank version
    // is the module declaring none.
    bool RecordsModuleVersions = true)
{
    public int EnabledCount => Entries.Count(e => e.IsEnabled);

    public LoadOrderSnapshot ToSnapshot() =>
        new([.. Entries.Select(e => new LoadOrderSnapshotEntry(e.Id, e.IsEnabled))], Sections);
}

public sealed record SharedProfileRead(SharedProfile? Profile, string Error = "")
{
    public bool Failed => Profile is null;

    public static SharedProfileRead Unreadable(string error) => new(null, error);
}

// A profile handed to another player. It arrives over Discord or Nexus from a stranger, so every
// value in it is data: nothing here becomes a path, a command or a type name, and a file that is not
// exactly what it claims to be is refused whole rather than imported in part.
public static class ProfileShareFile
{
    public const string Extension = ".bemprofile";

    // Written verbatim into every file and checked before anything else is read, so a .bemprofile
    // that is really something else is turned away by its own first field rather than by the shape
    // of what follows.
    public const string FormatMarker = "bannerlord-environment-manager/load-order-profile";

    // 1 wrote a version beside each module but nothing ever read one, so a file at that number cannot
    // be told apart from one whose modules simply declare nothing. 2 states that the versions in the
    // file are deliberate, which is what lets the import screen call a blank one "not known" rather
    // than a mismatch.
    public const int FormatVersion = 2;

    // The profile store's own cap, read from there rather than restated: the two ends of a shared
    // file have to agree, and a name this reader refuses is one no writer may produce.
    public const int MaxNameLength = LoadOrderProfileStore.MaxNameLength;
    public const int MaxIdLength = 200;
    public const int MaxVersionLength = 60;
    public const int MaxSections = 200;
    public const int MaxSectionLabelLength = 100;
    public const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static SharedProfile Describe(
        LoadOrderProfile profile, string gameVersion, ModuleEnvironment installed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installed);

        var byId = installed.ById;

        var entries = profile.Order.Entries.Select(entry =>
            byId.TryGetValue(new ModuleId(entry.Id), out var module)
                ? new SharedProfileEntry(
                    entry.Id, entry.IsEnabled, LoadOrderExporter.Version(module), module.DisplayName)
                : new SharedProfileEntry(entry.Id, entry.IsEnabled));

        return new SharedProfile(
            profile.Name, profile.SavedUtc, gameVersion?.Trim() ?? string.Empty, [.. entries],
            profile.Order.Dividers);
    }

    public static string Write(SharedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var shape = new
        {
            format = FormatMarker,
            formatVersion = FormatVersion,
            name = profile.Name,
            savedUtc = profile.SavedUtc.ToString("o"),
            gameVersion = profile.GameVersion,
            modules = profile.Entries.Select(e => new
            {
                id = e.Id,
                enabled = e.IsEnabled,
                version = e.Version,
                name = e.Name
            }),
            sections = profile.Sections.Select(s => new
            {
                label = s.Label,
                anchorId = s.AnchorId,
                collapsed = s.Collapsed,
                sourceModuleId = s.SourceModuleId
            })
        };

        return JsonSerializer.Serialize(shape, Format);
    }

    public static void Save(string path, SharedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(path);

        File.WriteAllText(path, Write(profile), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static SharedProfileRead Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        byte[] bytes;

        try
        {
            var info = new FileInfo(path);

            if (info.Exists && info.Length > MaxFileBytes)
            {
                return SharedProfileRead.Unreadable(Strings.Current.Format(
                    "Core.Interop.ProfileShareFile.TooLarge", MaxFileBytes / (1024 * 1024)));
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SharedProfileRead.Unreadable(
                Strings.Current.Format("Core.Interop.Read.CouldNotRead", ex.Message));
        }

        return bytes.Length > MaxFileBytes
            ? SharedProfileRead.Unreadable(Strings.Current.Format(
                "Core.Interop.ProfileShareFile.TooLarge", MaxFileBytes / (1024 * 1024)))
            : ReadText(LoadOrderInterop.Decode(bytes));
    }

    public static SharedProfileRead ReadText(string text)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text ?? string.Empty);
        }
        catch (JsonException ex)
        {
            return SharedProfileRead.Unreadable(
                Strings.Current.Format("Core.Interop.ProfileShareFile.NotJson", ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return Refuse("Core.Interop.ProfileShareFile.NotAnObject");

            if (Text(root, "format") != FormatMarker)
                return Refuse("Core.Interop.ProfileShareFile.NotAProfile");

            if (Property(root, "formatVersion") is not { ValueKind: JsonValueKind.Number } version
                || !version.TryGetInt32(out var declared)
                || declared < 1)
            {
                return Refuse("Core.Interop.ProfileShareFile.NoFormatVersion");
            }

            // Unknown fields a later format adds are ignored, which is what makes this file readable
            // by a BEM that has gained some. A whole format number ahead is the other direction, and
            // there is no honest way to guess what a field BEM has never seen does to the order.
            if (declared > FormatVersion)
            {
                return SharedProfileRead.Unreadable(Strings.Current.Format(
                    "Core.Interop.ProfileShareFile.NewerFormat", declared, FormatVersion));
            }

            var name = Text(root, "name").Trim();

            if (name.Length == 0)
                return Refuse("Core.Interop.ProfileShareFile.NoName");

            if (name.Length > MaxNameLength)
            {
                return SharedProfileRead.Unreadable(Strings.Current.Format(
                    "Core.Interop.ProfileShareFile.NameTooLong", MaxNameLength));
            }

            if (!DateTime.TryParse(
                    Text(root, "savedUtc"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var savedUtc))
            {
                return Refuse("Core.Interop.ProfileShareFile.NoSavedTime");
            }

            if (Property(root, "modules") is not { ValueKind: JsonValueKind.Array } modules)
                return Refuse("Core.Interop.ProfileShareFile.NoModules");

            var entries = new List<SharedProfileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;

            foreach (var module in modules.EnumerateArray())
            {
                position++;

                if (entries.Count == LoadOrderInterop.MaxEntries)
                {
                    return SharedProfileRead.Unreadable(Strings.Current.Format(
                        "Core.Interop.ProfileShareFile.TooManyModules", LoadOrderInterop.MaxEntries));
                }

                if (module.ValueKind != JsonValueKind.Object)
                {
                    return SharedProfileRead.Unreadable(Strings.Current.Format(
                        "Core.Interop.ProfileShareFile.BadEntry", position));
                }

                var id = Text(module, "id").Trim();

                if (id.Length == 0 || id.Length > MaxIdLength)
                {
                    return SharedProfileRead.Unreadable(Strings.Current.Format(
                        "Core.Interop.ProfileShareFile.BadEntry", position));
                }

                if (!seen.Add(id))
                {
                    return SharedProfileRead.Unreadable(Strings.Current.Format(
                        "Core.Interop.ProfileShareFile.RepeatedId", id));
                }

                var moduleVersion = Text(module, "version").Trim();

                if (moduleVersion.Length > MaxVersionLength)
                {
                    return SharedProfileRead.Unreadable(Strings.Current.Format(
                        "Core.Interop.ProfileShareFile.VersionTooLong", MaxVersionLength));
                }

                entries.Add(new SharedProfileEntry(
                    id,
                    Property(module, "enabled") is { ValueKind: JsonValueKind.True },
                    moduleVersion,
                    Capped(Text(module, "name"), MaxNameLength)));
            }

            if (entries.Count == 0)
                return Refuse("Core.Interop.ProfileShareFile.EmptyOrder");

            if (ReadSections(root) is not { } sections)
                return Refuse("Core.Interop.ProfileShareFile.BadSection");

            var gameVersion = Text(root, "gameVersion").Trim();

            if (gameVersion.Length > MaxVersionLength)
            {
                return SharedProfileRead.Unreadable(Strings.Current.Format(
                    "Core.Interop.ProfileShareFile.VersionTooLong", MaxVersionLength));
            }

            // A format 1 file that happens to carry versions is still carrying real ones, and throwing
            // them away to honor the format number would report "not recorded" over data that is right
            // there. Only a file that states none anywhere is treated as having recorded none.
            var recordsVersions = declared >= 2 || entries.Any(e => e.Version.Length > 0);

            return new SharedProfileRead(
                new SharedProfile(name, savedUtc, gameVersion, entries, sections, recordsVersions));
        }
    }

    // Sections are the only optional part: absent means the sender's list had none, and an empty list
    // says the same thing. A section that is present and malformed still refuses the file, because a
    // file BEM did not write is a file whose module list has not earned trust either.
    private static List<LoadOrderDividerSnapshotEntry>? ReadSections(JsonElement root)
    {
        var found = new List<LoadOrderDividerSnapshotEntry>();

        if (Property(root, "sections") is not { } sections)
            return found;

        if (sections.ValueKind == JsonValueKind.Null)
            return found;

        if (sections.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var section in sections.EnumerateArray())
        {
            if (found.Count == MaxSections || section.ValueKind != JsonValueKind.Object)
                return null;

            var label = Text(section, "label").Trim();

            if (label.Length == 0 || label.Length > MaxSectionLabelLength)
                return null;

            var anchor = Text(section, "anchorId").Trim();
            var source = Text(section, "sourceModuleId").Trim();

            if (anchor.Length > MaxIdLength || source.Length > MaxIdLength)
                return null;

            found.Add(new LoadOrderDividerSnapshotEntry(
                label,
                anchor.Length == 0 ? null : anchor,
                Property(section, "collapsed") is { ValueKind: JsonValueKind.True },
                source.Length == 0 ? null : source));
        }

        return found;
    }

    private static SharedProfileRead Refuse(string key) => SharedProfileRead.Unreadable(Strings.Current[key]);

    // Only for a module's display name, which has somewhere honest to fall back to: an entry with no
    // name is shown under its id. A version has no such fallback, so an over-length one refuses the
    // file rather than being blanked, which would have the preview say the profile records no game
    // version over a file that plainly states one.
    private static string Capped(string value, int limit) => value.Length > limit ? string.Empty : value;

    // JsonElement.TryGetProperty is case-sensitive, and a file a player has round-tripped through
    // another tool can come back with a different casing.
    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static string Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
