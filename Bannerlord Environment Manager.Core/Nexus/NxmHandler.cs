using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NxmHandlerAction
{
    Disabled,
    Registered,
    Reasserted,
    AlreadyOurs,
    Unregistered,
    RestoredPrevious,
    NothingToUndo,
    Failed
}

public sealed record NxmHandlerResult(NxmHandlerAction Action, string Message, bool ShouldAnnounce = false);

public sealed record ProtocolHandlerEntry(string? Command, string? DefaultValue, bool IsBem);

// The registry is Windows-only, so Core only ever sees this. The Windows implementation lives in
// Services, exactly as DPAPI does.
public interface INxmRegistry
{
    ProtocolHandlerEntry Read();

    void WriteHandler(string command, string defaultValue, bool markAsBem);

    void DeleteHandler();
}

public sealed record NxmSettings(
    bool HandlerEnabled = true,
    string? PreviousCommand = null,
    string? PreviousDefaultValue = null,
    bool FirstRegistrationAnnounced = false,
    DateTimeOffset? RegisteredUtc = null)
{
    public static NxmSettings Default { get; } = new();
}

public sealed class NxmSettingsStore(string filePath)
{
    public const string FileName = "nxm-settings.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public NxmSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<NxmSettings>(File.ReadAllText(FilePath), Format) ?? NxmSettings.Default
                : NxmSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return NxmSettings.Default;
        }
    }

    public void Save(NxmSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}

// Owns the nxm:// scheme on this user's account, and hands everything that is not a Bannerlord
// download to whoever held it before.
//
// Vortex rewrites this key on every launch of Vortex, unconditionally, unless its own "Handle Nexus
// Links" setting has been turned off by hand. BEM therefore re-asserts on every launch of BEM. The
// danger in doing that is recording your own displaced registration as the previous handler, which
// turns forwarding into an infinite call to yourself. MO2 guards against that by comparing file
// names; BEM cannot, because the exe is meant to be moved and renamed. The registration therefore
// carries a marker naming BEM as its owner, and a registration carrying it is never saved as
// something to forward to.
public sealed class NxmHandler(INxmRegistry registry, NxmSettingsStore store, string executablePath)
{
    public const string Scheme = "nxm";

    public const string ProgIdDefaultValue = "URL:NXM Protocol (Bannerlord Environment Manager)";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    public NxmSettings Settings => store.Load();

    public void SaveEnabled(bool enabled) => store.Save(store.Load() with { HandlerEnabled = enabled });

    public NxmHandlerResult Reassert() =>
        store.Load().HandlerEnabled
            ? Claim(userAsked: false)
            : new NxmHandlerResult(NxmHandlerAction.Disabled, Strings.Current["Core.Nexus.NxmHandler.NotSetToHandle"]);

    public NxmHandlerResult Register() => Claim(userAsked: true);

    public NxmHandlerResult Unregister()
    {
        var settings = store.Load();
        var current = ReadRegistry();

        if (current is null)
            return new NxmHandlerResult(NxmHandlerAction.Failed, Strings.Current["Core.Nexus.NxmHandler.CouldNotRead"]);

        if (!current.IsBem && settings.PreviousCommand is null && settings.RegisteredUtc is null)
            return new NxmHandlerResult(NxmHandlerAction.NothingToUndo,
                Strings.Current["Core.Nexus.NxmHandler.NothingToUndo"]);

        try
        {
            if (settings.PreviousCommand is { } previous)
            {
                registry.WriteHandler(previous, settings.PreviousDefaultValue ?? $"URL:{Scheme}", markAsBem: false);
                store.Save(settings with { PreviousCommand = null, PreviousDefaultValue = null, RegisteredUtc = null });

                return new NxmHandlerResult(NxmHandlerAction.RestoredPrevious,
                    Strings.Current.Format("Core.Nexus.NxmHandler.RestoredPrevious", previous));
            }

            registry.DeleteHandler();
            store.Save(settings with { PreviousCommand = null, PreviousDefaultValue = null, RegisteredUtc = null });

            return new NxmHandlerResult(NxmHandlerAction.Unregistered,
                Strings.Current["Core.Nexus.NxmHandler.Unregistered"]);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new NxmHandlerResult(NxmHandlerAction.Failed, Strings.Current.Format("Core.Nexus.NxmHandler.CouldNotUndo", ex.Message));
        }
    }

    public NxmRoute Route(NxmLink link) => NxmRouting.Decide(link, store.Load().PreviousCommand);

    private NxmHandlerResult Claim(bool userAsked)
    {
        var settings = store.Load();
        var current = ReadRegistry();

        if (current is null)
            return new NxmHandlerResult(NxmHandlerAction.Failed, Strings.Current["Core.Nexus.NxmHandler.CouldNotRead"]);

        var wanted = BuildCommand(executablePath);
        var ours = IsOurs(current);

        if (ours && string.Equals(current.Command, wanted, StringComparison.OrdinalIgnoreCase))
            return new NxmHandlerResult(NxmHandlerAction.AlreadyOurs, Strings.Current["Core.Nexus.NxmHandler.AlreadyOurs"]);

        var previousCommand = ours ? settings.PreviousCommand : current.Command ?? settings.PreviousCommand;
        var previousDefault = ours ? settings.PreviousDefaultValue : current.DefaultValue ?? settings.PreviousDefaultValue;

        try
        {
            registry.WriteHandler(wanted, ProgIdDefaultValue, markAsBem: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new NxmHandlerResult(NxmHandlerAction.Failed, Strings.Current.Format("Core.Nexus.NxmHandler.CouldNotWrite", ex.Message));
        }

        var announce = !settings.FirstRegistrationAnnounced;

        store.Save(settings with
        {
            PreviousCommand = previousCommand,
            PreviousDefaultValue = previousDefault,
            FirstRegistrationAnnounced = true,
            RegisteredUtc = DateTimeOffset.UtcNow
        });

        var handedFrom = previousCommand is null
            ? Strings.Current["Core.Nexus.NxmHandler.NothingHeldBefore"]
            : Strings.Current.Format("Core.Nexus.NxmHandler.HandedFrom", Executable(previousCommand));

        return new NxmHandlerResult(
            settings.RegisteredUtc is null && userAsked ? NxmHandlerAction.Registered : NxmHandlerAction.Reasserted,
            Strings.Current.Format("Core.Nexus.NxmHandler.NowHandles", handedFrom),
            announce);
    }

    private ProtocolHandlerEntry? ReadRegistry()
    {
        try
        {
            return registry.Read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // Two independent checks, because either alone is defeatable: the marker survives a rename, and
    // the path comparison survives a marker that some other tool cleared.
    private bool IsOurs(ProtocolHandlerEntry entry) =>
        entry.IsBem
        || (entry.Command is { } command
            && string.Equals(Executable(command), executablePath, StringComparison.OrdinalIgnoreCase));

    private static string Executable(string command)
    {
        var trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing < 0 ? trimmed[1..] : trimmed[1..closing];
        }

        var space = trimmed.IndexOf(' ');

        return space < 0 ? trimmed : trimmed[..space];
    }
}
