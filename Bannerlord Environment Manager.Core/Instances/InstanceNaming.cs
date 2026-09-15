using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// What became of a name somebody typed. Cleared is not a refusal: an empty box is the way back to
// the version's own name, and without it a rename would only ever go one direction.
public enum InstanceNameOutcome
{
    Named,
    Cleared,
    TooLong
}

public sealed record InstanceNameResult(InstanceNameOutcome Outcome, string Name)
{
    public bool Accepted => Outcome is InstanceNameOutcome.Named or InstanceNameOutcome.Cleared;

    // What the record stores: null once the name is cleared, so the row derives its own name again
    // rather than carrying an empty string that reads as a name nobody can see.
    public string? Stored => Outcome == InstanceNameOutcome.Named ? Name : null;
}

// A name the user chose, checked before it is written down. Nothing here composes a folder, but what
// is written down now reaches one: InstanceFolderName composes the folder from the purpose, this name
// and the version, so a name that stayed behind would leave a folder claiming to be something the
// instance no longer is. That is why a rename moves the folder rather than only the record. The move
// has to carry everything hanging off that folder or it strands something irreplaceable: the saves,
// the mods, the game files, the record's own GameFolder, which is an absolute path inside it, and
// BEM's per-version state, which is filed under the folder name. InstanceFolderAlignment moves the
// whole of it in one ordered pass, or reports the folder and leaves it exactly as it was. Sanitizing
// belongs to that composition too, so a separator or a colon in a name still costs nothing here and
// is not refused: it becomes a hyphen in the folder and stays whole in the row. Length is the only
// real limit, because the name is drawn in one column beside the version it must never crowd out.
public static class InstanceNaming
{
    public const int MaxLength = 64;

    public static InstanceNameResult Read(string? proposed)
    {
        var trimmed = (proposed ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            return new InstanceNameResult(InstanceNameOutcome.Cleared, string.Empty);

        return trimmed.Length > MaxLength
            ? new InstanceNameResult(InstanceNameOutcome.TooLong, trimmed)
            : new InstanceNameResult(InstanceNameOutcome.Named, trimmed);
    }
}

// The name a second copy of a version arrives under unless the user types a better one. Two rows
// both reading "v1.4.8 + War Sails" is the exact confusion a second copy is asked for in spite of,
// so a copy is never offered nothing at all: an unnamed copy composes the same folder as the first
// and is told from it only by the number the collision handling appends.
//
// What is offered carries no version, and that is the whole of the change the folder format forced.
// The folder already ends in the version, so a chosen name repeating it composes
// "(TEST) v1.4.8 + War Sails (2) - v1.4.8 + WS" and says the version twice. A bare disambiguator
// composes "(TEST) Copy 2 - v1.4.8 + WS", which is distinguishable on sight, and it arrives selected
// in the box so a name that means something replaces it whole rather than being typed around it.
//
// The number is the first one no instance already wears, so a third and a fourth copy keep counting.
// The search is bounded by the names in hand rather than by a constant: one more candidate than there
// are taken names cannot all be taken, and that also ends the search if an overlay catalog translates
// the number away and every candidate comes back the same text.
public static class InstanceCopyName
{
    public static string Next(IEnumerable<string> takenNames)
    {
        ArgumentNullException.ThrowIfNull(takenNames);

        var taken = takenNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var copy = 2; copy <= taken.Count + 2; copy++)
        {
            var candidate = Numbered(copy);

            if (!taken.Contains(candidate))
                return candidate;
        }

        return Numbered(2);
    }

    private static string Numbered(int copy) =>
        Strings.Current.Format("Core.Instances.CopyName.Numbered", copy);
}

// What a version is called wherever it is listed. NameOf is the bare name and stands in for the
// derived one, for a caller that writes the version down beside it; NamedVersionOf is the name and
// the version together, for every caller that does not. The marker is appended to whichever of them
// is shown, and it is read from the resting instance id rather than from any text, so a name the
// user typed can never claim to be the resting install. Two installs of one version and variant are
// told apart by the name the second one was given as it arrived, and by that marker when one of them
// is resting.
public static class InstanceLabel
{
    public static string NameOf(InstanceRecord? record) => NameOf(record, record?.DisplayName ?? string.Empty);

    public static string NameOf(InstanceRecord? record, string derived) =>
        record is not null && !string.IsNullOrWhiteSpace(record.ChosenName) ? record.ChosenName! : derived;

    // The chosen name and the version together, in the shape InstanceFolderName.For already composes
    // a folder from: the name leads, the version and its variant come last, and with no name there is
    // nothing for the separator to separate so the version stands alone. A name that replaced the
    // version left the user with "Realm of Thrones" above "Realm of Thrones" in Play's dropdown and
    // nothing saying which game build either one was; two such instances declared the same purpose
    // composed the same label to the byte. The long DLC form is used rather than the folder's short
    // key, because a label is read on its own line and not beside a dozen folders in Explorer.
    public static string NamedVersionOf(InstanceRecord? record, string derived)
    {
        var chosen = record?.ChosenName?.Trim();

        if (string.IsNullOrEmpty(chosen))
            return derived;

        return string.IsNullOrWhiteSpace(derived)
            ? chosen
            : Strings.Current.Format("Core.Instances.Label.NamedVersion", chosen, derived);
    }

    public static bool IsResting(string? instanceId, string? restingInstanceId) =>
        !string.IsNullOrEmpty(instanceId)
        && string.Equals(instanceId, restingInstanceId, StringComparison.Ordinal);

    // The marker a label carries, and there is only ever one of them. Resting is checked first
    // because the resting instance is the user's own game whatever its record declares, and a row
    // reading "(Resting) (Playing)" would say the same thing twice; an undeclared instance carries
    // no marker at all, which is what every instance written before this field existed reads as.
    // The declared purpose is shown here rather than in a column of its own because this suffix is
    // already how a version says what it is wherever it is listed, Play's dropdown included, and a
    // fact that only the Versions table carried would not reach the place a version is chosen.
    public static string For(string name, bool isResting, InstancePurpose purpose) => isResting
        ? Strings.Current.Format("Core.Instances.Label.Resting", name)
        : purpose switch
        {
            InstancePurpose.Testing => Strings.Current.Format("Core.Instances.Label.Testing", name),
            InstancePurpose.Playing => Strings.Current.Format("Core.Instances.Label.Playing", name),
            _ => name
        };

    // The whole label for a record, for a caller with no disk read of its own: the chosen name and
    // the version and DLC the record carries, plus the marker. The Versions page composes the same
    // parts from the DLC it has just found on disk; anywhere else - the Play page's
    // companion-left-behind notice among them - has only the record, and naming a version by
    // DisplayName alone reads "v1.4.8" for every install of v1.4.8 there is.
    public static string LabelOf(InstanceRecord? record, string? restingInstanceId)
    {
        if (record is null)
            return string.Empty;

        var version = ModuleVersion.Parse(record.RecordedGameVersion);

        var derived = version.IsEmpty
            ? record.DisplayName
            : GameVersionLabel.For(version, record.Dlc);

        return For(NamedVersionOf(record, derived), IsResting(record.Id, restingInstanceId), record.Purpose);
    }
}
