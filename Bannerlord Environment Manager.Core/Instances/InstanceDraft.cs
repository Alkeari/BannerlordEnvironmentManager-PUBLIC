using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// The record a download is named by, before there is an instance to read one from.
//
// A download picks its folder with InstanceRegistry.FolderForDownload before a byte is fetched, and
// the instance that lands in it is written down by InstanceManager.Register once the bytes are
// there. Both name that folder through InstanceFolderName, so they have to agree about every field
// that composes one: the version, the variant and the chosen name. They did not. Two view models
// each built the record by hand, both put the row's label where the version belongs, and neither
// passed the name the user had just typed. A label already spells the DLC out in words, so the
// variant went in twice and the name not at all: a copy of v1.3.15 + War Sails the user named
// "Realm of Thrones" was downloaded into "v1.3.15 + War Sails + WS".
//
// So there is one factory and no second author. The version goes in as a version rather than as a
// label because InstanceFolderName reads RecordedGameVersion first and appends the variant's own
// short names itself, and the chosen name goes in through InstanceNaming exactly as Register puts
// it there. A name too long to store is dropped here for the same reason it is dropped there: the
// folder is composed from what the record will hold, not from what was typed at it.
//
// The purpose is asked for in the same dialog and travels the same way, because it is the first
// thing the folder name states: a testing copy that could only be declared after the download was
// born under a name that was already wrong.
public static class InstanceDraft
{
    public static InstanceRecord ForDownload(
        ModuleVersion version, string branch, string buildId, GameDlcSet variant = default,
        string? chosenName = null, InstancePurpose purpose = InstancePurpose.Unspecified)
    {
        var text = version.ToString();

        return new InstanceRecord(
            string.Empty,
            text,
            branch,
            buildId,
            string.Empty,
            false,
            text,
            text,
            DateTimeOffset.UtcNow,
            null,
            variant,
            InstanceNaming.Read(chosenName).Stored,
            purpose);
    }
}
