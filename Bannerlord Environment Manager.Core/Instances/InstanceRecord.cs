namespace BannerlordEnvironmentManager.Core.Instances;

// GameFolderIsReferenced separates the two kinds of instance: a referenced one points at a folder
// somebody else owns, which in practice means Steam, and its version can change without BEM being
// asked. A managed one owns its Game folder and only changes when BEM downloads into it.
//
// DataGameVersion is the version this store's saves and settings were last used with. It is kept
// apart from RecordedGameVersion so drift is a comparison rather than a guess.
//
// Dlc defaults to GameDlcSet.Empty so a record whose JSON was written before this field existed
// reads as base-only, which is true of every instance BEM has built so far: no migration, no
// rewrite on read, no prompt.
//
// Purpose is what the instance is declared to be for, and it defaults to InstancePurpose.Unspecified
// exactly as Dlc defaults to empty: a record written before this field existed reads as undeclared,
// with no migration and nothing to answer at startup. Undeclared is deliberately the safe end,
// because the field's whole point is telling a mod's build tool which instances it may build into,
// and a default of Testing would have sanctioned every instance already on disk.
//
// ChosenName is the name the user gave this instance, and null until they give one. It is kept apart
// from DisplayName because DisplayName is what named the folder when the instance was written down,
// and the folder is the instance's identity: a rename that changed DisplayName would let a later
// path derivation name a folder that does not exist. Null rather than empty so a record written
// before the field existed and one whose name was cleared read alike.
public sealed record InstanceRecord(
    string Id,
    string DisplayName,
    string Branch,
    string BuildId,
    string GameFolder,
    bool GameFolderIsReferenced,
    string RecordedGameVersion,
    string DataGameVersion,
    DateTimeOffset Created,
    DateTimeOffset? LastLaunched,
    GameDlcSet Dlc = default,
    string? ChosenName = null,
    InstancePurpose Purpose = InstancePurpose.Unspecified);
