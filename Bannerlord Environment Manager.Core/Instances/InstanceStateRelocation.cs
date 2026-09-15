namespace BannerlordEnvironmentManager.Core.Instances;

// One entry this swap moved, kept so that a swap which fails part way can be put back exactly.
public sealed record StateMove(string From, string To);

// One folder that files state the same way: the resting version's entries at the top of Root, every
// other version's under Root\instances\<folder name>\. There is more than one because the captured
// crash artifacts deliberately sit outside BEM's own folder (a folder a crash scan walks may never sit
// under one holding the Nexus key) while keying themselves exactly the same way.
public sealed record StateArea(string Root, IReadOnlyList<string> EntryNames);

// BEM's own state about a version - the pins, the section dividers, the saved profiles, the accepted
// findings and risks, the launch history, the settings attributions, the load order backups, the files
// it cleared, the patch registries it captured, the archived mod settings and the captured crash
// artifacts - is filed by where the version sits rather than by which version it is. That is what makes
// the layout need no migration on any machine that exists today, and it is also what makes changing
// which version rests a move of two sets of files past each other.
//
// Without that move the incoming version silently inherits the outgoing one's judgment about a load
// order it has never seen, and the version that actually wrote it reads an empty folder.
//
// Both sides of every move are under one root on one volume, so each step is a rename rather than a
// copy: there is no half-copied profile folder to reason about, and putting a step back is another
// rename. Every area moves inside one plan, so a failure in the last of them puts the first back too.
public static class InstanceStateRelocation
{
    // The moves a swap would make, in the order it would make them. Within each area the outgoing
    // version goes first, because the incoming version's entries land on exactly the paths the outgoing
    // version's entries are leaving; done the other way round every entry both versions hold collides.
    public static IReadOnlyList<StateMove> Plan(
        IReadOnlyList<StateArea> areas, string? outgoingKey, string? incomingKey)
    {
        ArgumentNullException.ThrowIfNull(areas);

        if (string.Equals(outgoingKey, incomingKey, StringComparison.OrdinalIgnoreCase))
            return [];

        var moves = new List<StateMove>();

        foreach (var area in areas)
        {
            if (outgoingKey is not null)
                Collect(moves, area, area.Root, InstanceStateFolder.FolderIn(area.Root, outgoingKey));

            if (incomingKey is not null)
                Collect(moves, area, InstanceStateFolder.FolderIn(area.Root, incomingKey), area.Root);
        }

        return moves;
    }

    // Hands the top of every area to the incoming version and files the outgoing version's own state
    // under its instance folder. A no-op when the incoming version already rests, and safe to run again
    // after one that finished: with nothing left at the paths it reads from, it plans nothing.
    //
    // Nothing is merged and nothing is overwritten. A destination that already exists and is not itself
    // being vacated by an earlier step in the same plan means both versions claim one file, which no
    // correct sequence produces, so the whole swap is refused by name before a single entry moves.
    // Failing that way leaves both versions reading what they read before it was attempted.
    public static IReadOnlyList<StateMove> Swap(
        IReadOnlyList<StateArea> areas, string? outgoingKey, string? incomingKey)
    {
        var planned = Plan(areas, outgoingKey, incomingKey);

        RefuseIfAnythingWouldBeOverwritten(planned);

        var done = new List<StateMove>();

        try
        {
            foreach (var move in planned)
            {
                Move(move.From, move.To);
                done.Add(move);
            }
        }
        catch (Exception ex)
        {
            var stranded = Undo(done);

            if (stranded.Count == 0)
                throw;

            throw new IOException(
                $"Changing which version rests failed, and {stranded.Count} of BEM's own file(s) could not be "
                + $"put back where they were: {string.Join("; ", stranded)}. No save, mod or game file was "
                + "touched; move those back by hand, and BEM rebuilds what it can.",
                ex);
        }

        if (incomingKey is not null)
        {
            foreach (var area in areas)
                PruneIfEmpty(InstanceStateFolder.FolderIn(area.Root, incomingKey), area.Root);
        }

        return done;
    }

    // Every move in the list put back, newest first. Returns the destinations it could not move back
    // rather than throwing, because the caller is already reporting a failure and losing that one to
    // report this would say the wrong thing about what went wrong.
    public static IReadOnlyList<string> Undo(IReadOnlyList<StateMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        var stranded = new List<string>();

        foreach (var move in moves.Reverse())
        {
            try
            {
                Move(move.To, move.From);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stranded.Add(move.To);
            }
        }

        return stranded;
    }

    private static void Collect(List<StateMove> moves, StateArea area, string from, string to)
    {
        foreach (var name in area.EntryNames)
        {
            var source = Path.Combine(from, name);

            if (Exists(source))
                moves.Add(new StateMove(source, Path.Combine(to, name)));
        }
    }

    private static void RefuseIfAnythingWouldBeOverwritten(IReadOnlyList<StateMove> planned)
    {
        var vacated = new HashSet<string>(
            planned.Select(move => Full(move.From)), StringComparer.OrdinalIgnoreCase);

        foreach (var move in planned)
        {
            if (!Exists(move.To) || vacated.Contains(Full(move.To)))
                continue;

            throw new IOException(
                $"'{move.To}' already holds a version's own state, so '{move.From}' was not moved onto it and "
                + "nothing else was moved either. One of the two is a leftover from an interrupted switch and "
                + "has to be dealt with by hand.");
        }
    }

    private static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    // The instance folder the incoming version's state just left. Removed only when it is empty, and
    // never the area's own root: an empty folder is not state, and one left behind reads as a version
    // that has state when it has none.
    private static void PruneIfEmpty(string folder, string areaRoot)
    {
        if (string.Equals(Full(folder), Full(areaRoot), StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tidying is never a reason for a completed swap to be reported as a failure.
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Full(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
