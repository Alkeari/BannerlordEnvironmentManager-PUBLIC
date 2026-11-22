using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Before archives are extracted over modules already installed, the files they would replace are
    // checked for an owner. A file Vortex deployed unchanged, or one inside a Mod Organizer 2 mod
    // folder, is changed behind that manager's back, so the player is told first and decides.
    public partial class InstallViewModel
    {
        // False when the player stops the install.
        private async Task<bool> ConfirmOwnedFilesAsync(IReadOnlyList<ModArchiveEntry> targets)
        {
            var ids = targets
                .SelectMany(archive => archive.Modules.Select(module => module.ModuleId))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var gameFolders = ChosenDestinations()
                .Select(destination => destination.GameFolder)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0 || gameFolders.Count == 0)
                return true;

            var note = await Task.Run(() =>
            {
                var replaced = gameFolders
                    .SelectMany(folder => ModuleScanner.ScanAll(folder).Modules)
                    .Where(module => ids.Contains(module.Id.Value))
                    .Select(module => module.FolderPath);

                return DeploymentOwnershipNote.Describe(
                    DeploymentOwnershipNote.Find(DeploymentOwnershipNote.FilesUnder(replaced)));
            });

            if (note is null)
                return true;

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Install.Ownership.Title"],
                Content = new TextBlock
                {
                    Text = note + "\n\n" + Strings.Current["Install.Ownership.Question"],
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Install.Ownership.Primary"],
                CloseButtonText = Strings.Current["Install.Ownership.Close"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }
    }
}
