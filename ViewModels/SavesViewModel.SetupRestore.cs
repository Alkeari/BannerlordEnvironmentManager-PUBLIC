using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Interop;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Services;
using Microsoft.UI.Xaml.Media;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One mod's line in the restore's result: what became of it and, where the player can finish the
    // job by hand, the Nexus page that does it.
    public sealed class SaveSetupResultRowViewModel(SaveSetupModResult result)
    {
        public string Heading { get; } = Strings.Current.Format(
            "Saves.Setup.Result.Heading", result.DisplayName, StatusText(result.Status));

        public string Message { get; } = result.Message;

        public string Link { get; } = result.Link ?? string.Empty;

        public bool HasLink => Link.Length > 0;

        public Uri? LinkUri => HasLink ? new Uri(Link) : null;

        public Visibility LinkVisibility => HasLink ? Visibility.Visible : Visibility.Collapsed;

        public override string ToString() => Heading;

        private static string StatusText(SaveSetupModStatus status) => status switch
        {
            SaveSetupModStatus.Restored => Strings.Current["Saves.Setup.Status.Restored"],
            SaveSetupModStatus.CameWithGame => Strings.Current["Saves.Setup.Status.CameWithGame"],
            SaveSetupModStatus.KeptInstalledVersion => Strings.Current["Saves.Setup.Status.KeptInstalled"],
            SaveSetupModStatus.NeedsManualDownload => Strings.Current["Saves.Setup.Status.Manual"],
            SaveSetupModStatus.Missing => Strings.Current["Saves.Setup.Status.Missing"],
            SaveSetupModStatus.Canceled => Strings.Current["Saves.Setup.Status.Stopped"],
            _ => Strings.Current["Saves.Setup.Status.Failed"]
        };
    }

    // Restore This Save's Setup: builds a copy of the game at the version the selected save was written
    // by, puts each recorded mod into it at its recorded version where that can be had, copies the save
    // in, then makes the copy the version in use with the save's own load order. The save is only ever
    // read, the resting instance is never written, and the copy is a separate instance named after the
    // save that the player can remove from Versions like any other.
    public partial class SavesViewModel
    {
        private CancellationTokenSource? setupCancellation;

        public ObservableCollection<SaveSetupResultRowViewModel> SetupResults { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RestoreSetupCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseSetupCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopSetupCommand))]
        public partial bool IsSetupRunning { get; set; }

        [ObservableProperty]
        public partial bool IsSetupPanelVisible { get; set; }

        [ObservableProperty]
        public partial string SetupHeading { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SetupStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial double SetupProgress { get; set; }

        [ObservableProperty]
        public partial bool IsSetupProgressIndeterminate { get; set; }

        private bool CanRestoreSetup() => SelectedSave is { Failed: false } && !IsSetupRunning;

        private bool CanStopSetup() => IsSetupRunning;

        private bool CanCloseSetup() => !IsSetupRunning;

        [RelayCommand(CanExecute = nameof(CanStopSetup))]
        private void StopSetup()
        {
            SetupStatus = Strings.Current["Saves.Setup.Stopping"];
            setupCancellation?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanCloseSetup))]
        private void CloseSetup()
        {
            IsSetupPanelVisible = false;
            SetupResults.Clear();
        }

        [RelayCommand(CanExecute = nameof(CanRestoreSetup))]
        private async Task RestoreSetupAsync()
        {
            if (SelectedSave?.Save is not { Failed: false } save)
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                StatusMessage = Strings.Current["Saves.Restore.NotLoaded"];
                return;
            }

            if (Core.Launcher.RunningGame.AnyGameProcessRunning())
            {
                StatusMessage = Strings.Current["Saves.Setup.GameRunning"];
                return;
            }

            IsSetupRunning = true;
            StatusMessage = Strings.Current["Saves.Setup.Planning"];

            try
            {
                var prepared = await PlanSetupAsync(save, environment);

                if (prepared is null)
                    return;

                var (plan, chosen, manager, nexus) = prepared.Value;
                var target = chosen;

                if (plan.NothingToDo)
                {
                    StatusMessage = Strings.Current.Format("Saves.Setup.AlreadyMatches", save.Name);
                    return;
                }

                // Nothing a copy could bring closer: the game version matches and no recorded version
                // is on Nexus. The preview still opens, without its button, so the player reads which
                // mods are out of reach and why rather than a bare refusal.
                if (!plan.CanImprove && target.CanRestore)
                {
                    target = target with
                    {
                        Kind = SaveSetupTargetKind.Unavailable,
                        Reason = Strings.Current.Format("Saves.Setup.NothingReachable", save.Name)
                    };
                }

                var copyBytes = target.Kind is SaveSetupTargetKind.CopyInstance && target.Instance is { } source
                    ? await Task.Run(() => TryMeasure(source.Record.GameFolder))
                    : null;

                if (!await SaveSetupDialog.ConfirmAsync(save, plan, target, nexus, copyBytes, manager.RestingInstanceId))
                {
                    StatusMessage = Strings.Current.Format("Saves.Setup.Declined", save.Name);
                    return;
                }

                await RunSetupAsync(save, plan, target, manager, nexus);
            }
            finally
            {
                IsSetupRunning = false;
            }
        }

        private static long? TryMeasure(string gameFolder)
        {
            try
            {
                return SaveSetupFiles.MeasureGame(gameFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, $"Could not measure '{gameFolder}' for a save setup restore");
                return null;
            }
        }

        // Everything the preview states, read before it opens: the plan against the version in use, the
        // file lists of the mods whose recorded version is not here (fresh from Nexus where BEM can ask,
        // otherwise the copies already on this machine), where the copy would come from, and whether
        // this account can be served files unattended.
        private async Task<(SaveRollbackPlan Plan, SaveSetupTarget Target, InstanceManager Manager, SaveSetupNexusAccount Nexus)?>
            PlanSetupAsync(SaveFile save, EnvironmentViewModel environment)
        {
            var current = environment.BuildEnvironment();
            var links = environment.NexusLinksForSaves()
                .Where(link => link.NexusModId is not null)
                .GroupBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var listings = new Dictionary<int, NexusModFileListing>(NexusVersionStore.Default.Load().FilesByModId());
            var nexus = await SaveSetupNexusAccount.ReadAsync();

            if (nexus.Key is { } key)
            {
                var client = new NexusClient(new NexusHttpTransport());

                foreach (var modId in SaveRollback.NexusIdsToAsk(save, current, links))
                {
                    var files = await client.GetModFilesAsync(key, modId, CancellationToken.None);

                    if (files is { IsOk: true, Value: { } listing })
                        listings[modId] = listing;
                }
            }

            var installedVersion = GameVersionReader.Read(environment.GameInstallPath) is { IsEmpty: false } version
                ? version.ToString()
                : null;

            var plan = SaveRollback.Plan(save, current, installedVersion, links, listings);

            if (!plan.CanPlan)
            {
                StatusMessage = Strings.Current.Format("Saves.Restore.CouldNotRead", save.Name);
                return null;
            }

            var manager = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore())
            {
                Log = message => LoggingService.Log(message)
            };

            var switcher = ShellViewModels.Instance.VersionSwitcher;

            // Only asked when nothing here runs the version, because reading Steam's branch list is the
            // slow part and a copy of an installed instance does not need it.
            var installed = manager.List();
            var runsHere = installed.Any(i => ModuleVersion.Parse(i.Record.RecordedGameVersion)
                .CompareTo(ModuleVersion.Parse(plan.SaveGameVersion)) == 0);

            if (!runsHere && !switcher.HasSteamBranchList)
                await switcher.RefreshIfIdleAsync();

            Func<ModuleVersion, GameDlcSet, bool>? published = switcher.HasSteamBranchList
                ? (v, dlc) => switcher.OfferFor(v, dlc) is not null
                : null;

            var target = SaveSetupTargets.Choose(plan, save.Name, installed, manager.RestingInstanceId, published);

            return (plan, target, manager, nexus);
        }

        private async Task RunSetupAsync(
            SaveFile save, SaveRollbackPlan plan, SaveSetupTarget target, InstanceManager manager, SaveSetupNexusAccount nexus)
        {
            using var cancellation = new CancellationTokenSource();
            setupCancellation = cancellation;

            SetupResults.Clear();
            SetupHeading = Strings.Current.Format("Saves.Setup.Heading", save.Name, target.CopyName);
            SetupStatus = string.Empty;
            SetupProgress = 0;
            IsSetupProgressIndeterminate = true;
            IsSetupPanelVisible = true;

            var progress = new Progress<SaveSetupProgress>(report =>
            {
                SetupStatus = report.Message;
                IsSetupProgressIndeterminate = report.Fraction is null && report.Total <= 1;
                SetupProgress = report.Fraction is { } fraction
                    ? fraction * 100
                    : report.Total > 0 ? 100.0 * report.Done / report.Total : 0;
            });

            var actions = new SaveSetupActions(manager, manager.RestingInstanceId, nexus.CanFetch, nexus.Refusal);
            SaveSetupResult result;

            try
            {
                result = await SaveSetupRestore.RunAsync(save, plan, target, actions, progress, cancellation.Token);
            }
            finally
            {
                setupCancellation = null;
            }

            foreach (var mod in result.Mods.Where(m => m.Status is not SaveSetupModStatus.CameWithGame))
                SetupResults.Add(new SaveSetupResultRowViewModel(mod));

            IsSetupProgressIndeterminate = false;
            SetupProgress = 100;

            if (result.Instance is not { } copy || result.Canceled)
            {
                SetupStatus = result.Canceled
                    ? Strings.Current.Format("Saves.Setup.Result.Stopped", result.InstanceMessage)
                    : result.InstanceMessage;
                StatusMessage = SetupStatus;
                return;
            }

            var switched = await SwitchToCopyAsync(copy, save, manager);

            var summary = Strings.Current.Format(
                "Saves.Setup.Result.Summary",
                result.Mods.Count(m => m.Status is SaveSetupModStatus.Restored),
                result.Mods.Count(m => m.Status is not (SaveSetupModStatus.Restored or SaveSetupModStatus.CameWithGame)),
                InstanceLabel.NameOf(copy.Record));

            SetupStatus = $"{result.InstanceMessage} {summary} {switched}".Trim();
            StatusMessage = summary;
        }

        // The copy becomes the version in use, the save's own order is applied to it through the same
        // reconciliation the Restore Load Order button uses (so Undo covers it), and the list shows the
        // copy's saves with the copied save selected, so Launch Into This Save plays it straight away.
        private async Task<string> SwitchToCopyAsync(InstalledInstance copy, SaveFile save, InstanceManager manager)
        {
            var switcher = ShellViewModels.Instance.VersionSwitcher;
            var environment = ShellViewModels.Instance.Environment;

            await switcher.RefreshAsync();
            await switcher.SwitchToInstanceAsync(copy.Record.Id);

            if (!string.Equals(manager.Active()?.Record.Id, copy.Record.Id, StringComparison.Ordinal))
                return Strings.Current.Format("Saves.Setup.Result.NotSwitched", switcher.StatusMessage);

            await environment.Refresh();

            var read = SaveComparison.ToLoadOrder(save);
            var orderNote = string.Empty;

            if (!read.Failed)
            {
                var preview = LoadOrderImport.Preview(environment.BuildEnvironment(), read, "save");

                if (preview.CanApply && preview.HasChanges)
                    environment.RestoreOrderFrom(preview, Strings.Current.Format("Saves.Restore.UndoReason", save.Name));

                orderNote = preview.CanApply ? string.Empty : preview.Summary;
            }

            Refresh();

            SelectedSave = Saves.FirstOrDefault(row =>
                               string.Equals(row.Save.FileName, save.FileName, StringComparison.OrdinalIgnoreCase))
                           ?? SelectedSave;

            return $"{Strings.Current["Saves.Setup.Result.Switched"]} {orderNote}".Trim();
        }
    }

    // What this Nexus account can do for a restore, read once before the preview so the preview can
    // say it: a Premium account (or a file's author) is served files unattended, anyone else downloads
    // them by hand from the page each result links.
    internal sealed record SaveSetupNexusAccount(NexusApiKey? Key, bool CanFetch, string Refusal)
    {
        public static async Task<SaveSetupNexusAccount> ReadAsync()
        {
            var key = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();

            if (key is null)
                return new SaveSetupNexusAccount(null, false, Strings.Current["Saves.Setup.Account.NoKey"]);

            var account = await new NexusClient(new NexusHttpTransport()).ValidateAsync(key, CancellationToken.None);

            return account is { IsOk: true, Value: { } user }
                ? new SaveSetupNexusAccount(key, user.IsPremium,
                    user.IsPremium ? string.Empty : Strings.Current["Saves.Setup.Account.Free"])
                : new SaveSetupNexusAccount(key, false, account.Message);
        }
    }

    public partial class EnvironmentViewModel
    {
        // The same links the Nexus checks read, for the Saves page's restore plan.
        public IReadOnlyList<NexusModuleLink> NexusLinksForSaves() => NexusLinks();
    }

    // The restore's preview: where the copy comes from and how big it is, what is fetched from Nexus,
    // what is copied from this install, and what cannot come back and why. One primary button.
    internal static class SaveSetupDialog
    {
        private const int MaxLines = 200;

        public static async Task<bool> ConfirmAsync(
            SaveFile save, SaveRollbackPlan plan, SaveSetupTarget target, SaveSetupNexusAccount nexus, long? copyBytes,
            string? restingInstanceId)
        {
            // The right padding keeps the last characters of a line clear of the vertical scroll bar,
            // which draws over the content rather than beside it.
            var panel = new StackPanel
            {
                Spacing = 10,
                MinWidth = DialogText.ContentWidth,
                Padding = new Thickness(0, 0, 16, 0)
            };

            panel.Children.Add(Paragraph(GameSentence(target, copyBytes, restingInstanceId)));

            {
                var fetch = plan.ToFetch;
                var copied = plan.Steps.Where(s => s.Action is SaveRollbackAction.AlreadyMatches).ToList();
                var lost = plan.CannotRestore;

                // What cannot come back leads, because it decides whether the restore is worth doing;
                // the long list of mods already at their version comes last.
                Section(panel, Strings.Current.Format("Saves.Setup.Preview.LostHeading", lost.Count),
                    Strings.Current["Saves.Setup.Preview.LostExplanation"],
                    lost.Select(LostRow));

                Section(panel, Strings.Current.Format("Saves.Setup.Preview.FetchHeading", fetch.Count),
                    fetch.Count > 0 && !nexus.CanFetch ? nexus.Refusal : Strings.Current["Saves.Setup.Preview.FetchExplanation"],
                    fetch.Select(s => Strings.Current.Format("Saves.Setup.Preview.FetchRow", s.DisplayName, s.File?.Version ?? s.SaveVersion,
                        s.File?.Name ?? s.File?.FileName ?? string.Empty)));

                Section(panel, Strings.Current.Format("Saves.Setup.Preview.CopyHeading", copied.Count),
                    Strings.Current["Saves.Setup.Preview.CopyExplanation"],
                    copied.Select(s => Strings.Current.Format("Saves.Setup.Preview.CopyRow", s.DisplayName, s.SaveVersion)));

                if (target.CanRestore)
                    panel.Children.Add(Paragraph(Strings.Current["Saves.Setup.Preview.SafetyNote"], muted: true));
            }

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(Strings.Current.Format("Saves.Setup.Preview.Title", save.Name)),
                Content = new ScrollViewer { MaxHeight = 520, Content = panel },
                PrimaryButtonText = target.CanRestore ? Strings.Current["Saves.Setup.Preview.PrimaryButton"] : null,
                CloseButtonText = target.CanRestore
                    ? Strings.Current["Saves.Setup.Preview.CancelButton"]
                    : Strings.Current["Saves.Setup.Preview.CloseButton"],
                DefaultButton = target.CanRestore ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // Whether the copy goes to the same drive as its source, where the game files are hard-linked.
        private static bool WillLink(SaveSetupTarget target)
        {
            try
            {
                return target.Instance is { } source && SaveSetupFiles.WouldHardLink(source.Record.GameFolder,
                    new InstanceRegistry(new InstanceSettingsStore().Read().GamesRoot).GamesRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        private static string GameSentence(SaveSetupTarget target, long? copyBytes, string? restingInstanceId)
        {
            var wanted = GameVersionLabel.For(target.Version, target.Variant);

            return target.Kind switch
            {
                SaveSetupTargetKind.ReuseCopy => Strings.Current.Format("Saves.Setup.Preview.Game.Reuse", wanted, target.CopyName),
                SaveSetupTargetKind.CopyInstance => Strings.Current.Format(
                    WillLink(target) ? "Saves.Setup.Preview.Game.Link" : "Saves.Setup.Preview.Game.Copy",
                    wanted,
                    InstanceLabel.LabelOf(target.Instance?.Record, restingInstanceId),
                    target.CopyName,
                    copyBytes is { } bytes ? InstanceRemovalSize.FormatBytes(bytes) : Strings.Current["Saves.Setup.Preview.SizeUnknown"]),
                SaveSetupTargetKind.Download => Strings.Current.Format("Saves.Setup.Preview.Game.Download", wanted, target.CopyName),
                _ => target.Reason
            };
        }

        private static string LostRow(SaveRollbackStep step)
        {
            var reason = step.Action switch
            {
                SaveRollbackAction.Workshop when ShellViewModels.Instance.Environment.WorkshopKeep.Kept()
                    .Any(k => string.Equals(k.ModuleId, step.Id.Value, StringComparison.OrdinalIgnoreCase))
                    => Strings.Current["Core.Saves.Setup.Mod.WorkshopKept"],
                SaveRollbackAction.Workshop => Strings.Current["Core.Saves.Setup.Mod.Workshop"],
                SaveRollbackAction.NexusFileNotListed => Strings.Current.Format("Core.Saves.Setup.Mod.NotListed", step.SaveVersion),
                _ => Strings.Current["Core.Saves.Setup.Mod.NoNexusPage"]
            };

            var fallback = step.CanCopyInstalled
                ? Strings.Current.Format("Saves.Setup.Preview.LostFallback", step.InstalledVersion ?? string.Empty)
                : Strings.Current["Saves.Setup.Preview.LostNoFallback"];

            return Strings.Current.Format("Saves.Setup.Preview.LostRow", step.DisplayName, step.SaveVersion, reason, fallback);
        }

        private static void Section(StackPanel panel, string heading, string explanation, IEnumerable<string> lines)
        {
            var listed = lines.Take(MaxLines + 1).ToList();

            if (listed.Count == 0)
                return;

            panel.Children.Add(new TextBlock
            {
                Text = heading,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });

            panel.Children.Add(Paragraph(explanation, muted: true));
            panel.Children.Add(Paragraph(string.Join(Environment.NewLine, listed.Take(MaxLines))));

            if (listed.Count > MaxLines)
                panel.Children.Add(Paragraph(Strings.Current["Saves.Setup.Preview.AndMore"], muted: true));
        }

        private static TextBlock Paragraph(string text, bool muted = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

            if (muted && Application.Current.Resources.TryGetValue("AlkMutedTextBrush", out var brush) && brush is Brush muteBrush)
                block.Foreground = muteBrush;

            return block;
        }
    }
}
