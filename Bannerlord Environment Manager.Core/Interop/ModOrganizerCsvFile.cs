using System.Globalization;
using System.Text;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Interop;

// Mod Organizer's "Export to csv", read only. The user picks which columns it writes, so only
// #Mod_Name can be required; every other column is used when it is there. Mod Organizer quotes
// every text cell, doubles a quote inside one, leaves #Nexus_ID bare and writes rows in priority
// order, but a file that has been through a spreadsheet keeps none of that, so cells are read as
// ordinary CSV and the order is taken from #Mod_Priority when every row carries one.
public static class ModOrganizerCsvFile
{
    public const string SeparatorSuffix = "_separator";

    public const string NexusModPage = "https://www.nexusmods.com/mountandblade2bannerlord/mods/";

    public static LoadOrderFileRead Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var records = Records(text.TrimStart('﻿'));

        if (records.Count == 0)
            return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.ModOrganizerCsvFile.NoModNameColumn"]);

        var header = records[0];
        var name = Column(header, "Mod_Name");

        if (name < 0)
            return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.ModOrganizerCsvFile.NoModNameColumn"]);

        var priority = Column(header, "Mod_Priority");
        var status = Column(header, "Mod_Status");
        var version = Column(header, "Mod_Version");
        var nexusId = Column(header, "Nexus_ID");
        var nexusUrl = Column(header, "Mod_Nexus_URL");

        var rows = new List<(int Priority, LoadOrderFileEntry Entry)>();
        var separators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        var everyRowHasPriority = priority >= 0;

        foreach (var record in records.Skip(1))
        {
            // A blank line is not a row; a row too short to reach the name column is a damaged one.
            if (record.Count == 1 && record[0].Length == 0)
                continue;

            var modName = Cell(record, name);

            if (modName.Length == 0)
            {
                unreadable++;
                continue;
            }

            var rowPriority = 0;

            if (priority >= 0 && !int.TryParse(Cell(record, priority), NumberStyles.Integer, CultureInfo.InvariantCulture, out rowPriority))
                everyRowHasPriority = false;

            rows.Add((rowPriority, modName.EndsWith(SeparatorSuffix, StringComparison.OrdinalIgnoreCase)
                ? Separator(modName, separators)
                : new LoadOrderFileEntry(
                    modName,
                    Cell(record, version),
                    // "-" is the only thing Mod Organizer writes for a mod that is off. Anything else,
                    // a missing column included, is a mod the list means to load.
                    IsEnabled: Cell(record, status) != "-",
                    Name: modName,
                    Url: Url(Cell(record, nexusUrl), Cell(record, nexusId)))));

            if (rows.Count == LoadOrderInterop.MaxEntries)
                break;
        }

        if (rows.Count == 0)
            return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.ModOrganizerCsvFile.NoMods"]);

        // OrderBy is stable, so two rows a hand edit gave the same priority keep the file's order.
        var ordered = everyRowHasPriority ? rows.OrderBy(r => r.Priority).ToList() : rows;

        return LoadOrderInterop.Collect([.. ordered.Select(r => r.Entry)], unreadable);
    }

    // A Mod Organizer separator is a section heading, which is what a divider is here, so it comes
    // in as the divider id every other import already recognizes instead of as a missing mod.
    private static LoadOrderFileEntry Separator(string modName, HashSet<string> used)
    {
        var id = DividerConvention.ToId(modName[..^SeparatorSuffix.Length], used.Contains);

        used.Add(id);

        return new LoadOrderFileEntry(id);
    }

    // The page the file names wins. A bare id is a Bannerlord page, since that is the game this
    // list is being imported for; Mod Organizer writes -1 or 0 for a mod it has no page for.
    private static string? Url(string url, string id)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return url;

        return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? NexusModPage + number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static int Column(List<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].TrimStart('#'), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string Cell(List<string> record, int index) =>
        index >= 0 && index < record.Count ? record[index] : string.Empty;

    // Ordinary CSV: a quoted cell may hold commas, line breaks and doubled quotes. Splitting each
    // line on commas, as Novus Launcher does, breaks every mod whose name has a comma in it.
    private static List<List<string>> Records(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (quoted)
            {
                if (ch != '"')
                    cell.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                    quoted = false;

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;

                case ',':
                    record.Add(cell.ToString().Trim());
                    cell.Clear();
                    any = true;
                    break;

                case '\r':
                    break;

                case '\n':
                    record.Add(cell.ToString().Trim());
                    cell.Clear();
                    records.Add(record);
                    record = [];
                    any = false;
                    break;

                default:
                    cell.Append(ch);
                    any = true;
                    break;
            }
        }

        if (any || record.Count > 0)
        {
            record.Add(cell.ToString().Trim());
            records.Add(record);
        }

        return records;
    }
}
