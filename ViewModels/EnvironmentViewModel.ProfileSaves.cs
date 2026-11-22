using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Saves that belong to a profile. Off until a profile is switched on, so nobody's saves move by
    // surprise. A profile restore then moves the live saves into the profile being left and brings the
    // target's in, and the very first time it would move anything it asks first, because files leave
    // the saves folder. Files are only ever moved, never copied or deleted.
    public partial class EnvironmentViewModel
    {
        private const string SavesWarnedMarker = "first-switch-confirmed.marker";

        private bool settingProfileSavesChoice;

        private ProfileSavesStore ProfileSaves => new(ProfileSavesStore.DefaultRoot(ActiveDataRoot));

        public ObservableCollection<string> ProfileSavesNames { get; } = [];

        [ObservableProperty]
        public partial string? SelectedProfileSavesName { get; set; }

        [ObservableProperty]
        public partial bool SelectedProfileKeepsSaves { get; set; }

        [ObservableProperty]
        public partial string ProfileSavesNote { get; set; } = string.Empty;

        public void RefreshProfileSaves()
        {
            var keep = SelectedProfileSavesName;

            ProfileSavesNames.Clear();

            foreach (var profile in Profiles)
                ProfileSavesNames.Add(profile.Name);

            SelectedProfileSavesName = keep is not null && ProfileSavesNames.Contains(keep) ? keep : null;
            ShowProfileSaves();
        }

        partial void OnSelectedProfileSavesNameChanged(string? value) => ShowProfileSaves();

        partial void OnSelectedProfileKeepsSavesChanged(bool value)
        {
            if (settingProfileSavesChoice || SelectedProfileSavesName is not { } name)
                return;

            try
            {
                ProfileSaves.SetEnabled(name, value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Changing a profile's own saves failed");
            }

            ShowProfileSaves();
        }

        private void ShowProfileSaves()
        {
            settingProfileSavesChoice = true;

            try
            {
                if (SelectedProfileSavesName is not { } name)
                {
                    SelectedProfileKeepsSaves = false;
                    ProfileSavesNote = string.Empty;
                    return;
                }

                var store = ProfileSaves;
                var on = store.IsEnabled(name);

                SelectedProfileKeepsSaves = on;
                ProfileSavesNote = on
                    ? Strings.Current.Plural("Environment.ProfileSaves.Stored", store.StoredSaves(name).Count)
                    : Strings.Current["Environment.ProfileSaves.Off"];
            }
            finally
            {
                settingProfileSavesChoice = false;
            }
        }

        // Runs after a profile restore. Nothing moves unless the profile being left or the one entered
        // keeps its own saves; the first time something would, the player confirms. Declining leaves
        // every file where it is and the active profile unchanged, so the question comes again.
        private async Task SwitchProfileSavesAsync(string profileName)
        {
            try
            {
                var store = ProfileSaves;
                var from = store.ActiveName;
                var moves = !string.Equals(from, profileName, StringComparison.OrdinalIgnoreCase)
                    && ((from is not null && store.IsEnabled(from)) || store.IsEnabled(profileName));

                if (moves)
                {
                    var marker = Path.Combine(store.RootPath, SavesWarnedMarker);

                    if (!File.Exists(marker))
                    {
                        if (!await ConfirmSavesMoveAsync(profileName))
                        {
                            StatusMessage = Strings.Current.Format("Environment.ProfileSaves.Declined", profileName);
                            return;
                        }

                        Directory.CreateDirectory(store.RootPath);
                        File.WriteAllText(marker, string.Empty);
                    }
                }

                var result = store.SwitchTo(profileName, SaveReader.DefaultFolder(ActiveDataRoot));

                if (result.Stored + result.Brought + result.Kept > 0)
                {
                    StatusMessage = Strings.Current.Format(
                        "Environment.ProfileSaves.Switched", profileName, result.Stored, result.Brought, result.Kept);
                }

                ShowProfileSaves();
                RefreshProfileLaunch();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Switching the profile's saves failed");
            }
        }

        private static async Task<bool> ConfirmSavesMoveAsync(string profileName)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.ProfileSaves.Dialog.Title", profileName),
                Content = new TextBlock
                {
                    Text = Strings.Current["Environment.ProfileSaves.Dialog.Body"],
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Environment.ProfileSaves.Dialog.Primary"],
                CloseButtonText = Strings.Current["Environment.ProfileSaves.Dialog.Close"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }
    }
}
