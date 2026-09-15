namespace BannerlordEnvironmentManager.ViewModels;

// The Forensics destination's one-line answer: where the evidence stands. It reads straight off the
// shared Diagnostics view model, so it is a read-only summary with nothing of its own to refresh.
public sealed class ForensicsViewModel
{
    private readonly DiagnosticsViewModel diagnostics;

    public ForensicsViewModel(DiagnosticsViewModel diagnostics)
    {
        this.diagnostics = diagnostics;
    }

    public string ForensicsSummary
    {
        get
        {
            var runs = diagnostics.RunEvidenceRows.Count;
            var bisections = diagnostics.BisectionSessions.Count;
            return $"Captured runs: {runs}; bisection sessions: {bisections}.";
        }
    }
}
