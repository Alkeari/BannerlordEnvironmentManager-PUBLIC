namespace BannerlordEnvironmentManager.Core.Modules;

// Whether another mod manager's deployed files are active in a game folder right now: how many
// installed modules Vortex deployed there and how many sit in a Mod Organizer 2 mod folder.
// Read-only, built on the same records ModManagerDetector reads, and never throws.
public sealed record ActiveManagerDeployment(int VortexModules, int Mo2Modules)
{
    public static ActiveManagerDeployment None { get; } = new(0, 0);

    public bool IsActive => VortexModules > 0 || Mo2Modules > 0;

    public static ActiveManagerDeployment Find(
        string gameRoot, IEnumerable<ModuleToLabel> modules, IEnumerable<string>? mo2Roots = null)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return None;

        var labelled = ModManagerDetector.Detect(gameRoot, modules, null, mo2Roots).Values;

        return new ActiveManagerDeployment(
            labelled.Count(m => m.Manager == ModManager.Vortex),
            labelled.Count(m => m.Manager == ModManager.Mo2));
    }
}
