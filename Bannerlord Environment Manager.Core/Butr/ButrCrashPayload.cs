using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

public sealed record ButrCrashModule(
    string ModuleId,
    string ModuleVersion,
    string Name,
    bool IsOfficial,
    bool IsExternal = false,
    bool IsSingleplayer = true,
    bool IsMultiplayer = false,
    string? Url = null);

public sealed record ButrCrashInvolvement(string ModuleId, string FrameName);

public sealed record ButrCrashException(
    string TypeFullName,
    string Message,
    string CallStack,
    ButrCrashException? Inner = null);

// Everything BEM is willing to say about one crash, in strings only. Nothing here knows what a load
// order, a module scan or an attribution verdict is: the caller translates, this namespace speaks
// the wire's vocabulary and nothing else.
public sealed record ButrCrashSubmission(
    string GameVersion,
    ButrCrashException Exception,
    IReadOnlyList<ButrCrashModule> Modules,
    IReadOnlyList<ButrCrashInvolvement> Involved,
    string? SourceModuleId = null,
    string? LauncherType = null,
    string? LauncherVersion = null,
    string? LoaderName = null,
    string? LoaderVersion = null,
    string? Runtime = null,
    string? OperatingSystemVersion = null);

// Nothing that identifies a machine or a person may reach a third party. Every string that goes on
// the wire passes through here first, and the test that proves it walks the finished JSON rather
// than trusting any call site to have remembered.
public static partial class ButrRedaction
{
    public const string Removed = "[removed]";

    private const int ShortestScrubbableSecret = 3;

    // A stack frame's file and line are the single richest source of a Windows user name in a crash
    // report, and they say nothing about which module faulted that the frame itself does not.
    [GeneratedRegex(@"\s+in\s+[^\r\n]*?:line\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex FileAndLine { get; }

    // The lookbehind keeps a URL scheme out of it: without it the "s:/" of "https://" reads as a
    // drive letter and every mod's Nexus link is destroyed.
    //
    // A space is swallowed only when a separator still follows it, so "Mount and Blade II
    // Bannerlord\Configs" goes whole while "C:\logs and then it failed" stops at the path. Half a
    // path is worse than none: the half that survives is the half nearer the user's own folders.
    [GeneratedRegex(@"(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\)(?:[^\s""'<>|)\]]|[ \t](?=[^\r\n""'<>|)\]]*\\))*")]
    private static partial Regex WindowsPath { get; }

    // Long, mixed case, digit-bearing and unbroken: an api key or a token, never a .NET identifier,
    // which is broken by dots long before it reaches this length.
    [GeneratedRegex(@"[A-Za-z0-9+/=_-]{40,}")]
    private static partial Regex LongToken { get; }

    public static IReadOnlyList<string> MachineSecrets() =>
    [
        Environment.UserName,
        Environment.MachineName,
        Environment.UserDomainName
    ];

    public static string Text(string? value, IReadOnlyList<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = FileAndLine.Replace(value.ReplaceLineEndings("\n"), string.Empty);

        text = WindowsPath.Replace(text, Removed);

        foreach (var secret in secrets ?? MachineSecrets())
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= ShortestScrubbableSecret)
                text = Replace(text, secret);
        }

        return LongToken.Replace(text, match => LooksLikeAKey(match.Value) ? Removed : match.Value);
    }

    // Kept only when it is a public web address. A <Url> holding anything else is a local path as
    // often as it is a link, and the service reads this field to learn a mod's Nexus or Workshop id.
    public static string? Url(string? value, IReadOnlyList<string>? secrets = null)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        var clean = Text(uri.GetLeftPart(UriPartial.Path), secrets);

        return clean.Contains(Removed, StringComparison.Ordinal) ? null : clean;
    }

    private static bool LooksLikeAKey(string token) =>
        token.Any(char.IsDigit) && token.Any(char.IsUpper) && token.Any(char.IsLower);

    private static string Replace(string text, string secret)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var found = text.IndexOf(secret, index, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
                break;

            builder.Append(text, index, found - index).Append(Removed);
            index = found + secret.Length;
        }

        return index == 0 ? text : builder.Append(text, index, text.Length - index).ToString();
    }
}

public enum ButrPayloadOutcome
{
    Ready,
    NoGameVersion,
    NoModules,
    NoCallStack,
    NotAttributed
}

public sealed record ButrPayload(ButrPayloadOutcome Outcome, string Json, string Fingerprint, string Reason)
{
    public bool IsReady => Outcome is ButrPayloadOutcome.Ready;
}

// Builds the crash report BUTR's own uploader builds, in the shape BUTR's own server reads. The
// contract was taken from ButterLib's BUTRCrashUploader, from BUTR.CrashReport.Models, and from the
// server's CrashUploadController and JsonHandlerV14, not guessed.
//
// The compatibility score is a count: every report raises "involved" for the modules it blames and
// "not involved" for every other module in the list. A report BEM cannot attribute would therefore
// quietly improve every installed mod's public score on the strength of a crash nobody explained,
// so it is refused rather than sent empty.
public static class ButrCrashPayload
{
    public const byte ReportVersion = 14;

    private const string GameName = "Mount & Blade II Bannerlord";

    public static ButrPayload Build(
        ButrCrashSubmission submission,
        Guid id,
        IReadOnlyList<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (ButrGameVersion.Format(submission.GameVersion) is not { } gameVersion)
            return Refused(ButrPayloadOutcome.NoGameVersion, Strings.Current["Core.Butr.CrashPayload.NoGameVersion"]);

        if (submission.Modules.Count == 0)
            return Refused(ButrPayloadOutcome.NoModules, Strings.Current["Core.Butr.CrashPayload.NoModules"]);

        var exception = Clean(submission.Exception, secrets);

        if (exception is null || !CarriesFrames(exception.CallStack))
            return Refused(ButrPayloadOutcome.NoCallStack, Strings.Current["Core.Butr.CrashPayload.NoCallStack"]);

        var known = submission.Modules.Select(module => module.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var involved = submission.Involved
            .Where(entry => known.Contains(entry.ModuleId))
            .DistinctBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (involved.Count == 0)
            return Refused(ButrPayloadOutcome.NotAttributed, Strings.Current["Core.Butr.CrashPayload.NotAttributed"]);

        return new ButrPayload(
            ButrPayloadOutcome.Ready,
            Write(submission, gameVersion, exception, involved, id, secrets),
            Fingerprint(gameVersion, exception, submission.Modules),
            Strings.Current.Plural("Core.Butr.CrashPayload.Ready", submission.Modules.Count, involved.Count));
    }

    // Identifies the crash on this machine so the same one is never sent twice. Never sent anywhere:
    // the id on the wire is random, because two people hitting the same crash are two data points and
    // a shared identifier would silently collapse them into one.
    public static string Fingerprint(string gameVersion, ButrCrashException exception, IReadOnlyList<ButrCrashModule> modules)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(modules);

        var parts = new List<string> { gameVersion, exception.TypeFullName, exception.Message, exception.CallStack };

        parts.AddRange(modules
            .Select(module => $"{module.ModuleId}@{module.ModuleVersion}")
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase));

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));

        return Convert.ToHexStringLower(hash);
    }

    // A stack that was nothing but a path is nothing once the path is gone, and the service reads
    // the first call stack line to render the report, so an empty one takes the whole upload down.
    private static bool CarriesFrames(string callStack) =>
        !string.IsNullOrWhiteSpace(callStack.Replace(ButrRedaction.Removed, string.Empty));

    private static ButrPayload Refused(ButrPayloadOutcome outcome, string reason) =>
        new(outcome, string.Empty, string.Empty, reason);

    private static ButrCrashException? Clean(ButrCrashException? exception, IReadOnlyList<string>? secrets)
    {
        if (exception is null)
            return null;

        return new ButrCrashException(
            ButrRedaction.Text(exception.TypeFullName, secrets),
            ButrRedaction.Text(exception.Message, secrets),
            ButrRedaction.Text(exception.CallStack, secrets),
            Clean(exception.Inner, secrets));
    }

    private static string Write(
        ButrCrashSubmission submission,
        string gameVersion,
        ButrCrashException exception,
        IReadOnlyList<ButrCrashInvolvement> involved,
        Guid id,
        IReadOnlyList<string>? secrets)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("crashReport");
            writer.WriteStartObject();

            writer.WriteString("id", id);
            writer.WriteNumber("version", ReportVersion);

            writer.WritePropertyName("exception");
            WriteException(writer, exception, submission.SourceModuleId);

            writer.WritePropertyName("metadata");
            WriteMetadata(writer, submission, gameVersion, secrets);

            writer.WritePropertyName("modules");
            writer.WriteStartArray();

            foreach (var module in submission.Modules)
                WriteModule(writer, module, secrets);

            writer.WriteEndArray();

            writer.WritePropertyName("involvedModules");
            writer.WriteStartArray();

            foreach (var entry in involved)
            {
                writer.WriteStartObject();
                writer.WriteString("moduleOrLoaderPluginId", ButrRedaction.Text(entry.ModuleId, secrets));
                writer.WriteString("enhancedStacktraceFrameName", ButrRedaction.Text(entry.FrameName, secrets));
                Empty(writer, "additionalMetadata");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // BEM was not in the process when this crashed, so it has no assemblies, no native
            // modules, no Harmony registry and no loader plugin list to report. Empty says exactly
            // that, and the service reads none of them when it counts a score.
            Empty(writer, "enhancedStacktrace");
            Empty(writer, "assemblies");
            Empty(writer, "nativeModules");
            Empty(writer, "harmonyPatches");
            Empty(writer, "loaderPlugins");
            Empty(writer, "involvedLoaderPlugins");
            Empty(writer, "additionalMetadata");

            writer.WriteEndObject();

            Empty(writer, "logSources");

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteException(Utf8JsonWriter writer, ButrCrashException exception, string? sourceModuleId)
    {
        writer.WriteStartObject();
        writer.WriteNull("sourceAssemblyId");

        if (string.IsNullOrWhiteSpace(sourceModuleId))
            writer.WriteNull("sourceModuleId");
        else
            writer.WriteString("sourceModuleId", sourceModuleId);

        writer.WriteNull("sourceLoaderPluginId");
        writer.WriteString("type", exception.TypeFullName);
        writer.WriteString("message", exception.Message);
        writer.WriteString("callStack", exception.CallStack);

        if (exception.Inner is { } inner)
        {
            writer.WritePropertyName("innerException");
            WriteException(writer, inner, null);
        }
        else
        {
            writer.WriteNull("innerException");
        }

        Empty(writer, "additionalMetadata");
        writer.WriteEndObject();
    }

    private static void WriteMetadata(
        Utf8JsonWriter writer,
        ButrCrashSubmission submission,
        string gameVersion,
        IReadOnlyList<string>? secrets)
    {
        writer.WriteStartObject();
        writer.WriteString("gameName", GameName);
        writer.WriteString("gameVersion", gameVersion);
        Optional(writer, "loaderPluginProviderName", submission.LoaderName, secrets);
        Optional(writer, "loaderPluginProviderVersion", submission.LoaderVersion, secrets);
        Optional(writer, "launcherType", submission.LauncherType, secrets);
        Optional(writer, "launcherVersion", submission.LauncherVersion, secrets);
        Optional(writer, "runtime", submission.Runtime, secrets);
        writer.WriteString("operatingSystemType", "windows");
        Optional(writer, "operatingSystemVersion", submission.OperatingSystemVersion, secrets);
        Empty(writer, "additionalMetadata");
        writer.WriteEndObject();
    }

    private static void WriteModule(Utf8JsonWriter writer, ButrCrashModule module, IReadOnlyList<string>? secrets)
    {
        writer.WriteStartObject();
        writer.WriteString("id", ButrRedaction.Text(module.ModuleId, secrets));
        writer.WriteString("name", ButrRedaction.Text(module.Name, secrets));
        writer.WriteString("version", ButrRedaction.Text(module.ModuleVersion, secrets));
        writer.WriteBoolean("isExternal", module.IsExternal);
        writer.WriteBoolean("isOfficial", module.IsOfficial);
        writer.WriteBoolean("isSingleplayer", module.IsSingleplayer);
        writer.WriteBoolean("isMultiplayer", module.IsMultiplayer);

        if (ButrRedaction.Url(module.Url, secrets) is { } url)
            writer.WriteString("url", url);
        else
            writer.WriteNull("url");

        writer.WriteNull("updateInfo");
        Empty(writer, "dependencyMetadatas");
        Empty(writer, "subModules");
        Empty(writer, "capabilities");
        Empty(writer, "additionalMetadata");
        writer.WriteEndObject();
    }

    private static void Optional(Utf8JsonWriter writer, string name, string? value, IReadOnlyList<string>? secrets)
    {
        if (string.IsNullOrWhiteSpace(value))
            writer.WriteNull(name);
        else
            writer.WriteString(name, ButrRedaction.Text(value, secrets));
    }

    private static void Empty(Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        writer.WriteEndArray();
    }
}
