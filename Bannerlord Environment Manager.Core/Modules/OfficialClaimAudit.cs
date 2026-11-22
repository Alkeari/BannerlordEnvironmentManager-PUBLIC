namespace BannerlordEnvironmentManager.Core.Modules;

// What Authenticode had to say about a module's files. The three-value version of this could not tell
// "signed by TaleWorlds with a certificate this machine will not chain" from "signed by somebody
// else" from "not signed at all", and it reported all three as NotSignedByTaleWorlds. That collapse
// is what printed "is not signed by TaleWorlds" over a freshly downloaded 1.4.7 whose every binary
// carries a genuine TaleWorlds signature under a self-signed 2018 certificate Windows does not trust.
public enum OfficialSignature
{
    // Verification could not run: not Windows, or the module's files could not be read at all.
    Unavailable,
    // At least one assembly carries an Authenticode signature whose subject is TaleWorlds and whose
    // chain builds to a root this machine trusts. One of the two values a forger cannot manufacture.
    SignedByTaleWorlds,
    // At least one assembly carries a signature that covers the file's own bytes, was made by one of
    // TaleWorlds' pinned signing keys, and whose chain this machine will not build. The 1.4.7 build
    // the game's own downloader hands out is entirely this: genuine TaleWorlds files under the
    // self-signed 2018 certificate no Windows machine roots. The other value a forger cannot
    // manufacture, because the pin is over the key he would have to sign with rather than over the
    // names he is free to copy.
    TaleWorldsPinnedKey,
    // At least one assembly names TaleWorlds as the subject, and nothing corroborated it: the chain
    // did not build and the signing key is not one BEM pins. Anyone can issue himself a certificate
    // that says TaleWorlds, so this is the impostor's other fingerprint rather than a weaker kind of
    // proof.
    TaleWorldsUntrustedChain,
    // Assemblies are signed, and not one of them names TaleWorlds. The strongest impostor signal
    // there is: the game's own modules are never signed by a third party.
    SignedByOther,
    // Read successfully, the module ships at least one assembly, and not one of them carries a
    // signature. No genuine official module on either real install looks like this: each
    // one either ships no assembly at all or ships assemblies naming TaleWorlds, so a module of the
    // game's own name that lands here is not the game's own code.
    Unsigned,
    // Read successfully and the module ships no assembly at all. SandBoxCore is like this on every
    // install, which is why no signature rule can ever reach it and why this is not the same fact as
    // Unsigned. It is not by itself a reason to believe anything: only the modules named in
    // OfficialModules.WithoutAssemblies are the game's own and answer this.
    NoAssemblies
}

// What Authenticode said about one file, and the key that made the signature when the answer was a
// TaleWorlds signature over the file's own bytes whose chain this machine will not build.
//
// The key is carried in that one case and no other, which is the whole safety of comparing keys at
// all. Windows checks the digest before it checks the chain, so a certificate lifted off a genuine
// assembly and pasted onto hostile bytes answers a digest failure, contributes no key, and can never
// agree with anything; the forger would have to sign with a key he does not hold to produce one.
public readonly record struct FileSignature(OfficialSignature Verdict, string? UntrustedTaleWorldsKey = null);

// The same about a whole module, folded from its files. The key is null unless every untrusted
// TaleWorlds file in the module answered with the same one.
public readonly record struct ModuleSignature(OfficialSignature Verdict, string? UntrustedTaleWorldsKey = null);

public delegate ModuleSignature OfficialSignatureCheck(string moduleFolderPath);

// Authenticode chain validation is a Windows API and Core targets net10.0 with no Windows dependency,
// so the app project registers its check here at startup, the way GpuInfo keeps WMI out of Core.
public static class OfficialClaimVerifier
{
    public static OfficialSignatureCheck? Registered { get; set; }
}

// Why a module's official claim was accepted or refused. Recorded so a screen can distinguish a
// module BEM proved official from one it accepted on its name alone, rather than both reading as
// "Official" with no way to tell which evidence stood behind it.
public enum OfficialClaimBasis
{
    // Nothing was asked: the module makes no official claim, or no verifier is registered.
    NotAudited,
    // A TaleWorlds signature whose chain this machine trusts.
    SignatureVerified,
    // One of the game's own module ids, in a folder of that name, sitting directly under Modules,
    // and no other folder claiming the id. No signature this machine could validate.
    Identity,
    // Required by a module that was itself signature-verified.
    Vouched,
    // Verification could not run against this module's files at all, so the claim stands unrefuted.
    Unverifiable,
    // Refused: the module's assemblies are signed, and by somebody other than TaleWorlds.
    DemotedForeignSignature,
    // Refused: the module's assemblies are signed by a certificate that names TaleWorlds, no trusted
    // authority issued it, and the key that signed them is not one of TaleWorlds' own. A self-signed
    // certificate carrying that name costs nothing to make, so this is the shape a forgery takes.
    DemotedUnrecognizedCertificate,
    // Refused: the module declares the id of one of the game's own modules from somewhere the game
    // does not keep that module. The real module is elsewhere and is judged on its own evidence.
    DemotedBorrowedOfficialId,
    // Refused: no usable signature, and nothing about the module's identity matches the game's own.
    DemotedUnrecognized,
    // Refused: the module carries the name, the folder and the place of one of the game's own
    // modules, and it ships assemblies of which not one is signed by TaleWorlds. Every identity
    // signal is something an attacker controls, so the name alone cannot carry a claim over code
    // the game did not sign.
    DemotedWithoutGameCode
}

// A module's own SubModule.xml is not evidence. Any author can write <ModuleType value="Official"/>
// and thereby claim the Official badge, the Official sort tier, and immunity from every batch action,
// so a module can opt itself out of "Disable all" with one line. The claim is honored only when
// something outside that manifest corroborates it.
//
// The ladder below is first-rule-wins, and the order carries the whole safety of the design. A
// signature that refutes the claim is tested before identity - a foreign signer, and a certificate
// naming TaleWorlds that neither chains nor carries a pinned key - because otherwise replacing
// Modules\Native wholesale with hostile code would pass on the folder name alone.
//
// The second of those two rules asks one further question before it refuses, and
// CertificateTheInstallAgreesOn is that question: whether the certificate is the one the game's own
// modules on this install agree on with nothing to prove it. That is what a genuine build signed
// with a TaleWorlds certificate BEM does not pin looks like, and it is not what one hostile folder
// beside eight genuine ones looks like.
public static class OfficialClaimAudit
{
    public static IReadOnlyList<ModuleManifest> Apply(
        IReadOnlyList<ModuleManifest> modules,
        OfficialSignatureCheck? check)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (check is null)
            return modules;

        var claimants = modules.Where(m => m.IsOfficial).ToList();

        if (claimants.Count == 0)
            return modules;

        // Only the modules that claim official are checked. Authenticode verification hits the disk,
        // and a real install carries a couple of hundred modules against roughly ten claimants.
        // Every claimant is checked even when its name is one of the game's own, because rule 2 needs
        // the answer: the identity gate must never be reachable without the signature having spoken.
        var signatures = new Dictionary<string, ModuleSignature>(StringComparer.OrdinalIgnoreCase);

        foreach (var claimant in claimants)
        {
            if (!signatures.ContainsKey(claimant.FolderPath))
                signatures[claimant.FolderPath] = check(claimant.FolderPath);
        }

        // A catalog id claimed twice from the game's own folder for it identifies neither claimant, so
        // the gate refuses both. Only claimants sitting in that folder are counted: a Native - Copy
        // backup beside Modules\Native is ordinary practice and is not competing for Native's identity,
        // and counting it demoted the real Native as well as itself on an install whose certificates do
        // not chain. The copy still cannot pass the gate, because its folder is not named for the id.
        var canonicalCounts = modules
            .Where(IsInTheGamesOwnFolder)
            .GroupBy(m => m.Id)
            .ToDictionary(group => group.Key, group => group.Count());

        // Only a module whose own signature was proven may vouch, whether that proof was a trusted
        // chain or a pinned key: a module BEM could not verify is honored on its own claim but must
        // never launder someone else's, or an unverifiable module would vouch for a forger and carry
        // the claim straight back in. A 1.4.7 install has no trusted chain anywhere, so without the
        // pinned key counting here nothing on it could vouch for anything.
        var vouched = claimants
            .Where(m => IsProven(signatures[m.FolderPath].Verdict))
            .SelectMany(m => m.Dependencies)
            .Where(d => !d.IsOptional && !d.IsIncompatible)
            .Select(d => d.TargetId)
            .ToHashSet();

        var installCertificate = CertificateTheInstallAgreesOn(claimants, signatures, canonicalCounts);

        return [.. modules.Select(module => Judge(module, signatures, canonicalCounts, vouched, installCertificate))];
    }

    // How many of the game's own modules have to agree on an unrecognized certificate before it is
    // read as the install's own. Every real install carries at least six official modules that ship
    // assemblies, so three is well inside what a genuine build produces and well outside what a
    // hostile mod archive can: an archive drops one module folder, not a majority of the game's.
    private const int MinimumAgreeingModules = 3;

    // The certificate the install's own modules agree on, when agreement is the only evidence there
    // is. Null means there is none to read, and rule 2 then demotes every unrecognized certificate
    // exactly as it did before.
    //
    // Rule 2 has to demote a certificate that names TaleWorlds and proves nothing, or hostile code
    // dropped into Modules\Native under a self-signed certificate takes the game's own standing on
    // the folder name alone. What it also did was demote a genuine build: TaleWorlds has signed with
    // at least two certificates, only the 2018 one is pinned, and a third would answer
    // TaleWorldsUntrustedChain for every official module on the install at once, listing all nine as
    // community mods reachable by a batch Disable.
    //
    // Those two are not the same install, and the install says which one it is. A hostile module is
    // one folder: the game's own modules beside it still carry the real certificate, and if even one
    // of them proves it - a trusted chain, or the pinned key - then BEM knows what this install's
    // certificate looks like and a module answering with a different one is the odd one out. Only
    // when nothing on the whole install proves anything is agreement worth reading, and then the key
    // the game's own modules mostly carry is the install's own. A folder carrying a different key is
    // still refused against it, and a folder carrying none - a digest that does not cover its own
    // bytes, or two keys inside one module - is refused for want of one.
    //
    // What it costs a forger is the difference: one folder no longer buys official standing, because
    // he would have to replace the majority of the game's own module folders to move the agreement,
    // and an install in that state is not the game any more. The forgery the pin closed stays closed.
    private static string? CertificateTheInstallAgreesOn(
        IReadOnlyList<ModuleManifest> claimants,
        IReadOnlyDictionary<string, ModuleSignature> signatures,
        IReadOnlyDictionary<ModuleId, int> canonicalCounts)
    {
        if (claimants.Any(claimant => IsProven(signatures[claimant.FolderPath].Verdict)))
            return null;

        var byKey = claimants
            .Where(claimant => IsOneOfTheGamesOwn(claimant, canonicalCounts))
            .Select(claimant => signatures[claimant.FolderPath].UntrustedTaleWorldsKey)
            .OfType<string>()
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ToList();

        if (byKey.Count == 0 || byKey[0].Count() < MinimumAgreeingModules)
            return null;

        // A tie is no agreement. Half the game's own modules answering one key and half another is a
        // fact about an install nobody should be trusting either half of.
        return byKey.Count > 1 && byKey[1].Count() >= byKey[0].Count() ? null : byKey[0].Key;
    }

    // A module's verdict, folded from what Authenticode said about each of its files, best evidence
    // first. It lives in Core rather than beside the Windows API that produces the per-file answers
    // because this is the decision, and the decision is what has to be testable: the app project
    // holds only the WinVerifyTrust call, which no test can reach.
    //
    // A TaleWorlds subject anywhere outranks a foreign signature elsewhere on purpose, so an official
    // module that bundles a third-party helper assembly is not read as an impostor because of the
    // helper. Unreadable is last of the four remaining because it is the absence of evidence rather than
    // evidence: one file under a deny-read ACL used to answer for the whole module, so an impostor
    // that locked a single file inherited the honored-claim treatment over a folder of unsigned
    // assemblies BEM had read perfectly well. It still wins when nothing readable contradicts it,
    // which is what keeps a genuinely locked module from being demoted for a lock.
    //
    // The enumeration is walked lazily and abandoned on the first trusted signature, so a genuine
    // official module still answers on its first file rather than paying for a walk of its assets.
    public static OfficialSignature Fold(IEnumerable<OfficialSignature> fileSignatures)
    {
        ArgumentNullException.ThrowIfNull(fileSignatures);

        return FoldFiles(fileSignatures.Select(signature => new FileSignature(signature))).Verdict;
    }

    // The same fold, keeping the key behind an unrecognized TaleWorlds certificate so the install's
    // own modules can be compared with each other. There is one walk rather than two, because two
    // would be two rules that can come to disagree.
    public static ModuleSignature FoldFiles(IEnumerable<FileSignature> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var untrustedTaleWorlds = false;
        var foreign = false;
        var unsigned = false;
        var unreadable = false;
        var untrustedKey = (string?)null;
        var keysDisagree = false;

        foreach (var file in files)
        {
            switch (file.Verdict)
            {
                case OfficialSignature.SignedByTaleWorlds:
                    return new ModuleSignature(OfficialSignature.SignedByTaleWorlds);
                // A pinned key is proof of the same order as a trusted chain, so it ends the walk
                // for the same reason: nothing later in the folder can improve on it.
                case OfficialSignature.TaleWorldsPinnedKey:
                    return new ModuleSignature(OfficialSignature.TaleWorldsPinnedKey);
                case OfficialSignature.TaleWorldsUntrustedChain:
                    untrustedTaleWorlds = true;

                    // One module whose untrusted files answer with two different keys, or with none
                    // at all because a signature did not cover its own bytes, agrees with nothing.
                    // The module's key is dropped rather than the disagreement being settled in
                    // whichever direction the folder happened to be walked in.
                    if (file.UntrustedTaleWorldsKey is not { } key)
                        keysDisagree = true;
                    else if (untrustedKey is null)
                        untrustedKey = key;
                    else if (!string.Equals(untrustedKey, key, StringComparison.OrdinalIgnoreCase))
                        keysDisagree = true;

                    break;
                case OfficialSignature.SignedByOther:
                    foreign = true;
                    break;
                case OfficialSignature.Unsigned:
                    unsigned = true;
                    break;
                case OfficialSignature.Unavailable:
                    unreadable = true;
                    break;
            }
        }

        if (untrustedTaleWorlds)
            return new ModuleSignature(OfficialSignature.TaleWorldsUntrustedChain, keysDisagree ? null : untrustedKey);

        if (foreign)
            return new ModuleSignature(OfficialSignature.SignedByOther);

        if (unsigned)
            return new ModuleSignature(OfficialSignature.Unsigned);

        if (unreadable)
            return new ModuleSignature(OfficialSignature.Unavailable);

        // Nothing was enumerated at all. A module that ships no assembly is a different fact from one
        // whose assemblies are all unsigned, and the identity gate turns the two into opposite
        // verdicts: SandBoxCore ships nothing and is the game's own, while a Modules\Native full of
        // unsigned code is not.
        return new ModuleSignature(OfficialSignature.NoAssemblies);
    }

    private static ModuleManifest Judge(
        ModuleManifest module,
        IReadOnlyDictionary<string, ModuleSignature> signatures,
        IReadOnlyDictionary<ModuleId, int> canonicalCounts,
        IReadOnlySet<ModuleId> vouched,
        string? installCertificate)
    {
        if (!module.IsOfficial)
            return module;

        // Which bases demote is ModuleManifest's own rule, so the judged manifest is built first and
        // then asked. A second list here would be a list that can disagree with the first.
        var judged = module with
        {
            OfficialClaimBasis = Basis(module, signatures[module.FolderPath], canonicalCounts, vouched, installCertificate)
        };

        return judged.OfficialClaimDemoted ? judged with { Type = ModuleType.Community } : judged;
    }

    private static OfficialClaimBasis Basis(
        ModuleManifest module,
        ModuleSignature signature,
        IReadOnlyDictionary<ModuleId, int> canonicalCounts,
        IReadOnlySet<ModuleId> vouched,
        string? installCertificate) =>
        signature.Verdict switch
        {
            OfficialSignature.SignedByOther => OfficialClaimBasis.DemotedForeignSignature,
            // Above the identity gate for the same reason a foreign signature is: a certificate that
            // says TaleWorlds and proves nothing is something an attacker makes in a minute, and
            // while this fell through to the gate it bought hostile code in Modules\Native the game's
            // own standing on the folder name alone.
            //
            // The one certificate it does not refuse is the one the install's own modules agree on
            // and nothing on the install can prove, which is the shape a genuine build signed with a
            // third TaleWorlds certificate takes. That module falls through to the identity gate,
            // which still asks for the id, the folder, the place and the uniqueness before it stands.
            OfficialSignature.TaleWorldsUntrustedChain when !CarriesTheInstallsCertificate(signature, installCertificate) =>
                OfficialClaimBasis.DemotedUnrecognizedCertificate,
            OfficialSignature.SignedByTaleWorlds or OfficialSignature.TaleWorldsPinnedKey =>
                OfficialClaimBasis.SignatureVerified,
            _ when IsOneOfTheGamesOwn(module, canonicalCounts) => CarriesTheGamesOwnCode(module, signature.Verdict)
                ? OfficialClaimBasis.Identity
                : OfficialClaimBasis.DemotedWithoutGameCode,
            // The vouch carries the same code condition as the identity gate, and for the same reason.
            // The id a module declares is the one thing a voucher checks, and the impostor writes it,
            // so without this an unsigned folder of any name declaring Id="Native" was handed official
            // status by the real SandBox requiring Native - which is the identity gate's own refusal
            // laundered through a module that never named the impostor.
            _ when vouched.Contains(module.Id) && CarriesTheGamesOwnCode(module, signature.Verdict) => OfficialClaimBasis.Vouched,
            OfficialSignature.Unavailable => OfficialClaimBasis.Unverifiable,
            // The same refusal as the catch-all below, said accurately. This claimant took the id of
            // one of the game's own modules from somewhere the game does not keep it, so a sentence
            // built on the id names the real module, which is untouched, and a sentence saying the
            // name is not one of the game's own is simply false. The folder test is repeated rather
            // than inferred from the gate above, because the gate also refuses a claimant that is in
            // the game's own folder and shares the id with another folder there, and that one has
            // borrowed nothing.
            _ when OfficialModules.Contains(module.Id) && !IsInTheGamesOwnFolder(module) =>
                OfficialClaimBasis.DemotedBorrowedOfficialId,
            _ => OfficialClaimBasis.DemotedUnrecognized
        };

    // The second condition on the identity gate, and the reason unsigned hostile code dropped into
    // Modules\Native no longer passes it. Rule 2 fires only when a signature exists, so an attacker
    // who signs nothing matched every identity signal there is and inherited official status with
    // it, immunity from batch actions and exclusion from crash attribution included.
    //
    // Unavailable stays accepted on purpose. A file BEM could not open is not evidence of wrongdoing,
    // and a locked or in-use assembly must never read as an attack.
    //
    // NoAssemblies is accepted only for the modules the game itself ships without any: an empty
    // folder is the cheapest impostor there is, and while "ships none" alone satisfied this, a folder
    // called Native holding one SubModule.xml and nothing else kept the game's own standing.
    // SandBoxCore is the only official module that answers it on any of the three real installs, so
    // it is the only one the allowance is written for.
    private static bool CarriesTheGamesOwnCode(ModuleManifest module, OfficialSignature signature) =>
        signature switch
        {
            OfficialSignature.Unsigned => false,
            OfficialSignature.NoAssemblies => OfficialModules.ShipsNoAssemblies(module.Id),
            _ => true
        };

    // Whether this module's unrecognized certificate is the one the install's own modules agree on.
    // A module carrying no key never matches, so a signature that does not cover its own bytes is
    // refused here rather than rescued by a key it could not have made.
    private static bool CarriesTheInstallsCertificate(ModuleSignature signature, string? installCertificate) =>
        installCertificate is not null
        && signature.UntrustedTaleWorldsKey is { } key
        && string.Equals(key, installCertificate, StringComparison.OrdinalIgnoreCase);

    // What BEM proved rather than accepted: a chain this machine trusts, or a signature over the
    // file's own bytes made with one of TaleWorlds' pinned keys. Neither can be produced by anyone
    // who does not hold TaleWorlds' private key.
    private static bool IsProven(OfficialSignature signature) =>
        signature is OfficialSignature.SignedByTaleWorlds or OfficialSignature.TaleWorldsPinnedKey;

    // The identity gate. It is what rescues the 1.4.7 build the game's own downloader hands out,
    // whose certificate no longer chains, and what rescues SandBoxCore, which ships no assemblies at
    // all and so can never be signature-verified on any install.
    //
    // Four conditions, and all four have to hold. The id is one of the game's own; the folder is
    // named for that id, so a mod cannot borrow the name by declaring it from a folder called
    // something else; the folder sits directly under Modules, which is where the game puts its own
    // and is not where a Workshop copy lives; and no second folder in that same place declares the
    // same id, so a genuinely ambiguous install is refused rather than guessed at.
    private static bool IsOneOfTheGamesOwn(ModuleManifest module, IReadOnlyDictionary<ModuleId, int> canonicalCounts) =>
        OfficialModules.Contains(module.Id)
        && IsInTheGamesOwnFolder(module)
        && canonicalCounts.TryGetValue(module.Id, out var count)
        && count == 1;

    // Where the game itself puts a module of this id: a folder named for the id, directly under
    // Modules. Both halves are what a mod cannot borrow without taking the real module's place, and a
    // Workshop copy fails the second one because its parent folder is the Steam app id.
    private static bool IsInTheGamesOwnFolder(ModuleManifest module)
    {
        var folder = Path.GetFileName(module.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.Equals(folder, module.Id.Value, StringComparison.OrdinalIgnoreCase))
            return false;

        var parent = Path.GetFileName(Path.GetDirectoryName(module.FolderPath) ?? string.Empty);

        return string.Equals(parent, "Modules", StringComparison.OrdinalIgnoreCase);
    }
}
