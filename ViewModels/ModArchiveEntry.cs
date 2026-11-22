using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class ModArchiveEntry : ObservableObject
    {
        public ModArchiveEntry(string displayName, string filePath, string status, IReadOnlyCollection<DetectedModule> modules)
        {
            DisplayName = displayName;
            FilePath = filePath;
            Modules = modules.ToList();
            Status = status;
            IsSelected = false;
        }

        public string DisplayName { get; }
        public string FilePath { get; }
        public IReadOnlyList<DetectedModule> Modules { get; }
        public IReadOnlyList<BinPayload> BinPayloads { get; init; } = [];
        public IReadOnlyList<FileReplacementResolution> FileReplacements { get; init; } = [];
        public string? UnrecognizedLayout { get; init; }
        public string FileSizeDisplay { get; set; } = string.Empty;

        public string FileName => Path.GetFileName(FilePath);

        // A hint, and nothing in BEM may require it. Nexus rewrote its filename grammar three times in
        // 26 days, so a module's own manifest is preferred and the filename is only the fallback.
        public int? NexusModId =>
            Modules.Select(module => NexusArchiveName.TryGetModIdFromUrl(module.ManifestUrl)).FirstOrDefault(id => id is not null)
            ?? NexusArchiveName.TryGetModId(FilePath);

        public string? ModPageUrl =>
            NexusModId is { } modId ? NexusArchiveName.PageUrl(modId) : null;

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; }

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud for every row.
        public override string ToString() => DisplayName;
    }

    // A mod page BEM believes a module came from, waiting for the user to say whether it is right.
    // Nothing here has been recorded: ticking it is what records it.
    public partial class NexusIdMatchRow : ObservableObject
    {
        public NexusIdMatchRow(Core.Nexus.NexusIdProposal proposal)
        {
            ModuleId = proposal.ModuleId;
            ModuleName = proposal.ModuleName;
            NexusModId = proposal.NexusModId;
            Explanation = proposal.Explanation;
            ModPageUrl = proposal.ModPageUrl;
            SuggestedByDefault = proposal.SuggestedByDefault;
            IsSelected = proposal.SuggestedByDefault;
            Headline = proposal.Headline;
        }

        public string ModuleId { get; }

        public string ModuleName { get; }

        public int NexusModId { get; }

        public string Explanation { get; }

        public string ModPageUrl { get; }

        public bool SuggestedByDefault { get; }

        // The page's own name is in here when Nexus has been asked what it is called, because a bare
        // mod id is a number the user cannot judge.
        public string Headline { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        // The other half of ticking. An index match is a resemblance, not a proof, and a wrong one is
        // not wrong once: without somewhere to say so, the same page came back on every search and
        // whoever corrected it by hand corrected it again next time.
        [ObservableProperty]
        public partial bool IsRejected { get; set; }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value)
                IsRejected = false;
        }

        partial void OnIsRejectedChanged(bool value)
        {
            if (value)
                IsSelected = false;
        }

        public override string ToString() => Headline;
    }

    // A mod id the user already accepted. Recording one was reachable and taking it back was not, so
    // a page BUTR named wrongly stayed named wrongly: BUTR put ExpandedArmouryBL on mod 5317 and
    // HistoricalBannerIcons on 5622, and a real install's own logs say 11487 and 9340.
    public sealed class ConfirmedNexusIdRow(Core.Nexus.LearnedNexusId learned)
    {
        public string ModuleId { get; } = learned.ModuleId;

        public int NexusModId { get; } = learned.NexusModId;

        // The sentence that persuaded the user, kept beside the id so a link can be argued with later
        // rather than merely trusted.
        public string Basis { get; } = learned.Basis;

        public string ModPageUrl { get; } = NexusArchiveName.PageUrl(learned.NexusModId);

        public string Headline { get; } =
            $"{learned.ModuleId} - mod {learned.NexusModId}, confirmed {learned.RecordedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        public override string ToString() => Headline;
    }

    // A page the user turned down. Listed for the same reason a confirmed one is: a decision BEM keeps
    // and never shows is one nobody can correct, and a page rejected in error would otherwise be
    // silently unproposable forever.
    public sealed class RejectedNexusIdRow(Core.Nexus.RejectedNexusId rejected)
    {
        public string ModuleId { get; } = rejected.ModuleId;

        public int NexusModId { get; } = rejected.NexusModId;

        public string ModPageUrl { get; } = NexusArchiveName.PageUrl(rejected.NexusModId);

        public string Headline { get; } =
            $"{rejected.ModuleId} - not mod {rejected.NexusModId}, since {rejected.RecordedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        public override string ToString() => Headline;
    }

    public sealed record DetectedModule(
        string ArchiveFolderPath,
        string InstallFolderName,
        string? ModuleName,
        string? ManifestUrl = null,
        string? ModuleId = null)
    {
        public string DisplayLabel
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(ModuleName) ? InstallFolderName : ModuleName;
                return $"{name} → Modules";
            }
        }
    }
}
