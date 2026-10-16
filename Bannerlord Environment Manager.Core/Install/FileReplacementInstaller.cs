using System.Globalization;
using System.Text;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record FileReplacementPreview(
    string TargetPath,
    XmlMergePlan? Plan,
    bool TargetIsOfficialModule)
{
    public bool CanMerge => Plan is not null;

    public string Describe()
    {
        var name = Path.GetFileName(TargetPath);
        var lines = new List<string>();

        if (Plan is null)
        {
            lines.Add(Strings.Current.Format("Core.Install.FileReplacement.CannotCompare", name));
        }
        else
        {
            var changed = Plan.Changed.Count;

            lines.Add(Strings.Current.Plural(
                "Core.Install.FileReplacement.Changes", changed, name, Plan.Added.Count, Plan.Preserved.Count,
                Plan.CurrentEntryCount));

            if (Plan.ChangedAttributeNames.Count > 0)
            {
                lines.Add(Strings.Current.Format(
                    "Core.Install.FileReplacement.ValuesThatMove", string.Join(", ", Plan.ChangedAttributeNames)));
            }

            if (Plan.Preserved.Count > 0)
                lines.Add(Strings.Current.Plural("Core.Install.FileReplacement.WouldDelete", Plan.Preserved.Count));
        }

        if (TargetIsOfficialModule)
            lines.Add(Strings.Current["Core.Install.FileReplacement.OfficialModuleNote"]);

        lines.Add(Strings.Current.Format(
            "Core.Install.FileReplacement.BackedUpAs",
            Path.GetFileName(TargetPath) + FileReplacementInstaller.BackupExtension));

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed record FileReplacementOutcome(string TargetPath, string? BackupPath, bool Merged);

// One module file BEM wrote over, and the copy of what was there before it did. Listed as itself so a
// single replacement can be put back on its own: undoing one is a different act from putting every
// replaced file in the whole Modules folder back at once.
public sealed record ReplacedFile(string TargetPath, string RelativePath, DateTime ReplacedUtc)
{
    public string BackupPath => FileReplacementInstaller.BackupPathFor(TargetPath);

    public string DisplayName => ReplacedUtc == DateTime.MinValue
        ? RelativePath
        : Strings.Current.Format(
            "Core.Install.FileReplacement.ReplacedAt",
            RelativePath,
            ReplacedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));

    // A list item with no automation name of its own is announced by its ToString, and a record's own
    // ToString reads the type name and every member aloud.
    public override string ToString() => DisplayName;
}

public static class FileReplacementInstaller
{
    public const string BackupExtension = AtomicXmlFile.BackupExtension;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string BackupPathFor(string path) => AtomicXmlFile.BackupPathFor(path);

    public static FileReplacementPreview Preview(string targetPath, byte[] incoming, bool targetIsOfficialModule = false)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        return new FileReplacementPreview(targetPath, PlanFor(targetPath, incoming), targetIsOfficialModule);
    }

    public static FileReplacementOutcome Install(string targetPath, byte[] incoming, bool merge = true)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var currentBytes = File.Exists(targetPath) ? File.ReadAllBytes(targetPath) : null;
        var merged = merge && currentBytes is not null ? MergedText(currentBytes, incoming) : null;
        var backupPath = BackUpOriginal(targetPath);

        var directory = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (merged is null)
            File.WriteAllBytes(targetPath, incoming);
        else
            File.WriteAllBytes(targetPath, Encode(merged, HasByteOrderMark(currentBytes!)));

        return new FileReplacementOutcome(targetPath, backupPath, merged is not null);
    }

    // The first backup holds the file that was there before any mod touched it. A reinstall must not
    // replace it with the already-modded copy, or restore-to-vanilla stops meaning anything.
    public static string? BackUpOriginal(string targetPath)
    {
        if (!File.Exists(targetPath))
            return null;

        var backupPath = BackupPathFor(targetPath);

        if (!File.Exists(backupPath))
            File.Copy(targetPath, backupPath);

        return backupPath;
    }

    // Two installed modules can ship a file of the same name: the reference install has flora_kinds.xml
    // in both Native and NavalDLC. Which one an update archive means is answered by which one it shares
    // entries with, never by which module name looks likelier.
    public static FileReplacementCandidate? BestTarget(FileReplacementResolution resolution, byte[] incoming)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(incoming);

        if (resolution.Target is not null)
            return resolution.Target;

        if (Decode(incoming) is not { } incomingText || XmlMerge.EntryKeys(incomingText) is not { } incomingKeys)
            return null;

        var ranked = resolution.Candidates
            .Select(candidate => (Candidate: candidate, Shared: SharedEntryCount(candidate.TargetPath, incomingKeys)))
            .OrderByDescending(entry => entry.Shared)
            .ToList();

        if (ranked.Count == 0 || ranked[0].Shared == 0)
            return null;

        return ranked.Count > 1 && ranked[1].Shared == ranked[0].Shared ? null : ranked[0].Candidate;
    }

    public static bool HasBackup(string targetPath) => AtomicXmlFile.HasBackup(targetPath);

    public static bool RestoreOriginal(string targetPath)
    {
        try
        {
            return AtomicXmlFile.Restore(targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> FindBackups(string modulesFolderPath)
    {
        if (string.IsNullOrWhiteSpace(modulesFolderPath) || !Directory.Exists(modulesFolderPath))
            return [];

        try
        {
            return
            [
                .. Directory.EnumerateFiles(modulesFolderPath, $"*{BackupExtension}", SearchOption.AllDirectories)
                    .Select(path => path[..^BackupExtension.Length])
                    .Order(StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The same backups FindBackups names, as rows a person can pick one of. FindBackups stays the
    // shape RestoreAll wants; this is the shape a list needs.
    public static IReadOnlyList<ReplacedFile> List(string modulesFolderPath)
    {
        if (string.IsNullOrWhiteSpace(modulesFolderPath))
            return [];

        var replaced = new List<ReplacedFile>();

        foreach (var target in FindBackups(modulesFolderPath))
        {
            replaced.Add(new ReplacedFile(
                target,
                Path.GetRelativePath(modulesFolderPath, target),
                ReplacedAt(BackupPathFor(target))));
        }

        return replaced;
    }

    public static BackupRestoreResult RestoreAll(IEnumerable<string> targetPaths) => AtomicXmlFile.RestoreAll(targetPaths);

    // When BEM took the copy, which is when it wrote over the file. The backup's last write time is the
    // original file's own, carried across by the copy, so it would say when the mod author saved it.
    private static DateTime ReplacedAt(string backupPath)
    {
        try
        {
            return File.Exists(backupPath) ? File.GetCreationTimeUtc(backupPath) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static int SharedEntryCount(string path, IReadOnlyCollection<string> incomingKeys)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (Decode(bytes) is not { } text || XmlMerge.EntryKeys(text) is not { } keys)
            return 0;

        return keys.Count(incomingKeys.Contains);
    }

    private static XmlMergePlan? PlanFor(string targetPath, byte[] incoming)
    {
        if (!File.Exists(targetPath))
            return null;

        byte[] currentBytes;

        try
        {
            currentBytes = File.ReadAllBytes(targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (Decode(currentBytes) is not { } current || Decode(incoming) is not { } incomingText)
            return null;

        return XmlMerge.Plan(current, incomingText);
    }

    private static string? MergedText(byte[] currentBytes, byte[] incoming)
    {
        if (Decode(currentBytes) is not { } current || Decode(incoming) is not { } incomingText)
            return null;

        return XmlMerge.Plan(current, incomingText) is null ? null : XmlMerge.Apply(current, incomingText);
    }

    private static string? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || Array.IndexOf(bytes, (byte)0) >= 0)
            return null;

        try
        {
            return StrictUtf8.GetString(HasByteOrderMark(bytes) ? bytes.AsSpan(3) : bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] Encode(string text, bool byteOrderMark)
    {
        var body = StrictUtf8.GetBytes(text);

        return byteOrderMark ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
