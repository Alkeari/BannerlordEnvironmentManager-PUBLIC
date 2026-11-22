using BannerlordEnvironmentManager.Core.Launcher;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One risk the user has already read and chosen to launch past. Read straight from the store
    // rather than from a live preflight, so this list is complete even for a check the current load
    // order no longer triggers: a risk accepted for a mod that was since turned off should still be
    // visible and forgettable, not just quietly inert.
    public sealed class AcceptedRiskRowViewModel
    {
        private readonly Action<AcceptedRiskRowViewModel> forget;

        public AcceptedRiskRowViewModel(AcceptedRisk risk, Action<AcceptedRiskRowViewModel> forget)
        {
            ArgumentNullException.ThrowIfNull(risk);

            Risk = risk;
            this.forget = forget;
        }

        public AcceptedRisk Risk { get; }

        public string Headline => Risk.Headline;

        public string CheckText => Risk.Check.ToString();

        public string RecordedText => $"Accepted {Risk.RecordedUtc.ToLocalTime():d}";

        public void Forget() => forget(this);
    }
}
