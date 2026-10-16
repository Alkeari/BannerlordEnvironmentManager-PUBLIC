using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// What an instance's folder is called, and the whole of it:
//
//     (TEST) <chosen name, if there is one> - v1.4.8 + WS
//
// The folder is the first thing the user sees in the games root, and until this existed it said only
// which version was inside: a folder named "v1.4.8" beside another named "v1.4.8 + War Sails (WS)"
// left the user opening both to find out which one was the install to play and which one was a
// throwaway for testing a mod against.
//
// The prefix is carried by a Testing instance and by nothing else. Playing and Unspecified take no
// prefix at all, because the absence of "(TEST)" is what says an instance is not one: a "(PLAY)" tag
// would put a label on the common case and make every folder longer to say less.
//
// The chosen name leads when there is one, and the separator goes with it: with no name there is
// nothing for " - " to separate, so the name is "(TEST) v1.5.2 + WS" rather than "(TEST)  - v1.5.2 + WS".
//
// On every instance but one the version is present and last, in the short DLC form. GameVersionLabel
// writes each DLC's full name for a row that has a whole column to spend; a folder name is read at a
// glance in Explorer beside a dozen others, so it takes the same short names the DLC key is built from.
//
// The resting instance is the exception, and it is named for the role rather than for what is inside
// it: its game folder is Steam's own install, so Steam updates the version underneath it without BEM
// being asked, and a folder called "v1.4.8 + WS" says something that stops being true the moment that
// happens. "Resting Instance" cannot go stale, and the folder the user's own saves and adopted backup
// sit in keeps one name across every game update. The name follows the role, not the instance: hand
// resting status to another version and the outgoing one composes its ordinary name again.
//
// The name is a constant rather than a catalog string. It is a path on disk that other things key on -
// BEM's own per-version state is filed under it - and a localized folder name would rename real
// folders the moment the user changed language.
public static class InstanceFolderName
{
    public const string TestingPrefix = "(TEST) ";

    public const string NameSeparator = " - ";

    public const string RestingFolderName = "Resting Instance";

    public static string For(InstanceRecord record) => For(record, isResting: false);

    public static string For(InstanceRecord record, bool isResting)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (isResting)
            return RestingFolderName;

        var prefix = record.Purpose == InstancePurpose.Testing ? TestingPrefix : string.Empty;

        var chosen = string.IsNullOrWhiteSpace(record.ChosenName)
            ? string.Empty
            : record.ChosenName.Trim() + NameSeparator;

        return Sanitize(prefix + chosen + VersionPart(record), record);
    }

    // The version and its variant, which is the part every instance carries whatever else it does
    // not. RecordedGameVersion is read first and DisplayName is the fallback, exactly as
    // InstanceLabel.LabelOf reads them, so the folder and the row cannot disagree about which
    // version an instance holds.
    public static string VersionPart(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var version = ModuleVersion.Parse(record.RecordedGameVersion);
        var text = version.IsEmpty ? record.DisplayName : version.ToString();

        return record.Dlc.IsEmpty ? text : $"{text} + {string.Join(" + ", record.Dlc.ShortNames)}";
    }

    // The inverse of For, as far as a download's folder goes: the chosen name a folder name carries
    // ahead of this record's own version, empty when it carries none, and null when the folder is not
    // one this version composes at all. It is what says which version a folder full of partial bytes
    // was on its way to being, and the name typed into the download's own box is part of that folder
    // now, so a match on the bare version alone would leave a named copy with nothing to resume.
    //
    // The record's own chosen name is not read here. The caller asking holds a version Steam offers
    // rather than an instance, and the question is which version the folder is for, not who named it.
    public static string? ChosenNameIn(InstanceRecord record, string? folderName)
    {
        ArgumentNullException.ThrowIfNull(record);

        return ChosenNameBefore(VersionPart(record), folderName);
    }

    // The same read against a version part composed some other way, which is how the registry
    // recognizes the folders BEM's own earlier builds made.
    public static string? ChosenNameBefore(string versionPart, string? folderName)
    {
        var version = Clean(versionPart ?? string.Empty);

        if (version.Length == 0 || string.IsNullOrWhiteSpace(folderName))
            return null;

        var bare = WithoutCollisionSuffix(folderName.Trim());

        // The prefix comes off first and is not reported: it says what the instance is for, which is
        // a separate question from what it is called, and a testing instance with no name at all
        // would otherwise read as a folder this version never composed.
        var named = bare.StartsWith(TestingPrefix, StringComparison.Ordinal)
            ? bare[TestingPrefix.Length..]
            : bare;

        if (string.Equals(named, version, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var tail = NameSeparator + version;

        return named.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? named[..^tail.Length] : null;
    }

    // One trailing " (2)" and no more, because that is all the collision series ever appends. A
    // folder wearing two of them was named by something else and is nobody's download.
    private static string WithoutCollisionSuffix(string folderName)
    {
        if (!folderName.EndsWith(')'))
            return folderName;

        var opened = folderName.LastIndexOf(" (", StringComparison.Ordinal);

        if (opened < 0)
            return folderName;

        var digits = folderName[(opened + 2)..^1];

        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? folderName[..opened] : folderName;
    }

    // A chosen name is free text and a display name is free text, so a separator in either would make
    // a write land one level down where the registry's own enumeration never looks. Invalid characters
    // become hyphens so the folder still reads like the name; dots come off both ends, not just the
    // trailing one Windows refuses, because the enumeration skips every folder whose name starts with
    // one. A name with nothing left after all that falls back to the slug.
    private static string Sanitize(string composed, InstanceRecord record)
    {
        var cleaned = Clean(composed);

        return cleaned.Any(char.IsLetterOrDigit)
            ? cleaned
            : InstanceRegistry.SlugFor(record.DisplayName, record.Branch);
    }

    private static string Clean(string composed) =>
        new string([.. composed.Select(c => Invalid.Contains(c) ? '-' : c)])
            .Trim()
            .Trim('.', ' ');

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();
}
