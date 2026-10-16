using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One mod whose bundled BUTR stack copies BEM moved aside, read from the store rather than from a
    // live preflight, so it stays listed and can be put back after the mod is turned off.
    public sealed class SetAsideRowViewModel
    {
        private readonly Action<SetAsideRowViewModel> putBack;

        public SetAsideRowViewModel(SetAsideRecord record, Action<SetAsideRowViewModel> putBack)
        {
            ArgumentNullException.ThrowIfNull(record);

            Record = record;
            this.putBack = putBack;
        }

        public SetAsideRecord Record { get; }

        public string Text => Strings.Current.Format(
            "Environment.SetAsideRow.Text", Record.Bundler, Record.CreatedUtc.ToLocalTime().ToString("g"));

        public string Location => Record.BackupFolder;

        public void PutBack() => putBack(this);
    }
}
