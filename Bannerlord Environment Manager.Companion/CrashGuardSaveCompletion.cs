using System;
using System.Reflection;

namespace BannerlordEnvironmentManager.Companion;

internal enum SaveCompletionProgress
{
    NoPendingSave,
    Pending,
    Completed,
    Unsupported
}

// Game.OnTick normally delivers the asynchronous save callback after gameplay has ticked. During
// crash recovery gameplay must stay stopped, so deliver only that completion step. Reflection keeps
// the private game contract explicit and lets an unsupported game version fail without running ticks.
internal static class CrashGuardSaveCompletion
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static SaveCompletionProgress Pump(object? game)
    {
        if (game is null)
            return SaveCompletionProgress.Unsupported;

        FieldInfo? field = null;
        for (var type = game.GetType(); type != null && field is null; type = type.BaseType)
            field = type.GetField("_currentActiveSaveData", PrivateInstance | BindingFlags.DeclaredOnly);

        if (field is null || field.IsInitOnly || !field.FieldType.IsGenericType
            || field.FieldType.GetGenericTypeDefinition() != typeof(Tuple<,>))
            return SaveCompletionProgress.Unsupported;

        var active = field.GetValue(game);
        if (active is null)
            return SaveCompletionProgress.NoPendingSave;

        var tuple = active.GetType();
        var output = tuple.GetProperty("Item1")?.GetValue(active);
        var callback = tuple.GetProperty("Item2")?.GetValue(active);
        if (output is null || callback is null)
            return SaveCompletionProgress.Unsupported;

        var progress = output.GetType().GetProperty("IsContinuing");
        if (progress is null || progress.PropertyType != typeof(bool) || progress.GetIndexParameters().Length != 0)
            return SaveCompletionProgress.Unsupported;
        var continuing = progress.GetValue(output);
        if (continuing is not bool busy)
            return SaveCompletionProgress.Unsupported;
        if (busy)
            return SaveCompletionProgress.Pending;

        var arguments = field.FieldType.GetGenericArguments();
        if (arguments.Length != 2)
            return SaveCompletionProgress.Unsupported;

        var complete = field.DeclaringType?.GetMethod("OnSaveCompleted", PrivateInstance,
            null, arguments, null);
        if (complete is null)
            return SaveCompletionProgress.Unsupported;

        complete.Invoke(game, new[] { output, callback });
        // A completion callback may schedule another save. Clear only the one just delivered.
        if (ReferenceEquals(field.GetValue(game), active))
            field.SetValue(game, null);
        return SaveCompletionProgress.Completed;
    }
}
