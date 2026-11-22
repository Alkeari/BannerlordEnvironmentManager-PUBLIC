using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordEnvironmentManager.Companion;

// On unless the owner turned it off: with crash-guard.json saying disabled, this class is never touched and
// nothing is patched. An exception reaching the engine's frame callbacks would end the process, so this is the last
// managed code to see it. It writes the evidence first, because the save may not survive what follows.
internal static class CrashGuard
{
    private const double SaveTimeoutSeconds = 60;

    private static readonly object Gate = new object();

    private static string _runId = string.Empty;

    private static bool _caught;

    // Signatures the player pressed Continue on; a repeat of one is let pass without a window.
    private static readonly HashSet<string> Continued = new HashSet<string>(StringComparer.Ordinal);

    private static bool _savePending;

    private static bool _saveStarted;

    private static DateTime _saveDeadlineUtc;

    private static string _type = string.Empty;

    private static string _message = string.Empty;

    private static string _stack = string.Empty;

    private static string _where = string.Empty;

    private static string _saveName = string.Empty;

    private static string _saveResult = string.Empty;

    private static List<CrashPatchOwners.Entry> _owners = new List<CrashPatchOwners.Entry>();

    // The exe that shows the Bannerlord Exception Manager's window. Empty: no window, policy alone.
    private static string _bemPath = string.Empty;

    // Counts the crashes the player continued past, so each gets a record of its own.
    private static int _crashNumber;

    private static bool _canSave;

    private static bool _canContinue;

    private static bool _inMission;

    public static void Arm(string runId, string bemPath)
    {
        _runId = runId;
        _bemPath = bemPath ?? string.Empty;

        var harmony = new Harmony("bem.companion.crashguard");
        var finalizer = new HarmonyMethod(typeof(CrashGuard).GetMethod(nameof(Caught), BindingFlags.NonPublic | BindingFlags.Static));
        // Every hook stands or falls alone. The guard runs on every game version from v1.0.0 up, and a
        // method one of them lacks or names differently must cost that hook and no other: patching a
        // method that is not there throws, and one throw here used to leave the rest unhooked.
        // Each call passes its type and method as string literals, one call a hook and never a loop, so the
        // version simulation reads every target from the built IL and proves it against each game version.
        Hook(harmony, "TaleWorlds.Engine.EngineScreenManager", "PreTick", finalizer);
        Hook(harmony, "TaleWorlds.Engine.EngineScreenManager", "Tick", finalizer);
        Hook(harmony, "TaleWorlds.Engine.EngineScreenManager", "LateTick", finalizer);
        Hook(harmony, "TaleWorlds.Engine.EngineScreenManager", "Update", finalizer);

        Hook(
            harmony,
            "TaleWorlds.MountAndBlade.Module",
            "OnApplicationTick",
            finalizer,
            new HarmonyMethod(typeof(CrashGuard).GetMethod(nameof(PumpSave), BindingFlags.NonPublic | BindingFlags.Static)));

        // Closer to the failure than the hooks above, so Continue skips less of the frame: the rest of
        // one mission's tick instead of the whole application's. Each is found by name and left out
        // without a word on a game version that lacks it; the hooks above still stand there.
        Hook(harmony, "TaleWorlds.MountAndBlade.Mission", "Tick", finalizer);

        // The party a periodic tick is working on, so a crash inside one (his PartyUpgrader crash on a party
        // with no faction) names that party and the mod whose component made it; a stack never carries it.
        var noteParty = new HarmonyMethod(typeof(CrashGuard).GetMethod(nameof(NoteParty), BindingFlags.NonPublic | BindingFlags.Static));
        Hook(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", "DailyTickParty", null, noteParty);
        Hook(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", "HourlyTickParty", null, noteParty);
        Hook(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", "TickPartialHourlyAi", null, noteParty);

        // Before a state push the map is saved ahead of the ten-minute interval.
        var beforeTransition = new HarmonyMethod(typeof(CrashGuard).GetMethod(nameof(BeforeTransition), BindingFlags.NonPublic | BindingFlags.Static));
        Hook(harmony, "TaleWorlds.Core.GameStateManager", "PushState", null, beforeTransition);
        Hook(harmony, "TaleWorlds.Core.GameStateManager", "CleanAndPushState", null, beforeTransition);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        NativeFaultGuard.Install(OnNativeFault);
        NativeFaultGuard.InstallVectored();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    // A native fault that ended the game before any filter ran: the vectored handler saw it, and the
    // process now exits within seconds of it with nothing caught.
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            var fatal = NativeFaultGuard.LastFatalWithin(TimeSpan.FromSeconds(10));

            if (fatal.Length > 0 && !_caught)
                OnNativeFault("NativeFault", fatal);
        }
        catch
        {
        }
    }

    // The engine sets its own last-chance filter while it starts, after this guard; the first
    // application tick puts ours back on top, chained to the engine's.
    private static int _nativeTicks = 120;

    // A fault no managed handler saw: recorded with what Windows knows, then the game ends as it would.
    private static void OnNativeFault(string type, string message)
    {
        lock (Gate)
        {
            if (_caught)
                return;

            _caught = true;
            _crashNumber++;
            _type = type;
            _message = message;
            _stack = string.Empty;
            _where = "NativeFault";
            _saveName = string.Empty;
            _saveResult = string.Empty;
            _inMission = ReadInMission();
            _canContinue = false;
            _canSave = false;
            WriteRecord();
        }
    }

    private static void Hook(
        Harmony harmony, string typeName, string methodName, HarmonyMethod? finalizer, HarmonyMethod? prefix = null)
    {
        try
        {
            var type = AccessTools.TypeByName(typeName);
            var method = type is null ? null : AccessTools.DeclaredMethod(type, methodName);

            if (method is null || method.IsAbstract)
                return;

            harmony.Patch(method, prefix: prefix, finalizer: finalizer);
        }
        catch
        {
        }
    }

    private static object? _tickedParty;
    private static string _tickedPartyText = string.Empty;

    private static void NoteParty(object __0) => _tickedParty = __0;

    private static object? Member(object? target, string name)
    {
        if (target is null)
            return null;
        try
        {
            var type = target.GetType();
            var property = AccessTools.Property(type, name);
            if (property is not null)
                return property.GetValue(target, null);
            return AccessTools.Field(type, name)?.GetValue(target);
        }
        catch { return null; }
    }

    private static string Id(object? value) => value is null ? "none" : (Member(value, "StringId") as string ?? value.ToString() ?? "?");

    // Read by name, each part alone, so a game version that lacks one member costs only that part.
    internal static string DescribeParty(object? party, string? stack)
    {
        if (party is null || stack is null || !(stack.Contains("TickParty") || stack.Contains("TickPartialHourlyAi")))
            return string.Empty;

        var component = Member(party, "PartyComponent");
        var componentType = component?.GetType();
        return "party " + Id(party)
            + " \"" + (Member(party, "Name")?.ToString() ?? string.Empty) + "\""
            + ", clan " + Id(Member(party, "ActualClan"))
            + ", faction " + Id(Member(party, "MapFaction"))
            + ", leader " + Id(Member(party, "LeaderHero"))
            + ", component " + (componentType is null ? "none" : componentType.FullName + " from " + componentType.Assembly.GetName().Name);
    }

    private static Exception? Caught(Exception? __exception, MethodBase? __originalMethod)
    {
        if (__exception is null)
            return null;

        try
        {
            lock (Gate)
            {
                // The damaged frames keep throwing while the save finishes. Those are held back only
                // for as long as the save has left to run.
                if (_savePending)
                {
                    Trace("held back while saving");
                    return null;
                }

                var inner = __exception.GetBaseException();
                var signature = CrashGuardPolicy.Signature(inner.GetType().FullName ?? string.Empty, inner.StackTrace);
                Trace("caught " + signature + " caught=" + _caught + " continued=" + Continued.Contains(signature));
                if (!_caught && Continued.Contains(signature))
                    return null;

                var inCampaign = ReadInCampaign();
                var inMission = ReadInMission();
                var ironman = ReadIronman();
                var saveLimit = ReadSaveLimit();
                var action = CrashGuardPolicy.Decide(_caught, inCampaign, inMission, ironman, saveLimit);
                var first = !_caught;
                _caught = true;

                if (first)
                {
                    _crashNumber++;
                    _type = __exception.GetType().FullName ?? string.Empty;
                    _message = __exception.Message;
                    _stack = __exception.ToString();
                    _where = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name;
                    _owners = CrashPatchOwners.Read(__exception);
                    _tickedPartyText = DescribeParty(_tickedParty, _stack);
                    _saveName = string.Empty;
                    _saveResult = string.Empty;
                    _inMission = inMission;
                    _canContinue = CrashGuardPolicy.CanContinue(inner.GetType().FullName, inner.Message);
                    _canSave = CrashGuardPolicy.Decide(false, inCampaign, inMission, ironman, saveLimit) == CrashGuardAction.SaveThenClose;
                    WriteRecord();

                    // The frame is held here, on the game's own thread, until the player answers in the
                    // window or BEM is gone. No answer leaves the policy above in charge, which is what
                    // this guard did before it had a window.
                    switch (Unattended() ? ExceptionAnswer.None : CrashGuardWindow.Ask(_bemPath, RecordPath(), true))
                    {
                        case ExceptionAnswer.Continue when !_canContinue:
                            EndGame();
                            break;

                        case ExceptionAnswer.Continue:
                            Continued.Add(signature);
                            _caught = false;
                            return null;

                        case ExceptionAnswer.Close:
                            EndGame();
                            break;

                        case ExceptionAnswer.SaveAndClose:
                            if (!_canSave)
                                EndGame();

                            action = CrashGuardAction.SaveThenClose;
                            break;
                    }
                }

                if (action != CrashGuardAction.SaveThenClose)
                    return __exception;

                _savePending = true;
                _saveDeadlineUtc = DateTime.UtcNow.AddSeconds(SaveTimeoutSeconds);
                return null;
            }
        }
        catch (Exception guardFailure)
        {
            Trace("guard failed: " + guardFailure);
            return __exception;
        }
    }

    private static bool Unattended()
    {
        try
        {
            // Modules\<id>\bin\<platform>\<assembly>: the module folder is two above the assembly's own.
            var bin = Path.GetDirectoryName(typeof(CrashGuard).Assembly.Location);
            var module = bin is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(bin));
            if (module is null)
                return false;
            var marker = Path.Combine(module, CrashGuardPolicy.UnattendedMarkerFileName);
            return File.Exists(marker) && !CrashGuardPolicy.AsksPlayer(File.ReadAllText(marker));
        }
        catch
        {
            return false;
        }
    }

    // The process is ended outright, never through the runtime's own exit. That exit ends every other
    // thread and then unloads the game's libraries on the one left, and after a crash one of those waits
    // on a lock a thread that no longer exists was holding: seen on v1.4.8, where the game sat for
    // fifteen minutes with its window gone and its files still open under whatever was launched next.
    // Everything worth keeping has been written by the time the player chooses to close.
    private static void EndGame()
    {
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        catch
        {
        }

        Environment.Exit(1);
    }

    // Runs ahead of every module's tick, and while a save is pending no module ticks at all, so the code
    // that threw does not run again against the state being saved.
    private static bool PumpSave()
    {
        // The engine sets its own last-chance filter after modules load, and that filter shows its crash
        // dialog and waits there, so a filter below it records nothing until the player answers (a forced
        // native fault on v1.4.8 left no record). Taking the top again every few seconds keeps ours first.
        if (++_nativeTicks >= 120)
        {
            _nativeTicks = 0;
            NativeFaultGuard.Install(OnNativeFault);
        }

        if (!_savePending)
        {
            if (++_rollingTicks >= 60)
            {
                _rollingTicks = 0;
                TryRollingSave();
            }

            return true;
        }

        try
        {
            if (!_saveStarted)
            {
                _saveStarted = true;
                StartSave();
            }
            else
            {
                if (_saveResult.Length == 0
                    && CrashGuardSaveCompletion.Pump(Game.Current) == SaveCompletionProgress.Unsupported)
                    _saveResult = "CompletionUnavailable";

                if (_saveResult.Length > 0 || DateTime.UtcNow > _saveDeadlineUtc)
                {
                    if (_saveResult.Length == 0)
                        _saveResult = "TimedOut";

                    WriteRecord();
                    EndGame();
                }
            }
        }
        catch (Exception ex)
        {
            _saveResult = "Failed: " + ex.Message;
            WriteRecord();
            EndGame();
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartSave()
    {
        var name = CrashGuardPolicy.UniqueSaveName(DateTime.Now, MBSaveLoad.IsSaveGameFileExists);

        if (name is null)
        {
            _saveResult = "NoFreeName";
            return;
        }

        _saveName = name;
        WriteRecord();

        // SaveAsCurrentGame makes the new file the active slot, and a later quick save would then write
        // there instead of the player's own save.
        var slot = ReadActiveSlot();

        MBSaveLoad.SaveAsCurrentGame(Campaign.Current.SaveHandler.GetSaveMetaData(), name, result =>
        {
            _saveResult = result.Item1.ToString();
            RestoreSlot(slot);
        });
    }

    private static int _rollingTicks;

    private static DateTime? _lastRollingUtc;

    private static bool _rollingInFlight;

    // A save a few minutes old for the crashes that end the game before any emergency save can run.
    // Any failure here costs the rolling save and nothing else.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BeforeTransition()
    {
        TryRollingSave(true);
        return true;
    }

    private static void TryRollingSave(bool transition = false)
    {
        try
        {
            if (!ReadInCampaign())
                return;

            var now = DateTime.UtcNow;

            Func<bool, bool, bool, bool, bool, bool, bool, bool, DateTime?, DateTime, bool> allowed =
                transition ? CrashGuardPolicy.ShouldSaveBeforeTransition : CrashGuardPolicy.ShouldRollingSave;

            if (!allowed(
                    _caught, _savePending, _rollingInFlight, ReadOnMap(), ReadInMission(), ReadIronman(),
                    ReadSaveLimit(), RollingSaveExists(),
                    _lastRollingUtc, now))
                return;

            _rollingInFlight = true;
            _lastRollingUtc = now;
            var slot = ReadActiveSlot();

            SaveRolling(slot);
        }
        catch
        {
            _rollingInFlight = false;
        }
    }

    // The campaign map is the one state where the game itself autosaves; by type name, since the map
    // state's namespace moved between game versions.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadOnMap()
    {
        try { return Game.Current?.GameStateManager?.ActiveState?.GetType().Name == "MapState"; }
        catch { return false; }
    }

    // Each call into the game's save API sits in its own method, so a member a game version lacks
    // (ActiveSaveSlotName is missing on v1.0.0) fails when that method compiles, inside the caller's
    // try, rather than when the caller itself compiles, where nothing can catch it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SaveRolling(string? slot)
    {
        MBSaveLoad.SaveAsCurrentGame(Campaign.Current.SaveHandler.GetSaveMetaData(), CrashGuardPolicy.RollingSaveName, _ =>
        {
            RestoreSlot(slot);
            _rollingInFlight = false;
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RollingSaveExists() => MBSaveLoad.IsSaveGameFileExists(CrashGuardPolicy.RollingSaveName);

    private static string? ReadActiveSlot()
    {
        try { return typeof(MBSaveLoad).GetProperty("ActiveSaveSlotName")?.GetValue(null) as string; }
        catch { return null; }
    }

    private static void RestoreSlot(string? slot)
    {
        if (slot is null)
            return;

        try
        {
            typeof(MBSaveLoad).GetProperty("ActiveSaveSlotName")?.GetSetMethod(true)?.Invoke(null, new object?[] { slot });
        }
        catch
        {
            // The game is closing anyway; the slot only lasts as long as the process.
        }
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            lock (Gate)
            {
                if (_caught)
                    return;

                _caught = true;
                _crashNumber++;
                _type = e.ExceptionObject?.GetType().FullName ?? string.Empty;
                _message = (e.ExceptionObject as Exception)?.Message ?? string.Empty;
                _stack = e.ExceptionObject?.ToString() ?? string.Empty;
                _where = "UnhandledException";
                _owners = CrashPatchOwners.Read(e.ExceptionObject as Exception);
                _saveName = string.Empty;
                _saveResult = string.Empty;
                _inMission = ReadInMission();
                _canContinue = false;
                _canSave = false;
                WriteRecord();

                // The process is already ending, so the window only reports: nothing waits on it.
                if (!Unattended())
                    CrashGuardWindow.Ask(_bemPath, RecordPath(), false);
            }
        }
        catch
        {
            // The process is ending and this handler must not change how.
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadInCampaign()
    {
        try
        {
            return CrashGuardPolicy.IsPlayableCampaign(
                Campaign.Current != null, Game.Current != null, Hero.MainHero != null,
                Game.Current?.GameStateManager?.ActiveState?.GetType().Name);
        }
        catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadInMission()
    {
        try { return Mission.Current != null; }
        catch { return true; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadIronman()
    {
        try { return CampaignOptions.IsIronmanMode; }
        catch { return true; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadSaveLimit()
    {
        try { return MBSaveLoad.IsMaxNumberOfSavesReached(); }
        catch { return true; }
    }

    // The first crash of a run keeps the name BEM has always read; one the player continued past is numbered.
    // One line per exception the guard sees, beside its records, so a crash it lets pass is still on record.
    private static void Trace(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(DryRunFiles.GetCrashGuardRecordPath("x")) ?? ".", "guard.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + _runId + " " + text + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string RecordPath() =>
        DryRunFiles.GetCrashGuardRecordPath(_crashNumber <= 1 ? _runId : _runId + "-" + _crashNumber);

    // The game's exe sits in bin\<platform folder>, two levels under the install.
    private static string GamePath()
    {
        try
        {
            var bin = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'));

            return (bin is null ? null : Path.GetDirectoryName(bin)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CommandLine()
    {
        try
        {
            return Environment.CommandLine ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteAssemblies(JsonWriter writer)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();

            writer.BeginArray("assemblies");

            foreach (var assembly in loaded)
            {
                string name, version, location;

                try
                {
                    if (assembly.IsDynamic)
                        continue;

                    var identity = assembly.GetName();
                    name = identity.Name ?? string.Empty;
                    version = identity.Version?.ToString() ?? string.Empty;
                    location = assembly.Location ?? string.Empty;
                }
                catch
                {
                    continue;
                }

                writer.BeginObject();
                writer.Text("name", name);
                writer.Text("version", version);
                writer.Text("path", location);
                writer.EndObject();
            }

            writer.EndArray();
        }
        catch
        {
            // An addition to the record, like the owners.
        }
    }

    private static void WriteRecord()
    {
        try
        {
            var writer = new JsonWriter();
            writer.BeginObject();
            writer.Text("runId", _runId);
            writer.Text("utc", DateTime.UtcNow.ToString("O"));
            writer.Text("exceptionType", _type);
            writer.Text("message", _message);
            writer.Text("stack", _stack);
            writer.Text("where", _where);
            writer.Text("tickedParty", _tickedPartyText);
            writer.Text("saveName", _saveName);
            writer.Text("saveResult", _saveResult);
            writer.Number("crashNumber", _crashNumber);
            writer.Boolean("canContinue", _canContinue);
            writer.Boolean("canSave", _canSave);
            writer.Boolean("inMission", _inMission);
            writer.Text("gamePath", GamePath());
            writer.Strings("modules", ExceptionProtocol.ModulesFrom(CommandLine()));
            CrashPatchOwners.Write(writer, _owners);
            WriteAssemblies(writer);
            writer.EndObject();

            var path = RecordPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, writer.ToString());
        }
        catch
        {
            // Evidence that cannot be written must not become a second crash.
        }
    }
}
