using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Safety;

// The fingerprint of a trojanized mod, and nothing else.
//
// Every rule here has to hold to one standard: its expected output on a healthy install is empty. A
// check that fires on ordinary mods buries the one row that matters, and this project has already had
// to withdraw a check that did. Two rules were dropped or narrowed for exactly that reason, and the
// reasons are recorded next to them rather than in a commit message nobody reads.
//
// Rules are evaluated one string literal at a time, never against a concatenation of the file, so a
// rule that wants two tokens together cannot be satisfied by two unrelated blobs sitting next to each
// other in the file.
public static partial class SafetySignatures
{
    // Hosts that are not a mod phoning somewhere shady: certificate authorities, XML namespace ids,
    // the places mods are actually published and paid for, and the loopback and private ranges.
    //
    // Matched against the URL's HOST only. The tool this came from matched the whole URL, which meant
    // a path containing "github.com" would clear any host at all.
    [GeneratedRegex(
        @"(^|\.)(microsoft\.com|windows\.com|msftconnecttest\.com|digicert\.com|verisign\.com|globalsign\.com"
        + @"|sectigo\.com|comodoca\.com|symantec\.com|thawte\.com|entrust\.net|usertrust\.com|godaddy\.com"
        + @"|letsencrypt\.org|amazontrust\.com|aka\.ms|dot\.net|dotnetfoundation\.org|nuget\.org|visualstudio\.com"
        + @"|w3\.org|xmlsoap\.org|oasis-open\.org|purl\.org|adobe\.com|json\.org|json-schema\.org|xml\.org"
        + @"|newtonking\.com|newtonsoft\.com|github\.com|githubusercontent\.com|github\.io|gitlab\.com"
        + @"|bitbucket\.org|nexusmods\.com|taleworlds\.com|butr\.link|moddb\.com|bannerlord\.party"
        + @"|boosty\.to|patreon\.com|ko-fi\.com|kofi\.com|paypal\.com|buymeacoffee\.com|discord\.com|discord\.gg"
        + @"|reddit\.com|youtube\.com|youtu\.be|steamcommunity\.com|steampowered\.com|googleapis\.com"
        + @"|gstatic\.com|schemas\.android\.com|mono-project\.com|unity3d\.com)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrustedHost { get; }

    [GeneratedRegex(
        @"^\d{1,3}(\.\d{1,3}){3}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Host { get; }

    // No "com" in this list on purpose: .com is the commonest domain suffix, and a bare https://host.com
    // would otherwise read as a ".com executable". Legacy .com binaries are extinct; the collisions
    // are not.
    [GeneratedRegex(
        @"\.(ps1|psm1|exe|dll|bat|cmd|scr|vbs|vbe|jse|wsf|hta|msi|jar|reg)([?#]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutablePath { get; }

    [GeneratedRegex(
        @"https?://[A-Za-z0-9\.\-_~%]+(:\d+)?(/[A-Za-z0-9\.\-_~%/\?=&:@+!$'()*,;]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url { get; }

    // The PowerShell encoded-command flag FOLLOWED BY AN ACTUAL BLOB, in the same literal.
    //
    // The flag on its own is not evidence and must never be treated as such. Legitimate code
    // base64-encodes a script body it built at runtime purely to avoid quoting it through a command
    // line, and its literal therefore holds the flag and nothing else. On a real install one mod
    // does exactly that, and a rule keyed on the flag alone accused it. A blob that is already in the
    // file is a payload the author shipped, which is a different thing entirely.
    [GeneratedRegex(
        @"(^|\s|"")-e(nc|ncodedcommand)?\s+[A-Za-z0-9+/]{40,}={0,2}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodedCommandWithPayload { get; }

    [GeneratedRegex(
        @"(-w(indowstyle)?\s+hidden|-windowstyle\s+hidden|\bhidden\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiddenWindow { get; }

    [GeneratedRegex(
        @"\b(powershell(\.exe)?|pwsh(\.exe)?|cmd\.exe|mshta|wscript|cscript|rundll32|regsvr32)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptHost { get; }

    // Fetching something and running it. Not "can reach the network": that is SafetyCapability.
    [GeneratedRegex(
        @"(downloadstring|downloadfile|downloaddata|invoke-webrequest|start-bitstransfer|\biwr\b|\bcurl\b"
        + @"|\bwget\b|invoke-expression|\biex\b|frombase64string|\[convert\]::frombase64)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadAndRun { get; }

    // The places Windows runs things from. This matches the name and never a write to it: a literal
    // carries no verb, so what the code does with the name is outside what this can see.
    [GeneratedRegex(
        @"(CurrentVersion\\+Run(Once)?\b|\\+Start Menu\\+Programs\\+Startup\b|\bschtasks(\.exe)?\s+/create\b"
        + @"|Register-ScheduledTask\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartupPersistence { get; }

    // A base64 run long enough to be a script rather than a key or a hash.
    [GeneratedRegex(
        @"[A-Za-z0-9+/]{60,}={0,2}",
        RegexOptions.CultureInvariant)]
    private static partial Regex Base64Run { get; }

    // A pattern that describes a command is not a command.
    //
    // Found by running this scanner over a real downloads folder, which held the source of the
    // tool this capability came from. Its rule definitions name a script host, a hidden window and a
    // download primitive on one line, so the fingerprint matched a file whose whole purpose is to
    // detect that fingerprint. Any antivirus signature file, any write-up of the attack, and any mod
    // that defends against it would have been accused the same way.
    //
    // Every construct listed is one that cannot occur in a real command line or a Windows path: an
    // inline regex option, a lookaround, a counted quantifier or a character class.
    [GeneratedRegex(
        @"(\(\?[imsxn:=!])|(\{\d+,\d*\})|(\[\^)|(\[A-Za-z)|(\[0-9)|(\\[sdwSDW]\{)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DescribesRatherThanRuns { get; }

    private const int MatchedTextLimit = 200;

    public static IReadOnlyList<SafetyEvidence> Inspect(IEnumerable<string> literals, ISet<string> unrecognizedHosts)
    {
        ArgumentNullException.ThrowIfNull(literals);
        ArgumentNullException.ThrowIfNull(unrecognizedHosts);

        var evidence = new List<SafetyEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var literal in literals)
        {
            foreach (var found in InspectOne(literal, unrecognizedHosts))
            {
                if (seen.Add($"{found.Signal}|{found.Matched}"))
                    evidence.Add(found);
            }
        }

        return evidence;
    }

    private static IEnumerable<SafetyEvidence> InspectOne(string literal, ISet<string> unrecognizedHosts)
    {
        if (DescribesRatherThanRuns.IsMatch(literal))
            yield break;

        if (EncodedCommandWithPayload.Match(literal) is { Success: true } encoded)
        {
            yield return new SafetyEvidence(
                SafetySignal.EncodedPowerShellPayload,
                Trim(encoded.Value),
                Strings.Current["Core.Safety.Signature.EncodedCommand"]);
        }

        // A blob that decodes to a PowerShell downloader is the attack itself, whatever flag sits
        // next to it. Decoding is done here, in memory, and never executed.
        foreach (var blob in Base64Run.Matches(literal).Cast<Match>().Take(8))
        {
            if (Decode(blob.Value) is not { } decoded)
                continue;

            yield return new SafetyEvidence(
                SafetySignal.EncodedPowerShellPayload,
                Trim(decoded),
                Strings.Current["Core.Safety.Signature.DecodedPayload"]);
        }

        if (ScriptHost.IsMatch(literal) && HiddenWindow.IsMatch(literal) && DownloadAndRun.IsMatch(literal))
        {
            yield return new SafetyEvidence(
                SafetySignal.HiddenPowerShellDownload,
                Trim(literal),
                Strings.Current["Core.Safety.Signature.HiddenDownload"]);
        }

        // What was found, not what it means. The pattern matches a name, and a name in a string literal
        // is not a write: a mod that reads the Run key to check whether something else installed itself,
        // and a write-up of the attack, both carry the same literal. The one thing that is certainly
        // true is that the literal is there, so that is what the row says.
        if (StartupPersistence.Match(literal) is { Success: true } persistence)
        {
            yield return new SafetyEvidence(
                SafetySignal.StartupPersistence,
                Trim(literal),
                Strings.Current.Format("Core.Safety.Signature.StartupPersistence", persistence.Value.Trim()));
        }

        foreach (var evidence in Urls(literal, unrecognizedHosts))
            yield return evidence;
    }

    private static IEnumerable<SafetyEvidence> Urls(string literal, ISet<string> unrecognizedHosts)
    {
        foreach (var match in Url.Matches(literal).Cast<Match>().Take(64))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ')', '\'', '"');
            var host = HostOf(url);

            if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                continue;

            // The allowlist is consulted FIRST, before the payload-extension rule is ever reached.
            // The tool this came from tested the extension first and only fell through to the
            // allowlist in the else branch, which made Microsoft's own Visual C++ redistributable
            // link read as a payload download and produced the one false positive on a real
            // install. Order is the whole fix.
            if (TrustedHost.IsMatch(host))
                continue;

            if (Ipv4Host.IsMatch(host))
            {
                if (IsPrivateOrLoopback(host))
                    continue;

                yield return new SafetyEvidence(
                    SafetySignal.RawIpUrl,
                    url,
                    Strings.Current["Core.Safety.Signature.RawIp"]);

                continue;
            }

            if (ExecutablePath.IsMatch(url))
            {
                yield return new SafetyEvidence(
                    SafetySignal.PayloadUrl,
                    url,
                    Strings.Current["Core.Safety.Signature.PayloadUrl"]);

                continue;
            }

            unrecognizedHosts.Add(host);
        }
    }

    private static bool IsPrivateOrLoopback(string host)
    {
        var parts = host.Split('.');

        if (parts.Length != 4
            || !byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
            || !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var second))
            return true;

        return first switch
        {
            0 or 10 or 127 => true,
            169 => second == 254,
            172 => second is >= 16 and <= 31,
            192 => second == 168,
            _ => false
        };
    }

    private static string HostOf(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);

        if (scheme < 0)
            return string.Empty;

        var rest = url[(scheme + 3)..];
        var end = rest.AsSpan().IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');

        if (at >= 0)
            authority = authority[(at + 1)..];

        var port = authority.IndexOf(':', StringComparison.Ordinal);

        return (port < 0 ? authority : authority[..port]).TrimEnd('.');
    }

    // Returns the decoded text only when it reads as a downloader, so the caller never has to guess
    // which encoding was right. UTF-16LE is what PowerShell's own -EncodedCommand uses; UTF-8 is what
    // a hand-rolled encoder reaches for. Both are tried, and neither is executed.
    //
    // Deciding the encoding by inspecting the bytes and reporting whatever came out was tried first.
    // It read every compressed blob in a real assembly as CJK text, and the real install went from
    // zero suspicious modules to twelve. Deciding by whether the result is a downloader is the fix.
    private static string? Decode(string blob)
    {
        var trimmed = blob.TrimEnd('=');
        var usable = trimmed.Length / 4 * 4;

        if (usable < 40)
            return null;

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(trimmed[..usable]);
        }
        catch (FormatException)
        {
            return null;
        }

        foreach (var decoded in new[] { Encoding.Unicode.GetString(bytes), Encoding.UTF8.GetString(bytes) })
        {
            if (ScriptHost.IsMatch(decoded) && DownloadAndRun.IsMatch(decoded))
                return decoded;
        }

        return null;
    }

    private static string Trim(string value)
    {
        var collapsed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return collapsed.Length <= MatchedTextLimit
            ? collapsed
            : collapsed[..MatchedTextLimit] + "...";
    }
}
