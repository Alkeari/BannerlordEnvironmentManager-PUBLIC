using System.IO.Compression;
using System.Text;

namespace BannerlordEnvironmentManager.Core.Instances;

// The account names DepotDownloader holds a remembered sign-in for. A QR sign-in passes no -username,
// so BEM never learns whose account it was, and every later run with -qr shows a new code: a single
// War Sails download asked for three scans (2026-11-07). -remember-password stores the token under the
// account name in account.config, so reading that name back lets every later run pass -username and
// sign in with the stored token, asking for nothing.
//
// account.config is a protobuf message written through a raw DeflateStream into the tool's isolated
// storage. Field 4 is the LoginTokens map: each entry holds the account name (field 1) and the token
// (field 2). Only the names are read; the token is never copied out of the buffer.
public static class DepotDownloaderAccounts
{
    public const string FileName = "account.config";

    public static string IsolatedStorageRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsolatedStorage");

    public static IReadOnlyList<string> NamesIn(byte[] deflated)
    {
        ArgumentNullException.ThrowIfNull(deflated);

        byte[] message;

        try
        {
            using var input = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            message = buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            return [];
        }

        var names = new List<string>();
        var i = 0;

        while (i < message.Length)
        {
            if (!TryVarint(message, ref i, out var key))
                break;

            var field = key >> 3;
            var wire = key & 7;

            if (wire == 0)
            {
                if (!TryVarint(message, ref i, out _))
                    break;
                continue;
            }

            if (wire != 2 || !TryVarint(message, ref i, out var length) || length > (ulong)(message.Length - i))
                break;

            var start = i;
            i += (int)length;

            if (field == 4 && NameInEntry(message, start, i) is { } name)
                names.Add(name);
        }

        return names;
    }

    // The names in every account.config written at or after since, newest file first.
    public static IReadOnlyList<string> NamesWrittenSince(DateTime sinceUtc, string? root = null)
    {
        root ??= IsolatedStorageRoot;

        try
        {
            if (!Directory.Exists(root))
                return [];

            return Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= sinceUtc)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .SelectMany(file => NamesIn(File.ReadAllBytes(file.FullName)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Whether any account.config holds a remembered sign-in for this account.
    public static bool HasSignIn(string accountName, string? root = null) =>
        accountName.Length > 0
        && NamesWrittenSince(DateTime.MinValue, root).Contains(accountName, StringComparer.OrdinalIgnoreCase);

    // Signing out of Steam: every account.config that holds a remembered sign-in for this account
    // is deleted, so the next run asks again. A file naming only other accounts is left alone.
    // Returns how many files were removed.
    public static int Forget(string accountName, string? root = null)
    {
        root ??= IsolatedStorageRoot;

        if (accountName.Length == 0 || !Directory.Exists(root))
            return 0;

        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories).ToList())
        {
            try
            {
                if (!NamesIn(File.ReadAllBytes(path)).Contains(accountName, StringComparer.OrdinalIgnoreCase))
                    continue;

                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static string? NameInEntry(byte[] message, int start, int end)
    {
        var j = start;

        while (j < end)
        {
            if (!TryVarint(message, ref j, out var key))
                return null;

            if ((key & 7) == 0)
            {
                if (!TryVarint(message, ref j, out _))
                    return null;
                continue;
            }

            if ((key & 7) != 2 || !TryVarint(message, ref j, out var length) || length > (ulong)(end - j))
                return null;

            if (key >> 3 == 1)
            {
                var name = Encoding.UTF8.GetString(message, j, (int)length);
                return name.Length > 0 ? name : null;
            }

            j += (int)length;
        }

        return null;
    }

    private static bool TryVarint(byte[] data, ref int i, out ulong value)
    {
        value = 0;

        for (var shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;

            if (b < 0x80)
                return true;
        }

        return false;
    }
}
