using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed class IssueRowViewModel(LoadOrderIssue issue, ModuleFolderWithoutManifest? folder = null, Action<IssueRowViewModel>? acceptRisk = null, bool accepted = false)
    {
        public LoadOrderIssue Issue { get; } = issue;

        // Set only where the issue is about a folder on disk. Its repair is a removal rather than an
        // edit to the load order, so it cannot travel as a Fix, which is an in-memory rewrite.
        public ModuleFolderWithoutManifest? Folder { get; } = folder;

        public bool CanRemoveFolder => Folder is not null;

        public string Message => Issue.Message;

        public string ModuleId => Issue.ModuleId.Value;

        public string SeverityText => IssueKinds.Describe(Issue.Severity);

        public bool IsInformation => Issue.Severity == IssueSeverity.Information;

        public bool IsProminent => !IsInformation;

        public bool IsFixable => Issue.IsFixable;

        // Every error and warning, never a note. The key is the one the launch check uses for the same
        // finding, so accepting it here silences it there too.
        public bool CanAcceptRisk => acceptRisk is not null && !IsInformation && !Accepted;

        // A row that can show as accepted; the Play panel lists only open issues, so its rows pass false.
        public bool Accepted { get; } = accepted && issue.Severity != IssueSeverity.Information;

        public void AcceptRisk() => acceptRisk?.Invoke(this);

        // What a screen reader announces for this row, which is otherwise the type name.
        public override string ToString() => $"{SeverityText}: {Message}";
    }
}
