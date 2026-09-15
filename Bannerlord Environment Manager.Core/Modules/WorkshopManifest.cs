using System.Text;

namespace BannerlordEnvironmentManager.Core.Modules;

// Steam's own record of the Workshop items it holds for one app, kept as appworkshop_<appid>.acf beside
// the content folder. An item is listed under WorkshopItemDetails from the moment it is subscribed and
// under WorkshopItemsInstalled once its files are down, and unsubscribing takes it out of both. So an
// item folder whose id is in neither is no longer Steam's: whatever remains in it, a .bak an edit left
// beside SubModule.xml for one, is leftovers Steam will neither restore nor remove.
public static class WorkshopManifest
{
    private static readonly string[] HoldingBlocks = ["WorkshopItemsInstalled", "WorkshopItemDetails"];

    public static string PathFor(string steamappsFolder, string appId) =>
        Path.Combine(steamappsFolder, "workshop", $"appworkshop_{appId}.acf");

    // Null for a file that is missing, unreadable or not shaped like Steam's. A caller deciding whether a
    // folder may be deleted reads null as still Steam's, because the one wrong answer that costs anything
    // is deleting a subscription's files.
    public static IReadOnlySet<string>? HeldItemIds(string acfPath)
    {
        ArgumentNullException.ThrowIfNull(acfPath);

        try
        {
            return File.Exists(acfPath) ? Parse(File.ReadAllText(acfPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlySet<string>? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Tokenize(text) is not { } tokens)
            return null;

        var held = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        var sawRoot = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.IsClose)
            {
                if (path.Count == 0)
                    return null;

                path.RemoveAt(path.Count - 1);
                continue;
            }

            if (token.Text is null)
                return null;

            var next = index + 1 < tokens.Count ? tokens[index + 1] : default;

            if (next.IsOpen)
            {
                if (path.Count == 0)
                {
                    if (sawRoot || !token.Text.Equals("AppWorkshop", StringComparison.OrdinalIgnoreCase))
                        return null;

                    sawRoot = true;
                }
                else if (path.Count == 2 && HoldingBlocks.Contains(path[1], StringComparer.OrdinalIgnoreCase))
                {
                    held.Add(token.Text);
                }

                path.Add(token.Text);
                index++;
                continue;
            }

            if (next.Text is null)
                return null;

            index++;
        }

        return sawRoot && path.Count == 0 ? held : null;
    }

    private readonly record struct Token(string? Text, bool IsOpen, bool IsClose);

    private static List<Token>? Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '{' || character == '}')
            {
                tokens.Add(new Token(null, character == '{', character == '}'));
                index++;
                continue;
            }

            if (character != '"')
                return null;

            var value = new StringBuilder();
            index++;

            while (true)
            {
                if (index >= text.Length)
                    return null;

                var inner = text[index++];

                if (inner == '"')
                    break;

                if (inner == '\\' && index < text.Length)
                {
                    value.Append(text[index++]);
                    continue;
                }

                value.Append(inner);
            }

            tokens.Add(new Token(value.ToString(), false, false));
        }

        return tokens;
    }
}
