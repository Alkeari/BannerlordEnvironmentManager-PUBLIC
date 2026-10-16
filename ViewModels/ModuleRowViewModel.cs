using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
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

        // Two sources naming two different mod pages. It outranks the unknown pip on the row because it
        // is the reason for the unknown and the only part of it anybody can fix.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowUnknownUpdate))]
        public partial bool HasIdConflict { get; set; }

        public bool ShowUnknownUpdate => IsUpdateUnknown && !HasIdConflict;

        // Whether BEM holds a Nexus mod id at all. Absent means update checking cannot see this module,
        // which is a gap in what BEM knows rather than a statement that the mod has no page.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNexus))]
        [NotifyPropertyChangedFor(nameof(ShowNoNexusId))]
        public partial bool HasNexusId { get; set; }

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

        public string TierText => Tier is { } tier ? ModuleTierMap.Label(tier) : string.Empty;

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

        [ObservableProperty]
        public partial string IssueSummary { get; set; } = string.Empty;

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

        public string NotesAutomationName => Strings.Current.Format("Environment.NotesAutomationName", DisplayName);

        public string DisplayName => Entry.DisplayName;

        public string ModuleId => Entry.Id.Value;

        // These two stand where a version number would, so they are labels rather than prose and are
        // capitalized like one.
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

            if (ShowUnknownUpdate)
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
