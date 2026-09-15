using Windows.Storage.Pickers;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Sharing a profile with another player. Export writes one file; import reads one and saves it
    // beside the user's own profiles without touching the load order, because a file that arrived over
    // Discord has earned a look before it has earned the list.
    public partial class EnvironmentViewModel
    {
        private async Task ExportProfileAsync(ProfileRowViewModel row)
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.ProfileExport.NotLoaded"];
                return;
            }

            var gameVersion = InstalledGameVersion();
            var shared = ProfileShareFile.Describe(row.Profile, gameVersion, BuildEnvironment());

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = SuggestedProfileFileName(row.Name)
            };

            picker.FileTypeChoices.Add(
                Strings.Current["Core.Interop.LoadOrderFormat.BemProfile"], [ProfileShareFile.Extension]);

            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Environment.FilePicker.Unavailable"];
                return;
            }

            var file = await picker.PickSaveFileAsync();

            if (file is null)
                return;

            try
            {
                ProfileShareFile.Save(file.Path, shared);

                StatusMessage = Strings.Current.Plural(
                    "Environment.ProfileExport.Success", shared.Entries.Count, file.Path)
                    + (gameVersion.Length == 0
                        ? Strings.Current["Environment.ProfileExport.NoGameVersion"]
                        : Strings.Current.Format("Environment.ProfileExport.GameVersion", gameVersion));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to write a shared load order profile");
                StatusMessage = Strings.Current.Format("Environment.Export.WriteFailed", file.Name, ex.Message);
            }
        }

        [RelayCommand]
        public async Task ImportProfileAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.ProfileImport.NotLoaded"];
                return;
            }

            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };

            picker.FileTypeFilter.Add(ProfileShareFile.Extension);

            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Environment.FilePicker.Unavailable"];
                return;
            }

            var file = await picker.PickSingleFileAsync();

            if (file is null)
                return;

            var read = ProfileShareFile.Read(file.Path);

            if (read.Profile is not { } shared)
            {
                StatusMessage = Strings.Current.Format("Environment.Import.Failed", file.Name, read.Error);
                return;
            }

            string targetName;

            try
            {
                targetName = profileStore.AvailableName(shared.Name, "imported");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read the profile list before an import");
                StatusMessage = Strings.Current.Format("Environment.ProfileImport.SaveFailed", shared.Name, ex.Message);
                return;
            }

            var preview = ProfileShareImport.Preview(
                BuildEnvironment(), shared, InstalledGameVersion(), targetName);

            if (!await ProfileImportDialog.ConfirmAsync(file.Name, preview))
            {
                StatusMessage = Strings.Current.Format("Environment.Import.NotApplied", file.Name);
                return;
            }

            try
            {
                var saved = profileStore.Save(preview.TargetName, shared.ToSnapshot());

                RefreshSafety();

                var renamed = preview.Renamed
                    ? Strings.Current.Format("Environment.ProfileImport.Renamed", shared.Name, saved.Name)
                    : string.Empty;

                StatusMessage = Strings.Current.Format("Environment.ProfileImport.Saved", saved.Name) + renamed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save an imported load order profile");
                StatusMessage = Strings.Current.Format("Environment.ProfileImport.SaveFailed", targetName, ex.Message);
            }
        }

        private string InstalledGameVersion() =>
            GameVersionReader.Read(GameInstallPath) is { IsEmpty: false } version
                ? version.ToString()
                : string.Empty;

        // The picker rejects a suggested name holding a path character, and a profile name is free
        // text the user typed.
        private static string SuggestedProfileFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var stem = new string([.. name.Where(c => !invalid.Contains(c))]).Trim();

            return stem.Length == 0 ? "Bannerlord Load Order Profile" : stem;
        }
    }

    // What a profile from somewhere else would bring in, shown before it is saved. It says which of
    // its modules this install actually has, which it does not, which are here at a different version
    // from the one it was built with, and what game version it was built against, and it saves nothing
    // until this returns true.
    internal static class ProfileImportDialog
    {
        private const int MaxLines = 200;

        public static async Task<bool> ConfirmAsync(string fileName, ProfileImportPreview preview)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.ImportDialog.Title", fileName),
                Content = new ScrollViewer { MaxHeight = 520, Content = Report(preview) },
                PrimaryButtonText = Strings.Current["Environment.ProfileImportDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.ImportDialog.CloseButton.Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private static StackPanel Report(ProfileImportPreview preview)
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = DialogText.ContentWidth };

            panel.Children.Add(Paragraph(Strings.Current.Format(
                "Environment.ProfileImportDialog.Made",
                preview.Profile.Name,
                preview.Profile.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))));

            panel.Children.Add(Paragraph(preview.VersionNote));

            // Above the summary rather than below the lists: whether the mods here are the ones the
            // order was built with decides whether it works at all, and the sender's game version on
            // its own reads as a check that has been done when it has not.
            if (preview.ModuleVersionNote.Length > 0)
                panel.Children.Add(Paragraph(preview.ModuleVersionNote));

            panel.Children.Add(Paragraph(preview.Summary));

            if (preview.NothingInstalled)
                panel.Children.Add(Paragraph(Strings.Current["Environment.ProfileImportDialog.NothingInstalled"]));

            if (preview.Renamed)
            {
                panel.Children.Add(Paragraph(Strings.Current.Format(
                    "Environment.ProfileImportDialog.Renamed", preview.Profile.Name, preview.TargetName)));
            }

            panel.Children.Add(Paragraph(Strings.Current["Environment.ProfileImportDialog.Notice"]));

            Section(panel,
                Strings.Current.Format("Environment.ProfileImportDialog.Present.Heading", preview.Present.Count),
                Strings.Current["Environment.ProfileImportDialog.Present.Explanation"],
                preview.Present.Select(m => $"{m.DisplayName} ({m.Id})"));

            Section(panel,
                Strings.Current.Format("Environment.ProfileImportDialog.Deltas.Heading", preview.Deltas.Count),
                Strings.Current["Environment.ProfileImportDialog.Deltas.Explanation"],
                preview.Deltas.Select(d => Strings.Current.Format(
                    "Environment.ProfileImportDialog.Deltas.Row",
                    d.DisplayName, d.Id, d.FileVersion, d.InstalledVersion)));

            Section(panel,
                Strings.Current.Format(
                    "Environment.ProfileImportDialog.UnknownVersions.Heading", preview.UnknownVersions.Count),
                Strings.Current["Environment.ProfileImportDialog.UnknownVersions.Explanation"],
                preview.UnknownVersions.Select(UnknownVersionRow));

            Section(panel,
                Strings.Current.Format("Environment.ProfileImportDialog.Missing.Heading", preview.Missing.Count),
                Strings.Current["Environment.ProfileImportDialog.Missing.Explanation"],
                preview.Missing.Select(m => string.IsNullOrEmpty(m.Name)
                    ? $"{m.Id} {m.Version}".TrimEnd()
                    : $"{m.Name} ({m.Id}) {m.Version}".TrimEnd()));

            return panel;
        }

        // Which side said nothing is the whole content of this row: "not known" without it reads as a
        // fault in the profile when it is just as often the module here declaring no version.
        private static string UnknownVersionRow(VersionDelta delta)
        {
            var stated = delta.FileVersion.Trim();
            var here = delta.InstalledVersion.Trim();

            if (stated.Length == 0 && here.Length == 0)
            {
                return Strings.Current.Format(
                    "Environment.ProfileImportDialog.UnknownVersions.Row.Neither", delta.DisplayName, delta.Id);
            }

            return stated.Length == 0
                ? Strings.Current.Format(
                    "Environment.ProfileImportDialog.UnknownVersions.Row.NotInProfile",
                    delta.DisplayName, delta.Id, here)
                : Strings.Current.Format(
                    "Environment.ProfileImportDialog.UnknownVersions.Row.NotInstalledHere",
                    delta.DisplayName, delta.Id, stated);
        }

        private static void Section(StackPanel panel, string heading, string explanation, IEnumerable<string> lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = heading,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            panel.Children.Add(Paragraph(explanation, 0.7));

            var all = lines.ToList();
            var listed = all.Take(MaxLines).ToList();

            panel.Children.Add(Paragraph(listed.Count == 0
                ? Strings.Current["Environment.ImportDialog.Section.None"]
                : string.Join(Environment.NewLine, listed)));

            if (all.Count > listed.Count)
            {
                panel.Children.Add(Paragraph(
                    Strings.Current.Plural("Environment.ImportDialog.Section.More", all.Count - listed.Count), 0.7));
            }
        }

        private static TextBlock Paragraph(string text, double opacity = 1) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Opacity = opacity
        };
    }
}
