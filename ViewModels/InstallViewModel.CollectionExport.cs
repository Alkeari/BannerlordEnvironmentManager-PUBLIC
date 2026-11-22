using Windows.Storage.Pickers;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.ViewModels
{
    // A collection draft for the curator: the enabled mods in load order as Nexus pages and files, written
    // as a file BEM reads back and as a Markdown list of links beside it. Nexus has no way to publish a
    // collection from outside its site, so the draft is the shopping list to build one from; mods with no
    // Nexus page are said apart.
    public partial class InstallViewModel
    {
        [RelayCommand]
        private async Task ExportCollectionDraftAsync()
        {
            if (IsBusy || IsCollectionRunning)
                return;

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Strings.Current["Install.CollectionDraft.DefaultName"]
            };
            picker.FileTypeChoices.Add(Strings.Current["Install.CollectionDraft.FileType"], [".json"]);

            if (!TryInitializeCollectionPicker(picker))
            {
                ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.CollectionDraft.Title"],
                    Strings.Current["Install.Collection.PickerUnavailable"]);
                return;
            }

            if (await picker.PickSaveFileAsync() is not { } file)
                return;

            IsBusy = true;

            try
            {
                ResolveActiveInstanceInstallTarget();
                RefreshInstallDestinations();

                var destination = ChosenDestinations().FirstOrDefault(target => Directory.Exists(target.GameFolder));

                if (destination is null)
                {
                    ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.CollectionDraft.Title"],
                        Strings.Current["Install.ButrStack.NoDestination"]);
                    return;
                }

                var order = LoadOrderSnapshot.From(ShellViewModels.Instance.Environment.BuildEnvironment());
                var name = Path.GetFileNameWithoutExtension(file.Name);
                var markdownPath = Path.ChangeExtension(file.Path, ".md");

                var draft = await Task.Run(() =>
                {
                    var scan = ModuleScanner.ScanAll(destination.GameFolder);
                    var official = scan.Modules.Where(module => module.IsOfficial).Select(module => module.Id.Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var links = NexusModuleMatching.Link(
                        scan.Modules.Where(module => !module.IsOfficial),
                        recorded: ArchiveLinks,
                        confirmed: confirmedNexusIds);

                    var built = CollectionDraft.From(name, order, links, official.Contains);

                    File.WriteAllText(file.Path, built.ToJson());
                    File.WriteAllText(markdownPath, built.ToMarkdown());
                    return built;
                });

                var message = Strings.Current.Format("Install.CollectionDraft.Written",
                    file.Name, draft.Mods.Count, Path.GetFileName(markdownPath));

                if (draft.Unlinked.Count > 0)
                    message += " " + Strings.Current.Format("Install.CollectionDraft.Unlinked", draft.Unlinked.Count);

                ShowCollectionBar(InfoBarSeverity.Success, Strings.Current["Install.CollectionDraft.Title"], message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Writing a collection draft failed");
                ShowCollectionBar(InfoBarSeverity.Error, Strings.Current["Install.CollectionDraft.Title"],
                    Strings.Current.Format("Install.CollectionDraft.Failed", file.Name, ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
