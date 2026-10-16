using System.ComponentModel;
using System.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Instances;

// The seam that stops a child process outliving BEM itself.
//
// Core starts DepotDownloader and kills it on every path Core can see: a stop, a failure, an
// exception out of the readers. What Core cannot see is BEM ending - closed, crashed, or killed
// from Task Manager - which leaves a 58 GB download running with nothing watching it and no UI that
// knows about it. Only a Windows job object ends that one, and a job object is a P/Invoke, which
// belongs beside the app's other Windows services rather than in a library that declares no
// platform of its own.
//
// Unregistered this does nothing and every download still works: it adds an exit path, it does not
// own any of the existing ones.
public static class ChildProcessSupervision
{
    public static Action<Process>? Registered { get; set; }

    public static void Supervise(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            Registered?.Invoke(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            // A process that has already exited, or a machine that refuses the job object, costs the
            // last-resort exit path and nothing else. Failing the download over it would trade a
            // working download for a guarantee about a case that has not happened yet.
            _ = ex;
        }
    }
}
