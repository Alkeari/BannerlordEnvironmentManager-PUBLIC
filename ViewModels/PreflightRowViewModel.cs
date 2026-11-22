using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One row of the preflight, covering three things a person reads the same way: something the check
    // found, something the check could not look at, and what the last run of this exact load order did.
    // Only the first can carry a fix, and a row with none says why in its own detail line.
    public sealed partial class PreflightRowViewModel : ObservableObject
    {
        private readonly Action<PreflightFinding>? apply;
        private readonly Action<PreflightFinding>? acceptRisk;
        private readonly Action<PreflightFinding>? turnOff;

        public PreflightRowViewModel(
            PreflightFinding finding, Action<PreflightFinding> apply, Action<PreflightFinding>? acceptRisk = null,
            Action<PreflightFinding>? turnOff = null)
        {
            ArgumentNullException.ThrowIfNull(finding);

            Finding = finding;
            this.apply = apply;
            this.acceptRisk = acceptRisk;
            this.turnOff = turnOff;
            GradeText = finding.GradeText;
            Headline = finding.Headline;
            // A finding the owner accepted that came back because the game then crashed the way it
            // predicts says so first, so the return is never mistaken for BEM forgetting.
            Detail = finding.ResurfacedAfterCrash
                ? Strings.Current["Environment.PreflightRow.ResurfacedAfterCrash"]
                    + (finding.Detail.Length > 0 ? Environment.NewLine + finding.Detail : string.Empty)
                : finding.ChangedSinceIgnored.Length > 0
                    ? finding.ChangedSinceIgnored + (finding.Detail.Length > 0 ? Environment.NewLine + finding.Detail : string.Empty)
                    : finding.Detail;
            FixLabel = finding.FixLabel;
            Links = finding.Links ?? [];
            IsAccepted = finding.Accepted;
        }

        private PreflightRowViewModel(string gradeText, string headline, string detail)
        {
            GradeText = gradeText;
            Headline = headline;
            Detail = detail;
            FixLabel = string.Empty;
            Links = [];
        }

        // Said as its own row rather than folded into a count, because "could not look" and "looked and
        // found nothing" are opposite statements and the second is what a missing row implies.
        public static PreflightRowViewModel ForNotChecked(PreflightNote note)
        {
            ArgumentNullException.ThrowIfNull(note);

            return new PreflightRowViewModel(
                Strings.Current["Environment.PreflightRow.GradeText.NotChecked"], note.Reason, string.Empty);
        }

        // Every check that could not run, collapsed into one row (findings catalog K32).
        public static PreflightRowViewModel ForNotCheckedSummary(string summary, string detail) =>
            new(Strings.Current["Environment.PreflightRow.GradeText.NotChecked"], summary, detail);

        public static PreflightRowViewModel ForHistory(string line) =>
            new(Strings.Current["Environment.PreflightRow.GradeText.LastRun"], line, string.Empty);

        public PreflightFinding? Finding { get; }

        // Set on the first row of its grade: what the rows from here down have in common.
        public string GroupHeading { get; internal set; } = string.Empty;

        public bool HasGroupHeading => GroupHeading.Length > 0;

        public string GradeText { get; }

        public string Headline { get; }

        public string Detail { get; }

        public string FixLabel { get; }

        public IReadOnlyList<FindingLink> Links { get; }

        public bool HasLinks => Links.Count > 0;

        public bool HasDetail => Detail.Length > 0;

        // Shown only when the user asks for hidden findings; still acceptable like any other.
        public bool IsHiddenByDefault => Finding?.IsHiddenByDefault == true;

        // Starts as the preflight found it and follows Accept the Risk and Undo on this row, so the row
        // shows Accepted at once rather than after the next check.
        [ObservableProperty]
        public partial bool IsAccepted { get; set; }

        public bool CanUndo => Finding is not null && undo is not null;

        private Action<PreflightFinding>? undo;

        internal void SetUndo(Action<PreflightFinding> action) => undo = action;

        public void Undo()
        {
            if (Finding is null || undo is null)
                return;

            undo(Finding);
            IsAccepted = false;
        }

        public Controls.FindingSeverity Severity => Finding?.Grade switch
        {
            PreflightGrade.Critical => Controls.FindingSeverity.Error,
            PreflightGrade.Warning => Controls.FindingSeverity.Warning,
            _ => Controls.FindingSeverity.Note,
        };

        public bool IsError => Finding?.Grade == PreflightGrade.Critical;

        public bool IsWarning => Finding?.Grade == PreflightGrade.Warning;

        public bool IsFixable => Finding?.IsFixable == true;

        // Critical and Warning, so the owner can stop BEM raising either again. A note already lives
        // behind the disclosure and never interrupts a launch, so muting one would buy nothing.
        // Every error and warning can be accepted; the row itself shows whether it already is.
        public bool CanAccept => Finding is { Grade: PreflightGrade.Warning or PreflightGrade.Critical };

        public bool CanAcceptRisk => Finding is { Grade: PreflightGrade.Warning or PreflightGrade.Critical, Accepted: false };

        // Turning the module off, offered only beside a fix that keeps it on and always the last choice.
        public bool CanTurnOff => Finding?.TurnOff is not null && turnOff is not null;

        public string TurnOffLabel => Finding?.TurnOffLabel ?? string.Empty;

        public void TurnOff()
        {
            if (Finding is not null)
                turnOff?.Invoke(Finding);
        }

        public void Fix()
        {
            if (Finding is not null)
                apply?.Invoke(Finding);
        }

        public void AcceptRisk()
        {
            if (Finding is null)
                return;

            acceptRisk?.Invoke(Finding);
            IsAccepted = true;
        }

        // What a screen reader announces for this row, which is otherwise the type name.
        public override string ToString() =>
            Strings.Current.Format("Environment.PreflightRow.AutomationName", GradeText, Headline);
    }
}
