using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.DryRun;

// A guided bisect is not finished in one sitting: it is a list of tested configurations and their
// outcomes, and the user plays between them. It has to survive closing BEM.
public sealed class BisectionStore(string root)
{
    private const string FileExtension = ".bisect.json";

    // Per version. A saved search is a list of that version's module ids and what each configuration
    // did, so one machine-wide folder offered "Resume saved search" a search saved against another
    // version: resuming it writes module ids that version does not have into its LauncherData.xml, and
    // every launch inside it measures nothing. The resting version keeps the folder it has always read.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "bisect");

    public string Root => root;

    public void Save(BisectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(root);

        var path = PathOf(snapshot.Id);
        var temporary = path + ".partial";

        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    public BisectionSnapshot? Load(string id)
    {
        try
        {
            var path = PathOf(id);

            return File.Exists(path)
                ? JsonSerializer.Deserialize<BisectionSnapshot>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException)
        {
            return null;
        }
    }

    public IReadOnlyList<BisectionSnapshot> List()
    {
        if (!Directory.Exists(root))
            return [];

        var snapshots = new List<BisectionSnapshot>();

        foreach (var file in Directory.EnumerateFiles(root, "*" + FileExtension))
        {
            try
            {
                if (JsonSerializer.Deserialize<BisectionSnapshot>(File.ReadAllText(file)) is { } snapshot)
                    snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file that will not read is not a reason to hide the sessions that will.
            }
        }

        return [.. snapshots.OrderByDescending(s => s.UpdatedUtc)];
    }

    // A guided search needs the user to leave BEM and play, so BEM being closed or crashing between
    // experiments is ordinary rather than exceptional. A session still flagged as mid-experiment is
    // one BEM did not come back from, and the backup it names is the load order the user had before
    // the search touched anything. Without this the pre-experiment state is reachable from nowhere.
    public BisectionSnapshot? FindInterrupted() => ListInterrupted().FirstOrDefault();

    // Every one of them, newest first, because each carries its own pre-experiment backup and the
    // older ones are reachable from nowhere else. Offering only the newest and saying nothing about
    // the rest would leave the others' load orders stranded with no sign that they exist.
    public IReadOnlyList<BisectionSnapshot> ListInterrupted() =>
    [
        .. List().Where(snapshot =>
            snapshot.ExperimentInFlight
            && snapshot.SafetyBackupPath.Length > 0
            && File.Exists(snapshot.SafetyBackupPath))
    ];

    // Clears the flag without touching the search itself, so dismissing the offer or taking it up
    // never costs the user the session they can still pick up again.
    public bool ClearInterrupted(string id)
    {
        if (Load(id) is not { } snapshot)
            return false;

        try
        {
            Save(snapshot with { ExperimentInFlight = false });

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Both directions: a search the user has given up on has to be removable.
    public bool Delete(string id)
    {
        try
        {
            var path = PathOf(id);

            if (!File.Exists(path))
                return false;

            File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string PathOf(string id) => Path.Combine(root, Sanitize(id) + FileExtension);

    private static string Sanitize(string id)
    {
        var buffer = new StringBuilder(id.Length);

        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                buffer.Append(c);
        }

        return buffer.Length == 0 ? "session" : buffer.ToString();
    }
}
