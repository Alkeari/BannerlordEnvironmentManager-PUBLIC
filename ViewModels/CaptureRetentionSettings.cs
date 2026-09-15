using System.Text.Json;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // How many captures BEM keeps. It lives beside the shell settings rather than inside the capture
    // store, because the store is evidence and this is a preference: wiping the captures must never
    // take the number the user chose with it.
    public sealed class CaptureRetentionSettings
    {
        private static readonly string FilePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Environment Manager",
            "capture-retention.json");

        private sealed record Stored(int Keep, int KeepDumps);

        public CaptureRetentionSettings()
        {
            Load();
            Apply();
        }

        public int Keep { get; private set; } = ArtifactCaptureRetention.DefaultKeep;

        public int KeepDumps { get; private set; } = ArtifactCaptureRetention.DefaultKeepDumps;

        // The box hands back a double, and an empty box hands back NaN. False means neither number was
        // usable: nothing was applied and nothing was written, so the caller puts back what it displayed.
        public bool Set(double keep, double keepDumps)
        {
            if (!ArtifactCaptureRetention.TryReadLimit(keep, 1, out var wanted)
                || !ArtifactCaptureRetention.TryReadLimit(keepDumps, 0, out var wantedDumps))
            {
                return false;
            }

            if (Keep == wanted && KeepDumps == wantedDumps)
                return true;

            Keep = wanted;
            KeepDumps = wantedDumps;

            Apply();
            Save();

            return true;
        }

        // The background prune at the end of a capture reads the same numbers, so what the user typed
        // is what ages the store rather than only what a prune they pressed does.
        private void Apply() => ArtifactCaptureRetention.Use(Keep, KeepDumps);

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } stored)
                {
                    Keep = Math.Max(1, stored.Keep);
                    KeepDumps = Math.Max(0, stored.KeepDumps);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to load the capture retention settings");
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(Keep, KeepDumps)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the capture retention settings");
            }
        }
    }
}
