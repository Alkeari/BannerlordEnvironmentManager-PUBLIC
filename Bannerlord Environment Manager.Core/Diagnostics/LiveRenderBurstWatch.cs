using System.Text;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// The render-request burst while the game is still running, read from the engine log as it grows. A watch
// session polls it (every few seconds is plenty: the burst before the 20:11 native crash was 2,520 lines
// inside one second, ahead of the crash by however long the inventory stayed open), and gets a burst back
// exactly once, the first poll after the threshold is crossed, so a surface can say so while the player can
// still close the screen.
//
// It only reads what the engine already wrote, from where the last poll stopped, with the file shared for
// writing and deletion so the game is never blocked. A log that shrinks or is replaced starts over.
public sealed class LiveRenderBurstWatch
{
    private readonly string _logPath;
    private RenderRequestBurstScanner _scanner = new();
    private long _offset;
    private bool _reported;

    public LiveRenderBurstWatch(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        _logPath = logPath;
    }

    // The engine names its log after the game's process id: rgl_log_<pid>.txt in the shared logs folder.
    public static string LogPathFor(string logsFolder, int processId) =>
        Path.Combine(logsFolder, $"rgl_log_{processId}.txt");

    public string LogPath => _logPath;

    // The worst burst seen so far, whether or not it was already reported. Null below the threshold.
    public RenderRequestBurst? Current { get; private set; }

    // Reads what was appended since the last poll. Returns a burst the first time one is found and null on
    // every other poll, including when the log does not exist yet or cannot be read right now.
    public RenderRequestBurst? Poll()
    {
        string appended;

        try
        {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < _offset)
                Restart();

            if (stream.Length == _offset)
                return null;

            stream.Seek(_offset, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - _offset];
            var read = 0;

            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);

                if (n == 0)
                    break;

                read += n;
            }

            // Only whole lines are consumed: the engine may be mid-line (or mid-character), so whatever
            // follows the last newline is read again on the next poll.
            var end = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1);

            if (end < 0)
                return null;

            _offset += end + 1;
            appended = Encoding.UTF8.GetString(buffer, 0, end + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in appended.Split('\n'))
            _scanner.Add(line.TrimEnd('\r'));

        var result = _scanner.Result(Path.GetFileName(_logPath));

        if (result is null)
            return null;

        // Still running, so "ended abnormally" is not known yet and is never claimed here.
        Current = result with { EndedAbnormally = false };

        if (_reported)
            return null;

        _reported = true;
        return Current;
    }

    private void Restart()
    {
        _scanner = new RenderRequestBurstScanner();
        _offset = 0;
        _reported = false;
        Current = null;
    }
}
