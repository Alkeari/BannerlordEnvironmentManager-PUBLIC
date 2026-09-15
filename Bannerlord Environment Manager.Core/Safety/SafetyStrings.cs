using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace BannerlordEnvironmentManager.Core.Safety;

public sealed record ExtractedStrings(
    IReadOnlyList<string> Literals,
    IReadOnlyList<SafetyCapability> Capabilities,
    bool IsManagedAssembly);

// Where the payload strings actually live, and nowhere else.
//
// The tool this came from concatenated the ASCII and UTF-16 decoding of the whole file into one
// haystack and ran every rule over it. That produces two kinds of noise a targeted read does not.
// First, every type and method name a mod references is in the file as text, so "WebClient",
// "Process.Start" and "Assembly.Load" match on any assembly that merely mentions them. Second, a
// single haystack lets a rule that wants two tokens near each other match across two unrelated
// blobs that happen to be adjacent in the file.
//
// For a managed assembly the payload strings are ldstr literals, which live in the user-string heap,
// or they are in an embedded resource. Both are read here, and every rule is then evaluated against
// one literal at a time, so nothing can match across a boundary. What the code is able to do is read
// off the type and member references instead, exactly and cheaply, and is reported as capability
// rather than mixed into the same evidence.
//
// Nothing here loads or executes anything: PEReader over a stream, metadata only.
public static class SafetyStrings
{
    // Past this a "string" is an asset, not a command line. The longest real payload literal seen is
    // an encoded PowerShell blob, and those are kept: the cap is generous on purpose.
    private const int MaximumLiteralLength = 32 * 1024;

    private const int MinimumRunLength = 6;

    // Embedded resources hold fonts, images and localization tables as well as strings. Past this
    // there is nothing a fingerprint would find that is worth the sweep.
    private const int MaximumResourceBytes = 16 * 1024 * 1024;

    public static ExtractedStrings FromAssembly(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return new ExtractedStrings(FromBytes(ReadAll(stream)), [], false);

        var reader = peReader.GetMetadataReader();
        var literals = new List<string>();

        foreach (var literal in UserStrings(peReader, reader))
            literals.Add(literal);

        foreach (var literal in ResourceStrings(peReader))
            literals.Add(literal);

        return new ExtractedStrings(literals, Capabilities(reader), true);
    }

    // Anything that is not a managed assembly: a native dll, a .bat, a .ps1, a .lnk. A shortcut keeps
    // its target and arguments as UTF-16 inside the file, so the same sweep reaches those too.
    public static IReadOnlyList<string> FromBytes(ReadOnlySpan<byte> bytes)
    {
        var runs = new List<string>();

        Collect(bytes, stride: 1, runs);
        Collect(bytes, stride: 2, runs);

        return runs;
    }

    public static IReadOnlyList<string> FromText(string text) =>
        string.IsNullOrEmpty(text) ? [] : [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    // MetadataReader offers no way to walk the user-string heap, only to read one handle. The heap is
    // a run of length-prefixed blobs starting with an empty one, so the walk is the stride: decode
    // each blob's compressed length to find the next handle, and let the reader decode the text.
    private static List<string> UserStrings(PEReader peReader, MetadataReader reader)
    {
        var literals = new List<string>();

        ReadOnlySpan<byte> heap;
        int heapStart;

        try
        {
            heapStart = reader.GetHeapMetadataOffset(HeapIndex.UserString);

            var size = reader.GetHeapSize(HeapIndex.UserString);
            var metadata = peReader.GetMetadata().GetContent();

            if (size <= 1 || heapStart < 0 || heapStart + size > metadata.Length)
                return literals;

            heap = metadata.AsSpan().Slice(heapStart, size);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return literals;
        }

        var offset = 0;

        while (offset < heap.Length)
        {
            var blobStart = offset;

            if (!TryReadCompressedInteger(heap, ref offset, out var length) || length < 0)
                break;

            offset += length;

            if (offset > heap.Length)
                break;

            if (length <= 1)
                continue;

            try
            {
                var value = reader.GetUserString(MetadataTokens.UserStringHandle(blobStart));

                if (value.Length is > 0 and <= MaximumLiteralLength)
                    literals.Add(value);
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException)
            {
                break;
            }
        }

        return literals;
    }

    private static bool TryReadCompressedInteger(ReadOnlySpan<byte> bytes, ref int offset, out int value)
    {
        value = 0;

        if (offset >= bytes.Length)
            return false;

        var first = bytes[offset];

        if ((first & 0x80) == 0)
        {
            value = first;
            offset += 1;

            return true;
        }

        if ((first & 0xC0) == 0x80)
        {
            if (offset + 1 >= bytes.Length)
                return false;

            value = ((first & 0x3F) << 8) | bytes[offset + 1];
            offset += 2;

            return true;
        }

        if ((first & 0xE0) == 0xC0)
        {
            if (offset + 3 >= bytes.Length)
                return false;

            value = ((first & 0x1F) << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
            offset += 4;

            return true;
        }

        return false;
    }

    private static IEnumerable<string> ResourceStrings(PEReader peReader)
    {
        var corHeader = peReader.PEHeaders.CorHeader;

        if (corHeader is null || corHeader.ResourcesDirectory.Size is <= 0 or > MaximumResourceBytes)
            return [];

        ImmutableArray<byte> content;

        try
        {
            content = peReader
                .GetSectionData(corHeader.ResourcesDirectory.RelativeVirtualAddress)
                .GetContent(0, corHeader.ResourcesDirectory.Size);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return [];
        }

        return FromBytes(content.AsSpan());
    }

    private static IReadOnlyList<SafetyCapability> Capabilities(MetadataReader reader)
    {
        var found = new HashSet<SafetyCapability>();

        try
        {
            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                var @namespace = reader.GetString(reference.Namespace);
                var name = reader.GetString(reference.Name);

                if (@namespace.StartsWith("System.Net", StringComparison.Ordinal))
                    found.Add(SafetyCapability.Network);
                else if (@namespace is "System.Diagnostics" && name is "Process" or "ProcessStartInfo")
                    found.Add(SafetyCapability.Process);
                else if (@namespace.StartsWith("System.Reflection.Emit", StringComparison.Ordinal))
                    found.Add(SafetyCapability.DynamicCode);
                else if (@namespace is "Microsoft.Win32" && name.StartsWith("Registry", StringComparison.Ordinal))
                    found.Add(SafetyCapability.Registry);
            }

            foreach (var handle in reader.MemberReferences)
            {
                var name = reader.GetString(reader.GetMemberReference(handle).Name);

                if (name is "FromBase64String" or "DefineDynamicAssembly")
                    found.Add(SafetyCapability.DynamicCode);
            }
        }
        catch (BadImageFormatException)
        {
        }

        return [.. found.Order()];
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    // A run of printable characters. Stride 1 reads the bytes as ASCII; stride 2 reads them as
    // UTF-16LE, which is how a .NET string sits in a file that carries no metadata to ask. UTF-16 is
    // read from both alignments, because nothing guarantees a string starts on an even offset.
    private static void Collect(ReadOnlySpan<byte> bytes, int stride, List<string> runs)
    {
        for (var start = 0; start < stride; start++)
            CollectFrom(bytes, start, stride, runs);
    }

    private static void CollectFrom(ReadOnlySpan<byte> bytes, int start, int stride, List<string> runs)
    {
        var builder = new StringBuilder();

        for (var i = start; i < bytes.Length; i += stride)
        {
            var printable = bytes[i] is >= 0x20 and < 0x7F
                && (stride == 1 || (i + 1 < bytes.Length && bytes[i + 1] == 0));

            if (printable)
            {
                if (builder.Length < MaximumLiteralLength)
                    builder.Append((char)bytes[i]);

                continue;
            }

            if (builder.Length >= MinimumRunLength)
                runs.Add(builder.ToString());

            builder.Clear();
        }

        if (builder.Length >= MinimumRunLength)
            runs.Add(builder.ToString());
    }
}
