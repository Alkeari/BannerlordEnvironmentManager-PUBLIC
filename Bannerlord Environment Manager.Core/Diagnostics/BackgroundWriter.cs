using System.Collections.Concurrent;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Runs disk writes one after another on a thread of its own, so a caller on the UI thread (a log line,
// a saved setting) returns at once instead of waiting on the disk. The dumps of 2026-11-21 caught the
// UI thread inside File.AppendAllText from LoggingService.Log and File.WriteAllText from
// ShellNavigationSettings.Save. Jobs run in the order they were queued, a job that throws is skipped
// and the rest still run, and Flush waits for what is queued so the last lines before an exit land.
public sealed class BackgroundWriter
{
    private readonly BlockingCollection<Job> jobs = [];
    private readonly Action<Exception>? onFailure;
    private readonly Thread thread;

    private sealed record Job(Action Work, ManualResetEventSlim? Done);

    public BackgroundWriter(string name, Action<Exception>? onFailure = null)
    {
        this.onFailure = onFailure;
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public void Enqueue(Action write) => jobs.Add(new Job(write, null));

    // True when everything queued before the call has run within the timeout.
    // A job that flushes its own writer would wait on itself until the timeout, so that call returns false at once.
    public bool Flush(TimeSpan timeout)
    {
        if (Thread.CurrentThread == thread)
            return false;

        using var done = new ManualResetEventSlim();
        jobs.Add(new Job(() => { }, done));
        return done.Wait(timeout);
    }

    private void Run()
    {
        foreach (var job in jobs.GetConsumingEnumerable())
        {
            try { job.Work(); }
            catch (Exception ex)
            {
                try { onFailure?.Invoke(ex); } catch { /* a failure report must never end the writer */ }
            }
            finally { job.Done?.Set(); }
        }
    }
}
