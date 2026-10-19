using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record NexusSsoTokens(string SessionId, string? ConnectionToken = null);

// The connection token resumes an already-approved session without the user approving anything
// again, which makes it nearly as valuable as the key it was exchanged for. It therefore gets
// identical treatment to the key: encrypted through the same protector, written beside it under
// local application data, never logged, never in a report, never in a settings bundle.
public sealed class NexusSsoTokenStore
{
    public const string FileName = "nexus-sso-session.dat";

    private static readonly JsonSerializerOptions Format = new();

    private readonly ISecretProtector protector;

    public NexusSsoTokenStore(string directory, ISecretProtector protector)
    {
        this.protector = protector;
        FilePath = Path.Combine(directory, FileName);
    }

    public string FilePath { get; }

    public NexusSsoTokens? Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0)
                return null;

            var plaintext = protector.Unprotect(File.ReadAllBytes(FilePath));

            return string.IsNullOrWhiteSpace(plaintext)
                ? null
                : JsonSerializer.Deserialize<NexusSsoTokens>(plaintext, Format) is { SessionId.Length: > 0 } tokens
                    ? tokens
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // A token that cannot be written is not a failure worth stopping a sign-in over: the user simply
    // approves again next time. It is a failure worth refusing to write half of, so the round trip
    // is checked first exactly as the key store checks its own.
    public bool Save(NexusSsoTokens tokens)
    {
        try
        {
            var plaintext = JsonSerializer.Serialize(tokens, Format);
            var cipherText = protector.Protect(plaintext);

            if (protector.Unprotect(cipherText) != plaintext)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, cipherText);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
