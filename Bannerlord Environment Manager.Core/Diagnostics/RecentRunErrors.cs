using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// The lines of the newest run logs on disk that say a call into the game failed to bind: a missing
// method, a missing field, a type that would not load, plus a collection modified mid-loop with the stack
// frames under it. Those are the lines MissingGameApis.NamedBy
// matches against, so only they are kept, and the preflight never holds whole multi-megabyte logs.
public static class RecentRunErrors
{
    private static readonly Regex Binding = new(
        @"(?:Method|Field) not found: |Could not load type '",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A log written before this is an earlier setup of the mods, so what it says about a call proves
    // nothing about the current one.
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    public static string Read(
        IEnumerable<DiscoveredLog> logs, DateTimeOffset now, TimeSpan? window = null, int maxFiles = 24, long maxBytesEach = 8_000_000)
    {
        ArgumentNullException.ThrowIfNull(logs);

        var since = now - (window ?? DefaultWindow);
        var text = new StringBuilder();

        foreach (var log in logs.Where(l => l.Written >= since && l.SizeBytes <= maxBytesEach)
                     .OrderByDescending(l => l.Written)
                     .Take(maxFiles))
        {
            try
            {
                using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                string? line;
                var frames = 0;
                var inInvokeStack = false;
                var dataLoad = new EngineDataLoadLog();
                while ((line = reader.ReadLine()) is not null)
                {
                    // Module data the engine rejected, and the objects it then never registered.
                    if (dataLoad.Feed(line, text))
                        continue;

                    // An engine "[FTL] Crash Report: Exception occurred inside invoke" block: its header,
                    // target type, inner message and the module paths of its stack, which is what names the
                    // module (EngineInvokeCrashes). Kept whole so the block parses the same here as in the log.
                    if (EngineInvokeCrashes.IsBlockLine(line))
                    {
                        text.AppendLine(line.Length > 600 ? line[..600] : line);
                        inInvokeStack = line.Contains("Crash Report StackTrace:", StringComparison.Ordinal);
                        frames = 0;
                        continue;
                    }

                    if (inInvokeStack)
                    {
                        if (line.StartsWith('['))
                        {
                            inInvokeStack = false;
                        }
                        else
                        {
                            if (EngineInvokeCrashes.IsStackPath(line))
                                text.AppendLine(line.Length > 600 ? line[..600] : line);

                            continue;
                        }
                    }

                    // Single-line evidence for the smaller launch checks: a module's stale shader cache
                    // and a declared assembly that is not a valid image.
                    if (RunLogSignals.IsSignal(line))
                    {
                        text.AppendLine(line.Length > 600 ? line[..600] : line);
                        frames = 0;
                        continue;
                    }

                    if (Binding.IsMatch(line) || CollectionModifiedErrors.IsMarker(line))
                    {
                        text.AppendLine(line.Length > 600 ? line[..600] : line);
                        frames = CollectionModifiedErrors.IsMarker(line) ? 12 : 0;
                    }
                    else if (frames > 0 && line.TrimStart().StartsWith("at ", StringComparison.Ordinal))
                    {
                        // The stack under a collection-modified message is what names the mod.
                        text.AppendLine(line.Length > 600 ? line[..600] : line);
                        frames--;
                    }
                    else
                    {
                        frames = 0;
                    }
                }

                dataLoad.Flush(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that is locked or gone is one fewer piece of evidence, never a failed launch check.
            }
        }

        return text.ToString();
    }
}
