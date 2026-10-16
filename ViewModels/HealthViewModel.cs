using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels;

// The Health destination's answer to "is my setup sound": one count per family of checks, each
// summed from the live collections, plus a one-line summary. These are read-only aggregates, so the
// only thing this view model owns is the moment a change should be re-read to the screen.
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly DiagnosticsViewModel diagnostics;
    private readonly ModSafetyViewModel safety;

    public HealthViewModel(DiagnosticsViewModel diagnostics, ModSafetyViewModel safety)
    {
        this.diagnostics = diagnostics;
        this.safety = safety;

        diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
        safety.PropertyChanged += OnDiagnosticsPropertyChanged;

        diagnostics.XmlOverlapConflicts.CollectionChanged += OnFindingsChanged;
        diagnostics.XmlOverlapOverrides.CollectionChanged += OnFindingsChanged;
        diagnostics.Collisions.CollectionChanged += OnFindingsChanged;
        diagnostics.DryRunFindings.CollectionChanged += OnFindingsChanged;
    }

    public int InstallChecksCount => diagnostics.InstallCheckFindingCount;

    public int OverlapsCount =>
        diagnostics.XmlOverlapConflicts.Count + diagnostics.XmlOverlapOverrides.Count + diagnostics.Collisions.Count;

    public int SafetyCount => safety.AlarmCount;

    public int BootCount => diagnostics.DryRunFindings.Count(r => !r.Accepted);

    // Never folded into the counts above going quiet: an accepted finding is read, not erased, and a
    // badge that only ever shrank when something was accepted would read as a clean install that never
    // had anything to accept. Overlaps has no accept mechanism of its own, so it never contributes here.
    public int AcceptedCount =>
        diagnostics.AcceptedInstallCheckCount + diagnostics.AcceptedBootCheckCount + safety.AcceptedAlarmCount;

    public string HealthSummary
    {
        get
        {
            var total = InstallChecksCount + OverlapsCount + SafetyCount + BootCount;
            var head = total == 0
                ? Strings.Current["Health.Summary.NoFindings"]
                : Strings.Current.Plural("Health.Summary.FindingCount", total);

            return AcceptedCount == 0
                ? head
                : $"{head} {Strings.Current.Plural("Health.Summary.AcceptedSuffix", AcceptedCount)}";
        }
    }

    public void Refresh() => _ = diagnostics.CheckInstallQuicklyAsync();

    private void OnDiagnosticsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiagnosticsViewModel.InstallCheckFindingCount)
            or nameof(DiagnosticsViewModel.AcceptedInstallCheckCount)
            or nameof(DiagnosticsViewModel.AcceptedBootCheckCount)
            or nameof(ModSafetyViewModel.AlarmCount)
            or nameof(ModSafetyViewModel.AcceptedAlarmCount))
        {
            RaiseAll();
        }
    }

    private void OnFindingsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RaiseAll();

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(InstallChecksCount));
        OnPropertyChanged(nameof(OverlapsCount));
        OnPropertyChanged(nameof(SafetyCount));
        OnPropertyChanged(nameof(BootCount));
        OnPropertyChanged(nameof(AcceptedCount));
        OnPropertyChanged(nameof(HealthSummary));
    }
}
