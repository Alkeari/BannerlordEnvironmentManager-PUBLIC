using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Nexus;

// What the one-click stack install decided to do about one of its members. Every answer other than
// Install and Update is a decision to leave what is on disk alone, and each one says why: a stack
// install that quietly wrote over a newer copy would be worse than one that did nothing.
public enum ButrStackStep
{
    Install,
    Update,
    AlreadyCurrent,
    // The copy on disk declares a version ahead of the one Nexus offers, or one that cannot be read
    // against it at all. Both are refusals to overwrite on a guess.
    LeftAlone,
    NoFileOffered,
    ListingNotRead
}

public sealed record ButrStackItem(
    Prerequisite Prerequisite,
    ButrStackStep Step,
    string? InstalledVersion,
    NexusModFile? File,
    string Reason)
{
    public string Name => Prerequisite.Name;

    public int NexusModId => Prerequisite.NexusModId;

    public bool NeedsFetch => Step is ButrStackStep.Install or ButrStackStep.Update;

    // The file id and version are already in hand, because the choice was made off Nexus's own
    // published file list, so this carries the same provenance an autonomous update carries.
    public ModUpdateTarget? Target => NeedsFetch && File is { } file
        ? new ModUpdateTarget(Prerequisite.Id, Prerequisite.Name, Prerequisite.NexusModId, file.FileId, file.FileName, file.Version)
        : null;
}

public sealed record ButrStackPlan(IReadOnlyList<ButrStackItem> Items)
{
    public IReadOnlyList<ButrStackItem> Fetches => [.. Items.Where(item => item.NeedsFetch)];

    public IReadOnlyList<ModUpdateTarget> Targets => [.. Fetches.Select(item => item.Target!)];

    // A member whose page never arrived. It is not a verdict about what is on disk: BEM asked nothing
    // and learned nothing, and the caller that reports the run has to be able to tell that apart from
    // a member it really did check.
    public IReadOnlyList<ButrStackItem> Unread => [.. Items.Where(item => item.Step is ButrStackStep.ListingNotRead)];

    // The members that were read and need nothing fetched, which are the ones worth a line of their
    // own. An unread member is reported where it failed, with the transport's own message, so listing
    // it here as well says the same thing twice in weaker words.
    public IReadOnlyList<ButrStackItem> Decided =>
        [.. Items.Where(item => !item.NeedsFetch && item.Step is not ButrStackStep.ListingNotRead)];

    public bool NothingToFetch => Fetches.Count == 0;

    // "Checked, and there is nothing to do", which is the only state that may be reported as a success.
    // Nothing to fetch is not the same claim: five pages that could not be read produce nothing to
    // fetch as well, and reporting that as everything being current is a success claim over work that
    // never happened.
    public bool EverythingCurrent => Fetches.Count == 0 && Unread.Count == 0;

    public bool NothingCouldBeRead => Items.Count > 0 && Unread.Count == Items.Count;
}

// What BEM found on disk for one stack member. A null version is not "no version": it is BEM not
// having read one, and it is the reason an installed copy is left alone rather than replaced.
public readonly record struct ButrStackInstalled(bool IsInstalled, string? Version);

// The BUTR stack is Prerequisites.All: the same five things every Bannerlord mod is built against,
// with the same recorded Nexus mod ids. There is deliberately no second list here, so adding a stack
// member stays one row in Prerequisites rather than a new code path in two places.
public static class ButrStack
{
    public static IReadOnlyList<Prerequisite> Members => Prerequisites.All;

    // The website's own Mod Manager Download route, aimed at one exact file. This is what a free
    // account has instead of an API download link: Nexus mints the single-use grant when the user
    // clicks, and the nxm:// link that arrives carries it. The file id is on the query so the page
    // opens on that file rather than on whichever one the author has pinned to the top.
    public static string ModManagerDownloadUrl(int modId, int fileId) =>
        $"{Install.NexusArchiveName.PageUrl(modId)}?tab=files&file_id={fileId}&nmm=1";

    // One current MAIN file per page is what all five of these publish, so MAIN is the rule and the
    // newest upload settles a page that ever carries two. The fallback is the newest file Nexus still
    // files under any current category, which is what ModUpdatePlanner already falls back to; a page
    // with no current file at all yields nothing rather than an old version.
    public static NexusModFile? ChooseFile(NexusModFileListing? listing)
    {
        if (listing is null)
            return null;

        var current = listing.Files.Where(file => file.Standing is NexusFileStanding.Current).ToList();

        return Newest(current.Where(file => NexusFileCategories.IsMain(file.CategoryName, file.CategoryId)))
               ?? Newest(current);
    }

    private static NexusModFile? Newest(IEnumerable<NexusModFile> files) =>
        files.OrderByDescending(file => file.UploadedUtc ?? DateTimeOffset.MinValue).FirstOrDefault();

    public static ButrStackPlan Plan(
        IReadOnlyDictionary<int, NexusModFileListing> filesByModId,
        IReadOnlyDictionary<string, ButrStackInstalled> installedById)
    {
        ArgumentNullException.ThrowIfNull(filesByModId);
        ArgumentNullException.ThrowIfNull(installedById);

        return new ButrStackPlan([.. Members.Select(member => Decide(member, filesByModId, installedById))]);
    }

    private static ButrStackItem Decide(
        Prerequisite member,
        IReadOnlyDictionary<int, NexusModFileListing> filesByModId,
        IReadOnlyDictionary<string, ButrStackInstalled> installedById)
    {
        if (!filesByModId.TryGetValue(member.NexusModId, out var listing))
            return new ButrStackItem(member, ButrStackStep.ListingNotRead, null, null,
                Strings.Current["Core.Nexus.ButrStack.ListingNotRead"]);

        if (ChooseFile(listing) is not { } file)
            return new ButrStackItem(member, ButrStackStep.NoFileOffered, null, null,
                Strings.Current["Core.Nexus.ButrStack.NoFileOffered"]);

        var offered = file.Version ?? string.Empty;
        var installed = installedById.TryGetValue(member.Id, out var found) ? found : default;

        if (!installed.IsInstalled)
            return new ButrStackItem(member, ButrStackStep.Install, null, file,
                Strings.Current.Format("Core.Nexus.ButrStack.NotInstalled", Describe(offered)));

        if (installed.Version is not { Length: > 0 } local)
            return new ButrStackItem(member, ButrStackStep.LeftAlone, null, file,
                Strings.Current["Core.Nexus.ButrStack.VersionUnreadable"]);

        if (ModuleUpdateVerdicts.SameVersion(local, offered))
            return new ButrStackItem(member, ButrStackStep.AlreadyCurrent, local, file,
                Strings.Current.Format("Core.Nexus.ButrStack.AlreadyCurrent", local));

        return ModuleUpdateVerdicts.Compare(local, offered) switch
        {
            ModuleVersionComparison.Behind => new ButrStackItem(member, ButrStackStep.Update, local, file,
                Strings.Current.Format("Core.Nexus.ButrStack.Behind", local, Describe(offered))),
            ModuleVersionComparison.Ahead => new ButrStackItem(member, ButrStackStep.LeftAlone, local, file,
                Strings.Current.Format("Core.Nexus.ButrStack.LocalIsNewer", local, Describe(offered))),
            _ => new ButrStackItem(member, ButrStackStep.LeftAlone, local, file,
                Strings.Current.Format("Core.Nexus.ButrStack.NotComparable", local, Describe(offered)))
        };
    }

    private static string Describe(string version) =>
        version.Length > 0 ? version : Strings.Current["Core.Nexus.ButrStack.UnstatedVersion"];
}

// What is on disk for each stack member. The module members are answered from the module list, which
// is where the game itself reads them from; BLSE is not a module and has no manifest anywhere, so its
// version comes off the loader executable's own file version, which is the only version of it that
// exists on disk.
public static class ButrStackInstallState
{
    public static IReadOnlyDictionary<string, ButrStackInstalled> Read(
        string? gameInstallPath,
        IReadOnlyList<ModuleManifest> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var byId = new Dictionary<string, ButrStackInstalled>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in Prerequisites.Inspect(gameInstallPath, modules.Select(module => module.Id)))
        {
            var member = state.Prerequisite;

            byId[member.Id] = new ButrStackInstalled(
                state.IsInstalled,
                state.IsInstalled ? VersionOf(member, gameInstallPath, modules) : null);
        }

        return byId;
    }

    private static string? VersionOf(Prerequisite member, string? gameInstallPath, IReadOnlyList<ModuleManifest> modules)
    {
        if (member.Kind is PrerequisiteKind.Loader)
            return Prerequisites.FindLoaderPath(gameInstallPath) is { } loader ? LoaderVersion(loader) : null;

        var module = modules.FirstOrDefault(item => item.Id.Value.Equals(member.Id, StringComparison.OrdinalIgnoreCase));

        if (module is null)
            return null;

        // What SubModule.xml literally says wins over the parsed form, because the parse drops a zero
        // fourth component and the file is the thing the game reads.
        return module.VersionText is { Length: > 0 } text ? text : module.Version.ToString();
    }

    private static string? LoaderVersion(string loaderPath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(loaderPath).FileVersion is { Length: > 0 } version ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }
}
