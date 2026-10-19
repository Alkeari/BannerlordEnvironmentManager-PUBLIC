namespace BannerlordEnvironmentManager.Core.Safety;

// Subject is the certificate's whole subject line; CommonName is the readable head of it. Trusted
// means the chain built to a root this machine trusts, which is the only part a forger cannot fake.
public sealed record AuthenticodeSigner(string Subject, string CommonName, bool ChainTrusted);

public delegate AuthenticodeSigner? AuthenticodeSignerLookup(string filePath);

// Authenticode is a Windows API and Core targets net10.0 with no Windows dependency, so the app
// registers its lookup here at startup. It is the same implementation the official-claim audit uses,
// asked a different question: that one asks "is this TaleWorlds", this one asks "who is it".
public static class AuthenticodeVerifier
{
    public static AuthenticodeSignerLookup? Registered { get; set; }
}

// Publishers whose signature clears a module's capabilities. Deliberately does not clear fingerprint
// evidence: a certificate proves who published a file, which is a different question from what the
// file contains.
public static class TrustedPublishers
{
    private static readonly string[] Names =
    [
        "BUTR",
        "Bannerlord Unofficial Tools",
        "Aragas",
        "TaleWorlds",
        "Microsoft",
        ".NET Foundation",
        "Valve"
    ];

    public static bool IsTrusted(AuthenticodeSigner? signer) =>
        signer is { ChainTrusted: true }
        && Names.Any(name => signer.Subject.Contains(name, StringComparison.OrdinalIgnoreCase));
}
