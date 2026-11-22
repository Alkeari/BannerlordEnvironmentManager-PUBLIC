using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One DLC BEM knows how to build into an existing game folder. ModuleFolder is the folder name the
// DLC ships under inside the game's own Modules folder (confirmed against a real Steam install for
// War Sails: Modules\NavalDLC\SubModule.xml), which is what GameDlcDetector matches against disk.
public sealed record GameDlcInfo(int AppId, string DisplayName, string ShortName, string ModuleFolder);

// The one place a DLC's identity is written down. Bannerlord will ship more DLC than War Sails, so
// the next one is a row here, not a new special case scattered through the instance and download
// code.
public static class GameDlc
{
    public const int BaseAppId = 261550;

    public static readonly IReadOnlyList<GameDlcInfo> Known =
    [
        new GameDlcInfo(2927200, "War Sails", "WS", "NavalDLC")
    ];

    public static GameDlcInfo? ById(int appId) => Known.FirstOrDefault(dlc => dlc.AppId == appId);

    public static GameDlcInfo? ByName(string displayName) =>
        Known.FirstOrDefault(dlc => string.Equals(dlc.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    // Every DLC BEM knows about that a given instance does not carry yet, in the same order Known
    // lists them. This is what decides whether an instance offers an "add" action at all, and for
    // which DLC: an instance already carrying everything Known has gets none.
    public static IReadOnlyList<GameDlcInfo> MissingFrom(GameDlcSet variant) =>
        [.. Known.Where(dlc => !variant.AppIds.Contains(dlc.AppId))];
}

// The set of DLC app ids an instance was built with; empty for a base install. Sorted on
// construction so two instances built from the same DLC in a different order compare, hash and name
// their folder identically, rather than folder naming depending on the order a download happened to
// run in.
[JsonConverter(typeof(GameDlcSetJsonConverter))]
public readonly struct GameDlcSet : IEquatable<GameDlcSet>
{
    public static readonly GameDlcSet Empty = default;

    private readonly int[]? appIds;

    public GameDlcSet(IEnumerable<int> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        this.appIds = [.. appIds.Distinct().OrderBy(id => id)];
    }

    // The default struct value has a null backing field: an InstanceRecord whose JSON never had a
    // Dlc property deserializes to default(GameDlcSet), and that has to read as empty rather than
    // throw or need a migration.
    public IReadOnlyList<int> AppIds => appIds ?? [];

    public bool IsEmpty => AppIds.Count == 0;

    // A stable, order-independent key safe to fold into a folder name: empty for the base game,
    // otherwise the known short names (or the raw app id for a DLC BEM does not recognize yet)
    // joined in app id order.
    public string FolderKey => string.Join("+", AppIds.Select(id => GameDlc.ById(id)?.ShortName ?? id.ToString()));

    public IReadOnlyList<string> DisplayNames =>
        [.. AppIds.Select(id => GameDlc.ById(id)?.DisplayName ?? id.ToString())];

    // The same set in the short form a folder name is read at a glance in: "WS" rather than "War
    // Sails". Kept apart from FolderKey, which joins them with no spaces because it is one token
    // folded into an Id as well as into a name.
    public IReadOnlyList<string> ShortNames =>
        [.. AppIds.Select(id => GameDlc.ById(id)?.ShortName ?? id.ToString())];

    public bool Equals(GameDlcSet other) => AppIds.SequenceEqual(other.AppIds);

    public override bool Equals(object? obj) => obj is GameDlcSet other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in AppIds)
            hash.Add(id);

        return hash.ToHashCode();
    }

    public static bool operator ==(GameDlcSet left, GameDlcSet right) => left.Equals(right);

    public static bool operator !=(GameDlcSet left, GameDlcSet right) => !left.Equals(right);
}

// Serializes as a plain array of app ids so instance.json stays readable, and a record with no Dlc
// property at all (every instance BEM built before this field existed) deserializes to
// GameDlcSet.Empty rather than failing.
public sealed class GameDlcSetJsonConverter : JsonConverter<GameDlcSet>
{
    public override GameDlcSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<int[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, GameDlcSet value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.AppIds, options);
}
