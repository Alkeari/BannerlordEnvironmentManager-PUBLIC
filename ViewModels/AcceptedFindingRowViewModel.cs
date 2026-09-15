using BannerlordEnvironmentManager.Core.Diagnostics;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One accepted finding, from any source that shares AcceptedFindingStore: an install check, a mod
    // safety alarm, a boot check result. Read straight from the store rather than from a live check, so
    // this list is complete even for a family whose page has not been reopened since the finding was
    // accepted.
    public sealed class AcceptedFindingRowViewModel
    {
        private readonly Action<AcceptedFindingRowViewModel> forget;

        public AcceptedFindingRowViewModel(AcceptedFinding finding, Action<AcceptedFindingRowViewModel> forget)
        {
            ArgumentNullException.ThrowIfNull(finding);

            Finding = finding;
            this.forget = forget;
        }

        public AcceptedFinding Finding { get; }

        public string Headline => Finding.Headline;

        public string CategoryText => Finding.Category;

        public string RecordedText => $"Accepted {Finding.RecordedUtc.ToLocalTime():d}";

        public void Forget() => forget(this);
    }
}
