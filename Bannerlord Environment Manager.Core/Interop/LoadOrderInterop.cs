using System.Text;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Interop;

public static class LoadOrderInterop
{
    // These files come off the internet, so the entry count is capped before anything is allocated
    // per entry. No real load order is anywhere near this.
    public const int MaxEntries = 5000;

    public static IReadOnlyList<LoadOrderFileFormat> Importable { get; } =
        [LoadOrderFileFormat.BmList, LoadOrderFileFormat.NovusPreset, LoadOrderFileFormat.MtOrder];

    public static IReadOnlyList<LoadOrderFileFormat> Exportable { get; } =
        [LoadOrderFileFormat.BmList, LoadOrderFileFormat.NovusPreset, LoadOrderFileFormat.Text];

    public static string Extension(LoadOrderFileFormat format) => format switch
    {
        LoadOrderFileFormat.BmList => ".bmlist",
        LoadOrderFileFormat.NovusPreset => ".xml",
        LoadOrderFileFormat.MtOrder => ".mtorder",
        _ => ".txt"
    };

    public static string DisplayName(LoadOrderFileFormat format) => format switch
    {
        LoadOrderFileFormat.BmList => Strings.Current["Core.Interop.LoadOrderFormat.BmList"],
        LoadOrderFileFormat.NovusPreset => Strings.Current["Core.Interop.LoadOrderFormat.NovusPreset"],
        LoadOrderFileFormat.MtOrder => Strings.Current["Core.Interop.LoadOrderFormat.MtOrder"],
        _ => Strings.Current["Core.Interop.LoadOrderFormat.Text"]
    };

    public static LoadOrderFileFormat? FromExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bmlist" => LoadOrderFileFormat.BmList,
            ".xml" => LoadOrderFileFormat.NovusPreset,
            ".mtorder" => LoadOrderFileFormat.MtOrder,
            ".txt" => LoadOrderFileFormat.Text,
            _ => null
        };
    }

    public static string Write(LoadOrderFileFormat format, IReadOnlyList<LoadOrderFileEntry> entries) =>
        Describe(format, entries).Content;

    public static LoadOrderFileWrite Describe(LoadOrderFileFormat format, IReadOnlyList<LoadOrderFileEntry> entries) =>
        format switch
        {
            LoadOrderFileFormat.NovusPreset => new LoadOrderFileWrite(NovusPresetFile.Write(entries), []),
            LoadOrderFileFormat.MtOrder => throw new NotSupportedException(
                "BEM does not write .mtorder: only ModdingTools.com's own helper reads it."),
            _ => BmListFile.Describe(entries)
        };

    public static LoadOrderFileWrite Save(string path, LoadOrderFileFormat format, IReadOnlyList<LoadOrderFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(path);

        var written = Describe(format, entries);

        File.WriteAllText(path, written.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return written;
    }

    public static LoadOrderFileRead Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var format = FromExtension(path);

        if (format is null)
            return LoadOrderFileRead.Unreadable(
                Strings.Current.Format("Core.Interop.Read.UnknownExtension", Path.GetExtension(path)));

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return LoadOrderFileRead.Unreadable(Strings.Current.Format("Core.Interop.Read.CouldNotRead", ex.Message));
        }

        return ReadText(format.Value, Decode(bytes));
    }

    public static LoadOrderFileRead ReadText(LoadOrderFileFormat format, string text) => format switch
    {
        LoadOrderFileFormat.BmList => BmListFile.Read(text),
        LoadOrderFileFormat.NovusPreset => NovusPresetFile.Read(text),
        LoadOrderFileFormat.MtOrder => MtOrderFile.Read(text),
        _ => LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.Read.NoTextFormat"])
    };

    // Users edit these in Notepad, which adds a BOM, and BUTR's own .bmlist reader does not strip one:
    // a UTF-8 BOM there corrupts the first id and silently drops the first module.
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return Encoding.UTF8.GetString(bytes);
    }

    // The file is a load order, so the first position is the intended one: a repeated id keeps the
    // first occurrence and the rest are reported rather than dropped in silence.
    internal static LoadOrderFileRead Collect(IReadOnlyList<LoadOrderFileEntry> entries, int unreadableLines = 0)
    {
        var kept = new List<LoadOrderFileEntry>(entries.Count);
        var duplicates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Id))
            {
                duplicates.Add(entry.Id);
                continue;
            }

            kept.Add(entry);

            if (kept.Count == MaxEntries)
                break;
        }

        return new LoadOrderFileRead(kept, duplicates, unreadableLines);
    }
}
