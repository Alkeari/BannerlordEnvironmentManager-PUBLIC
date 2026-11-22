using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One reason on a Play row. Fix is null when the finding has no repair of its own.
    public sealed class ModuleRowProblem(string reason, bool isError, string fixLabel, string fixTooltip, System.Windows.Input.ICommand? fix)
    {
        public string Reason { get; } = reason;

        public bool IsError { get; } = isError;

        public string FixLabel { get; } = fixLabel;

        public string FixTooltip { get; } = fixTooltip;

        public System.Windows.Input.ICommand? Fix { get; } = fix;

        public bool HasFix => Fix is not null;
    }

    public partial class ModuleRowViewModel : ObservableObject, ILoadOrderRowViewModel
    {
        public ModuleRowViewModel(ModuleEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            Entry = entry;
            IsEnabled = entry.IsEnabled;
        }

        public ModuleEntry Entry { get; }

        public bool IsDivider => false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(StateNeedsAttention))]
        [NotifyPropertyChangedFor(nameof(StateIsQuiet))]
        public partial bool IsEnabled { get; set; }

        // Whether this row is part of the multi-select set the context menu acts on. Kept off the
        // ListView's own selection entirely, because the native multi-select draws a checkbox per row
        // that reads as the module being enabled. The highlight it drives is the only visible mark.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectionBackground))]
        [NotifyPropertyChangedFor(nameof(SelectionBorder))]
        public partial bool IsUiSelected { get; set; }

        public Microsoft.UI.Xaml.Media.Brush SelectionBackground =>
            IsUiSelected
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkSelectionBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        public Microsoft.UI.Xaml.Media.Brush SelectionBorder =>
            IsUiSelected
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkWarmGoldShadowBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        [ObservableProperty]
        public partial int Position { get; set; }

        [ObservableProperty]
        public partial bool IsPinned { get; set; }

        // The player's own note and tags for this mod, read from ModNotesStore. Shown under the module
        // id, and searched, so typing a tag finds every mod carrying it.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UserNoteLine))]
        [NotifyPropertyChangedFor(nameof(HasUserNoteLine))]
        public partial string UserNote { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UserNoteLine))]
        [NotifyPropertyChangedFor(nameof(HasUserNoteLine))]
        public partial IReadOnlyList<string> UserTags { get; set; } = [];

        public string UserNoteLine => string.Join("  ", UserTags.Select(tag => "#" + tag))
            + (UserTags.Count > 0 && UserNote.Length > 0 ? "  " : string.Empty)
            + UserNote;

        public bool HasUserNoteLine => UserNoteLine.Length > 0;

        // Where a mod with no Nexus page came from, as the player wrote it.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NotOnNexusSourceLine))]
        [NotifyPropertyChangedFor(nameof(HasNotOnNexusSourceLine))]
        public partial string UserSource { get; set; } = string.Empty;

        public string NotOnNexusSourceLine => (ShowNoNexusId || IsNotOnNexus) && UserSource.Length > 0
            ? Core.Localization.Strings.Current.Format("Library.Source.NotOnNexusFrom", UserSource)
            : string.Empty;

        public bool HasNotOnNexusSourceLine => NotOnNexusSourceLine.Length > 0;

        // The user's own statement that this module is not published on Nexus, never something BEM
        // worked out: "no install record and no mod id" describes a hand-extracted mod just as well as
        // a self-authored one. It sits beside IsPinned rather than among the badges read off the
        // manifest, because those two are the only flags on this row the user set by hand.
        [ObservableProperty]
        public partial bool IsNotOnNexus { get; set; }

        // What the last update check concluded about this row. Kept as separate states rather than one
        // string because they are what a pip is drawn from, and "probably" is a different claim from
        // "is": one compares the file Nexus published against the file that was installed, the other is
        // reasoning about a date because nothing identified the file.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(StateNeedsAttention))]
        [NotifyPropertyChangedFor(nameof(StateIsQuiet))]
        public partial bool IsOutOfDate { get; set; }

        [ObservableProperty]
        public partial bool IsProbablyOutOfDate { get; set; }

        // The user's statement that this copy was compiled here. It sits with IsNotOnNexus rather than
        // with the update states because it is a thing they said, not a thing the check concluded.
        [ObservableProperty]
        public partial bool IsBuiltLocally { get; set; }

        // The check ran and reached no verdict for this module. Only ever true after an answer has come
        // back: before the first check every row is unjudged, and drawing this on all of them would say
        // BEM had failed at something it had not yet been asked to do.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowUnknownUpdate))]
        public partial bool IsUpdateUnknown { get; set; }

        // He chose to stay on this version, so nothing on the row offers the newer one.
        [ObservableProperty]
        public partial bool IsUpdateHeld { get; set; }

        [ObservableProperty]
        public partial string? HeldReason { get; set; }

        // Two sources naming two different mod pages. It outranks the unknown pip on the row because it
        // is the reason for the unknown and the only part of it anybody can fix.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowUnknownUpdate))]
        public partial bool HasIdConflict { get; set; }

        // Only the pip: the row's state stays IsUpdateUnknown, which the filter and the summary count both read.
        public bool ShowUnknownUpdate => IsUpdateUnknown && !HasIdConflict;

        // Whether BEM holds a Nexus mod id at all. Absent means update checking cannot see this module,
        // which is a gap in what BEM knows rather than a statement that the mod has no page.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNexus))]
        [NotifyPropertyChangedFor(nameof(ShowNoNexusId))]
        public partial bool HasNexusId { get; set; }

        // Nexus answered that no published file matches the archive this module was installed from. Said
        // beside "No Nexus mod id" so the row explains why no id can be found, never written as a verdict.
        [ObservableProperty]
        public partial bool NexusHasNoMatchingFile { get; set; }

        // Who put this module on disk, read from the disk and BEM's own records. Hand is the default and
        // draws nothing, so a Pill only ever says something a user might not already assume.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsManagedByBem))]
        [NotifyPropertyChangedFor(nameof(IsManagedByVortex))]
        [NotifyPropertyChangedFor(nameof(IsManagedByMo2))]
        public partial ModManager Manager { get; set; } = ModManager.Hand;

        // The Nexus mod id MO2 holds in the module's meta.ini, kept for Find Mod IDs to use later.
        public int? ManagerNexusModId { get; set; }

        public bool IsManagedByBem => Manager == ModManager.Bem;

        public bool IsManagedByVortex => Manager == ModManager.Vortex;

        public bool IsManagedByMo2 => Manager == ModManager.Mo2;

        // Where a community module came from, as a closed partition: it is a Workshop subscription, or it
        // is a Nexus mod, or it is one BEM holds no id for. Official modules are none of the three and
        // carry their own badge instead.
        //
        // A Workshop module is never missing a Nexus id, because it is not supposed to have one. Marking
        // it as a gap would invent a worry and would put thirteen false alarms on the reference install.
        public bool ShowNexus => !IsOfficial && !IsWorkshop && HasNexusId;

        public bool ShowNoNexusId => !IsOfficial && !IsWorkshop && !HasNexusId;

        // Null for a row with no manifest to classify: an orphan or an unreadable module already says
        // so in its own badge, and inventing a tier for it would be a guess presented as a fact.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTier))]
        [NotifyPropertyChangedFor(nameof(TierText))]
        [NotifyPropertyChangedFor(nameof(TierTooltip))]
        public partial ModuleTier? Tier { get; set; }

        public bool HasTier => Tier is not null;

        // The badge only. Sorting and the load-order warnings read Tier, never this text.
        public string TierText => Tier is { } tier ? ModuleCategoryLabel.For(tier, NexusCategory) : string.Empty;

        // The category Nexus Mods files this module's page under, when BEM knows the page and has read it.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TierText))]
        public partial string? NexusCategory { get; set; }

        // An official module's tier is Official, and the badge beside it already says Official, with the
        // optional distinction the tier does not carry. Showing both said the same word twice on one row.
        public bool ShowTier => HasTier && !IsOfficial;

        // Without the prefix this is an unlabelled second line under the name, which reads as a subtitle
        // rather than as the id the game actually matches on.
        public string ModuleIdText => Strings.Current.Format("Environment.ModuleIdText", ModuleId);

        public string TierTooltip => Tier is { } tier
            ? Strings.Current.Format(
                "Environment.TierTooltip", ModuleTierMap.Describe(tier), ModuleTierMap.Explain(tier))
            : string.Empty;

        // Why this module cannot work, one short reason each with the fix beside it where one exists. Set
        // from Core's ModuleProblems mapping; the reasons and fixes are Health's own, not copies of them.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblems))]
        public partial IReadOnlyList<ModuleRowProblem> Problems { get; set; } = [];

        public bool HasProblems => Problems.Count > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(StateNeedsAttention))]
        [NotifyPropertyChangedFor(nameof(StateIsQuiet))]
        public partial string IssueSummary { get; set; } = string.Empty;

        // The State column: the worst thing known about the row, otherwise whether it is on.
        public string StateText => Strings.Current[
            IsOrphan ? "Environment.RowState.Missing"
            : IsUnreadable ? "Environment.RowState.Unreadable"
            : IssueSummary.Length > 0 ? "Environment.RowState.Issue"
            : IsOutOfDate ? "Environment.RowState.NeedsUpdate"
            : IsEnabled ? "Environment.RowState.On"
            : "Environment.RowState.Off"];

        public bool StateNeedsAttention => IsOrphan || IsUnreadable || IssueSummary.Length > 0 || IsOutOfDate;

        public bool StateIsQuiet => !StateNeedsAttention;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NoteCount))]
        [NotifyPropertyChangedFor(nameof(HasNotes))]
        [NotifyPropertyChangedFor(nameof(NotesHeader))]
        [NotifyPropertyChangedFor(nameof(AreNotesVisible))]
        public partial IReadOnlyList<string> Notes { get; set; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AreNotesVisible))]
        public partial bool ShowNotes { get; set; } = LaunchSettings.ShowNotesDefault;

        public int NoteCount => Notes.Count;

        public bool HasNotes => NoteCount > 0;

        public bool AreNotesVisible => HasNotes && ShowNotes;

        public string NotesHeader => Strings.Current.Plural("Environment.NotesHeader", NoteCount);

        public string LoadRulesAutomationName => Strings.Current.Format("Environment.LoadRulesAutomationName", DisplayName);

        public string NotesAutomationName => Strings.Current.Format("Environment.NotesAutomationName", DisplayName);

        public string DisplayName => Entry.DisplayName;

        public string ModuleId => Entry.Id.Value;

        // These two stand where a version number would, so they are labels rather than prose and are
        // capitalized like one.
        // The Nexus file this copy came from, when its manifest states another version.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayVersion))]
        public partial string? FileVersion { get; set; }

        public string DisplayVersion => FileVersion is { } file
            ? Strings.Current.Format("Environment.ModuleVersion.WithFile", VersionText, file)
            : VersionText;

        public string VersionText => Entry switch
        {
            { IsOrphan: true } => Strings.Current["Environment.ModuleVersion.NotInstalled"],
            { IsUnreadable: true } => Strings.Current["Environment.ModuleVersion.Unreadable"],
            _ => Entry.Version.ToString()
        };

        public bool IsOrphan => Entry.IsOrphan;

        public bool IsUnreadable => Entry.IsUnreadable;

        // Manual has no badge on purpose: it is what almost every row is, so a badge on all of them
        // would say nothing. The badges mark the rows that are not simply a mod you installed.
        public bool IsWorkshop => Entry.Origin == ModuleOrigin.Workshop;

        public bool IsOfficial => Entry.IsOfficial;

        // NavalDLC and BirthAndDeath are the game's own and are still meant to be switchable, while
        // Native is not. Both read as "Official" until the row says which kind, and the difference is
        // the whole answer to "is it safe to turn this off".
        public bool IsOfficialOptional => Entry.Manifest?.Type == ModuleType.OfficialOptional;

        public string OfficialText => IsOfficialOptional
            ? Strings.Current["Environment.OfficialOptionalMark"]
            : Strings.Current["Environment.Filter.Official.Name"];

        public string OfficialTooltip => IsOfficialOptional
            ? Strings.Current["Environment.OfficialOptionalMark.Tooltip"]
            : Strings.Current["Environment.OfficialMark.Tooltip"];

        public bool CanToggle => !Entry.IsOrphan;

        // Set by the settings scan: true only when a settings folder definitely belongs to this mod.
        [ObservableProperty]
        public partial bool HasSettingsFile { get; set; }

        // Everything this row says about itself, in the words it says it in. The search box used to
        // read the name and the id and nothing else, so every pip on the row was a fact you could see
        // and could not search for: no way to ask for the modules marked No Nexus ID, or Out of Date,
        // or Workshop, when those are exactly the questions the marks exist to answer.
        //
        // A term compared against typed text is not the same thing as a term compared against another
        // hardcoded string, and the localization rule for the two is opposite. Here the comparison is
        // the user's own typed text against the words on their own screen: a German user searching
        // types what the German badge says, so the badge's own key is exactly what belongs here, not
        // its English source text. The question for every term below is only "does this correspond to
        // text the row actually renders": if it does and that text is localized, the term must be the
        // localized value too, or a translated screen stops being searchable in its own language. If it
        // renders nothing (an id, a raw enum, a checkbox with no caption), the literal stays, because
        // there is no translated counterpart to drift out of sync with.
        public IEnumerable<string> SearchTerms()
        {
            yield return DisplayName;
            yield return ModuleId;
            yield return VersionText;

            foreach (var tag in UserTags)
                yield return tag;

            if (UserNote.Length > 0)
                yield return UserNote;

            // Enabled/Disabled render as a CheckBox's checked state, never as a word on the row, so
            // there is no localized text these could fall out of sync with.
            yield return IsEnabled ? "Enabled" : "Disabled";

            if (IsOfficial)
                yield return OfficialText;

            if (IsWorkshop)
                yield return Strings.Current["Environment.Filter.Workshop.Name"];

            if (ShowNexus)
                yield return Strings.Current["Environment.Filter.Nexus.Name"];

            if (ShowNoNexusId)
                yield return Strings.Current["Environment.Filter.NoNexusId.Name"];

            if (ShowTier)
                yield return TierText;

            if (IsOrphan)
                yield return Strings.Current["Environment.OrphanMark"];

            if (IsUnreadable)
                yield return Strings.Current["Environment.Filter.Unreadable.Name"];

            if (IsOutOfDate)
                yield return Strings.Current["Environment.Filter.OutOfDate.Name"];

            if (IsProbablyOutOfDate)
                yield return Strings.Current["Environment.Filter.ProbablyOutOfDate.Name"];

            if (HasIdConflict)
                yield return Strings.Current["Environment.Filter.IdConflict.Name"];

            if (IsUpdateUnknown)
                yield return Strings.Current["Environment.Filter.UpdateUnknown.Name"];

            if (IsPinned)
                yield return Strings.Current["Environment.Filter.Pinned.Name"];

            if (IsNotOnNexus)
                yield return Strings.Current["Environment.Filter.NotOnNexus.Name"];

            if (IsBuiltLocally)
                yield return Strings.Current["Environment.Filter.BuiltHere.Name"];

            // The row never renders the bare word "Notes": what it shows is NotesHeader, a
            // count-and-noun sentence ("1 note" / "{0} notes") that changes shape with the count and
            // with the language. There is no single translated string this literal could be replaced
            // with without it going stale the moment the count crosses one, so it stays a literal
            // search tag rather than a copy of text nobody sees.
            if (HasNotes)
                yield return "Notes";

            // Same reasoning as Notes: the row shows IssueSummary itself (yielded on the next line), a
            // full generated sentence, never the bare word "Issues".
            if (IssueSummary.Length > 0)
            {
                yield return "Issues";
                yield return IssueSummary;
            }
        }

        // Matched twice: once as typed, so "Nexus" finds what a reader sees, and once with everything
        // that is not a letter or a digit taken out of both sides, so "Starvation Stopper" finds
        // StarvationStopper and "outofdate" finds Out of Date. A mod's display name and its module id
        // differ by exactly that punctuation for most of a load order.
        public bool MatchesSearch(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            var squashed = Squash(query);

            foreach (var term in SearchTerms())
            {
                if (term.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (squashed.Length > 0 && Squash(term).Contains(squashed, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string Squash(string text) =>
            new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud once per module, 238 times over.
        public override string ToString() => DisplayName;

        public ModuleEntry ToEntry() =>
            Entry with { IsEnabled = Entry.IsOrphan ? Entry.IsEnabled : IsEnabled };
    }
}
