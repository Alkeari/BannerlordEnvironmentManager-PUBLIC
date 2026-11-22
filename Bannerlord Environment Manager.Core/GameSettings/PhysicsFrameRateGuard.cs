using System.Globalization;
using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// The game option "Dynamic Physics Simulation Frame Rate" (NativeOptionsType.PhysicsTickRate, two
// choices in the options screen) is stored in engine_config.txt under physics_tick_rate, and the game
// has been reported to put it back to High on its own. This remembers the value the player chose and
// writes it back before a launch when the file no longer holds it.
//
// Only that one line changes. The file is read and rebuilt through KeyValueConfigDocument, which keeps
// every other byte, the encoding, the byte order mark and each line's own ending, and the write is a
// temporary file moved over the original, so a failed write leaves the game's file as it was.
public enum PhysicsFrameRateOutcome
{
    // Nothing was asked of the guard: no value is recorded.
    NotRecording,
    // The file already holds the recorded value, so it was not touched.
    Unchanged,
    // The file held another value and now holds the recorded one.
    Restored,
    // A value was recorded from the file.
    Recorded,
    MissingFile,
    // The file has no physics_tick_rate line. Never added: a line the game did not write is not BEM's.
    MissingKey,
    // The value is not a finite number in range, so nothing is recorded or written.
    OutOfRange,
    ReadOnly,
    Failed
}

public sealed record PhysicsFrameRateResult(PhysicsFrameRateOutcome Outcome, string? Value = null, string? Error = null);

public sealed record PhysicsFrameRateRecord(string? Value = null);

public sealed class PhysicsFrameRateStore(string filePath)
{
    public const string FileName = "physics-frame-rate.json";

    public string FilePath { get; } = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public static PhysicsFrameRateStore Beside(string instanceSettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSettingsPath);

        return new PhysicsFrameRateStore(
            Path.Combine(Path.GetDirectoryName(instanceSettingsPath) ?? string.Empty, FileName));
    }

    // An unreadable record reads as no record, so a damaged file stops the guard rather than writing
    // a value nobody can vouch for.
    public PhysicsFrameRateRecord Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PhysicsFrameRateRecord>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Write(PhysicsFrameRateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);

            throw;
        }
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}

public static class PhysicsFrameRateGuard
{
    public const string Key = "physics_tick_rate";

    // The options screen offers two choices, but the engine writes a rate rather than an index (a real
    // file holds 73.7792), so the bound only rejects what cannot be a rate at all.
    public const double MaximumValue = 1000;

    public static string EngineConfigPath(string configsFolder) =>
        Path.Combine(configsFolder, GameSettingsFiles.EngineConfigFileName);

    public static bool IsInRange(string? value) => TryParse(value, out _);

    // Reads the value the file holds now and keeps it, which is how the player's choice is recorded:
    // they set it in the game, then turn the guard on.
    public static PhysicsFrameRateResult Record(string enginePath, PhysicsFrameRateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var read = ReadValue(enginePath);

        if (read.Outcome != PhysicsFrameRateOutcome.Unchanged)
            return read;

        if (!IsInRange(read.Value))
            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.OutOfRange, read.Value);

        store.Write(new PhysicsFrameRateRecord(read.Value));

        return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Recorded, read.Value);
    }

    public static PhysicsFrameRateResult RestoreIfReset(string enginePath, PhysicsFrameRateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var recorded = store.Read().Value;

        if (recorded is null)
            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.NotRecording);

        if (!TryParse(recorded, out var wanted))
            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.OutOfRange, recorded);

        try
        {
            if (!File.Exists(enginePath))
                return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.MissingFile);

            var document = KeyValueConfigDocument.Parse(ConfigText.Read(enginePath));

            if (!document.Values.TryGetValue(Key, out var current))
                return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.MissingKey);

            if (TryParse(current, out var now) && now == wanted)
                return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Unchanged, current);

            if (new FileInfo(enginePath).IsReadOnly)
                return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.ReadOnly, current);

            document.Set(Key, recorded);

            var temporary = enginePath + ".bem.tmp";

            try
            {
                File.WriteAllBytes(temporary, document.ToBytes());
                // Replace keeps the original's attributes and permissions, which a plain move would drop.
                File.Replace(temporary, enginePath, null);
            }
            catch
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);

                throw;
            }

            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Restored, recorded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Failed, Error: ex.Message);
        }
    }

    private static PhysicsFrameRateResult ReadValue(string enginePath)
    {
        try
        {
            if (!File.Exists(enginePath))
                return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.MissingFile);

            var document = KeyValueConfigDocument.Parse(ConfigText.Read(enginePath));

            return document.Values.TryGetValue(Key, out var value)
                ? new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Unchanged, value)
                : new PhysicsFrameRateResult(PhysicsFrameRateOutcome.MissingKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PhysicsFrameRateResult(PhysicsFrameRateOutcome.Failed, Error: ex.Message);
        }
    }

    private static bool TryParse(string? text, out double value)
    {
        value = 0;

        return text is not null
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value)
            && value >= 0
            && value <= MaximumValue;
    }
}
