namespace BannerlordEnvironmentManager.Core.Nexus;

// A credential, wrapped so that the only way to read it is to ask for it by name. ToString is
// overridden because the realistic leak is not someone printing the key deliberately, it is a key
// landing in a log line, an exception message or a report through ordinary string interpolation.
public sealed class NexusApiKey
{
    public const string HiddenText = "Nexus API key (hidden)";

    private const int MinimumLength = 16;

    private readonly string value;

    private NexusApiKey(string value) => this.value = value;

    public static NexusApiKey? TryCreate(string? raw)
    {
        var trimmed = raw?.Trim();

        return string.IsNullOrEmpty(trimmed)
            || trimmed.Length < MinimumLength
            || trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
                ? null
                : new NexusApiKey(trimmed);
    }

    public string Masked => $"...{value[^4..]}";

    public string Reveal() => value;

    public override string ToString() => HiddenText;
}

// Windows DPAPI is the real implementation and it is Windows-only, so it lives in Services. Core only
// ever sees this.
public interface ISecretProtector
{
    byte[] Protect(string plaintext);

    string? Unprotect(byte[] ciphertext);
}
