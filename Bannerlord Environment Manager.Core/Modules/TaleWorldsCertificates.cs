using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Modules;

// The signing keys BEM will believe when Windows will not build a chain for them.
//
// A subject naming TaleWorlds is worth nothing: anyone can put that string in a certificate they
// issue to themselves, and until this existed such a certificate carried a module through the
// identity gate on the game's own folder name. Older Bannerlord builds genuinely need an untrusted
// chain accepted - a v1.4.7 download is signed with TaleWorlds' self-signed 2018 certificate, which
// no Windows machine roots - so the untrusted case cannot simply be refused. What separates the two
// is the key: the forger can copy every name, serial and date off the real certificate, but he
// cannot sign with a private key he does not have, and a certificate carrying the real public key
// over a signature he made verifies against nothing.
//
// The pin is over the public key rather than the certificate thumbprint. Both are equally beyond a
// forger's reach, because both cover the key, but a thumbprint changes when the same key is put in
// a new certificate: a renewed or re-issued TaleWorlds certificate over the same key would demote
// every official module on an install BEM was built to rescue. The key survives that, and it is the
// thing the signature actually proves possession of. A thumbprint would also be pinning a SHA-1
// digest, which is the weaker hash of the two.
public static class TaleWorldsCertificates
{
    // SHA-256 over the DER SubjectPublicKeyInfo of CN=TaleWorlds Entertainment, self-signed,
    // serial 61EB518586D5D0884531D7FBC0316B69, valid 2018-05-24 to 2039-12-31, RSA 2048. Read on
    // 2026-09-08 off a real v1.4.7 + War Sails install, which the game's own downloader
    // produced; every assembly of every official module there carries it and Windows answers
    // CERT_E_UNTRUSTEDROOT for all of them.
    public const string SelfSigned2018 = "7F2F9CFAC78983A2F611E677738B48F2FC55A734E02B38C132017914A81EF1A8";

    private static readonly HashSet<string> Pinned =
        new([SelfSigned2018], StringComparer.OrdinalIgnoreCase);

    // The keys are held as hex rather than bytes so that adding one is a line a person can read
    // against the output of a certificate viewer.
    public static IReadOnlyCollection<string> PinnedPublicKeys => Pinned;

    // The WinVerifyTrust answers that mean the signature covered the file's own bytes and only the
    // chain is in question. Windows checks the digest first and reports a digest failure ahead of any
    // chain policy failure, so an answer in this set is a file whose bytes match what was signed;
    // TRUST_E_BAD_DIGEST, TRUST_E_NOSIGNATURE and everything else are not in it and never will be.
    //
    // It is a list of what is allowed rather than of what is refused, because an HRESULT nobody
    // anticipated must fall on the refusing side. CERT_E_REVOKED and CERT_E_REVOCATION_FAILURE are
    // deliberately absent: revocation checking is switched off on the call, so neither can come back,
    // and a revoked TaleWorlds key is not one to believe on a pin.
    private static readonly HashSet<int> ChainPolicyFailures =
    [
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x800B010A), // CERT_E_CHAINING
        unchecked((int)0x800B010D), // CERT_E_UNTRUSTEDTESTROOT
        unchecked((int)0x800B0101)  // CERT_E_EXPIRED
    ];

    // Whether this verdict leaves the signature itself standing. Only these may reach the pin or
    // carry a key forward to be compared with another module's: the pin is worth nothing over bytes
    // the signature does not cover.
    public static bool IsChainPolicyFailure(int winVerifyTrustResult) =>
        ChainPolicyFailures.Contains(winVerifyTrustResult);

    public static bool IsPinned(string? publicKeySha256) =>
        !string.IsNullOrWhiteSpace(publicKeySha256) && Pinned.Contains(publicKeySha256.Trim());

    public static string Hex(ReadOnlySpan<byte> hash) =>
        string.Create(hash.Length * 2, hash.ToArray(), static (span, bytes) =>
        {
            for (var index = 0; index < bytes.Length; index++)
                bytes[index].TryFormat(span[(index * 2)..], out _, "X2", CultureInfo.InvariantCulture);
        });
}
