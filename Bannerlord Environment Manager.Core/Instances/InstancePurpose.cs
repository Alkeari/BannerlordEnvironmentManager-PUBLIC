using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Instances;

// What an instance is for, declared rather than inferred. Until this existed the only answer was a
// guess: the resting instance was gameplay and every other managed instance was assumed to be a
// place a mod build could be installed, which stops being true the moment a managed instance is one
// the user actually plays in.
//
// Unspecified is first so it is the default, and that is the safe end of the scale: an instance that
// has never been declared is not a sanctioned build target. The opposite default would have turned
// every instance already on disk into one the day this shipped.
[JsonConverter(typeof(InstancePurposeJsonConverter))]
public enum InstancePurpose
{
    Unspecified,
    Playing,
    Testing
}

// Serializes as the name rather than a number so instance.json says "Testing" to a build script that
// has never heard of BEM. A value that is missing, null, numeric or simply not one of the three reads
// as Unspecified instead of throwing: a JsonException here costs the whole instance, because
// InstanceRegistry.TryRead answers a bad file with null and the version drops off the list.
public sealed class InstancePurposeJsonConverter : JsonConverter<InstancePurpose>
{
    public override InstancePurpose Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return InstancePurpose.Unspecified;
        }

        return Enum.TryParse<InstancePurpose>(reader.GetString(), ignoreCase: true, out var purpose)
            ? purpose
            : InstancePurpose.Unspecified;
    }

    public override void Write(Utf8JsonWriter writer, InstancePurpose value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
