using System.Text;

namespace BannerlordEnvironmentManager.Core.Instances;

// One thing DepotDownloader's output has finished saying: a whole line, or a question it is now
// waiting on. Prompt is null for a line, and Text is the question for a prompt.
public sealed record DepotDownloaderOutputEvent(string Text, SteamPrompt? Prompt);

// Turns whatever arrived from a pipe into lines and prompts, however the pipe split it.
//
// A prompt is written with no newline after it and the process then waits, so a line reader would
// sit on a question nobody was asked. That is why this works on characters. What it must never do
// is call a half-written line a question: stdout arrives in chunks, so
// "Got manifest request code for depot 228988 from app 228980, manifest 6645201662696499616, result: OK"
// is briefly a fragment ending in a colon and a space, and reading that as a question is what killed
// a v1.3.15 download outright. Only the sentences DepotDownloaderTool.PromptIn names are questions;
// everything else is output, whether it has arrived whole or not.
public sealed class DepotDownloaderOutputReader
{
    private readonly StringBuilder pending = new();

    // What has arrived since the last newline or prompt, which is nothing at all between lines.
    public string Pending => pending.ToString();

    public IReadOnlyList<DepotDownloaderOutputEvent> Feed(string chunk)
    {
        var events = new List<DepotDownloaderOutputEvent>();

        if (string.IsNullOrEmpty(chunk))
            return events;

        foreach (var character in chunk)
        {
            if (character == '\n')
            {
                events.Add(new DepotDownloaderOutputEvent(pending.ToString().TrimEnd('\r'), null));
                pending.Clear();
                continue;
            }

            pending.Append(character);

            // Every prompt DepotDownloader writes ends in a colon and a space, so nothing but a
            // trailing space is worth the comparison.
            if (character != ' ' || DepotDownloaderTool.PromptIn(pending.ToString()) is not { } prompt)
                continue;

            events.Add(new DepotDownloaderOutputEvent(prompt.Question, prompt));
            pending.Clear();
        }

        return events;
    }

    // Whatever the process left unterminated when its output ended, once.
    public string? Flush()
    {
        if (pending.Length == 0)
            return null;

        var rest = pending.ToString();
        pending.Clear();

        return rest;
    }
}
