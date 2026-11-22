using Windows.Storage.Pickers;
using WinRT.Interop;
using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Load order interchange. The two common formats are what the buttons say; the rarer ones sit in
    // the picker's own file-type dropdown rather than taking toolbar space of their own.
    public partial class EnvironmentViewModel
    {
        [RelayCommand]
        public async Task ImportLoadOrderAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Import.NotLoaded"];
                return;
            }

            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };

            foreach (var format in LoadOrderInterop.Importable)
                picker.FileTypeFilter.Add(LoadOrderInterop.Extension(format));

            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Environment.FilePicker.Unavailable"];
                return;
            }

            var file = await picker.PickSingleFileAsync();

            if (file is null)
                return;

            LoadOrderFileRead read;

            try
            {
                read = LoadOrderInterop.Read(file.Path, Mo2FolderModules());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read a load order file");
                StatusMessage = Strings.Current.Format("Environment.Import.ReadFailed", file.Name, ex.Message);
                return;
            }

            if (read.Failed)
            {
                StatusMessage = Strings.Current.Format("Environment.Import.Failed", file.Name, read.Error);
                return;
            }

            await ApplyImportAsync(file.Name, read);
        }

        // Vortex keeps its load order as JSON in the game folder (or under its own folder for the game);
        // the newest file that reads as one is the profile in use.
        [RelayCommand]
        public async Task ImportVortexLoadOrderAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Import.NotLoaded"];
                return;
            }

            var found = VortexLoadOrderFile.FindFiles(GameInstallPath)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();

            if (found is null)
            {
                StatusMessage = Strings.Current["Environment.Import.Vortex.NoneFound"];
                return;
            }

            await ImportFromPathAsync(found, Mo2FolderModules());
        }

        // The selected profile's modlist.txt of the Mod Organizer 2 instance that manages this game.
        [RelayCommand]
        public async Task ImportMo2LoadOrderAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Import.NotLoaded"];
                return;
            }

            var instance = Mo2Instances().FirstOrDefault(i => i.ModLists.Count > 0);

            if (instance is null)
            {
                StatusMessage = Strings.Current["Environment.Import.Mo2.NoneFound"];
                return;
            }

            await ImportFromPathAsync(instance.ModLists[0], Mo2FolderModules());
        }

        private async Task ImportFromPathAsync(string path, IReadOnlyDictionary<string, IReadOnlyList<string>> mo2Map)
        {
            var name = Path.GetFileName(path);
            LoadOrderFileRead read;

            try
            {
                read = LoadOrderInterop.Read(path, mo2Map);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read a load order file");
                StatusMessage = Strings.Current.Format("Environment.Import.ReadFailed", name, ex.Message);
                return;
            }

            if (read.Failed)
            {
                StatusMessage = Strings.Current.Format("Environment.Import.Failed", name, read.Error);
                return;
            }

            await ApplyImportAsync(name, read);
        }

        private IReadOnlyList<ModManagerDetector.Mo2Instance> Mo2Instances() =>
            string.IsNullOrWhiteSpace(GameInstallPath)
                ? []
                : ModManagerDetector.Mo2Instances(GameInstallPath, ModManagerDetector.DefaultMo2Roots());

        // Every MO2 mod folder of every instance for this game, mapped to the module ids it carries.
        private IReadOnlyDictionary<string, IReadOnlyList<string>> Mo2FolderModules()
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in Mo2Instances())
                foreach (var (folder, modules) in instance.FolderModules)
                    map.TryAdd(folder, modules);

            return map;
        }

        [RelayCommand]
        public async Task ExportMo2LoadOrderAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Export.NotLoaded"];
                return;
            }

            var instances = Mo2Instances();
            var map = Mo2FolderModules();

            if (map.Count == 0)
            {
                StatusMessage = Strings.Current["Environment.Export.Mo2.NoneFound"];
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(Mo2ModListFile.FileName)
            };

            picker.FileTypeChoices.Add(LoadOrderInterop.DisplayName(LoadOrderFileFormat.Mo2ModList), [".txt"]);

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
                // The target's own lines keep their places for anything that is not a Bannerlord module
                // (texture packs, separators); with no file there yet, the selected profile is the template.
                var template = File.Exists(file.Path)
                    ? file.Path
                    : instances.SelectMany(i => i.ModLists).FirstOrDefault();

                var existing = template is null ? null : LoadOrderInterop.Decode(await File.ReadAllBytesAsync(template));
                var written = Mo2ModListFile.Write(BuildEnvironment(), map, existing);

                await File.WriteAllTextAsync(file.Path, written.Content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                StatusMessage = written.Unrepresentable.Count == 0
                    ? Strings.Current.Format("Environment.Export.Mo2.Done", file.Name)
                    : Strings.Current.Plural(
                        "Environment.Export.Mo2.DoneWithout", written.Unrepresentable.Count, file.Name,
                        string.Join(", ", written.Unrepresentable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to write a modlist.txt");
                StatusMessage = Strings.Current.Format("Environment.Export.WriteFailed", file.Name, ex.Message);
            }
        }

        private async Task ApplyImportAsync(string fileName, LoadOrderFileRead read)
        {
            var preview = LoadOrderImport.Preview(BuildEnvironment(), read);

            if (!await ImportDialog.ConfirmAsync(fileName, preview))
            {
                StatusMessage = preview.CanApply
                    ? Strings.Current.Format("Environment.Import.NotApplied", fileName)
                    : Strings.Current.Format("Environment.Import.Discarded", preview.Summary);
                return;
            }

            var before = BuildEnvironment();

            RecordUndo(before, Strings.Current.Format("Environment.Import.UndoReason", fileName));
            Apply(preview.Result);
            PersistOrder();

            if (preview.Dividers.Count > 0)
            {
                // Re-importing the same file is normal (resetting to a known-good state, re-running
                // after a tweak), and every section it recognizes should replace whatever section this
                // import previously left anchored to the same module rather than stacking a duplicate
                // on top of it. A null AnchorId is the end-of-list sentinel, which never conflicts with
                // another end-of-list divider, so it is left alone: only anchored sections are replaced.
                var touchedAnchors = preview.Dividers
                    .Where(d => d.AnchorId is not null)
                    .Select(d => d.AnchorId!.Value)
                    .ToHashSet();

                var kept = Dividers
                    .Where(d => d.AnchorId is not { } anchor || !touchedAnchors.Contains(anchor))
                    .ToList();

                Dividers.Clear();

                foreach (var divider in kept)
                    Dividers.Add(divider);

                foreach (var divider in preview.Dividers)
                    Dividers.Add(divider);

                SaveDividersPublic();

                // Apply above already rebuilt the list, before any of this ran, so without a second
                // pass the sections the file brought in would not appear until something unrelated
                // refreshed the tab.
                RefreshVisibleModules();
            }

            StatusMessage = Strings.Current.Format("Environment.Import.Success", fileName, preview.Summary);
        }

        [RelayCommand]
        public async Task ExportLoadOrderAsync()
        {
            if (!IsLoaded)
            {
                StatusMessage = Strings.Current["Environment.Export.NotLoaded"];
                return;
            }

            var plan = LoadOrderExporter.Describe(BuildEnvironment(), [.. Dividers]);

            if (plan.Entries.Count == 0)
            {
                StatusMessage = Strings.Current["Environment.Export.NothingEnabled"];
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "Bannerlord Load Order"
            };

            foreach (var format in LoadOrderInterop.Exportable)
                picker.FileTypeChoices.Add(LoadOrderInterop.DisplayName(format), [LoadOrderInterop.Extension(format)]);

            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Environment.FilePicker.Unavailable"];
                return;
            }

            var file = await picker.PickSaveFileAsync();

            if (file is null)
                return;

            var chosen = LoadOrderInterop.FromExtension(file.Path) ?? LoadOrderFileFormat.BmList;

            try
            {
                var written = LoadOrderInterop.Save(file.Path, chosen, plan.Entries);

                StatusMessage = DescribeExport(file.Name, plan, written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to write a load order file");
                StatusMessage = Strings.Current.Format("Environment.Export.WriteFailed", file.Name, ex.Message);
            }
        }

        private static string DescribeExport(string fileName, LoadOrderExportPlan plan, LoadOrderFileWrite written)
        {
            var count = plan.Entries.Count - written.Unrepresentable.Count;

            var message = Strings.Current.Plural("Environment.Export.Summary", count, fileName);

            if (written.Unrepresentable.Count > 0)
            {
                message += Strings.Current.Plural(
                    "Environment.Export.Unrepresentable",
                    written.Unrepresentable.Count,
                    string.Join(", ", written.Unrepresentable));
            }

            if (plan.WithoutVersion.Count > 0)
            {
                message += Strings.Current.Plural(
                    "Environment.Export.WithoutVersion",
                    plan.WithoutVersion.Count,
                    BmListFile.UnknownVersion,
                    string.Join(", ", plan.WithoutVersion));
            }

            return message;
        }

        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;

            if (window is null)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            switch (picker)
            {
                case FileOpenPicker openPicker:
                    InitializeWithWindow.Initialize(openPicker, hwnd);
                    return true;

                case FileSavePicker savePicker:
                    InitializeWithWindow.Initialize(savePicker, hwnd);
                    return true;

                default:
                    return false;
            }
        }
    }

    // The reconciliation report, not a success message. What the user is being asked to accept is the
    // whole of it: what matched, what they are missing, what the file says nothing about and what would
    // happen to it, and which versions differ. Nothing is applied until this returns true.
    internal static class ImportDialog
    {
        private const int MaxLines = 200;

        public static async Task<bool> ConfirmAsync(string fileName, LoadOrderImportPreview preview)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Environment.ImportDialog.Title", fileName),
                Content = new ScrollViewer { MaxHeight = 520, Content = Report(preview) },
                PrimaryButtonText = preview.CanApply ? Strings.Current["Environment.ImportDialog.PrimaryButton"] : null,
                CloseButtonText = preview.CanApply
                    ? Strings.Current["Environment.ImportDialog.CloseButton.Cancel"]
                    : Strings.Current["Environment.ImportDialog.CloseButton"],
                DefaultButton = preview.CanApply ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private static StackPanel Report(LoadOrderImportPreview preview)
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = DialogText.ContentWidth };

            panel.Children.Add(Paragraph(preview.Summary));

            if (!preview.CanApply)
                return panel;

            panel.Children.Add(Paragraph(Strings.Current["Environment.ImportDialog.WriteNotice"]));

            Section(panel, Strings.Current.Format("Environment.ImportDialog.Matched.Heading", preview.Matched.Count),
                Strings.Current["Environment.ImportDialog.Matched.Explanation"],
                preview.Matched.Select(m => m.KeptAsIs
                    ? Strings.Current.Format("Environment.ImportDialog.Matched.KeptState", m.DisplayName, m.Id)
                    : $"{m.DisplayName} ({m.Id})"));

            Section(panel, Strings.Current.Format("Environment.ImportDialog.Missing.Heading", preview.Missing.Count),
                Strings.Current["Environment.ImportDialog.Missing.Explanation"],
                []);

            // Each missing module links to where it can be found: the page the file names, or a Nexus
            // search for its name. The None line Section wrote is replaced by the links.
            if (preview.Missing.Count > 0)
            {
                panel.Children.RemoveAt(panel.Children.Count - 1);

                foreach (var missing in preview.Missing.Take(MaxLines))
                {
                    panel.Children.Add(new HyperlinkButton
                    {
                        Content = $"{missing.Name ?? missing.Id} {missing.Version}".TrimEnd(),
                        NavigateUri = missing.FindUri,
                        Padding = new Thickness(0)
                    });
                }

                if (preview.Missing.Count > MaxLines)
                    panel.Children.Add(Paragraph(
                        Strings.Current.Plural("Environment.ImportDialog.Section.More", preview.Missing.Count - MaxLines), 0.7));
            }

            Section(panel, Strings.Current.Format("Environment.ImportDialog.Absent.Heading", preview.Absent.Count),
                Strings.Current["Environment.ImportDialog.Absent.Explanation"],
                preview.Absent.Select(a => a.KeptAsIs
                    ? Strings.Current.Format("Environment.ImportDialog.Absent.KeptState", a.DisplayName, a.Id)
                    : a.WasEnabled
                        ? Strings.Current.Format("Environment.ImportDialog.Absent.TurnedOff", a.DisplayName, a.Id)
                        : Strings.Current.Format("Environment.ImportDialog.Absent.AlreadyOff", a.DisplayName, a.Id)));

            Section(panel, Strings.Current.Format("Environment.ImportDialog.Deltas.Heading", preview.Deltas.Count),
                Strings.Current["Environment.ImportDialog.Deltas.Explanation"],
                preview.Deltas.Select(d => Strings.Current.Format(
                    "Environment.ImportDialog.Deltas.Row", d.DisplayName, d.Id, d.FileVersion, d.InstalledVersion)));

            // An import outranks every ordering rule BEM has, because a heavily tweaked load order can
            // work precisely because it does not comply. The file's order is kept exactly as it came
            // and this section is what the user is taking on by keeping it.
            if (preview.OrderIssues.Count > 0)
            {
                Section(panel, Strings.Current.Format("Environment.ImportDialog.OrderIssues.Heading", preview.OrderIssues.Count),
                    Strings.Current.Plural("Environment.ImportDialog.OrderIssues.Explanation.Stated", preview.OrderErrors)
                        + " " + Strings.Current.Plural(
                            "Environment.ImportDialog.OrderIssues.Explanation.Implied", preview.OrderWarnings),
                    preview.OrderIssues.Select(i =>
                    {
                        var label = IssueSeverities.IsStated(i.Kind)
                            ? Strings.Current["Environment.ImportDialog.OrderIssue.Stated"]
                            : Strings.Current["Environment.ImportDialog.OrderIssue.Implied"];

                        return $"{label}: {i.Message}";
                    }));
            }

            if (preview.DuplicateIds.Count > 0)
            {
                Section(panel, Strings.Current.Format("Environment.ImportDialog.DuplicateIds.Heading", preview.DuplicateIds.Count),
                    Strings.Current["Environment.ImportDialog.DuplicateIds.Explanation"],
                    preview.DuplicateIds);
            }

            if (preview.UnreadableLines > 0)
            {
                panel.Children.Add(Paragraph(
                    Strings.Current.Plural("Environment.ImportDialog.UnreadableLines", preview.UnreadableLines)));
            }

            if (preview.Dividers.Count > 0)
            {
                Section(panel, Strings.Current.Format("Environment.ImportDialog.Dividers.Heading", preview.Dividers.Count),
                    Strings.Current["Environment.ImportDialog.Dividers.Explanation"],
                    preview.Dividers.Select(d => d.Label));
            }

            return panel;
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

            var listed = lines.Take(MaxLines).ToList();
            var total = lines.Count();

            panel.Children.Add(Paragraph(listed.Count == 0
                ? Strings.Current["Environment.ImportDialog.Section.None"]
                : string.Join(Environment.NewLine, listed)));

            if (total > listed.Count)
                panel.Children.Add(Paragraph(
                    Strings.Current.Plural("Environment.ImportDialog.Section.More", total - listed.Count), 0.7));
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
