using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The declared ordering edges of one list of modules, and nothing else. A load order is valid exactly
// when it is a linearisation of this graph, so two modules that cannot reach each other through it are
// tied: either order of that pair satisfies every declared constraint.
public sealed class ConstraintGraph
{
    private const int Bits = 64;

    private readonly int[][] successors;
    private readonly int[][] predecessors;
    private readonly int[] inDegree;
    private readonly int words;

    private ulong[]? reachable;

    private ConstraintGraph(int[][] successors, int[][] predecessors, int[] inDegree)
    {
        this.successors = successors;
        this.predecessors = predecessors;
        this.inDegree = inDegree;
        words = (successors.Length + Bits - 1) / Bits;
    }

    public int Count => successors.Length;

    public static ConstraintGraph For(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var indexOf = new Dictionary<ModuleId, int>();

        for (var i = 0; i < entries.Count; i++)
            indexOf[entries[i].Id] = i;

        var edges = new HashSet<int>[entries.Count];
        var incoming = new List<int>[entries.Count];
        var inDegree = new int[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            edges[i] = [];
            incoming[i] = [];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var dependency in entries[i].Dependencies)
            {
                if (dependency.IsIncompatible || dependency.Order == DependencyOrder.None)
                    continue;

                if (!indexOf.TryGetValue(dependency.TargetId, out var target) || target == i)
                    continue;

                var (from, to) = dependency.Order == DependencyOrder.LoadBeforeThis
                    ? (target, i)
                    : (i, target);

                if (edges[from].Add(to))
                {
                    inDegree[to]++;
                    incoming[to].Add(from);
                }
            }
        }

        return new ConstraintGraph(
            [.. edges.Select(set => set.ToArray())],
            [.. incoming.Select(list => list.ToArray())],
            inDegree);
    }

    public IReadOnlyList<int> SuccessorsOf(int index) => successors[index];

    public IReadOnlyList<int> PredecessorsOf(int index) => predecessors[index];

    // A fresh copy every call: a topological pass counts these down to zero and must not consume the
    // graph, which the tie analysis reads afterwards.
    public int[] InDegrees() => [.. inDegree];

    public bool Constrains(int from, int to)
    {
        if (from == to)
            return false;

        var closure = Reachability();

        return (closure[(from * words) + (to / Bits)] & (1UL << (to % Bits))) != 0;
    }

    public bool AreTied(int a, int b) => a != b && !Constrains(a, b) && !Constrains(b, a);

    // How many modules tie with at least one other module. This is the number that says how much of a
    // load order the declared constraints actually decide.
    public int TiedModuleCount()
    {
        var tied = 0;

        for (var a = 0; a < Count; a++)
        {
            for (var b = 0; b < Count; b++)
            {
                if (AreTied(a, b))
                {
                    tied++;
                    break;
                }
            }
        }

        return tied;
    }

    // The strongly connected components of a subset, in the reverse topological order of the
    // condensation that Tarjan's algorithm produces, so walking the result backwards places a component
    // only after everything it must load after.
    //
    // The distinction this exists for: a component of one is a module that is not in a loop, even when
    // it could not be ordered because something upstream of it is. Naming it as part of the loop would
    // put an innocent module in front of the user as the cause.
    public IReadOnlyList<IReadOnlyList<int>> ComponentsOf(IReadOnlyList<int> among)
    {
        ArgumentNullException.ThrowIfNull(among);

        var inSubset = new bool[Count];

        foreach (var node in among)
            inSubset[node] = true;

        var order = new int[Count];
        var low = new int[Count];
        var onStack = new bool[Count];

        Array.Fill(order, -1);

        var open = new Stack<int>();
        var work = new Stack<(int Node, int Child)>();
        var components = new List<IReadOnlyList<int>>();
        var next = 0;

        foreach (var start in among)
        {
            if (order[start] >= 0)
                continue;

            order[start] = low[start] = next++;
            open.Push(start);
            onStack[start] = true;
            work.Push((start, 0));

            while (work.Count > 0)
            {
                var (node, child) = work.Pop();
                var descended = false;

                for (var i = child; i < successors[node].Length; i++)
                {
                    var successor = successors[node][i];

                    if (!inSubset[successor])
                        continue;

                    if (order[successor] < 0)
                    {
                        work.Push((node, i + 1));
                        order[successor] = low[successor] = next++;
                        open.Push(successor);
                        onStack[successor] = true;
                        work.Push((successor, 0));
                        descended = true;
                        break;
                    }

                    if (onStack[successor])
                        low[node] = Math.Min(low[node], order[successor]);
                }

                if (descended)
                    continue;

                if (low[node] == order[node])
                {
                    var component = new List<int>();
                    int member;

                    do
                    {
                        member = open.Pop();
                        onStack[member] = false;
                        component.Add(member);
                    }
                    while (member != node);

                    component.Sort();
                    components.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        return components;
    }

    public IEnumerable<(int A, int B)> TiedPairs()
    {
        for (var a = 0; a < Count; a++)
        {
            for (var b = a + 1; b < Count; b++)
            {
                if (AreTied(a, b))
                    yield return (a, b);
            }
        }
    }

    // Reverse topological order, so every successor's own closure is already final when a node unions
    // it in. Nodes caught in a cycle never come off the queue and keep an empty closure; the sort
    // reports those as cycles, and a tie inside an unorderable loop is moot.
    private ulong[] Reachability()
    {
        if (reachable is not null)
            return reachable;

        var closure = new ulong[Count * words];
        var remaining = InDegrees();
        var order = new List<int>(Count);
        var ready = new Queue<int>();

        for (var i = 0; i < Count; i++)
        {
            if (remaining[i] == 0)
                ready.Enqueue(i);
        }

        while (ready.TryDequeue(out var current))
        {
            order.Add(current);

            foreach (var successor in successors[current])
            {
                if (--remaining[successor] == 0)
                    ready.Enqueue(successor);
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];

            foreach (var successor in successors[node])
            {
                closure[(node * words) + (successor / Bits)] |= 1UL << (successor % Bits);

                for (var word = 0; word < words; word++)
                    closure[(node * words) + word] |= closure[(successor * words) + word];
            }
        }

        reachable = closure;

        return closure;
    }
}
