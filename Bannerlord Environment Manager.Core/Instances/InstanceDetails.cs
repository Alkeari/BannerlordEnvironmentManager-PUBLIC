using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record InstanceDetail(string Label, string Value);

// The facts behind a version row, assembled once so the dialog that shows them holds no knowledge of
// its own. The size arrives already formatted, because the byte formatting belongs with the row that
// measured the folder; everything else is read off the record and off what the disk actually carries.
public static class InstanceDetails
{
    public static IReadOnlyList<InstanceDetail> For(
        InstanceRecord record, GameDlcSet detectedDlc, string sizeOnDisk)
    {
        ArgumentNullException.ThrowIfNull(record);

        var strings = Strings.Current;

        return
        [
            new InstanceDetail(strings["Core.Instances.Details.Branch"], Stated(record.Branch)),
            new InstanceDetail(strings["Core.Instances.Details.BuildId"], Stated(record.BuildId)),
            new InstanceDetail(
                strings["Core.Instances.Details.Dlc"],
                detectedDlc.IsEmpty
                    ? strings["Core.Instances.Details.None"]
                    : string.Join(" + ", detectedDlc.DisplayNames)),
            new InstanceDetail(
                strings["Core.Instances.Details.Kind"],
                record.GameFolderIsReferenced
                    ? strings["Core.Instances.Details.KindReferenced"]
                    : strings["Core.Instances.Details.KindManaged"]),
            new InstanceDetail(strings["Core.Instances.Details.SizeOnDisk"], Stated(sizeOnDisk)),
            new InstanceDetail(
                strings["Core.Instances.Details.Created"],
                record.Created.ToLocalTime().DateTime.ToString("g", CultureInfo.CurrentCulture))
        ];
    }

    // A record BEM wrote before it learned to keep the branch and build id has neither, and a blank
    // value reads as a dialog that lost the fact rather than one that never had it.
    private static string Stated(string value) =>
        string.IsNullOrWhiteSpace(value) ? Strings.Current["Core.Instances.Details.Unknown"] : value;
}
