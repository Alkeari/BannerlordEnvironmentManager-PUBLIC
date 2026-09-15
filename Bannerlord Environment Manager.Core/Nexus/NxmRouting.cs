using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NxmAction
{
    Handle,
    Forward,
    NoHandler
}

public sealed record NxmRoute(NxmAction Action, string? ForwardCommand, string Reason);

// BEM answers for Bannerlord downloads and passes everything else straight through to whoever held
// the scheme before it. Swallowing another game's download, or a Vortex login callback, would be a
// far worse failure than never claiming the scheme at all.
public static class NxmRouting
{
    public static NxmRoute Decide(NxmLink link, string? previousCommand)
    {
        if (link.IsBannerlordDownload)
            return new NxmRoute(NxmAction.Handle, null, Strings.Current.Format("Core.Nexus.Routing.BannerlordMod", link.ModId, link.FileId));

        var what = link.Kind switch
        {
            NxmLinkKind.OtherGameMod => Strings.Current.Format("Core.Nexus.Routing.What.OtherGameMod", link.GameDomain),
            NxmLinkKind.Collection => Strings.Current.Format("Core.Nexus.Routing.What.Collection", link.GameDomain ?? "Nexus"),
            NxmLinkKind.OAuthCallback => Strings.Current["Core.Nexus.Routing.What.OAuthCallback"],
            NxmLinkKind.Premium => Strings.Current["Core.Nexus.Routing.What.Premium"],
            _ => Strings.Current["Core.Nexus.Routing.What.Unrecognized"]
        };

        return string.IsNullOrWhiteSpace(previousCommand)
            ? new NxmRoute(NxmAction.NoHandler, null,
                Strings.Current.Format("Core.Nexus.Routing.NoHandler", what))
            : new NxmRoute(NxmAction.Forward, previousCommand, Strings.Current.Format("Core.Nexus.Routing.Forwarded", what));
    }

    // The saved command is replayed whole. Vortex registers itself as "Vortex.exe" -d "%1", and
    // replaying only the executable would hand Vortex a link with no -d, which it then treats as
    // something else entirely.
    public static (string Executable, string Arguments)? BuildForward(string? previousCommand, string url)
    {
        var command = previousCommand?.Trim();

        if (string.IsNullOrEmpty(command))
            return null;

        string executable;
        string rest;

        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);

            if (closing < 0)
                return null;

            executable = command[1..closing];
            rest = command[(closing + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            executable = space < 0 ? command : command[..space];
            rest = space < 0 ? string.Empty : command[(space + 1)..].Trim();
        }

        if (executable.Length == 0)
            return null;

        var arguments = rest.Contains("%1", StringComparison.Ordinal)
            ? rest.Replace("%1", url, StringComparison.Ordinal)
            : string.IsNullOrEmpty(rest) ? $"\"{url}\"" : $"{rest} \"{url}\"";

        return (executable, arguments);
    }
}
