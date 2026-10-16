using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// Refusal is its own outcome, not a failure: nothing was wrong with the paths, the repair simply was
// not allowed to touch them while someone else held them.
public sealed record RepairReport(
    IReadOnlyList<string> Repaired,
    IReadOnlyList<string> Failed,
    string? Refusal = null)
{
    public bool AnythingRepaired => Repaired.Count > 0;

    public bool WasRefused => Refusal is not null;

    public string Describe() => this switch
    {
        { Refusal: { } reason } => reason,
        { Failed.Count: > 0 } => Strings.Current.Plural("Core.Instances.Repair.Failed", Failed.Count),
        { Repaired.Count: > 0 } => Strings.Current.Plural("Core.Instances.Repair.Repaired", Repaired.Count),
        _ => Strings.Current["Core.Instances.Repair.Nothing"]
    };
}

// The second line of defense behind ActivationSession's own restore. A power cut or a killed process
// leaves junctions in place, and the next BEM start puts them back rather than letting the next launch
// write a version's data into another version's store.
//
// A junction is not the only wreckage worth looking for. A restore that died partway can leave a
// canonical path missing or empty with the resting instance's data still in its store, which no
// junction check would ever notice, so that state is repaired here too.
public sealed class StartupRepair(CanonicalPathSet paths, string restingInstanceFolder, Action<string>? log = null)
{
    // The same lock a launch holds, for the same reason. A junction is live for the length of a launch
    // and nothing holds a handle on the junction itself, so a second BEM window starting mid-launch
    // would find every path in need of repair and rename the resting data into place under a running
    // game. Repairing is what this type is for, so it waits for the launch rather than racing it.
    public RepairReport Run()
    {
        ActivationLock activation;

        try
        {
            activation = ActivationLock.Acquire(paths);
        }
        catch (IOException ex)
        {
            return new RepairReport([], [], ex.Message);
        }

        using (activation)
        {
            var repaired = new List<string>();
            var failed = new List<string>();

            foreach (var pair in paths.Pairs)
            {
                if (!CanonicalRestore.NeedsRestore(pair, restingInstanceFolder))
                    continue;

                try
                {
                    CanonicalRestore.Restore(pair, restingInstanceFolder);
                    repaired.Add(pair.LivePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Startup repair could not put '{pair.LivePath}' back: {ex}");
                    failed.Add(pair.LivePath);
                }
            }

            return new RepairReport(repaired, failed);
        }
    }
}
