using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Named sets of enabled mods. Picking one shows what applying it would turn on and off, applying
    // it moves only the enabled flags (the order is kept), and the whole switch is one step on the
    // same undo list every other Play change uses.
    public partial class EnvironmentViewModel
    {
        private EnabledSetStore EnabledSets => new(EnabledSetStore.DefaultPath(ActiveDataRoot));

        public ObservableCollection<string> EnabledSetNames { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyEnabledSetCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteEnabledSetCommand))]
        public partial string? SelectedEnabledSetName { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveEnabledSetCommand))]
        public partial string NewEnabledSetName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EnabledSetDiffText { get; set; } = string.Empty;

        public int MaxEnabledSetNameLength => EnabledSetStore.MaxNameLength;

        private bool CanSaveEnabledSet => IsLoaded && !string.IsNullOrWhiteSpace(NewEnabledSetName);

        private bool CanUseSelectedEnabledSet => IsLoaded && !string.IsNullOrWhiteSpace(SelectedEnabledSetName);

        partial void OnSelectedEnabledSetNameChanged(string? value) => ShowEnabledSetDiff();

        public void RefreshEnabledSets()
        {
            var keep = SelectedEnabledSetName;

            EnabledSetNames.Clear();

            foreach (var set in EnabledSets.List())
                EnabledSetNames.Add(set.Name);

            SelectedEnabledSetName = keep is not null && EnabledSetNames.Contains(keep) ? keep : null;
            ShowEnabledSetDiff();
        }

        // What the pick would do against the list as it stands now, said before anything changes.
        private void ShowEnabledSetDiff()
        {
            if (!IsLoaded || SelectedEnabledSetName is not { } name || EnabledSets.Find(name) is not { } set)
            {
                EnabledSetDiffText = string.Empty;
                return;
            }

            var preview = EnabledSetSwitch.Apply(LoadOrderSnapshot.From(BuildEnvironment()), set.Enabled, out var missing);
            var strings = Strings.Current;

            if (preview.Diff.IsIdentical && missing.Count == 0)
            {
                EnabledSetDiffText = strings["Environment.EnabledSets.Identical"];
                return;
            }

            var lines = new List<string>
            {
                strings.Format("Environment.EnabledSets.Diff", preview.Diff.TurnedOn.Count, preview.Diff.TurnedOff.Count, preview.Diff.Unchanged),
            };

            if (preview.Diff.TurnedOn.Count > 0)
                lines.Add(strings.Format("Environment.EnabledSets.TurnsOn", string.Join(", ", preview.Diff.TurnedOn)));

            if (preview.Diff.TurnedOff.Count > 0)
                lines.Add(strings.Format("Environment.EnabledSets.TurnsOff", string.Join(", ", preview.Diff.TurnedOff)));

            if (missing.Count > 0)
                lines.Add(strings.Format("Environment.EnabledSets.Missing", string.Join(", ", missing)));

            EnabledSetDiffText = string.Join(System.Environment.NewLine, lines);
        }

        [RelayCommand(CanExecute = nameof(CanSaveEnabledSet))]
        public void SaveEnabledSet()
        {
            if (!CanSaveEnabledSet)
                return;

            var name = NewEnabledSetName.Trim();
            var enabled = EnabledSetDiff.EnabledIds(LoadOrderSnapshot.From(BuildEnvironment())).ToList();

            if (!EnabledSets.Save(name, enabled))
            {
                StatusMessage = Strings.Current.Format("Environment.EnabledSets.SaveFailed", name);
                return;
            }

            NewEnabledSetName = string.Empty;
            RefreshEnabledSets();
            SelectedEnabledSetName = EnabledSetNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            StatusMessage = Strings.Current.Plural("Environment.EnabledSets.Saved", enabled.Count, name);
        }

        [RelayCommand(CanExecute = nameof(CanUseSelectedEnabledSet))]
        public void ApplyEnabledSet()
        {
            if (!CanUseSelectedEnabledSet || EnabledSets.Find(SelectedEnabledSetName!) is not { } set)
                return;

            var before = BuildEnvironment();
            var switched = EnabledSetSwitch.Apply(LoadOrderSnapshot.From(before), set.Enabled, out _);

            if (switched.Diff.IsIdentical)
            {
                StatusMessage = Strings.Current.Format("Environment.EnabledSets.AlreadyApplied", set.Name);
                return;
            }

            var restored = switched.Result.ApplyTo(before, ownTrusted: true);

            RecordUndo(before, Strings.Current.Format("Environment.UndoDescription.EnabledSet", set.Name));
            Apply(restored.Environment);
            PersistOrder();
            ShowEnabledSetDiff();

            StatusMessage = Strings.Current.Format(
                "Environment.EnabledSets.Applied", set.Name, switched.Diff.TurnedOn.Count, switched.Diff.TurnedOff.Count);
        }

        [RelayCommand(CanExecute = nameof(CanUseSelectedEnabledSet))]
        public void DeleteEnabledSet()
        {
            if (SelectedEnabledSetName is not { } name)
                return;

            if (EnabledSets.Delete(name))
                StatusMessage = Strings.Current.Format("Environment.EnabledSets.Deleted", name);

            RefreshEnabledSets();
        }
    }
}
