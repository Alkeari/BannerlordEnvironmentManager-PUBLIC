using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum ModuleVersionType
{
    Invalid,
    Alpha,
    Beta,
    Development,
    EarlyAccess,
    Release
}

public readonly record struct ModuleVersion(
    ModuleVersionType Type,
    int Major,
    int Minor,
    int Revision,
    int ChangeSet) : IComparable<ModuleVersion>
{
    public static ModuleVersion Empty => new(ModuleVersionType.Invalid, 0, 0, 0, 0);

    public bool IsEmpty => Type == ModuleVersionType.Invalid;

    public static ModuleVersion Parse(string? text) =>
        TryParse(text, out var version) ? version : Empty;

    public static bool TryParse(string? text, out ModuleVersion version)
    {
        version = Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().Trim();
        var type = ModuleVersionType.Release;

        if (char.IsLetter(span[0]))
        {
            type = char.ToLowerInvariant(span[0]) switch
            {
                'a' => ModuleVersionType.Alpha,
                'b' => ModuleVersionType.Beta,
                'd' => ModuleVersionType.Development,
                'e' => ModuleVersionType.EarlyAccess,
                'v' => ModuleVersionType.Release,
                _ => ModuleVersionType.Invalid
            };

            if (type == ModuleVersionType.Invalid)
                return false;

            span = span[1..];
        }

        if (span.IsEmpty)
            return false;

        Span<int> parts = [0, 0, 0, 0];
        var index = 0;

        foreach (var range in span.Split('.'))
        {
            if (index == parts.Length)
                break;

            if (!int.TryParse(span[range], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return false;

            parts[index++] = value;
        }

        if (index == 0)
            return false;

        version = new ModuleVersion(type, parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    public int CompareTo(ModuleVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Revision.CompareTo(other.Revision);
    }

    public static bool operator <(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        if (IsEmpty)
            return string.Empty;

        var prefix = Type switch
        {
            ModuleVersionType.Alpha => "a",
            ModuleVersionType.Beta => "b",
            ModuleVersionType.Development => "d",
            ModuleVersionType.EarlyAccess => "e",
            _ => "v"
        };

        return ChangeSet == 0
            ? $"{prefix}{Major}.{Minor}.{Revision}"
            : $"{prefix}{Major}.{Minor}.{Revision}.{ChangeSet}";
    }
}
