namespace BannerlordEnvironmentManager.Core.Modules;

public readonly record struct ModuleId(string Value)
{
    public static ModuleId None => new(string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public bool Equals(ModuleId other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value ?? string.Empty;
}
