using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// Running every registered stylesheet means merging every dataset, which measures about thirteen
// seconds on a 232-module install. That is far too long to put in front of a launch, and Diagnostics
// already pays it whenever the user opens Mod Overlaps, so the result is kept here for the preflight
// to reuse.
//
// A launch never waits for this. When nothing has been stored for this exact set of enabled modules
// the preflight says the stylesheets were not examined, which is true and is not the same as clean.
// The set is part of the key because enabling one more mod can change what every stylesheet does.
public static class XmlTransformCache
{
    private static readonly object Gate = new();

    private static string _signature = string.Empty;

    private static IReadOnlyList<XmlTransformOutcome> _transforms = [];

    public static string SignatureFor(IEnumerable<ModuleId> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);

        return string.Join("|", enabled.Select(id => id.Value).OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
    }

    public static void Store(string signature, IReadOnlyList<XmlTransformOutcome> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);

        lock (Gate)
        {
            _signature = signature;
            _transforms = transforms;
        }
    }

    public static IReadOnlyList<XmlTransformOutcome>? For(string signature)
    {
        lock (Gate)
        {
            return _signature.Length > 0 && _signature == signature ? _transforms : null;
        }
    }

    // The emptied-dataset check on its own, so the launch check never depends on Mod Overlaps having been
    // opened (findings catalog G8). Meant for a background task at app start and after the load order
    // changes: it returns the stored result for this exact set when there is one, stores an empty result
    // at once when no enabled module registers a stylesheet, and otherwise runs the merge and stores what
    // every stylesheet did. The merge is the slow part, so a caller that must not wait passes a token.
    public static IReadOnlyList<XmlTransformOutcome> Ensure(
        IReadOnlyList<ModuleEntry> loadOrder, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var signature = SignatureFor(loadOrder.Where(e => e.IsEnabled).Select(e => e.Id));

        if (For(signature) is { } stored)
            return stored;

        cancellation.ThrowIfCancellationRequested();

        IReadOnlyList<XmlTransformOutcome> transforms = XmlDatasetOverlaps.HasStylesheets(loadOrder)
            ? XmlDatasetOverlaps.Inspect(loadOrder).Transforms
            : [];

        cancellation.ThrowIfCancellationRequested();

        Store(signature, transforms);

        return transforms;
    }

    // Only for tests, which would otherwise leak a stored result into one another.
    public static void Clear()
    {
        lock (Gate)
        {
            _signature = string.Empty;
            _transforms = [];
        }
    }
}
