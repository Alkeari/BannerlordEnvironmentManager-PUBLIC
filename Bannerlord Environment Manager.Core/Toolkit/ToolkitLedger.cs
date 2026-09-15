using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// What BEM installed, so that what BEM did not install is never removed. A copy that was already on the
// machine belongs to whoever put it there.
public sealed class ToolkitLedger
{
    private readonly string filePath;
    private readonly HashSet<string> installed = new(StringComparer.Ordinal);

    public ToolkitLedger() : this(DefaultPath) { }

    public ToolkitLedger(string filePath)
    {
        this.filePath = filePath;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "toolkit-installs.json");

    public string LedgerPath => filePath;

    public bool WasInstalledByBem(string toolId) => installed.Contains(toolId);

    public void Record(string toolId)
    {
        if (installed.Add(toolId))
            Save();
    }

    public void Forget(string toolId)
    {
        if (installed.Remove(toolId))
            Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            if (JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath)) is { } stored)
            {
                foreach (var id in stored)
                    installed.Add(id);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable ledger means BEM cannot prove it installed anything, which is the safe
            // reading: it offers to remove nothing rather than removing somebody else's copy.
            _ = ex;
        }
    }

    private void Save()
    {
        try
        {
            var folder = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(filePath, JsonSerializer.Serialize(installed.Order(StringComparer.Ordinal)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
