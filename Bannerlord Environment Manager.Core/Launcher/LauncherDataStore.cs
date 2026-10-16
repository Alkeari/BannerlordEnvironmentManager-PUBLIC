using System.Xml;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Launcher;

public sealed record LauncherModEntry(ModuleId Id, bool IsSelected, string LastKnownVersion = "");

// No side-by-side .bem.bak is written for this file: nothing in BEM could ever list or restore it, and
// the backup store already keeps a dated copy of every distinct state, which the Play tab can
// read back.
public sealed class LauncherDataStore(string filePath, LoadOrderBackupStore? backups = null)
{
    public string FilePath { get; } = filePath;

    public bool Exists => File.Exists(FilePath);

    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) => Path.Combine(
        (dataRoot ?? InstanceDataRoot.ForMachine()).Configs,
        "LauncherData.xml");

    public IReadOnlyList<LauncherModEntry> Read()
    {
        if (!Exists)
            return [];

        var modDatas = Load().Root
            ?.Element("SingleplayerData")
            ?.Element("ModDatas");

        if (modDatas is null)
            return [];

        return [.. modDatas.Elements("UserModData")
            .Select(e => new LauncherModEntry(
                new ModuleId(e.Element("Id")?.Value?.Trim() ?? string.Empty),
                string.Equals(e.Element("IsSelected")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            .Where(e => !e.Id.IsEmpty)];
    }

    // The reason is what gets a dated backup taken. Null means none, which is what continuous editing
    // needs: the load order is written on every change now, and capturing a copy per keystroke would
    // push the state worth restoring out of the store within a minute of ordinary work. The caller
    // takes one at the start of a burst of edits instead.
    //
    // The default reason is a separate overload rather than a default parameter value, because the
    // catalog lookup it needs is not a compile-time constant.
    public void Write(IReadOnlyList<LauncherModEntry> entries) =>
        Write(entries, Strings.Current["Core.Launcher.DataStore.BackupReason.BeforeSave"]);

    public void Write(IReadOnlyList<LauncherModEntry> entries, string? backupReason)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (backupReason is not null)
            backups?.Capture(FilePath, backupReason);

        var document = Exists ? Load() : CreateDocument();
        var root = document.Root ?? throw new InvalidDataException($"'{FilePath}' has no root element.");

        var singleplayer = root.Element("SingleplayerData");
        if (singleplayer is null)
        {
            singleplayer = new XElement("SingleplayerData");
            root.Add(singleplayer);
        }

        var modDatas = singleplayer.Element("ModDatas");
        if (modDatas is null)
        {
            modDatas = new XElement("ModDatas");
            singleplayer.Add(modDatas);
        }

        // Vanilla treats a LastKnownVersion that differs from the installed module version as "reset
        // this module to its default selection" (UserModData.IsUpdatedToBeDefault). Reusing each existing
        // element instead of rebuilding it keeps LastKnownVersion and any other field BEM does not
        // understand, so a disabled default module is not silently re-enabled on the next launch.
        var existing = modDatas.Elements("UserModData")
            .GroupBy(e => new ModuleId(e.Element("Id")?.Value?.Trim() ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.First());

        var ordered = new List<XElement>(entries.Count);
        var written = new HashSet<ModuleId>();

        foreach (var entry in entries)
        {
            // A duplicate id in entries would otherwise remove the same reused XElement twice.
            // Match the file-side duplicate handling above: the first occurrence wins.
            if (!written.Add(entry.Id))
                continue;

            XElement element;
            if (existing.TryGetValue(entry.Id, out var found))
            {
                element = found;
                element.Remove();
                SetChild(element, "IsSelected", entry.IsSelected ? "true" : "false");
            }
            else
            {
                // Vanilla serializes UserModData as an ordered sequence, so Id, LastKnownVersion, IsSelected
                // is the only order the game's XmlSerializer accepts. An empty LastKnownVersion would read
                // as a version mismatch, so a module with no known version gets no element at all.
                element = new XElement("UserModData", new XElement("Id", entry.Id.Value));

                if (!string.IsNullOrEmpty(entry.LastKnownVersion))
                    element.Add(new XElement("LastKnownVersion", entry.LastKnownVersion));

                element.Add(new XElement("IsSelected", entry.IsSelected ? "true" : "false"));
            }

            ordered.Add(element);
        }

        // Only the entries are rewritten. Removing every node would take a comment or an element BEM
        // does not model down with them, and this file is not BEM's to prune.
        foreach (var element in modDatas.Elements("UserModData").ToList())
            element.Remove();

        modDatas.Add(ordered);

        AtomicXmlFile.Save(document, FilePath, writeBackup: false);
    }

    private static void SetChild(XElement parent, string name, string value)
    {
        var child = parent.Element(name);

        if (child is null)
            parent.Add(new XElement(name, value));
        else
            child.Value = value;
    }

    // A game crash can truncate LauncherData.xml. Callers already treat a bad file as InvalidDataException,
    // so the XLinq exception must not escape and take the app down with it.
    private XDocument Load()
    {
        try
        {
            return XDocument.Load(FilePath);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"'{FilePath}' is not valid XML: {ex.Message}", ex);
        }
    }

    private static XDocument CreateDocument() => new(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("UserData",
            new XElement("GameType", "Singleplayer"),
            new XElement("SingleplayerData", new XElement("ModDatas"))));
}
