using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Modules;

public static class SubModuleXmlWriter
{
    // Every declaration is rewritten to the version the target module declares in its own
    // SubModule.xml, never to one shared version. The official modules do not agree on one:
    // on a War Sails install Native is v1.4.8 while NavalDLC is v1.2.8, so assuming the base
    // game version turns a correct dependency into a broken one inside another author's file.
    public static int SetDependencyVersions(
        string manifestPath,
        IReadOnlyDictionary<ModuleId, ModuleVersion> installedVersions)
    {
        ArgumentNullException.ThrowIfNull(installedVersions);

        var file = XmlTextFile.Read(manifestPath);
        var rewrite = XmlAttributeRewriter.SetAttributes(file.Value, element => ChooseVersion(element, installedVersions));

        // Saving an unchanged file is not free: on the first run it leaves a backup of a file that
        // was never edited.
        if (rewrite.Changed == 0)
            return 0;

        // SubModule.xml belongs to the mod author, so keep the pristine copy and never leave a
        // half-written file where the game expects a manifest.
        AtomicXmlFile.Save(file.ToBytes(rewrite.Xml), manifestPath, writeBackup: true);
        return rewrite.Changed;
    }

    public static int WidenDependencyVersions(
        string manifestPath,
        IReadOnlyDictionary<ModuleId, ModuleVersion> installedVersions)
    {
        ArgumentNullException.ThrowIfNull(installedVersions);

        var file = XmlTextFile.Read(manifestPath);
        var rewrite = XmlAttributeRewriter.SetAttributes(file.Value, element => ChooseWidening(element, installedVersions));

        if (rewrite.Changed == 0)
            return 0;

        AtomicXmlFile.Save(file.ToBytes(rewrite.Xml), manifestPath, writeBackup: true);
        return rewrite.Changed;
    }

    private static (string Name, string Value)? ChooseVersion(
        XmlElementView element,
        IReadOnlyDictionary<ModuleId, ModuleVersion> installedVersions)
    {
        if (IsDeclaration(element, "DependedModules", "DependedModule"))
        {
            if (!element.Attributes.ContainsKey("DependentVersion"))
                return null;

            return TryReadTargetVersion(element, "Id", installedVersions, out var text) ? ("DependentVersion", text) : null;
        }

        if (IsDeclaration(element, "DependedModuleMetadatas", "DependedModuleMetadata"))
        {
            if (!element.Attributes.TryGetValue("version", out var declared) || declared.Contains('*', StringComparison.Ordinal))
                return null;

            return TryReadTargetVersion(element, "id", installedVersions, out var text) ? ("version", text) : null;
        }

        return null;
    }

    private static (string Name, string Value)? ChooseWidening(
        XmlElementView element,
        IReadOnlyDictionary<ModuleId, ModuleVersion> installedVersions)
    {
        if (!IsDeclaration(element, "DependedModuleMetadatas", "DependedModuleMetadata"))
            return null;

        if (!element.Attributes.TryGetValue("version", out var declared) || !declared.Contains('*', StringComparison.Ordinal))
            return null;

        if (!TryReadDeclaredFloor(declared, out var declaredFloor))
            return null;

        var targetId = new ModuleId(element.Attributes.GetValueOrDefault("id", string.Empty));

        if (!installedVersions.TryGetValue(targetId, out var installed) || installed.IsEmpty)
            return null;

        // A declared major that differs from the installed one is a real incompatibility
        // claim by the author, not a stale range, so it stays as written.
        if (declaredFloor.Major != installed.Major)
            return null;

        // Widening is only ever safe as a relaxation of a range the installed version already
        // satisfies. Below the floor the declaration is not stale, it is the user's only warning
        // that the dependency needs updating, and rewriting it silences every launcher.
        if (installed < declaredFloor)
            return null;

        // The other launchers compare the version type as well as the numbers, so the widened
        // range has to carry the installed version's prefix rather than a hardcoded "v".
        return ("version", $"{installed.ToString()[0]}{installed.Major}.*");
    }

    private static bool IsDeclaration(XmlElementView element, string parentName, string elementName) =>
        element.Path.Count == 2
        && string.Equals(element.Path[1], parentName, StringComparison.Ordinal)
        && string.Equals(element.Name, elementName, StringComparison.Ordinal);

    // A target that is not installed has no known version, so its declaration is the only record
    // of what the author asked for and is left exactly as written.
    private static bool TryReadTargetVersion(
        XmlElementView element,
        string idAttributeName,
        IReadOnlyDictionary<ModuleId, ModuleVersion> installedVersions,
        out string text)
    {
        text = string.Empty;

        var targetId = new ModuleId(element.Attributes.GetValueOrDefault(idAttributeName, string.Empty));

        if (!installedVersions.TryGetValue(targetId, out var installed) || installed.IsEmpty)
            return false;

        text = installed.ToString();
        return true;
    }

    private static bool TryReadDeclaredFloor(string text, out ModuleVersion floor) =>
        ModuleVersion.TryParse(text.Replace('*', '0'), out floor);
}
