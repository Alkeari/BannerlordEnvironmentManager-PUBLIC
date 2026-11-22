namespace BannerlordEnvironmentManager.Core.Game;

public readonly record struct RunningProcess(int Id, string Name);

public readonly record struct DetectedLaunch(int Id, string Name, bool StartedByBem);

/// <summary>
/// Sees the game start, whoever started it, from successive lists of the processes that are running.
/// The first list only records what is already running, since a game open before BEM began watching was
/// not launched while it watched. A process id seen again is the same launch; one that vanishes and
/// returns under a new id is a new launch.
/// </summary>
public sealed class GameLaunchDetector
{
    private readonly HashSet<int> known = [];
    private readonly HashSet<int> ownedByBem = [];
    private bool seeded;

    /// <summary>True for the processes a Bannerlord run starts as: the game and each launcher.</summary>
    public static bool IsGameProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        return bare.StartsWith("Bannerlord", StringComparison.OrdinalIgnoreCase)
            || bare.StartsWith("TaleWorlds.MountAndBlade.Launcher", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The game processes BEM itself starts are recorded here so they are never offered as foreign.</summary>
    public void MarkStartedByBem(int processId)
    {
        lock (known)
            ownedByBem.Add(processId);
    }

    /// <summary>The Bannerlord processes in the list that were not in an earlier one.</summary>
    public IReadOnlyList<DetectedLaunch> Poll(IEnumerable<RunningProcess> running)
    {
        ArgumentNullException.ThrowIfNull(running);

        lock (known)
        {
            var current = running.Where(p => IsGameProcess(p.Name)).ToList();
            var fresh = new List<DetectedLaunch>();

            foreach (var process in current)
            {
                if (known.Add(process.Id) && seeded)
                    fresh.Add(new DetectedLaunch(process.Id, process.Name, ownedByBem.Contains(process.Id)));
            }

            var alive = current.Select(p => p.Id).ToHashSet();
            known.RemoveWhere(id => !alive.Contains(id));
            ownedByBem.RemoveWhere(id => !alive.Contains(id));
            seeded = true;

            return fresh;
        }
    }
}
