using BannerlordEnvironmentManager.Controls;
using BannerlordEnvironmentManager.Core.Diagnostics;

namespace BannerlordEnvironmentManager.ViewModels;

// Which Health family a finding came from: the chip row filters on it.
public enum HealthFamily
{
    InstallChecks,
    Overlaps,
    Safety,
    Boot,
}

// One finding on the Health page, whatever check raised it, in the shape FindingRow draws: glyph,
// headline, what to do, the check's own buttons, and Accept the Risk with Undo. It wraps the check's own
// row so accepting and undoing go through exactly the code each check always used
// (AcceptedFindingStore), and an accepted finding stays in the list, marked, and still counts.
public sealed partial class HealthFindingRowViewModel : ObservableObject
{
    private readonly Action<HealthFindingRowViewModel> accept;
    private readonly Action<HealthFindingRowViewModel> undo;

    public HealthFindingRowViewModel(
        HealthFamily family,
        FindingSeverity severity,
        string headline,
        string detail,
        string evidence,
        bool accepted,
        bool canAccept,
        IReadOnlyList<KnownIssueActionViewModel> actions,
        object source,
        Action<HealthFindingRowViewModel> accept,
        Action<HealthFindingRowViewModel> undo,
        bool isHiddenByDefault = false)
    {
        Family = family;
        Severity = severity;
        Headline = headline;
        Detail = detail;
        Evidence = evidence;
        IsAccepted = accepted;
        CanAccept = canAccept;
        Actions = actions;
        Source = source;
        IsHiddenByDefault = isHiddenByDefault;
        this.accept = accept;
        this.undo = undo;

        // What the last button pressed on the check's own row did (Make <mod> Win, above all), shown on
        // this row too: the Health page is where he pressed it, and a refusal said only on the Overlaps
        // page read as a button that did nothing.
        if (source is KnownIssueRowViewModel issue)
        {
            ActionResult = issue.ActionResult;
            issue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(KnownIssueRowViewModel.ActionResult))
                    ActionResult = issue.ActionResult;
            };
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionResult))]
    public partial string ActionResult { get; set; } = string.Empty;

    public bool HasActionResult => ActionResult.Length > 0;

    // Findings that say the same thing about the same mods share this key, so one press can settle all
    // of them, since most share the same issue between mods.
    // A patch-order row shows the same fix text whatever the method, so its own problem lines (which name
    // the mods and the order) are part of its key, or every such row would count as "like" every other.
    public string GroupKey => HealthGroups.Key(Family.ToString(), Source is PatchOrderRowViewModel ? Evidence + "|" + Detail : Detail);

    // How many open findings on the page share this one's key, itself included.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSimilar))]
    public partial int SimilarCount { get; set; } = 1;

    public bool HasSimilar => SimilarCount > 1 && CanAccept && !IsAccepted;

    // A finding gets a tick box when a batch action can apply to it: Accept the Risk or a shared remedy.
    [ObservableProperty]
    public partial bool CanSelect { get; set; }

    public string SelectionKey => $"{Family}|{Headline}|{Detail}";

    [ObservableProperty]
    public partial string AcceptSimilarLabel { get; set; } = string.Empty;

    public Action<HealthFindingRowViewModel>? AcceptSimilarHandler { get; set; }

    // One press selects every open finding of this kind, so any batch action then applies to exactly
    // those, so all items with the same or a similar issue can be selected at once.
    [ObservableProperty]
    public partial string SelectSimilarLabel { get; set; } = string.Empty;

    public Action<HealthFindingRowViewModel>? SelectSimilarHandler { get; set; }

    public Action? SelectionChanged { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        _ = value;
        SelectionChanged?.Invoke();
    }

    [RelayCommand]
    private void AcceptSimilar() => AcceptSimilarHandler?.Invoke(this);

    [RelayCommand]
    private void SelectSimilar() => SelectSimilarHandler?.Invoke(this);

    public HealthFamily Family { get; }

    public FindingSeverity Severity { get; }

    public bool IsError => Severity == FindingSeverity.Error;

    public string Headline { get; }

    public string Detail { get; }

    public string Evidence { get; }

    public bool CanAccept { get; }

    public IReadOnlyList<KnownIssueActionViewModel> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    // What the row shows beside Accept the Risk: every action that changes something, so a finding with two
    // ways out (turn off either mod) offers both. Opening a folder only looks, so those wait in Details,
    // unless looking is all the finding offers.
    public IReadOnlyList<KnownIssueActionViewModel> PrimaryActions =>
        Actions.Where(a => !OnlyLooks(a)) is var changing && changing.Any() ? [.. changing] : [.. Actions.Take(1)];

    public IEnumerable<KnownIssueActionViewModel> OtherActions => Actions.Except(PrimaryActions);

    private static bool OnlyLooks(KnownIssueActionViewModel action) =>
        action.Parameter is string path && Path.IsPathRooted(path);

    // The check's own row: a KnownIssueRowViewModel or a SafetyRowViewModel.
    public object Source { get; }

    // Fired on clean sessions and matched no crash (the 2026-10-30 deep dive): counted, acceptable, and
    // listed only when the user asks for hidden findings.
    public bool IsHiddenByDefault { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSimilar))]
    public partial bool IsAccepted { get; set; }

    // Set by Health so one press and a batch press take the same path: recorded under Health's own key,
    // then the check's own accept, then the list redrawn so the row leaves at once.
    public Action<HealthFindingRowViewModel>? AcceptHandler { get; set; }

    public Action<HealthFindingRowViewModel>? UndoHandler { get; set; }

    internal void AcceptAtSource() => accept(this);

    internal void UndoAtSource() => undo(this);

    [RelayCommand]
    private void Accept()
    {
        if (AcceptHandler is { } handler)
        {
            handler(this);
            return;
        }

        accept(this);
        IsAccepted = true;
    }

    [RelayCommand]
    private void Undo()
    {
        if (UndoHandler is { } handler)
        {
            handler(this);
            return;
        }

        undo(this);
        IsAccepted = false;
    }
}
