using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Modules;

public sealed record ModuleVersionRange
{
    private const int Wildcard = -1;

    private readonly int[]? pattern;
    private readonly ModuleVersion minimum;

    private ModuleVersionRange(string text, int[]? pattern, ModuleVersion minimum)
    {
        Text = text;
        this.pattern = pattern;
        this.minimum = minimum;
    }

    public static ModuleVersionRange Any { get; } = new(string.Empty, null, ModuleVersion.Empty);

    public string Text { get; }

    public bool IsAny => pattern is null && minimum.IsEmpty;

    public static ModuleVersionRange Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Any;

        var trimmed = text.Trim();

        if (!trimmed.Contains('*', StringComparison.Ordinal))
        {
            return ModuleVersion.TryParse(trimmed, out var version)
                ? new ModuleVersionRange(trimmed, null, version)
                : Any;
        }

        var body = char.IsLetter(trimmed[0]) ? trimmed[1..] : trimmed;
        var components = body.Split('.');
        var parsed = new int[3];

        for (var i = 0; i < parsed.Length; i++)
        {
            if (i >= components.Length)
            {
                parsed[i] = Wildcard;
                continue;
            }

            var component = components[i];

            if (component == "*")
                parsed[i] = Wildcard;
            else if (int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                parsed[i] = value;
            else
                return Any;
        }

        return new ModuleVersionRange(trimmed, parsed, ModuleVersion.Empty);
    }

    public bool Matches(ModuleVersion version)
    {
        if (IsAny)
            return true;

        if (version.IsEmpty)
            return false;

        if (pattern is null)
            return version >= minimum;

        Span<int> actual = [version.Major, version.Minor, version.Revision];

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != Wildcard && pattern[i] != actual[i])
                return false;
        }

        return true;
    }

    public override string ToString() => IsAny ? "*" : Text;
}
