namespace BannerlordEnvironmentManager.Core.GameSettings;

// The bytes of a settings file BEM wrote for an instance that had none, and the values it put in it.
public sealed record GameSettingsSeedFile(byte[] Bytes, IReadOnlyDictionary<string, string> Values);

// Writing a settings file for an instance that has never had one.
//
// Every other route through sharing edits a file the game wrote. This one does not: an instance BEM
// has downloaded and never launched has no Configs folder at all, so there is nothing to freeze,
// nothing to project onto and nothing to capture. Doing nothing leaves that instance on the defaults
// the game writes at its first launch, which is exactly when a player most wants their settings to
// follow them, so the shared set is written straight into the file instead.
//
// ---------------------------------------------------------------------------------------------
// THE ASSUMPTION THIS RESTS ON, AND THE ONLY PLACE IT IS WRITTEN DOWN
//
// Bannerlord accepts a settings file that declares fewer keys than the build knows about, and fills
// every key the file leaves out from its own defaults rather than refusing the file, crashing, or
// resetting the keys it did read.
//
// It is an assumption, not a measurement. Nobody has launched a build against a short file. It
// matters because the shared set is a superset across versions and an older build's real file is a
// subset of it: the intersection rule in GameSettingsMerge.Project is what keeps a newer build's
// keys off an older instance, and that rule cannot run here, because there is no instance file to
// intersect against. A seed therefore carries every shared key the exclusions do not remove,
// including keys the build has never heard of, and on the first run it is also missing whatever keys
// that build has and the shared set has not met yet.
//
// If the assumption turns out to be false, the fallback is to stop writing a file at all and instead
// let the first launch write the build's own defaults, then apply and capture from the run after it:
// that is one edit, deleting the seed branch in GameSettingsSync.Apply, and it puts the behavior back
// to the GameSettingsOutcome.NotPresent it had before. The weaker fallback, if a short file is
// accepted but an unknown key is not, is to seed only the keys the shared set learned from a build
// whose version matches the instance's.
// ---------------------------------------------------------------------------------------------
public static class GameSettingsSeed
{
    // Null when there is nothing honest to write: a file kind BEM cannot compose (see
    // GameSettingsFiles.FormatOf) or a shared set with nothing in it for that kind.
    public static GameSettingsSeedFile? Build(
        GameSettingsFileKind kind,
        IReadOnlyDictionary<string, string> shared)
    {
        ArgumentNullException.ThrowIfNull(shared);

        if (GameSettingsFiles.FormatOf(kind) is not { } format)
            return null;

        // The same rule the projection and the capture keep: a key that is an instance's own truth
        // is never carried between instances, and a file BEM creates has even less business
        // declaring one, since the instance has not yet had a first run, a clean exit or a save.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in shared)
        {
            if (!GameSettingsExclusions.IsExcluded(kind, key))
                values[key] = value;
        }

        if (values.Count == 0)
            return null;

        // In the order the shared set holds them, which is the order the instance it was captured
        // from wrote them. Nothing depends on it: the game rewrites the whole file when it exits.
        var text = string.Concat(values.Select(pair => pair.Key + format.Separator + pair.Value + format.LineEnding));

        if (!format.TrailingLineEnding)
            text = text[..^format.LineEnding.Length];

        return new GameSettingsSeedFile(ConfigText.Create(format.HasByteOrderMark).ToBytes(text), values);
    }
}
