using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One folder that was renamed, and the per-version state folders that did not make it across with it.
public sealed record InstanceFolderMove(string InstanceId, string From, string To)
{
    public IReadOnlyList<string> StateLeftBehind { get; init; } = [];
}

// One folder that was left exactly as it was, and why. Wanted is the name it would have taken.
public sealed record InstanceFolderRefusal(string InstanceId, string Folder, string Wanted, string Reason);

public sealed record InstanceFolderAlignmentReport(
    IReadOnlyList<InstanceFolderMove> Moved,
    IReadOnlyList<InstanceFolderRefusal> Refused,
    string? Refusal = null)
{
    public bool AnythingMoved => Moved.Count > 0;

    public bool AnythingToSay => AnythingMoved || Refused.Count > 0 || Refusal is not null;

    private IReadOnlyList<InstanceFolderMove> Stranded => [.. Moved.Where(move => move.StateLeftBehind.Count > 0)];

    public string Describe()
    {
        if (Refusal is { } refused)
            return refused;

        var said = new List<string>();

        if (AnythingMoved)
        {
            said.Add(Strings.Current.Plural(
                "Core.Instances.FolderNames.Renamed",
                Moved.Count,
                string.Join(", ", Moved.Select(move => Strings.Current.Format(
                    "Core.Instances.FolderNames.Item",
                    Path.GetFileName(move.From),
                    Path.GetFileName(move.To))))));
        }

        if (Stranded.Count > 0)
        {
            said.Add(Strings.Current.Plural(
                "Core.Instances.FolderNames.StateLeftBehind",
                Stranded.Count,
                string.Join(", ", Stranded.SelectMany(move => move.StateLeftBehind))));
        }

        if (Refused.Count > 0)
        {
            said.Add(Strings.Current.Plural(
                "Core.Instances.FolderNames.Refused",
                Refused.Count,
                string.Join(", ", Refused.Select(refusal => Strings.Current.Format(
                    "Core.Instances.FolderNames.RefusedItem",
                    Path.GetFileName(refusal.Folder),
                    refusal.Wanted,
                    refusal.Reason)))));
        }

        return said.Count == 0 ? Strings.Current["Core.Instances.FolderNames.Nothing"] : string.Join(" ", said);
    }
}

// Every instance folder brought into line with what InstanceFolderName says the instance is.
//
// The folder is the instance's identity on disk, so a rename is not a string change: the saves, the
// mods, the game files, the adopted backup and the instance.json all live inside it, the record's own
// GameFolder names it, and BEM's per-version state is filed under it by name. So the whole of it moves
// or none of it does.
//
// The order is the folder first, the record second, the state third, and it is chosen for what an
// interruption leaves behind:
//
//   - Nothing before the folder move has happened, so a crash there leaves the instance untouched.
//   - The folder move is a single rename inside one volume, so every irreplaceable thing an instance
//     owns arrives together or stays together. There is no half-moved save.
//   - A crash between the folder move and the record write leaves GameFolder naming a path that is
//     gone. That is the one stale-pointer window, one file write wide, and it heals itself: the pass
//     starts by re-deriving GameFolder for any managed instance whose recorded Game folder is missing
//     while its own Game folder is right there.
//   - A crash between the record write and the state move leaves BEM's own opinions about that
//     version's load order - the pins, the dividers, the profiles, the accepted findings, the launch
//     history - filed under the old folder name, and the version starts with none of them. No save,
//     mod or game file is affected, the old folder is still sitting there under the old name, and the
//     report names it so the user can move it across. This is the price of filing state by folder name
//     rather than by instance Id, and closing it properly means changing what the state is keyed on.
//
// Anything holding the folder - the game running out of it, a shell window sitting inside it, a
// scanner with a file open - makes Directory.Move throw, and that instance is reported and left
// exactly as it was. Nothing partial is possible there, because the move is the first step.
public static class InstanceFolderAlignment
{
    private const int MaxCollisionSuffix = 999;

    // Two instances can compose one name, and a folder renamed out from under another instance's
    // target within one run leaves that one on a numbered sibling it does not want. Repeating the
    // sweep until it stops moving anything settles both inside one call rather than at the next start.
    private const int MaxPasses = 3;

    // onlyInstanceId narrows what may move to a single instance, for the caller that has just changed
    // one record and has no business renaming the folders of instances it did not touch. Every other
    // instance still counts as occupying its folder, so a narrowed run cannot take a name that is
    // already spoken for.
    //
    // restingInstanceId is which instance holds the resting role, because that role names a folder and
    // no record carries it: it is written in settings.json, and an instance stops wearing the name the
    // moment it stops resting. A caller that has no resting instance yet passes null and every folder
    // composes the name it always did.
    public static InstanceFolderAlignmentReport Run(
        IReadOnlyList<InstalledInstance> installed,
        IReadOnlyList<string> stateRoots,
        string? onlyInstanceId = null,
        string? restingInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(stateRoots);

        var origin = new Dictionary<string, string>(StringComparer.Ordinal);
        var landed = new Dictionary<string, InstanceFolderMove>(StringComparer.Ordinal);
        var refused = new List<InstanceFolderRefusal>();

        var current = HealMissingGameFolders(installed);

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            refused.Clear();
            var anythingMoved = false;

            for (var index = 0; index < current.Count; index++)
            {
                var instance = current[index];

                if (onlyInstanceId is not null
                    && !string.Equals(instance.Record.Id, onlyInstanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var attempt = MoveOne(instance, stateRoots, restingInstanceId);

                if (attempt.Landed is { } landing && attempt.Step is { } step)
                {
                    current[index] = landing;
                    origin.TryAdd(step.InstanceId, step.From);
                    landed[step.InstanceId] = step with { From = origin[step.InstanceId] };
                    anythingMoved = true;
                }
                else if (attempt.Refusal is { } refusal)
                {
                    refused.Add(refusal);
                }
            }

            if (!anythingMoved)
                break;
        }

        return new InstanceFolderAlignmentReport(
            [.. landed.Values.Where(move => !string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase))],
            [.. refused]);
    }

    // What one instance's folder should be called, given every folder already occupied. Null when the
    // folder already says what it should, a case-only difference included: Directory.Move refuses a
    // rename whose source and destination differ only in case, so treating one as work to do would put
    // the pass in a loop it could never finish.
    public static string? TargetFor(InstalledInstance instance, string gamesRoot, string? restingInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var currentName = Path.GetFileName(
            instance.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var wanted = InstanceFolderName.For(
            instance.Record, InstanceLabel.IsResting(instance.Record.Id, restingInstanceId));

        foreach (var candidate in Series(wanted))
        {
            if (string.Equals(candidate, currentName, StringComparison.OrdinalIgnoreCase))
                return null;

            var path = Path.Combine(gamesRoot, candidate);

            if (!Directory.Exists(path) && !File.Exists(path))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> Series(string wanted)
    {
        yield return wanted;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
            yield return $"{wanted} ({suffix})";
    }

    private readonly record struct MoveAttempt(
        InstalledInstance? Landed, InstanceFolderMove? Step, InstanceFolderRefusal? Refusal);

    private static MoveAttempt MoveOne(
        InstalledInstance instance, IReadOnlyList<string> stateRoots, string? restingInstanceId)
    {
        var gamesRoot = Path.GetDirectoryName(
            instance.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (gamesRoot is null || TargetFor(instance, gamesRoot, restingInstanceId) is not { } target)
            return default;

        var destination = Path.Combine(gamesRoot, target);

        try
        {
            Directory.Move(instance.Folder, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveAttempt(null, null, new InstanceFolderRefusal(
                instance.Record.Id, instance.Folder, target, ex.Message));
        }

        var record = Rebased(instance.Record, instance.Folder, destination);

        if (record != instance.Record)
            WriteRecord(destination, record);

        var stranded = MoveState(stateRoots, instance.Folder, destination);

        return new MoveAttempt(
            new InstalledInstance(destination, record),
            new InstanceFolderMove(record.Id, instance.Folder, destination) { StateLeftBehind = stranded },
            null);
    }

    // The record's own Game folder, moved along with the folder it sits under. A referenced instance
    // is left completely alone: its Game folder is Steam's own install, it sits nowhere near the
    // instance folder, and rewriting it would point the resting instance at a game that is not there.
    // A managed record whose Game folder is somehow not under its own folder is left alone for the
    // same reason - a path this pass did not move is not a path this pass may rewrite.
    private static InstanceRecord Rebased(InstanceRecord record, string from, string to)
    {
        if (record.GameFolderIsReferenced)
            return record;

        var relative = RelativeUnder(from, record.GameFolder);

        return relative is null ? record : record with { GameFolder = Path.Combine(to, relative) };
    }

    // The one window an interruption can leave open, closed on the next run: a managed instance whose
    // recorded Game folder is gone while its own Game folder is right where the layout puts it was
    // renamed by a pass that did not get as far as writing the record.
    private static List<InstalledInstance> HealMissingGameFolders(IReadOnlyList<InstalledInstance> installed)
    {
        var healed = new List<InstalledInstance>(installed.Count);

        foreach (var instance in installed)
        {
            var record = instance.Record;
            var derived = InstanceLayout.GameFolder(instance.Folder);

            if (record.GameFolderIsReferenced
                || string.Equals(record.GameFolder, derived, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(record.GameFolder)
                || !Directory.Exists(derived))
            {
                healed.Add(instance);
                continue;
            }

            var corrected = record with { GameFolder = derived };
            WriteRecord(instance.Folder, corrected);
            healed.Add(new InstalledInstance(instance.Folder, corrected));
        }

        return healed;
    }

    // BEM's own state for this version, filed under the folder name, moved to the new one in every
    // root that files it. A destination that already exists is another version's state and is never
    // merged onto: that folder is named instead, and it stays where it is for the user to deal with.
    private static IReadOnlyList<string> MoveState(
        IReadOnlyList<string> stateRoots, string oldFolder, string newFolder)
    {
        var oldKey = InstanceStateFolder.KeyFor(InstanceDataRoot.ForInstance(oldFolder));
        var newKey = InstanceStateFolder.KeyFor(InstanceDataRoot.ForInstance(newFolder));

        if (oldKey is null || newKey is null || string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
            return [];

        var stranded = new List<string>();

        foreach (var root in stateRoots)
        {
            var from = InstanceStateFolder.FolderIn(root, oldKey);
            var to = InstanceStateFolder.FolderIn(root, newKey);

            if (!Directory.Exists(from))
                continue;

            if (Directory.Exists(to))
            {
                stranded.Add(from);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                stranded.Add(from);
            }
        }

        return stranded;
    }

    private static string? RelativeUnder(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return full[(root.Length + 1)..];
    }

    // Mirrors the registry's own replace-rather-than-edit write. A half-written instance.json costs
    // the user an instance, and this one is written straight after a folder move, which is the one
    // moment the record and the folder disagree.
    private static void WriteRecord(string instanceFolder, InstanceRecord record)
    {
        try
        {
            var path = InstanceLayout.MetadataPath(instanceFolder);
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(record, MetadataFormat));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static readonly JsonSerializerOptions MetadataFormat = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
