using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Safety;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BannerlordEnvironmentManager.Services
{
    // Answers the one question Core cannot: does this module actually ship anything TaleWorlds signed?
    // Authenticode chain validation is a Windows API and Core targets net10.0 with no Windows
    // dependency, so the check lives here and is registered with Core at startup.
    [SupportedOSPlatform("windows")]
    internal static partial class TaleWorldsSignature
    {
        // The real subject is
        // CN=TALEWORLDS ENTERTAINMENT YAZILIM TEKNOLOJILERI A.S., O=TALEWORLDS ENTERTAINMENT ...
        // except that the game's certificate spells it with the Turkish dotted capital I. Comparing
        // the whole name would make correctness depend on this file's encoding and on culture-sensitive
        // casing, and one mismatch would demote Native itself, so only the ASCII head is compared.
        // Specificity comes from the chain check, not from the string: to pass, a forger would need a
        // CA to issue a real code-signing certificate to an organization named TALEWORLDS ENTERTA...
        private const string PublisherPrefix = "TALEWORLDS ENTERTA";

        private const string OrganizationOid = "2.5.4.10";

        private const string CommonNameOid = "2.5.4.3";

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;

        private const uint RevokeNone = 0;

        private const uint ChoiceFile = 1;

        private const uint StateActionVerify = 1;

        private const uint StateActionClose = 2;

        // The signed assemblies carry their whole chain in the PKCS#7 blob, so nothing has to be
        // fetched. Verification runs while the user waits on a scan and must never block on a network.
        private const uint CacheOnlyUrlRetrieval = 0x00001000;

        private const int Trusted = 0;

        [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int WinVerifyTrust(IntPtr window, in Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr Union;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }

        // Reading a file's signature is a Windows call and stays here; what those per-file answers add
        // up to is a decision, and it lives in Core where a test can reach it. The enumeration is
        // lazy, so OfficialClaimAudit.Fold abandoning it on the first trusted signature still means a
        // genuine official module never pays for a walk of its assets.
        public static ModuleSignature Check(string moduleFolderPath)
        {
            try
            {
                if (!Directory.Exists(moduleFolderPath))
                    return new ModuleSignature(OfficialSignature.Unavailable);

                return OfficialClaimAudit.FoldFiles(CandidateAssemblies(moduleFolderPath).Select(CheckFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Verifying the official claim of '{moduleFolderPath}' failed");
                return new ModuleSignature(OfficialSignature.Unavailable);
            }
        }

        // bin\ first: that is where a module's assemblies live, so a genuine official module answers on
        // its first file and never pays for a walk of its assets.
        private static IEnumerable<string> CandidateAssemblies(string moduleFolderPath)
        {
            var bin = Path.Combine(moduleFolderPath, "bin");
            var binPrefix = bin + Path.DirectorySeparatorChar;

            if (Directory.Exists(bin))
            {
                foreach (var file in Assemblies(bin))
                    yield return file;
            }

            foreach (var file in Assemblies(moduleFolderPath))
            {
                if (!file.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }

        private static IEnumerable<string> Assemblies(string root) =>
            Directory
                .EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                })
                .Where(file =>
                    file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        // Who signed this file, or null when it is not signed at all or could not be read. The mod
        // safety scanner needs the signer's name rather than a TaleWorlds yes-or-no, so the one
        // Authenticode implementation in BEM answers both questions instead of being forked.
        //
        // Registered with Core at startup the same way Check is.
        public static AuthenticodeSigner? Signer(string path)
        {
            var (subject, _, _) = ReadSubject(path);

            return subject is null ? null : new AuthenticodeSigner(subject.Name, CommonName(subject), Verify(path) == Trusted);
        }

        private static FileSignature CheckFile(string path)
        {
            var (subject, publicKey, unreadable) = ReadSubject(path);

            if (subject is null)
                return new FileSignature(unreadable ? OfficialSignature.Unavailable : OfficialSignature.Unsigned);

            if (!IsTaleWorlds(subject))
                return new FileSignature(OfficialSignature.SignedByOther);

            // A subject naming TaleWorlds is worth nothing on its own: anyone can embed a PKCS#7 blob
            // that says so. A chain that builds to a trusted root proves it, and so does a signature
            // over this file's own bytes made with one of TaleWorlds' pinned keys, which is what the
            // game's own 1.4.7 download carries: genuine files under a self-signed 2018 certificate no
            // Windows machine chains. Anything else naming TaleWorlds is refused.
            var verdict = Verify(path);

            if (verdict == Trusted)
                return new FileSignature(OfficialSignature.SignedByTaleWorlds);

            // Only a chain-policy verdict is eligible for the pin, or for having its key carried on
            // to be compared with the install's other modules, and that is the whole strength of
            // both. Windows checks the digest before it checks the chain, so a file whose bytes the
            // signature does not cover answers TRUST_E_BAD_DIGEST, is refused here and contributes
            // no key to anything: lifting the real certificate out of a genuine assembly and pasting
            // it onto hostile code buys the forger nothing, because he still cannot sign with a key
            // he does not hold.
            //
            // The set is wider than the untrusted root alone because a missing intermediate is the
            // same fact about the same signature: ProviderFlags forbids fetching one over the
            // network, so a machine without it cached answers CERT_E_CHAINING for a file the next
            // machine chains perfectly well.
            if (!TaleWorldsCertificates.IsChainPolicyFailure(verdict))
                return new FileSignature(OfficialSignature.TaleWorldsUntrustedChain);

            var key = PublicKeyHash(publicKey);

            return TaleWorldsCertificates.IsPinned(key)
                ? new FileSignature(OfficialSignature.TaleWorldsPinnedKey)
                : new FileSignature(OfficialSignature.TaleWorldsUntrustedChain, key);
        }

        private static string? PublicKeyHash(byte[]? subjectPublicKeyInfo) =>
            subjectPublicKeyInfo is null
                ? null
                : TaleWorldsCertificates.Hex(SHA256.HashData(subjectPublicKeyInfo));

        // Unreadable separates "this file carries no signature" from "this file could not be opened",
        // which the official-claim audit turns into two different verdicts and must keep doing.
        private static (X500DistinguishedName? Subject, byte[]? PublicKey, bool Unreadable) ReadSubject(string path)
        {
            try
            {
                // X509CertificateLoader, which the obsoletion points at, loads a certificate from bytes
                // and has no way to pull the signer out of a signed PE. This remains the only managed
                // API that does, so the bytes come from here and the load itself uses the new loader.
#pragma warning disable SYSLIB0057
                using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var certificate = X509CertificateLoader.LoadCertificate(signer.GetRawCertData());

                return (certificate.SubjectName, certificate.PublicKey.ExportSubjectPublicKeyInfo(), false);
            }
            catch (CryptographicException)
            {
                // CreateFromSignedFile answers "this file carries no signature", "this file is locked"
                // and "this file's ACL denies me" with the same exception type and no code worth
                // reading, so without this an unreadable file was recorded as unsigned - the one value
                // that demotes a folder wearing an official name. One open, and only on files that
                // produced no signature, tells the two apart. It costs nothing on a genuine official
                // module, which answers on the first signed assembly in bin\ and never reaches here.
                return (null, null, !CanBeOpened(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, null, true);
            }
        }

        // Read sharing is what the game itself takes when it loads a module's assemblies, so a file
        // this returns false for is a file the game could not load either.
        private static bool CanBeOpened(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string CommonName(X500DistinguishedName subject)
        {
            foreach (var name in subject.EnumerateRelativeDistinguishedNames())
            {
                if (!name.HasMultipleElements
                    && name.GetSingleElementType().Value is CommonNameOid
                    && name.GetSingleElementValue() is { Length: > 0 } value)
                    return value;
            }

            return subject.Name;
        }

        private static bool IsTaleWorlds(X500DistinguishedName subject)
        {
            foreach (var name in subject.EnumerateRelativeDistinguishedNames())
            {
                if (name.HasMultipleElements)
                    continue;

                if (name.GetSingleElementType().Value is not (OrganizationOid or CommonNameOid))
                    continue;

                if (name.GetSingleElementValue()?.StartsWith(PublisherPrefix, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }

            return false;
        }

        // The HRESULT rather than a yes-or-no, because "the chain does not build" and "the signature
        // does not cover these bytes" are the two answers the pin has to tell apart, and WinVerifyTrust
        // reports the digest failure ahead of any chain policy failure. Measured against real
        // installs: a genuine 1.4.7 assembly answers CERT_E_UNTRUSTEDROOT, a genuine 1.5.2 assembly
        // answers S_OK, and a byte-flipped copy of the 1.4.7 assembly answers TRUST_E_BAD_DIGEST.
        private static int Verify(string path)
        {
            var filePath = IntPtr.Zero;
            var fileInfo = IntPtr.Zero;
            var trustData = IntPtr.Zero;

            try
            {
                filePath = Marshal.StringToHGlobalUni(path);

                var file = new TrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf<TrustFileInfo>(),
                    FilePath = filePath,
                    FileHandle = IntPtr.Zero,
                    KnownSubject = IntPtr.Zero
                };

                fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
                Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);

                var data = new TrustData
                {
                    Size = (uint)Marshal.SizeOf<TrustData>(),
                    PolicyCallbackData = IntPtr.Zero,
                    SipClientData = IntPtr.Zero,
                    UiChoice = UiNone,
                    RevocationChecks = RevokeNone,
                    UnionChoice = ChoiceFile,
                    Union = fileInfo,
                    StateAction = StateActionVerify,
                    StateData = IntPtr.Zero,
                    UrlReference = IntPtr.Zero,
                    ProviderFlags = CacheOnlyUrlRetrieval,
                    UiContext = 0
                };

                trustData = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
                Marshal.StructureToPtr(data, trustData, fDeleteOld: false);

                var result = WinVerifyTrust(IntPtr.Zero, in GenericVerifyV2, trustData);

                // The verify call allocates state that only a matching close call releases, so the
                // close has to run against the same buffer even when the verdict was "not trusted".
                var close = Marshal.PtrToStructure<TrustData>(trustData);
                close.StateAction = StateActionClose;
                Marshal.StructureToPtr(close, trustData, fDeleteOld: false);
                _ = WinVerifyTrust(IntPtr.Zero, in GenericVerifyV2, trustData);

                return result;
            }
            finally
            {
                if (trustData != IntPtr.Zero)
                    Marshal.FreeHGlobal(trustData);

                if (fileInfo != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfo);

                if (filePath != IntPtr.Zero)
                    Marshal.FreeHGlobal(filePath);
            }
        }
    }
}
