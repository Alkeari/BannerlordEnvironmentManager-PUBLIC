namespace BannerlordEnvironmentManager.Core.Settings;

public enum McmSettingKind
{
    Bool,
    Integer,
    Float,
    Text,
    Dropdown,
}

public sealed record McmSettingProperty(
    string PropertyName,
    McmSettingKind Kind,
    string DisplayName,
    string Group,
    int GroupOrder,
    int Order,
    string HintText,
    bool RequireRestart,
    double? Minimum,
    double? Maximum,
    string ValueFormat);

public sealed record McmSettingsSchema(
    string ClassName,
    string Id,
    string FolderName,
    string FormatType,
    string ModuleFolder,
    IReadOnlyList<McmSettingProperty> Properties);
