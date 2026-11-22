using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

// The file work a save setup restore does on this machine: copying a game install without its mods,
// copying one module folder, and copying the save. Every copy is into a folder the restore owns; the
// source is only read. Nothing here deletes anything outside the destination it was given.
public static class SaveSetupFiles
{
    // Folders BEM writes into a game folder while it works: never part of the game.
    private const string WorkingPrefix = ".bem";

    private const int BufferSize = 1 << 20;

    // What a copy of the game would write: every file except the community modules. The game's own
    // modules (DLC included) are the game; everything else under Modules is what the copy exists to
    // replace with the save's versions.
    public static IEnumerable<string> GameFiles(string sourceGameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceGameFolder);

        var modules = Path.GetFullPath(Path.Combine(sourceGameFolder, "Modules"));

        foreach (var file in Directory.EnumerateFiles(sourceGameFolder, "*", SearchOption.TopDirectoryOnly))
            yield return file;

        foreach (var folder in Directory.EnumerateDirectories(sourceGameFolder))
        {
            var name = Path.GetFileName(folder);

            if (name.StartsWith(WorkingPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(Path.GetFullPath(folder), modules, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var module in Directory.EnumerateDirectories(folder))
                {
                    if (!OfficialModules.Contains(Path.GetFileName(module)))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(module, "*", SearchOption.AllDirectories))
                        yield return file;
                }

                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }

    public static long MeasureGame(string sourceGameFolder) =>
        GameFiles(sourceGameFolder).Sum(file => new FileInfo(file).Length);

    // The bytes free where the copy would go, or null when the drive cannot be read.
    public static long? FreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static async Task CopyGameAsync(
        string sourceGameFolder,
        string destinationGameFolder,
        IProgress<SaveSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceGameFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationGameFolder);

        var files = GameFiles(sourceGameFolder).Select(file => new FileInfo(file)).ToList();
        var total = Math.Max(1L, files.Sum(file => file.Length));
        var copied = 0L;

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];
            var relative = Path.GetRelativePath(sourceGameFolder, file.FullName);
            var target = Path.Combine(destinationGameFolder, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // A hard link on the same drive costs no space and no copying time: the game's files are
            // only read by a copy that exists to run an older mod set. Anything that cannot be linked
            // (another drive, a file system without links, a file already there) is copied instead.
            if (!TryHardLink(file.FullName, target))
                await CopyFileAsync(file.FullName, target, cancellationToken);

            copied += file.Length;
            progress?.Report(new SaveSetupProgress(
                Strings.Current.Format("Core.Saves.Setup.Progress.Game", relative), index + 1, files.Count,
                (double)copied / total));
        }
    }

    // One module folder into the copy's Modules folder under its own folder name. An existing folder of
    // that name is replaced only after the new one is whole beside it, so a failure halfway leaves the
    // copy with the module it had rather than with half of one.
    //
    // A Workshop item sits in a folder named after its Workshop number, and mods address their own
    // files as Modules\<their id>, so a caller names the folder after the module id in that case.
    public static async Task CopyModuleAsync(
        string sourceModuleFolder, string destinationModulesFolder, string? folderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModuleFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationModulesFolder);

        var name = string.IsNullOrWhiteSpace(folderName)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceModuleFolder))
            : folderName;
        var target = Path.Combine(destinationModulesFolder, name);
        var staging = target + WorkingPrefix + "-restoring";

        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        foreach (var file in Directory.EnumerateFiles(sourceModuleFolder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(staging, Path.GetRelativePath(sourceModuleFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileAsync(file, destination, cancellationToken);
        }

        Directory.CreateDirectory(staging);

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        Directory.Move(staging, target);
    }

    // The save into the copy's saves folder, so the campaign can be loaded there. It never overwrites:
    // a file of the same name that is byte for byte this save is already the copy, and one that is
    // anything else is somebody's other campaign, so this one takes a free name beside it.
    public static async Task<string> CopySaveAsync(
        string savePath, string destinationSavesFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationSavesFolder);

        Directory.CreateDirectory(destinationSavesFolder);

        var name = Path.GetFileNameWithoutExtension(savePath);
        var extension = Path.GetExtension(savePath);
        var target = Path.Combine(destinationSavesFolder, name + extension);

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(savePath), StringComparison.OrdinalIgnoreCase))
            return target;

        for (var copy = 2; File.Exists(target); copy++)
        {
            if (await SameBytesAsync(savePath, target, cancellationToken))
                return target;

            target = Path.Combine(destinationSavesFolder, $"{name} ({copy}){extension}");
        }

        await CopyFileAsync(savePath, target, cancellationToken);

        return target;
    }

    // Owner's choice (2026-11-07): the restore hard-links the game's files when the copy is on the same
    // drive, and copies them otherwise.
    public static bool TryHardLink(string source, string destination)
    {
        if (!OperatingSystem.IsWindows() || File.Exists(destination))
            return false;

        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)),
                StringComparison.OrdinalIgnoreCase))
            return false;

        return CreateHardLinkW(destination, source, IntPtr.Zero);
    }

    // Whether a copy at the destination would link rather than copy, so the preview can say it needs no space.
    public static bool WouldHardLink(string sourceGameFolder, string destinationFolder) =>
        OperatingSystem.IsWindows()
        && string.Equals(Path.GetPathRoot(Path.GetFullPath(sourceGameFolder)), Path.GetPathRoot(Path.GetFullPath(destinationFolder)),
            StringComparison.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous);

        await input.CopyToAsync(output, BufferSize, cancellationToken);
    }

    private static async Task<bool> SameBytesAsync(string left, string right, CancellationToken cancellationToken)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);

        if (a.Length != b.Length)
            return false;

        var bufferA = new byte[BufferSize];
        var bufferB = new byte[BufferSize];

        await using var streamA = a.OpenRead();
        await using var streamB = b.OpenRead();

        while (true)
        {
            var readA = await streamA.ReadAtLeastAsync(bufferA, BufferSize, throwOnEndOfStream: false, cancellationToken);
            var readB = await streamB.ReadAtLeastAsync(bufferB, BufferSize, throwOnEndOfStream: false, cancellationToken);

            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                return false;

            if (readA == 0)
                return true;
        }
    }
}
