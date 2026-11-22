using System.IO.Compression;

namespace BannerlordEnvironmentManager.Core.Instances;

// What DepotDownloader itself recorded about the folder it downloads into, in
// Game\.DepotDownloader\depot.config: the manifest each depot was last brought up to. It writes
// ulong.MaxValue for a depot the moment it starts on it and the real manifest id only once every file
// of that depot is written, and it sizes every file to its final length before filling it in. So a
// download cut off partway weighs exactly what a finished one does, and only this record tells them
// apart: a v1.4.8 stopped at 70 percent measured its full 60 GB and held module files of nothing but
// zero bytes.
//
// The file is a raw deflate stream of a protobuf map<uint32, uint64>: field 1 per entry, the depot id
// as its field 1 and the manifest id as its field 2.
public static class DepotDownloadState
{
    public const ulong InProgressManifestId = ulong.MaxValue;

    public static string ConfigPath(string gameFolder) =>
        Path.Combine(gameFolder, ".DepotDownloader", "depot.config");

    // A folder with no record says nothing either way, so it is not called unfinished; a record that
    // cannot be read is, because the only thing it was written to say is whether the download ended.
    public static bool HasUnfinishedDepot(string gameFolder)
    {
        var path = ConfigPath(gameFolder);

        if (!File.Exists(path))
            return false;

        try
        {
            return Read(File.ReadAllBytes(path)).Values.Any(manifest => manifest == InProgressManifestId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return true;
        }
    }

    public static IReadOnlyDictionary<uint, ulong> Read(byte[] config)
    {
        ArgumentNullException.ThrowIfNull(config);

        using var compressed = new MemoryStream(config);
        using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        inflate.CopyTo(plain);

        var data = plain.ToArray();
        var depots = new Dictionary<uint, ulong>();
        var index = 0;

        while (index < data.Length)
        {
            if (data[index++] != 0x0A)
                throw new InvalidDataException("depot.config holds a field other than its depot map.");

            var length = (int)Varint(data, ref index);
            var end = index + length;

            if (end > data.Length)
                throw new InvalidDataException("depot.config ends inside an entry.");

            uint? depot = null;
            ulong? manifest = null;

            while (index < end)
            {
                var tag = data[index++];
                var value = Varint(data, ref index);

                if (tag == 0x08)
                    depot = (uint)value;
                else if (tag == 0x10)
                    manifest = value;
            }

            if (depot is { } id)
                depots[id] = manifest ?? 0;
        }

        return depots;
    }

    private static ulong Varint(byte[] data, ref int index)
    {
        ulong value = 0;

        for (var shift = 0; shift < 64; shift += 7)
        {
            if (index >= data.Length)
                throw new InvalidDataException("depot.config ends inside a number.");

            var b = data[index++];
            value |= (ulong)(b & 0x7F) << shift;

            if (b < 0x80)
                return value;
        }

        throw new InvalidDataException("depot.config holds a number longer than 64 bits.");
    }
}
