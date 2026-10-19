using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// An instance and the folder it was read from. The folder is the instance's identity on disk: a display
// name is the user's to change, and re-deriving a path from it after a rename names a folder that does
// not exist while the saves sit in the old one.
public sealed record InstalledInstance(string Folder, InstanceRecord Record);

// One instance is one folder in the games root holding an instance.json. Enumerating folders rather
// than keeping a central index means a user who moves, copies or deletes an instance folder in
// Explorer gets the result they expect, and a corrupt file costs one instance rather than the list.
public sealed class InstanceRegistry(string gamesRoot)
{
    private const int MaxCollisionSuffix = 999;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string GamesRoot { get; } = gamesRoot;

    public IReadOnlyList<InstalledInstance> ReadInstalled()
    {
        if (!Directory.Exists(GamesRoot))
            return [];

        var installed = new List<InstalledInstance>();

        foreach (var folder in Directory.EnumerateDirectories(GamesRoot))
        {
            if (Path.GetFileName(folder).StartsWith('.'))
                continue;

            if (TryRead(folder) is { } record)
                installed.Add(new InstalledInstance(folder, record));
        }

        return [.. installed.OrderBy(i => i.Record.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<InstanceRecord> Read() => [.. ReadInstalled().Select(i => i.Record)];

    public InstanceRecord? Find(string id) =>
        Read().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    // The folder an instance already occupies, or the one it would be created in. Never a path derived
    // from a name the user has since changed.
    public string FolderFor(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return FolderOf(record) ?? FreePath(Path.Combine(GamesRoot, FolderNameFor(record)));
    }

    // One copy, one folder. A download goes to the first folder in the name's own series that no
    // registered instance occupies, whatever is sitting in it: an attempt that failed left a partial
    // copy, and downloading beside it turns one copy into two folders and one of them into waste.
    // DepotDownloader picks up where it stopped in the folder it already filled.
    //
    // The only thing that pushes a download along the series is another registered instance already
    // living under that name, which is either a different version wearing it or an earlier copy of
    // this same one. Walking the series rather than asking FreePath for the first name nothing at all
    // occupies is what keeps the rule true for a second copy: a stopped second-copy download left
    // "v1.4.8 (2)" full of bytes, and FreePath would have answered "v1.4.8 (3)" and fetched them
    // again.
    public string FolderForDownload(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (FolderOf(record) is { } existing)
            return existing;

        var occupied = ReadInstalled()
            .Select(instance => instance.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in FolderSeries(Path.Combine(GamesRoot, FolderNameFor(record))))
        {
            if (!occupied.Contains(candidate))
                return candidate;
        }

        throw new IOException($"'{FolderNameFor(record)}' already names {MaxCollisionSuffix} instances.");
    }

    // Every folder name a download for one record can land in, in the order it tries them: the name
    // itself, then the numbered siblings a second copy takes. FreePath numbers a folder the same way,
    // and InstanceFolderName reads that same suffix back off a folder it is handed.
    private static IEnumerable<string> FolderSeries(string named)
    {
        yield return named;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
            yield return $"{named} ({suffix})";
    }

    // Whether a folder in the games root is one a download for this record could have made. A second
    // copy downloads into a numbered sibling and a copy the user named wears that name ahead of the
    // version, so matching the bare name alone would leave the partial bytes of a stopped copy with
    // no version against them and nothing to resume. InstanceFolderName reads the whole shape back.
    public static bool NamesDownloadFolder(InstanceRecord record, string folderName)
    {
        ArgumentNullException.ThrowIfNull(record);

        return InstanceFolderName.ChosenNameIn(record, folderName) is not null
            || NamesEarlierBuildsDownloadFolder(record, folderName);
    }

    // The folder an earlier build made for a variant download, which stated the variant twice: the
    // row's label already spelled the DLC out in words and the short names were appended to it all
    // the same. Nothing composes this any more. It is read and never written, so a download stopped
    // part way into one of those folders is still offered a resume rather than fetched again, and a
    // base download is not affected either way because both builds compose that folder identically.
    private static bool NamesEarlierBuildsDownloadFolder(InstanceRecord record, string folderName)
    {
        if (record.Dlc.IsEmpty)
            return false;

        var version = ModuleVersion.Parse(record.RecordedGameVersion);
        var text = version.IsEmpty ? record.DisplayName : version.ToString();

        var label = $"{text} + {string.Join(" + ", record.Dlc.DisplayNames)}";

        return InstanceFolderName.ChosenNameBefore(
            $"{label} + {string.Join(" + ", record.Dlc.ShortNames)}", folderName) is not null;
    }

    public string? FolderOf(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return ReadInstalled()
            .FirstOrDefault(i => string.Equals(i.Record.Id, record.Id, StringComparison.Ordinal))
            ?.Folder;
    }

    public void Write(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var folder = FolderFor(record);

        Directory.CreateDirectory(folder);
        WriteAtomic(InstanceLayout.MetadataPath(folder), JsonSerializer.Serialize(record, Format));
    }

    // The folder a record wants to be in, composed and sanitized by InstanceFolderName. One rule for
    // a download picking its folder, for a write, and for the pass that brings existing folders into
    // line, because three rules would let a folder be named one thing on the way in and renamed to
    // another the moment BEM next started.
    public static string FolderNameFor(InstanceRecord record) => InstanceFolderName.For(record);

    // The Id an instance about to be written down should carry: the slug for its version, branch and
    // variant, then made unique against every instance already in the games root.
    //
    // Uniqueness is checked rather than assumed because the slug alone cannot promise it. An Id is
    // how Find, RestingInstanceId and ActiveInstanceId name an instance, and they take the first
    // record that answers, so a second record wearing an Id already in use does not merely look
    // confusing: it shadows the first, and the resting version can end up resolving to the wrong
    // install. A hand-edited record, a folder copied in Explorer or a future DLC whose short name
    // slugs into an existing Id all reach that state without any download being at fault.
    //
    // The numbered suffix is the same answer FreePath gives a folder name, in slug form rather than
    // " (2)"; the two are kept apart because what they test for availability, and what they do when
    // they run out, have nothing in common beyond the count they stop at.
    public string MintId(string version, string branch, GameDlcSet dlc)
    {
        var desired = SlugFor(version, branch, dlc);
        var taken = new HashSet<string>(Read().Select(record => record.Id), StringComparer.Ordinal);

        if (!taken.Contains(desired))
            return desired;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = $"{desired}-{suffix}";

            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"'{desired}' already identifies {MaxCollisionSuffix} instances.");
    }

    // The DLC set is folded in exactly as FolderNameFor folds it into the folder name, so one
    // version's base instance and its variant are two identities rather than one, and there is one
    // rule for both rather than two. An empty set mints what the two-argument form mints, which is
    // the Id every instance written down so far already carries: no existing record changes shape.
    // The variant is folded onto the base Id rather than onto the two parts it was built from, so
    // the collapse below happens once and holds for a variant too.
    public static string SlugFor(string version, string branch, GameDlcSet dlc) =>
        dlc.IsEmpty ? SlugFor(version, branch) : SlugFor(SlugFor(version, branch), dlc.FolderKey);

    // A simple lowercase-and-hyphenate: keep letters and digits, turn everything else into a hyphen,
    // then collapse runs of hyphens so "v1.5.2" and "beta" join as "v1-5-2-beta" rather than "v1--5--2".
    //
    // A version and a branch that read the same are one word, not two. TaleWorlds publishes old
    // versions as branches named after the version, so a download of v1.3.15 is a download of branch
    // "v1.3.15" and the join said it twice: "v1-3-15-v1-3-15-ws". An Id is durable identity - the key
    // instances.json is filed under, and what RestingInstanceId and ActiveInstanceId point at - so it
    // cannot be tidied up afterwards without re-identifying the record. Nothing already written down
    // changes shape: only the case where the two are equal collapses, and every branch that is not a
    // version number still joins exactly as it did.
    public static string SlugFor(string version, string branch)
    {
        var joined = string.Equals(version, branch, StringComparison.OrdinalIgnoreCase)
            ? version
            : $"{version}-{branch}";

        var raw = joined.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();

        var slug = string.Join('-', new string(raw).Split('-', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

        return slug.Length == 0 ? "instance" : slug;
    }

    // Display names are the user's and nothing stops two instances sharing one, and "A/B" and "A-B"
    // sanitize to the same folder besides. Handing the second one the first one's folder would have
    // Write overwrite an existing instance.json and take that instance off the list. The numbered
    // sibling is the same answer the quarantine store and the settings archive give.
    private static string FreePath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return path;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = $"{path} ({suffix})";

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"'{path}' already has {MaxCollisionSuffix} instances beside it.");
    }

    private static InstanceRecord? TryRead(string folder)
    {
        var path = InstanceLayout.MetadataPath(folder);

        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(path), Format);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A half-written instance.json would cost the user an instance, so the file is replaced rather
    // than edited in place.
    private static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
