using System;
using TaleWorlds.MountAndBlade;

namespace BannerlordEnvironmentManager.Companion;

// The class the game constructs. Its full name is a contract with BEM, which writes it into the
// generated SubModule.xml as SubModuleClassTypeName; see CompanionManifest in BEM's Core.
//
// Two rules govern everything below. First, nothing happens at all without a marker argument, so
// a companion folder left behind after a failed run is inert in a real play session. Second, no
// exception ever leaves these methods: a companion that destabilizes a real session is worse than
// no companion.
public sealed class SubModule : MBSubModuleBase
{
    private DryRunSession? _session;

    private DryRunSession? _watch;

    // Module.LoadSubModules constructs every submodule base first and only afterwards calls
    // InitializeSubModuleBases, so this constructor runs before any module's OnSubModuleLoad. That
    // is the whole reason the session starts here rather than in OnSubModuleLoad: by the time the
    // companion's own OnSubModuleLoad runs, the only way left to watch the other modules is to
    // rewrite their methods, and a dry run that rewrites the code it is measuring measures a
    // configuration the user never runs.
    public SubModule()
    {
        try
        {
            var request = CompanionGate.Read();

            if (request.Mode == CompanionMode.DryRun)
            {
                _session = DryRunSession.Start(request.RunId, CompanionMode.DryRun);
                return;
            }

            // Watch mode observes exactly what a dry run observes and never exits. It used to be a
            // first-chance handler and nothing else, which meant an ordinary launch produced no
            // timeline at all: no module began or finished, and nothing said who was patching. A
            // crash on an ordinary launch is the crash that actually happens, so it gets the same
            // evidence the boot check gets.
            if (request.Mode == CompanionMode.Watch)
                _watch = DryRunSession.Start(request.RunId, CompanionMode.Watch);
        }
        catch
        {
            _session = null;
            _watch = null;
        }
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();

        try
        {
            // The companion loads early, so this fires at the start of that pass rather than the
            // end of it. It is recorded as a phase marker only: it says module loading finished and
            // the initial screen pass began, which is what tells BEM where the game got to if it
            // dies before the first tick.
            _session?.NotePhase("before-initial-screen");
            _watch?.NotePhase("before-initial-screen");
        }
        catch
        {
            // Nothing here is worth a crash.
        }
    }

    // A session that ends normally should not be left with only what the last five-second flush
    // happened to catch.
    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();

        var watch = _watch;
        _watch = null;

        try
        {
            watch?.CompleteWatch();
        }
        catch
        {
            // The process is on its way out either way.
        }
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);

        // The per-frame capture budget resets here and the trace is flushed here, never inside the
        // first-chance handler. A watch session runs for as long as the player plays.
        if (_watch != null)
        {
            try
            {
                _watch.WatchTick();
            }
            catch
            {
                // A tick that cannot flush still has to return to the game.
            }

            return;
        }

        var session = _session;

        if (session is null)
            return;

        // Cleared first so a failure below cannot leave a second tick running the same capture.
        _session = null;

        try
        {
            session.Complete();
        }
        catch
        {
            // The exit still has to happen: a dry run that leaves the game running because the
            // capture failed is worse than one that captures nothing.
        }

        Exit();
    }

    // Environment.Exit rather than a process kill: it does not return, so no second tick can run,
    // and it lets the native shutdown release the display and audio devices instead of leaving them
    // held. Whatever the game writes on the way out is made moot by BEM snapshotting
    // LauncherData.xml and Configs before every run and restoring them afterwards. If the shutdown
    // hangs, BEM's own timeout kills the process.
    private static void Exit()
    {
        try
        {
            Environment.Exit(0);
        }
        catch
        {
            // Unreachable in practice. If it is ever reached the run simply continues as a normal
            // session and BEM's timeout ends it.
        }
    }
}
