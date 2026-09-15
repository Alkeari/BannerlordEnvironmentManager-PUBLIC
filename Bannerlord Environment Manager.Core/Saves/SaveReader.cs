using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

// Exists is what separates "you have no saves" from "BEM could not look", which mean opposite things
// to whoever reads the page.
public sealed record SaveFolderRead(
    string Folder,
    bool Exists,
    IReadOnlyList<SaveFile> Saves,
    string Error = "");

// Reads only the metadata header a .sav file opens with: a 4-byte little-endian length, then that many
// bytes of UTF-8 JSON shaped {"List":{...}}. Everything after it is a Deflate stream holding the
// campaign itself, and BEM never touches it: nothing on the Saves page needs it, and decompressing a
// 6 MB body per row to read a character name would be absurd.
public static class SaveReader
{
    private const string SaveExtension = ".sav";
    private const string ModulesKey = "Modules";
    private const string ModuleVersionPrefix = "Module_";

    // The folder name is no longer built here: which store the saves come from depends on the instance
    // being shown, and only the caller knows that.
    public static string DefaultFolder(InstanceDataRoot? dataRoot = null) =>
        (dataRoot ?? InstanceDataRoot.ForMachine()).GameSaves;

    public static SaveFolderRead ReadFolder() => ReadFolder(DefaultFolder());

    public static SaveFolderRead ReadFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new SaveFolderRead(string.Empty, false, []);

        if (!Directory.Exists(folder))
            return new SaveFolderRead(folder, false, []);

        string[] files;

        try
        {
            files = Directory.GetFiles(folder, "*" + SaveExtension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveFolderRead(folder, true, [], $"{folder} could not be read: {ex.Message}");
        }

        var read = files.Select(file => (Save: Read(file), Written: WrittenAt(file)));

        return new SaveFolderRead(
            folder,
            true,
            [.. read
                .OrderByDescending(r => r.Save.Created ?? r.Written)
                .ThenBy(r => r.Save.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Save)]);
    }

    public static SaveFile Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            var length = new byte[4];

            if (stream.Read(length, 0, length.Length) != length.Length)
            {
                return SaveFile.Unreadable(path,
                    $"The file is {Bytes(stream.Length)} long, which is too short to hold the header a save begins with.");
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(length);

            if (headerLength <= 0)
            {
                return SaveFile.Unreadable(path,
                    $"The file says its header is {Bytes(headerLength)} long, which is not a length. It is not a save, or it is damaged.");
            }

            var remaining = stream.Length - length.Length;

            if (headerLength > remaining)
            {
                return SaveFile.Unreadable(path,
                    $"The file says its header is {Bytes(headerLength)} long but only {Bytes(remaining)} follow, so it is truncated.");
            }

            var header = new byte[headerLength];
            stream.ReadExactly(header);

            return Parse(path, header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return SaveFile.Unreadable(path, $"The file could not be read: {ex.Message}");
        }
    }

    private static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";

    private static SaveFile Parse(string path, byte[] header)
    {
        Dictionary<string, string> values;

        try
        {
            values = ReadHeaderValues(header);
        }
        catch (JsonException ex)
        {
            return SaveFile.Unreadable(path,
                $"The header is not the metadata a save carries: {ex.Message}");
        }
        catch (DecoderFallbackException ex)
        {
            return SaveFile.Unreadable(path, $"The header is not readable text: {ex.Message}");
        }

        if (!values.TryGetValue(ModulesKey, out var modules))
        {
            return SaveFile.Unreadable(path,
                "The header carries no module list, so it is not a save BEM can report on.");
        }

        return new SaveFile(
            path,
            Text(values, "ApplicationVersion"),
            Text(values, "CharacterName"),
            Whole(values, "MainHeroLevel"),
            Fraction(values, "DayLong"),
            Ticks(values, "CreationTime"),
            ReadModules(modules, values));
    }

    private static Dictionary<string, string> ReadHeaderValues(byte[] header)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(header);

        if (document.RootElement.ValueKind is not JsonValueKind.Object
            || !document.RootElement.TryGetProperty("List", out var list)
            || list.ValueKind is not JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in list.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String)
                values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return values;
    }

    // The order is the load order the save was made with, so it comes from the semicolon-joined Modules
    // value and never from enumerating the Module_ keys, whose order is the JSON writer's business.
    private static IReadOnlyList<SaveModuleRecord> ReadModules(
        string modules,
        Dictionary<string, string> values)
    {
        var records = new List<SaveModuleRecord>();
        var seen = new HashSet<ModuleId>();

        foreach (var part in modules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = new ModuleId(part);

            if (!seen.Add(id))
                continue;

            records.Add(new SaveModuleRecord(id, Text(values, ModuleVersionPrefix + part)));
        }

        return records;
    }

    private static string Text(Dictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key, string.Empty);

    private static int Whole(Dictionary<string, string> values, string key) =>
        int.TryParse(Text(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double Fraction(Dictionary<string, string> values, string key) =>
        double.TryParse(Text(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static DateTime? Ticks(Dictionary<string, string> values, string key)
    {
        if (!long.TryParse(Text(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            return null;

        return ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks)
            : null;
    }

    private static DateTime WrittenAt(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
