using System.Collections.ObjectModel;
using System.Threading;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Toolkit;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The programs a BEM capability is gated on and that BEM cannot ship. Nothing here runs until a
    // button is pressed, and the command a button will run is printed next to it before the press.
    public partial class ToolkitViewModel : ObservableObject
    {
        private readonly ToolkitLedger ledger = new();

        private readonly ToolkitInstaller installer;

        public ToolkitViewModel()
        {
            installer = new ToolkitInstaller(
                WinGetRunner.RunAsync, ToolkitCatalog.Locate, WinGetLocator.Locate, ledger);

            foreach (var tool in ToolkitCatalog.Tools)
                Tools.Add(new ToolkitRow(tool, installer, ledger));

            Refresh();
        }

        public ObservableCollection<ToolkitRow> Tools { get; } = [];

        [ObservableProperty]
        public partial string InstallerNote { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string LedgerNote { get; set; } = string.Empty;

        [RelayCommand]
        private void Refresh()
        {
            InstallerNote = WinGetLocator.Locate() is { } winGet
                ? Strings.Current.Format("Toolkit.InstallerNote", winGet)
                : ToolkitInstaller.NoWinGet;

            LedgerNote = Strings.Current.Format("Toolkit.LedgerNote", ledger.LedgerPath);

            foreach (var row in Tools)
                row.Refresh();
        }
    }

    public partial class ToolkitRow : ObservableObject
    {
        private readonly ToolkitInstaller installer;

        private readonly ToolkitLedger ledger;

        public ToolkitRow(ExternalTool tool, ToolkitInstaller installer, ToolkitLedger ledger)
        {
            Tool = tool;
            this.installer = installer;
            this.ledger = ledger;
            Refresh();
        }

        public ExternalTool Tool { get; }

        public string Name => Tool.Name;

        public string Capability => Tool.Capability;

        public string HomePage => Tool.HomePage;

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstalledPath { get; set; } = string.Empty;

        // Empty unless a button is showing, so what is on screen is always what would run.
        [ObservableProperty]
        public partial string CommandText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Note { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool CanInstall { get; set; }

        [ObservableProperty]
        public partial bool CanUninstall { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public override string ToString() => Name;

        public void Refresh()
        {
            var path = ToolkitCatalog.Locate(Tool.Id);
            var installedByBem = ledger.WasInstalledByBem(Tool.Id);

            InstalledPath = path ?? string.Empty;
            StatusText = path is null
                ? Strings.Current.Format("Toolkit.StatusText.NotInstalled", Name)
                : Strings.Current.Format("Toolkit.StatusText.Installed", Name);
            CanInstall = path is null && installer.InstallCommand(Tool) is not null;
            CanUninstall = path is not null && installedByBem && installer.UninstallCommand(Tool) is not null;

            var command = CanInstall
                ? installer.InstallCommand(Tool)
                : CanUninstall
                    ? installer.UninstallCommand(Tool)
                    : null;

            CommandText = command?.DisplayText ?? string.Empty;
            Note = Describe(path, installedByBem);
        }

        // One sentence saying what is different right now, so the section reads the same whether a tool
        // is there or not: what is lost while it is absent, and who owns the copy once it is present.
        private string Describe(string? path, bool installedByBem)
        {
            if (path is null)
            {
                return Tool.CanInstall
                    ? Tool.WithoutIt
                    : $"{Tool.WithoutIt} {Tool.NotInstallableReason}";
            }

            return installedByBem
                ? Strings.Current["Toolkit.Describe.InstalledByBem"]
                : Strings.Current["Toolkit.Describe.NotInstalledByBem"];
        }

        [RelayCommand]
        private Task InstallAsync() => RunAsync(
            () => installer.InstallAsync(Tool, CancellationToken.None), "install", "Toolkit.InstallFailedNote");

        [RelayCommand]
        private Task UninstallAsync() => RunAsync(
            () => installer.UninstallAsync(Tool, CancellationToken.None), "remove", "Toolkit.RemoveFailedNote");

        [RelayCommand]
        private void OpenHomePage()
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Tool.HomePage) { UseShellExecute = true });

                if (process is null)
                    Note = Strings.Current.Format("Toolkit.OpenHomePageNote", Tool.HomePage);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Note = Strings.Current.Format("Toolkit.OpenHomePageFailedNote", Tool.HomePage, ex.Message);
                LoggingService.LogException(ex, $"Failed to open {Tool.HomePage}");
            }
        }

        private async Task RunAsync(Func<Task<ToolkitOutcome>> action, string verb, string failureKey)
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                var outcome = await action();
                Refresh();
                Note = outcome.Message;
                LoggingService.Log($"Toolkit {verb} of {Name}: {outcome.Kind}. {outcome.Message}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Refresh();
                Note = Strings.Current.Format(failureKey, Name, ex.Message);
                LoggingService.LogException(ex, $"Failed to {verb} {Name} from the Toolkit");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
