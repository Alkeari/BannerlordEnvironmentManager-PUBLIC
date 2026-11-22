using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One "[FTL] Crash Report: Exception occurred inside invoke" block from a ButterLib or engine log: the UI
// action the engine was invoking, the view model it targeted, the inner exception message, and the module
// folders in the stack in the order they appear (the first is the module the failure ran through).
public sealed record EngineInvokeCrash(
    DateTimeOffset? When,
    string Invoked,
    string TargetType,
    string InnerMessage,
    IReadOnlyList<string> ModuleFolders)
{
    // The exception's own category, untranslated, for keys and deduplication: "Could not load type",
    // "Method not found", "Collection was modified", or the message up to its first quote or period.
    public string MessageKind
    {
        get
        {
            var text = InnerMessage.Trim();
            var cut = text.IndexOfAny(['\'', ':', '.', ';']);

            return cut > 0 ? text[..cut].Trim() : text;
        }
    }

    public bool IsCollectionModified => InnerMessage.Contains("Collection was modified", StringComparison.Ordinal);
}

// Gap G3 in the findings catalog: only "Collection was modified" and binding-failure lines were read from
// these blocks, and the block's own stack, which names the module (MainQuestNoble before five native
// crashes, RaiseYourBanner's Mission.SpawnAgent, AutoEquipCompanions, Cultured Start Reloaded), was not.
// The stack is written as file paths ("...\Modules\MainQuestNoble\bin\Win64_Shipping_Client\
// MainQuestNoble.dll@100663303@-1"), not as "at" frames.
public static partial class EngineInvokeCrashes
{
    public const string Marker = "Crash Report: Exception occurred inside invoke";

    private const string StackMarker = "Crash Report StackTrace:";

    [GeneratedRegex(@"^\[(?<when>\d{4}-\d{2}-\d{2}T[^\]]+)\]")]
    private static partial Regex Stamp { get; }

    [GeneratedRegex(@"Exception occurred inside invoke:\s*(?<invoked>.*)$")]
    private static partial Regex Header { get; }

    [GeneratedRegex(@"[\\/]Modules[\\/](?<folder>[^\\/]+)[\\/]bin[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex ModulePath { get; }

    public static bool IsBlockLine(string line) =>
        line.Contains(Marker, StringComparison.Ordinal)
        || line.StartsWith("Target type:", StringComparison.Ordinal)
        || line.StartsWith("Inner message:", StringComparison.Ordinal)
        || line.Contains(StackMarker, StringComparison.Ordinal);

    public static bool IsStackPath(string line) => ModulePath.IsMatch(line);

    public static IReadOnlyList<EngineInvokeCrash> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = new List<EngineInvokeCrash>();
        Builder? open = null;
        var inStack = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Contains(Marker, StringComparison.Ordinal))
            {
                if (open is not null)
                    found.Add(open.Build());

                open = new Builder
                {
                    When = ReadStamp(line),
                    Invoked = Header.Match(line) is { Success: true } header ? header.Groups["invoked"].Value.Trim() : string.Empty
                };
                inStack = false;
                continue;
            }

            if (open is null)
                continue;

            if (line.StartsWith("Target type:", StringComparison.Ordinal))
            {
                open.TargetType = line["Target type:".Length..].Trim();
            }
            else if (line.StartsWith("Inner message:", StringComparison.Ordinal))
            {
                open.InnerMessage = line["Inner message:".Length..].Trim();
            }
            else if (line.Contains(StackMarker, StringComparison.Ordinal))
            {
                inStack = true;
                AddFolder(open, line);
            }
            else if (inStack && ModulePath.IsMatch(line))
            {
                AddFolder(open, line);
            }
            else if (inStack && line.StartsWith('['))
            {
                found.Add(open.Build());
                open = null;
                inStack = false;
            }
        }

        if (open is not null)
            found.Add(open.Build());

        return found;
    }

    private static void AddFolder(Builder builder, string line)
    {
        if (ModulePath.Match(line) is { Success: true } match)
        {
            var folder = match.Groups["folder"].Value;

            if (!builder.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                builder.Folders.Add(folder);
        }
    }

    private static DateTimeOffset? ReadStamp(string line) =>
        Stamp.Match(line) is { Success: true } match
        && DateTimeOffset.TryParse(match.Groups["when"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when
            : null;

    private sealed class Builder
    {
        public DateTimeOffset? When { get; init; }

        public string Invoked { get; init; } = string.Empty;

        public string TargetType { get; set; } = string.Empty;

        public string InnerMessage { get; set; } = string.Empty;

        public List<string> Folders { get; } = [];

        public EngineInvokeCrash Build() => new(When, Invoked, TargetType, InnerMessage, [.. Folders]);
    }
}
