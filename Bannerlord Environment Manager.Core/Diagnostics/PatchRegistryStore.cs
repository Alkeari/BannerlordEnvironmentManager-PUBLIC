using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Reason is the short sentence. Details carries the module names behind it, which run to dozens on a
// heavily modded install and must never be pasted into the sentence: the panel this reads out on is one
// the user passes through constantly, and a paragraph of ids there buries the one line that matters.
public sealed record PatchRegistryLookup(
    PatchRegistry? Registry,
    bool IsStale,
    string Reason,
    string Details = "")
{
    public bool CanAttribute => Registry is not null;
}

public sealed record StoredPatchRegistry(
    string Key,
    string Path,
    IReadOnlyList<ModuleId> ModuleSet,
    string? GameVersion,
    DateTime CapturedUtc,
    string RunId,
    int Patches);

// A crash report arrives after the fact, so the registry a dry run captured has to outlive the run.
// It is stored keyed by the module set it came from, and a lookup for a different set hands the
// registry back LABELED stale rather than quietly attributing against the wrong data. That label is
// what rule R9 reads.
public sealed class PatchRegistryStore(string root)
{
    private const string FileExtension = ".json";

    private static string NoRegistry => Strings.Current["Core.Diagnostics.PatchRegistryStore.NoRegistry"];

    // Per version. A registry is keyed on the module set it was captured from, and a module set only
    // means anything against the game version that loaded it: one machine-wide store handed a v1.4.8
    // capture to a v1.5.2 lookup as the newest fallback and attributed a crash against patches that
    // build never ran. The resting version keeps the path it has always used, so every registry
    // already captured is still found.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "patch-registry");

    public string Root => root;

    public void Save(PatchRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Directory.CreateDirectory(root);

        var path = Path.Combine(root, KeyOf(registry.ModuleSet) + FileExtension);
        var temporary = path + ".partial";

        File.WriteAllText(temporary, JsonSerializer.Serialize(Persisted.From(registry)), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    public PatchRegistryLookup Load(IReadOnlyList<ModuleId> moduleSet, string? gameVersion = null)
    {
        ArgumentNullException.ThrowIfNull(moduleSet);

        var exact = Read(Path.Combine(root, KeyOf(moduleSet) + FileExtension));

        if (exact is not null)
            return Judge(exact, moduleSet, gameVersion);

        var newest = List().OrderByDescending(s => s.CapturedUtc).FirstOrDefault();

        if (newest is null)
            return new PatchRegistryLookup(null, false, NoRegistry);

        var fallback = Read(newest.Path);

        return fallback is null
            ? new PatchRegistryLookup(null, false, NoRegistry)
            : Judge(fallback, moduleSet, gameVersion);
    }

    public IReadOnlyList<StoredPatchRegistry> List()
    {
        if (!Directory.Exists(root))
            return [];

        var stored = new List<StoredPatchRegistry>();

        foreach (var file in Directory.EnumerateFiles(root, "*" + FileExtension))
        {
            var registry = Read(file);

            if (registry is null)
                continue;

            stored.Add(new StoredPatchRegistry(
                Path.GetFileNameWithoutExtension(file),
                file,
                registry.ModuleSet,
                registry.GameVersion,
                registry.CapturedUtc,
                registry.RunId,
                registry.Patches.Count));
        }

        return stored;
    }

    // Both directions: anything BEM writes on the user's behalf it has to be able to throw away.
    public int Clear()
    {
        if (!Directory.Exists(root))
            return 0;

        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*" + FileExtension).ToList())
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that will not delete is not a reason to leave the rest behind.
            }
        }

        return removed;
    }

    private static PatchRegistryLookup Judge(
        PatchRegistry registry,
        IReadOnlyList<ModuleId> moduleSet,
        string? gameVersion)
    {
        var reasons = new List<string>();
        var details = new List<string>();

        var captured = new HashSet<ModuleId>(registry.ModuleSet);
        var wanted = new HashSet<ModuleId>(moduleSet);

        var added = wanted.Except(captured).Select(m => m.Value).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = captured.Except(wanted).Select(m => m.Value).Order(StringComparer.OrdinalIgnoreCase).ToList();

        if (added.Count > 0)
        {
            reasons.Add(Strings.Current.Plural("Core.Diagnostics.PatchRegistryStore.Reason.Added", added.Count));
            details.Add(Strings.Current.Format(
                "Core.Diagnostics.PatchRegistryStore.Detail.Added", string.Join(", ", added)));
        }

        if (removed.Count > 0)
        {
            reasons.Add(Strings.Current.Format("Core.Diagnostics.PatchRegistryStore.Reason.Removed", removed.Count));
            details.Add(Strings.Current.Format(
                "Core.Diagnostics.PatchRegistryStore.Detail.Removed", string.Join(", ", removed)));
        }

        if (!GameVersionMatch.SameGameVersion(registry.GameVersion, gameVersion))
        {
            reasons.Add(Strings.Current.Format(
                "Core.Diagnostics.PatchRegistryStore.Reason.GameVersion", registry.GameVersion, gameVersion));
        }

        if (reasons.Count == 0)
        {
            // The version is stated on the fresh sentence too. The build number the capture carries is
            // not what the manifest reports, and saying which build the reading came from is the part
            // that can be checked.
            var on = string.IsNullOrWhiteSpace(registry.GameVersion)
                ? string.Empty
                : Strings.Current.Format("Core.Diagnostics.PatchRegistryStore.OnGameVersion", registry.GameVersion);

            return new PatchRegistryLookup(
                registry,
                false,
                Strings.Current.Format(
                    "Core.Diagnostics.PatchRegistryStore.Fresh",
                    registry.CapturedUtc.ToString("u", CultureInfo.InvariantCulture),
                    on,
                    registry.ModuleSet.Count,
                    registry.Patches.Count,
                    registry.PatchedMethodCount));
        }

        return new PatchRegistryLookup(
            registry,
            true,
            Strings.Current.Format("Core.Diagnostics.PatchRegistryStore.Stale", string.Join(", ", reasons)),
            string.Join(" ", details));
    }

    private static PatchRegistry? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path))?.ToRegistry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Order-independent: a load order that was reordered but not changed is the same module set, and
    // a registry captured from it is still exact.
    private static string KeyOf(IReadOnlyList<ModuleId> moduleSet)
    {
        var joined = string.Join(
            "\n",
            moduleSet.Select(m => m.Value ?? string.Empty)
                .Select(v => v.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }

    private sealed record PersistedPatch(
        string Type,
        string Method,
        string Module,
        string Kind,
        string Owner,
        string AttributedBy,
        string PatchMethod);

    private sealed record Persisted(
        int Schema,
        string RunId,
        string? GameVersion,
        DateTime CapturedUtc,
        int PatchedMethodCount,
        IReadOnlyList<string> ModuleSet,
        IReadOnlyList<PersistedPatch> Patches)
    {
        public static Persisted From(PatchRegistry registry) => new(
            1,
            registry.RunId,
            registry.GameVersion,
            registry.CapturedUtc,
            registry.PatchedMethodCount,
            [.. registry.ModuleSet.Select(m => m.Value)],
            [
                .. registry.Patches.Select(p => new PersistedPatch(
                    p.TargetTypeFullName,
                    p.TargetMethodName,
                    p.ModuleId.Value,
                    p.Kind.ToString(),
                    p.Owner,
                    p.AttributedBy.ToString(),
                    p.PatchMethod))
            ]);

        public PatchRegistry ToRegistry() => new(
            [
                .. (Patches ?? []).Select(p => new PatchRecord(
                    p.Type ?? string.Empty,
                    p.Method ?? string.Empty,
                    new ModuleId(p.Module ?? string.Empty),
                    Enum.TryParse<PatchKind>(p.Kind, ignoreCase: true, out var kind) ? kind : PatchKind.Prefix,
                    p.Owner ?? string.Empty,
                    Enum.TryParse<PatchAttribution>(p.AttributedBy, ignoreCase: true, out var by)
                        ? by
                        : PatchAttribution.Unattributed,
                    p.PatchMethod ?? string.Empty))
            ],
            [.. (ModuleSet ?? []).Select(m => new ModuleId(m))],
            GameVersion,
            CapturedUtc,
            RunId ?? string.Empty,
            PatchedMethodCount);
    }
}
