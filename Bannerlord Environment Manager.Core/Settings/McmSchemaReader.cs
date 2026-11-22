using Mono.Cecil;

namespace BannerlordEnvironmentManager.Core.Settings;

// Reads a mod's MCM v5 attribute settings straight from its assemblies, without loading them, so
// the player gets real controls instead of a raw file. Everything is matched by attribute and base
// class NAME, never by a reference to MCM, so a mod built against any MCM v5 build reads the same.
public static class McmSchemaReader
{
    public const string BinFolder = @"bin\Win64_Shipping_Client";

    private static readonly string[] SettingsBases =
    [
        "AttributeGlobalSettings", "AttributePerSaveSettings", "AttributePerCampaignSettings",
        "AttributeSettings", "AttributeEmbeddedSettings", "AttributeBaseSettings",
    ];

    public static IReadOnlyList<McmSettingsSchema> ReadModule(string moduleFolder)
    {
        var bin = Path.Combine(moduleFolder, BinFolder);
        if (!Directory.Exists(bin))
            return [];

        var schemas = new List<McmSettingsSchema>();
        foreach (var dll in Directory.EnumerateFiles(bin, "*.dll"))
            schemas.AddRange(ReadAssembly(dll, moduleFolder));
        return schemas;
    }

    public static IReadOnlyList<McmSettingsSchema> ReadAssembly(string dllPath, string moduleFolder = "")
    {
        try
        {
            var parameters = new ReaderParameters { ReadSymbols = false, InMemory = true, AssemblyResolver = new NullResolver() };
            using var assembly = AssemblyDefinition.ReadAssembly(dllPath, parameters);
            var result = new List<McmSettingsSchema>();

            foreach (var type in assembly.MainModule.GetTypes())
            {
                if (type.IsAbstract || !DerivesFromSettings(type))
                    continue;

                var properties = CollectProperties(type);
                if (properties.Count == 0)
                    continue;

                var id = ConstantGetter(type, "Id") ?? type.Name;
                var folder = ConstantGetter(type, "FolderName") ?? string.Empty;
                var format = ConstantGetter(type, "FormatType") ?? "xml";
                result.Add(new McmSettingsSchema(type.FullName, id, folder, format, moduleFolder, properties));
            }

            return result;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    // MCM prefixes localizable text with "{=key}"; the player sees only the text after it.
    public static string StripLocalization(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var value = text.Trim();
        if (value.StartsWith("{=", StringComparison.Ordinal))
        {
            var close = value.IndexOf('}');
            if (close > 0)
                value = value[(close + 1)..];
        }
        return value.Trim();
    }

    private static bool DerivesFromSettings(TypeDefinition type)
    {
        TypeReference? current = type.BaseType;
        while (current is not null)
        {
            var name = current.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
                name = name[..tick];
            if (SettingsBases.Contains(name, StringComparer.Ordinal))
                return true;
            current = (current as TypeDefinition)?.BaseType;
        }
        return false;
    }

    private static List<McmSettingProperty> CollectProperties(TypeDefinition type)
    {
        var found = new List<McmSettingProperty>();
        for (TypeDefinition? current = type; current is not null; current = current.BaseType as TypeDefinition)
        {
            foreach (var property in current.Properties)
            {
                if (found.Any(known => known.PropertyName == property.Name))
                    continue;
                var parsed = Parse(property);
                if (parsed is not null)
                    found.Add(parsed);
            }
        }

        return [.. found
            .OrderBy(p => p.GroupOrder).ThenBy(p => p.Group, StringComparer.Ordinal)
            .ThenBy(p => p.Order).ThenBy(p => p.PropertyName, StringComparer.Ordinal)];
    }

    private static McmSettingProperty? Parse(PropertyDefinition property)
    {
        CustomAttribute? setting = null;
        McmSettingKind kind = default;
        foreach (var attribute in property.CustomAttributes)
        {
            McmSettingKind? matched = attribute.AttributeType.Name switch
            {
                "SettingPropertyBoolAttribute" => McmSettingKind.Bool,
                "SettingPropertyIntegerAttribute" => McmSettingKind.Integer,
                "SettingPropertyFloatingIntegerAttribute" or "SettingPropertyFloatAttribute" => McmSettingKind.Float,
                "SettingPropertyTextAttribute" => McmSettingKind.Text,
                "SettingPropertyDropdownAttribute" => McmSettingKind.Dropdown,
                _ => null,
            };
            if (matched is not null)
            {
                setting = attribute;
                kind = matched.Value;
                break;
            }
        }
        if (setting is null)
            return null;

        var args = setting.ConstructorArguments;
        var display = args.Count > 0 && args[0].Value is string s ? s : property.Name;
        double? min = null, max = null;
        if (kind is McmSettingKind.Integer or McmSettingKind.Float && args.Count >= 3)
        {
            min = ToDouble(args[1].Value);
            max = ToDouble(args[2].Value);
        }

        var group = string.Empty;
        var groupOrder = 0;
        var groupAttribute = property.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "SettingPropertyGroupAttribute");
        if (groupAttribute is not null)
        {
            if (groupAttribute.ConstructorArguments.Count > 0 && groupAttribute.ConstructorArguments[0].Value is string g)
                group = StripLocalization(g);
            if (groupAttribute.ConstructorArguments.Count > 1)
                groupOrder = (int)(ToDouble(groupAttribute.ConstructorArguments[1].Value) ?? 0);
            foreach (var named in groupAttribute.Properties.Where(n => n.Name == "GroupOrder"))
                groupOrder = (int)(ToDouble(named.Argument.Value) ?? 0);
        }

        string Named(string key) =>
            setting.Properties.Where(n => n.Name == key).Select(n => n.Argument.Value as string).FirstOrDefault() ?? string.Empty;
        var order = setting.Properties.Where(n => n.Name == "Order").Select(n => (int)(ToDouble(n.Argument.Value) ?? 0)).FirstOrDefault();
        var restart = setting.Properties.Any(n => n.Name == "RequireRestart" && n.Argument.Value is true);

        return new McmSettingProperty(
            property.Name, kind, StripLocalization(display), group, groupOrder, order,
            StripLocalization(Named("HintText")), restart, min, max, Named("ValueFormat"));
    }

    private static double? ToDouble(object? value) => value switch
    {
        int i => i,
        float f => f,
        double d => d,
        long l => l,
        _ => null,
    };

    // A property such as `public override string Id => "Mod_v1";` compiles to a getter that is
    // ldstr then ret; anything computed is unreadable here and yields null.
    private static string? ConstantGetter(TypeDefinition type, string name)
    {
        var getter = type.Properties.FirstOrDefault(p => p.Name == name)?.GetMethod;
        if (getter is null || !getter.HasBody)
            return null;
        var literal = getter.Body.Instructions.FirstOrDefault(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr);
        return literal?.Operand as string;
    }

    private sealed class NullResolver : IAssemblyResolver
    {
        public AssemblyDefinition Resolve(AssemblyNameReference name) => throw new AssemblyResolutionException(name);
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => throw new AssemblyResolutionException(name);
        public void Dispose() { }
    }
}
