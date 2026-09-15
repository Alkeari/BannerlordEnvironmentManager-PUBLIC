using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed class IssueRowViewModel(LoadOrderIssue issue, ModuleFolderWithoutManifest? folder = null)
    {
        public LoadOrderIssue Issue { get; } = issue;

        // Set only where the issue is about a folder on disk. Its repair is a removal rather than an
        // edit to the load order, so it cannot travel as a Fix, which is an in-memory rewrite.
        public ModuleFolderWithoutManifest? Folder { get; } = folder;

        public bool CanRemoveFolder => Folder is not null;

        public string Message => Issue.Message;

        public string ModuleId => Issue.ModuleId.Value;

        public string SeverityText => Issue.Severity switch
        {
            IssueSeverity.Error => "Error",
            IssueSeverity.Warning => "Warning",
            IssueSeverity.Information => "Information",
            _ => Issue.Severity.ToString()
        };

        public bool IsInformation => Issue.Severity == IssueSeverity.Information;

        public bool IsProminent => !IsInformation;

        public bool IsFixable => Issue.IsFixable;

        // What a screen reader announces for this row, which is otherwise the type name.
        public override string ToString() => $"{SeverityText}: {Message}";
    }
}
