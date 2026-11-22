using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace BannerlordEnvironmentManager.Companion;

// At catch time, which mods had a Harmony patch on each method of the exception's stack. A patched method
// shows on the stack as a replacement (Method_PatchN) that names no mod, so BEM needs the owners from the
// running game, where Harmony still knows them. Everything here is read-only, reflective where Harmony's
// public surface differs between versions, and swallows every failure: the record is evidence, and the
// Companion stays inert and never throws.
internal static class CrashPatchOwners
{
    private const int MaxFrames = 200;

    private static ModuleMap? _map;

    public static void Write(JsonWriter writer, List<Entry> owners)
    {
        try
        {
            if (owners.Count == 0)
                return;

            writer.BeginArray("patchOwners");

            foreach (var entry in owners)
            {
                writer.BeginObject();
                writer.Text("type", entry.Type);
                writer.Text("method", entry.Method);
                writer.Strings("modules", entry.Modules);
                writer.EndObject();
            }

            writer.EndArray();
        }
        catch
        {
            // The rest of the record is already written; owners are an addition.
        }
    }

    // Read once, at the first catch, because the stack is gone by the time the record is rewritten.
    public static List<Entry> Read(Exception? exception)
    {
        var result = new List<Entry>();

        try
        {
            if (exception is null)
                return result;

            _map ??= ModuleMap.Build();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var examined = 0;

            for (var current = exception; current != null && examined < MaxFrames; current = current.InnerException)
            {
                foreach (var frame in new StackTrace(current, false).GetFrames() ?? Array.Empty<StackFrame>())
                {
                    if (++examined > MaxFrames)
                        break;

                    Add(result, seen, frame);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static void Add(List<Entry> result, HashSet<string> seen, StackFrame frame)
    {
        try
        {
            var method = frame.GetMethod();
            var original = OriginalOf(frame) ?? method;

            if (original is null)
                return;

            var info = Harmony.GetPatchInfo(original);

            if (info is null)
                return;

            var type = original.DeclaringType?.FullName ?? string.Empty;
            var key = type + "::" + original.Name;

            if (!seen.Add(key))
                return;

            var modules = new List<string>();

            foreach (var group in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
            {
                if (group is null)
                    continue;

                foreach (var patch in group)
                {
                    var module = ModuleOf(patch);

                    if (module.Length > 0 && !modules.Contains(module))
                        modules.Add(module);
                }
            }

            if (modules.Count > 0)
                result.Add(new Entry(type, original.Name, modules));
        }
        catch
        {
        }
    }

    // Harmony 2.2 and later can map a replacement frame back to the method it replaced. Absent that, the
    // frame's own method is tried, which is the original when its IL was only transpiled.
    private static MethodBase? OriginalOf(StackFrame frame)
    {
        try
        {
            var lookup = typeof(Harmony).GetMethod(
                "GetOriginalMethodFromStackframe", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(StackFrame) }, null);

            return lookup?.Invoke(null, new object[] { frame }) as MethodBase;
        }
        catch
        {
            return null;
        }
    }

    private static string ModuleOf(Patch patch)
    {
        try
        {
            var declaring = patch.PatchMethod?.DeclaringType;

            if (declaring != null && declaring.Assembly != null && !declaring.Assembly.IsDynamic)
            {
                var byAssembly = _map!.Resolve(declaring);

                if (byAssembly.Length > 0)
                    return byAssembly;
            }

            return _map!.ResolveOwner(patch.owner ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal sealed class Entry
    {
        public Entry(string type, string method, List<string> modules)
        {
            Type = type;
            Method = method;
            Modules = modules;
        }

        public string Type { get; }

        public string Method { get; }

        public List<string> Modules { get; }
    }
}
