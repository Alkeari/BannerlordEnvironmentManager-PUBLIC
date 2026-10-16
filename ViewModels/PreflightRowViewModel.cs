using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One row of the preflight, covering three things a person reads the same way: something the check
    // found, something the check could not look at, and what the last run of this exact load order did.
    // Only the first can carry a fix, and a row with none says why in its own detail line.
    public sealed class PreflightRowViewModel
    {
        private readonly Action<PreflightFinding>? apply;
        private readonly Action<PreflightFinding>? acceptRisk;

        public PreflightRowViewModel(
            PreflightFinding finding, Action<PreflightFinding> apply, Action<PreflightFinding>? acceptRisk = null)
        {
            ArgumentNullException.ThrowIfNull(finding);

            Finding = finding;
            this.apply = apply;
            this.acceptRisk = acceptRisk;
            GradeText = finding.GradeText;
            Headline = finding.Headline;
            Detail = finding.Detail;
            FixLabel = finding.FixLabel;
        }

        private PreflightRowViewModel(string gradeText, string headline, string detail)
        {
            GradeText = gradeText;
            Headline = headline;
            Detail = detail;
            FixLabel = string.Empty;
        }

        // Said as its own row rather than folded into a count, because "could not look" and "looked and
        // found nothing" are opposite statements and the second is what a missing row implies.
        public static PreflightRowViewModel ForNotChecked(PreflightNote note)
        {
            ArgumentNullException.ThrowIfNull(note);

            return new PreflightRowViewModel(
                Strings.Current["Environment.PreflightRow.GradeText.NotChecked"], note.Reason, string.Empty);
        }

        public static PreflightRowViewModel ForHistory(string line) =>
            new(Strings.Current["Environment.PreflightRow.GradeText.LastRun"], line, string.Empty);

        public PreflightFinding? Finding { get; }

        public string GradeText { get; }

        public string Headline { get; }

        public string Detail { get; }

        public string FixLabel { get; }

        public bool HasDetail => Detail.Length > 0;

        public bool IsFixable => Finding?.IsFixable == true;

        // Warning only. A note already lives behind the disclosure and never gates a launch, so muting
        // one would buy nothing. A Critical is a hard fact about what the loader does, not a judgment
        // call to override: accepting it would not stop the game failing, only stop BEM saying why.
        public bool CanAcceptRisk => Finding is { Grade: PreflightGrade.Warning, Accepted: false };

        public void Fix()
        {
            if (Finding is not null)
                apply?.Invoke(Finding);
        }

        public void AcceptRisk()
        {
            if (Finding is not null)
                acceptRisk?.Invoke(Finding);
        }

        // What a screen reader announces for this row, which is otherwise the type name.
        public override string ToString() =>
            Strings.Current.Format("Environment.PreflightRow.AutomationName", GradeText, Headline);
    }
}
