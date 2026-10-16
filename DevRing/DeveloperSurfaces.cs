#if DEV_BEM
namespace BannerlordEnvironmentManager.DevRing
{
    using BannerlordEnvironmentManager.Converters;
    using BannerlordEnvironmentManager.Localization;
    using BannerlordEnvironmentManager.ViewModels;
    using BannerlordEnvironmentManager.Views;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Automation;
    using Microsoft.UI.Xaml.Controls;
    using Microsoft.UI.Xaml.Controls.Primitives;
    using Microsoft.UI.Xaml.Data;
    using Microsoft.UI.Xaml.Media;
    using System.Collections.Specialized;
    using System.ComponentModel;

    // The outer ring's presentation, and the only place in the app that builds a control in code
    // rather than in XAML.
    //
    // A dev-only surface has to be absent from the exe that reaches Nexus, not present and hidden: a
    // Visibility binding still compiles the element, its text and its command into the published
    // build, and the whole reason for the ring is that a switch someone can find is not a boundary.
    // XAML has no #if, so the six elements that moved out here are built and inserted from code the
    // C# compiler removes entirely without DEV_BEM. Each page's XAML carries a comment where its
    // element used to be, naming this file, so the next reader finds the other half.
    //
    // Everything is placed relative to a named neighbor rather than at a literal index, so a page
    // that gains a row above does not silently move a developer control somewhere it does not belong.
    internal static class DeveloperSurfaces
    {
        // Versions: Purpose, directly below Rename. A purpose tells a mod's build tool a testing
        // instance from one that gets played in, and a player has no build tool. Only the declaring is
        // in the ring: every build reads InstanceRecord.Purpose and labels the row with it.
        internal static void AddTo(VersionsPage page)
        {
            var purpose = new MenuFlyoutSubItem();
            Loc.SetText(purpose, "Versions.Menu.Purpose");
            Loc.SetTooltip(purpose, "Versions.Menu.Purpose.Tooltip");

            purpose.Items.Add(PurposeItem(page, "Versions.Menu.Purpose.Testing",
                page.ViewModel.SetPurposeTestingCommand, nameof(page.ViewModel.Menu.PurposeTestingTooltip)));
            purpose.Items.Add(PurposeItem(page, "Versions.Menu.Purpose.Playing",
                page.ViewModel.SetPurposePlayingCommand, nameof(page.ViewModel.Menu.PurposePlayingTooltip)));
            purpose.Items.Add(PurposeItem(page, "Versions.Menu.Purpose.Undeclared",
                page.ViewModel.SetPurposeUndeclaredCommand, nameof(page.ViewModel.Menu.PurposeUndeclaredTooltip)));

            InsertAfter<MenuFlyoutItemBase>(page.InstanceRowMenu.Items, page.RenameMenuItem, purpose);
        }

        // All three are disabled together on the resting row, which is the install played on whatever
        // a record says, and each carries the reason for its own item in its tooltip.
        private static MenuFlyoutItem PurposeItem(
            VersionsPage page, string textKey, System.Windows.Input.ICommand command, string tooltipProperty)
        {
            var item = new MenuFlyoutItem { Command = command };
            Loc.SetText(item, textKey);

            BindingOperations.SetBinding(item, Control.IsEnabledProperty,
                OneWay(page.ViewModel, "Menu.CanSetPurpose"));
            BindingOperations.SetBinding(item, ToolTipService.ToolTipProperty,
                OneWay(page.ViewModel, "Menu." + tooltipProperty));

            return item;
        }

        // Play: Mark as built on this machine, directly below Mark as Not on Nexus, which stays in
        // every build because a mod can come from Discord or a friend; and Open SubModule.xml,
        // directly below Open Module Folder, which stays because a player has real reasons to reach
        // the folder while reading another author's raw manifest is the author's move.
        internal static void AddTo(EnvironmentPage page)
        {
            var item = new MenuFlyoutItem { Command = page.ViewModel.ToggleContextBuiltLocallyCommand };
            Loc.SetTooltip(item, "Environment.ToggleBuiltLocallyMenuItem.Tooltip");

            BindingOperations.SetBinding(item, MenuFlyoutItem.TextProperty,
                OneWay(page.ViewModel, nameof(page.ViewModel.ContextBuiltLocallyLabel)));

            InsertAfter<MenuFlyoutItemBase>(page.ModuleRowMenu.Items, page.NotOnNexusMenuItem, item);

            // Handed back to the page as well as inserted, because the menu map there hides and shows
            // every single-module item by name while a multi-selection stands.
            var manifest = new MenuFlyoutItem { Command = page.ViewModel.OpenContextModuleManifestCommand };
            Loc.SetText(manifest, "Environment.OpenManifestMenuItem");
            Loc.SetTooltip(manifest, "Environment.OpenManifestMenuItem.Tooltip");

            page.ModuleOpenManifestItem = manifest;
            InsertAfter<MenuFlyoutItemBase>(page.ModuleRowMenu.Items, page.ModuleOpenFolderItem, manifest);
        }

        // Forensics: the whole Read the Compiled Method expander, back where it sat, directly after the
        // report detail. Nothing in it can be filled without a decompiler, and only the ring has one,
        // so it goes whole rather than as a shell a published build cannot use. The crash reading, the
        // ranked suspects and the frame attribution behind the dropdown are unchanged in every build.
        //
        // Also the two dump buttons, under the sentence saying what Windows keeps: they write Windows'
        // own registry keys machine-wide and ask for administrator rights.
        internal static void AddTo(DiagnosticsPage page)
        {
            InsertAfter<UIElement>(page.DetailColumn.Children, page.ReportDetail, DecompileExpander(page));

            var dumps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

            dumps.Children.Add(ActionButton(
                page.DiagnosticsViewModel.ConfigureFullDumpsCommand, "Diagnostics.KeepFullDumpsButton", 270));

            dumps.Children.Add(ActionButton(
                page.DiagnosticsViewModel.RestoreDumpSettingsCommand, "Diagnostics.RestoreDumpSettingsButton", 220));

            InsertAfter<UIElement>(page.DumpEvidence.Children, page.DumpSettingsSentence, dumps);
        }

        // Out of the way by default and as wide as it needs to be: source is read unwrapped, and the
        // one scroller around this column carries the width, so the prose is held to the viewport and
        // the source is not.
        private static Expander DecompileExpander(DiagnosticsPage page)
        {
            var vm = page.DiagnosticsViewModel;

            // The header is the toggle, so its text opts out; the body stays selectable.
            var header = new TextBlock
            {
                Style = (Style)Application.Current.Resources["SubHeadingText"],
                IsTextSelectionEnabled = false
            };

            Loc.SetText(header, "Diagnostics.DecompileExpander.Header");
            Loc.SetTooltip(header, "Diagnostics.DecompileExpander.Tooltip");

            var blurb = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = (Brush)Application.Current.Resources["AlkSteelGrayBrush"]
            };

            Loc.SetText(blurb, "Diagnostics.DecompileBlurb");

            var status = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };

            BindingOperations.SetBinding(status, TextBlock.TextProperty,
                OneWay(vm, nameof(vm.DecompileStatus)));
            BindingOperations.SetBinding(status, UIElement.VisibilityProperty,
                WhenSaidSomething(vm, nameof(vm.DecompileStatus)));

            var inner = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };

            BindingOperations.SetBinding(inner, FrameworkElement.MaxWidthProperty,
                OneWay(page.DetailScroller, nameof(page.DetailScroller.ViewportWidth)));

            inner.Children.Add(blurb);
            inner.Children.Add(DecompileActions(vm));
            inner.Children.Add(new FrameDropdown(vm).Box);
            inner.Children.Add(status);

            var outer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };

            // Anything inside the one scroller that scrolled sideways would steal the wheel from it.
            ScrollViewer.SetHorizontalScrollMode(outer, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(outer, ScrollBarVisibility.Disabled);

            outer.Children.Add(inner);
            outer.Children.Add(SourcePanel(vm));

            var expander = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                IsExpanded = false,
                Padding = new Thickness(12, 4, 12, 8),
                Header = header,
                Content = outer
            };

            Loc.SetAutomationName(expander, "Diagnostics.DecompileExpander.Header");

            var shown = OneWay(vm, nameof(vm.HasSelectedReport));
            shown.Converter = new BoolToVisibilityConverter();

            BindingOperations.SetBinding(expander, UIElement.VisibilityProperty, shown);

            return expander;
        }

        // Show the Faulting Method, Decompile the Selected Frame, Stop, then Open the Assembly in
        // dotPeek, with the progress ring last, in the order they have always read.
        private static StackPanel DecompileActions(DiagnosticsViewModel vm)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

            row.Children.Add(ActionButton(
                vm.ShowFaultingMethodCommand, "Diagnostics.ShowFaultingMethodButton", 210));
            row.Children.Add(ActionButton(
                vm.DecompileSelectedFrameCommand, "Diagnostics.DecompileSelectedFrameButton", 220));
            row.Children.Add(ActionButton(
                vm.CancelDecompileCommand, "Diagnostics.StopDecompileButton", 80));

            var dotPeek = ActionButton(vm.OpenInDotPeekCommand, "Diagnostics.OpenInDotPeekButton", 240);

            // Absent means absent: with no dotPeek on this machine the button is not shown, and the
            // path is read once at construction exactly as the XAML read it.
            dotPeek.Visibility = vm.HasDotPeek ? Visibility.Visible : Visibility.Collapsed;
            row.Children.Add(dotPeek);

            var ring = new ProgressRing { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };

            BindingOperations.SetBinding(ring, ProgressRing.IsActiveProperty,
                OneWay(vm, nameof(vm.IsDecompiling)));

            row.Children.Add(ring);

            return row;
        }

        // The frame dropdown. Each row is a ComboBoxItem built here rather than a data object rendered
        // through a template, because a DataTemplate cannot be constructed in code and the second line
        // has to carry the muted foreground: every resource lookup below fails at the lookup rather
        // than silently inside a parsed string.
        //
        // The item holds its row on Tag, and selection reads it back off Tag, so SelectedFrame keeps
        // exactly the semantics the rest of the ring already depends on.
        private sealed class FrameDropdown
        {
            private readonly DiagnosticsViewModel viewModel;

            // Set while this class is the one moving the selection, so the two directions cannot chase
            // each other.
            private bool syncing;

            internal ComboBox Box { get; } = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 900
            };

            internal FrameDropdown(DiagnosticsViewModel viewModel)
            {
                this.viewModel = viewModel;

                Loc.SetPlaceholder(Box, "Diagnostics.FramesComboBox.Placeholder");
                Loc.SetAutomationName(Box, "Diagnostics.FramesComboBox.AutomationName");
                Loc.SetTooltip(Box, "Diagnostics.FramesComboBox.Tooltip");

                Attach();
                Rebuild();

                // Forensics is not a cached page, so a fresh one is built every time it is opened and
                // the view model it listens to outlives all of them. Without the Unloaded half, every
                // visit would leave another dropdown subscribed to the same singleton for the life of
                // the app. Attach is idempotent, so a second Loaded costs nothing.
                Box.Loaded += (_, _) =>
                {
                    Attach();
                    Rebuild();
                };

                Box.Unloaded += (_, _) => Detach();
            }

            private void Attach()
            {
                Detach();

                viewModel.Frames.CollectionChanged += OnFramesChanged;
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
                Box.SelectionChanged += OnSelectionChanged;
            }

            private void Detach()
            {
                viewModel.Frames.CollectionChanged -= OnFramesChanged;
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                Box.SelectionChanged -= OnSelectionChanged;
            }

            private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                _ = sender;
                _ = e;

                Rebuild();
            }

            private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                _ = sender;

                if (!syncing && e.PropertyName == nameof(DiagnosticsViewModel.SelectedFrame))
                    Sync(() => Select(viewModel.SelectedFrame));
            }

            private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                _ = sender;
                _ = e;

                if (syncing)
                    return;

                viewModel.SelectedFrame = (Box.SelectedItem as ComboBoxItem)?.Tag as CrashFrameRowViewModel;
            }

            // The whole list rather than the one changed row: a crash stack is tens of frames, and the
            // report the page is showing replaces all of them at once.
            private void Rebuild() => Sync(() =>
            {
                Box.Items.Clear();

                foreach (var row in viewModel.Frames)
                    Box.Items.Add(ItemFor(row));

                Select(viewModel.SelectedFrame);
            });

            private void Select(CrashFrameRowViewModel? row) =>
                Box.SelectedItem = row is null
                    ? null
                    : Box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, row));

            private void Sync(Action change)
            {
                syncing = true;

                try
                {
                    change();
                }
                finally
                {
                    syncing = false;
                }
            }

            // The frame, then where it came from. A dropdown row is picked by clicking it, so both
            // lines opt out of text selection, and the item carries the frame's own name for a screen
            // reader rather than letting one be assembled out of the two blocks.
            private static ComboBoxItem ItemFor(CrashFrameRowViewModel row)
            {
                var lines = new StackPanel { Spacing = 1, Padding = new Thickness(2) };

                lines.Children.Add(RowLine(row.Label, null));
                lines.Children.Add(RowLine(row.Origin, (Brush)Application.Current.Resources["AlkSteelGrayBrush"]));

                var item = new ComboBoxItem { Content = lines, Tag = row };
                AutomationProperties.SetName(item, row.Label);

                return item;
            }

            private static TextBlock RowLine(string text, Brush? muted)
            {
                var line = new TextBlock
                {
                    Text = text,
                    Style = (Style)Application.Current.Resources["BodyText"],
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = false
                };

                if (muted is not null)
                    line.Foreground = muted;

                return line;
            }
        }

        private static Border SourcePanel(DiagnosticsViewModel vm)
        {
            var source = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true
            };

            BindingOperations.SetBinding(source, TextBlock.TextProperty,
                OneWay(vm, nameof(vm.DecompiledSource)));

            var panel = new Border
            {
                Background = (Brush)Application.Current.Resources["AlkSurfaceRaisedBrush"],
                BorderBrush = (Brush)Application.Current.Resources["AlkHairlineBrush"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = source
            };

            BindingOperations.SetBinding(panel, UIElement.VisibilityProperty,
                WhenSaidSomething(vm, nameof(vm.DecompiledSource)));

            return panel;
        }

        // Shown only while the property has something to say, exactly as the XAML's
        // NullOrEmptyToVisibility did.
        private static Binding WhenSaidSomething(object source, string path)
        {
            var binding = OneWay(source, path);
            binding.Converter = new NullOrEmptyToVisibilityConverter();

            return binding;
        }

        // Settings: Keep the crash handler under a debugger, appended to the crash handling group.
        // Every build still sends the flag when the stored setting says to; only the box moved.
        internal static void AddTo(SettingsPage page)
        {
            var box = new CheckBox { Style = (Style)Application.Current.Resources["AlkCheckBox"] };

            Loc.SetContent(box, "Settings.KeepCrashHandlerUnderDebuggerCheckBox");
            Loc.SetTooltip(box, "Settings.KeepCrashHandlerUnderDebuggerCheckBox.Tooltip");

            BindingOperations.SetBinding(box, ToggleButton.IsCheckedProperty, new Binding
            {
                Source = page.EnvironmentViewModel,
                Path = new PropertyPath(nameof(page.EnvironmentViewModel.KeepCrashHandlerUnderDebugger)),
                Mode = BindingMode.TwoWay
            });

            page.CrashHandlerOptions.Children.Add(box);
        }

        // Library: Read Deleted Archive Names back on the end of the coverage row, Ask About
        // Unrecognized Again on a line of its own below it because that row already runs past 900 px,
        // and the sentence the first of them writes below the BUTR index summary.
        internal static void AddTo(InstallPage page)
        {
            var deleted = new Button
            {
                Command = page.ViewModel.RecoverDeletedArchiveLinksCommand,
                Style = (Style)Application.Current.Resources["Action"],
                MinWidth = 210
            };

            Loc.SetContent(deleted, "Install.ReadDeletedArchiveNamesButton");
            Loc.SetTooltip(deleted, "Install.ReadDeletedArchiveNamesButton.Tooltip");
            page.CoverageActions.Children.Add(deleted);

            var again = new Button
            {
                Command = page.ViewModel.AskAboutUnrecognizedAgainCommand,
                Style = (Style)Application.Current.Resources["Action"],
                MinWidth = 290
            };

            Loc.SetContent(again, "Install.AskAboutUnrecognizedAgainButton");
            Loc.SetTooltip(again, "Install.AskAboutUnrecognizedAgainButton.Tooltip");

            var ownLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            ownLine.Children.Add(again);
            InsertAfter<UIElement>(page.CoverageBand.Children, page.CoverageActionsBand, ownLine);

            var summary = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };

            var text = OneWay(page.ViewModel, nameof(page.ViewModel.DeletedArchiveRecoverySummary));
            var shown = OneWay(page.ViewModel, nameof(page.ViewModel.DeletedArchiveRecoverySummary));
            shown.Converter = new NullOrEmptyToVisibilityConverter();

            BindingOperations.SetBinding(summary, TextBlock.TextProperty, text);
            BindingOperations.SetBinding(summary, UIElement.VisibilityProperty, shown);

            InsertAfter<UIElement>(page.CoverageBand.Children, page.ButrIndexSummaryText, summary);

            // Library: Forget the Confirmed ID goes back between Open the Mod Page and Refresh
            // Confirmed List. Play's module row carries the same correction where a user meets the
            // problem, so nothing is closed off. Both lists and Propose It Again stay in every build:
            // a decision BEM keeps and acts on has to be visible and has to open both ways.
            InsertAfter<UIElement>(page.ConfirmedIdActions.Children, page.OpenConfirmedPageButton, ActionButton(
                page.ViewModel.ForgetConfirmedNexusIdCommand, "Install.ForgetConfirmedIdButton", 170));

            // Only the by-hand refresh, on the end of the row Propose It Again already holds.
            page.RejectedIdActions.Children.Add(ActionButton(
                page.ViewModel.ListRejectedNexusIdsCommand, "Install.RefreshRejectedListButton", 150));
        }

        // SubModule Validator: both version rewrites, each back where it sat. Rewriting the version
        // another author declared in their own SubModule.xml silences the launcher's compatibility
        // check without changing whether the mod works, so it is a judgement about a declaration
        // rather than a repair. Widen Wildcard Versions and both restores stay in every build.
        internal static void AddTo(SubModuleValidatorPage page)
        {
            InsertAfter<UIElement>(page.ValidatorActions.Children, page.LoadModulesButton, ActionButton(
                page.ValidatorViewModel.UpdateAllVersionsCommand, "Validator.UpdateAllVersionsButton", 180));

            var module = ActionButton(
                page.ValidatorViewModel.UpdateSelectedModuleVersionsCommand, "Validator.UpdateModuleVersionsButton", 0);

            module.HorizontalAlignment = HorizontalAlignment.Left;

            InsertAfter<UIElement>(page.ModuleDetailActions.Children, page.SelectedModuleHeadings, module);
        }

        // Every button the ring inserts is an Action, carries its own text and tooltip key, and takes
        // the width its XAML gave it; 0 means the XAML set none.
        private static Button ActionButton(System.Windows.Input.ICommand command, string key, double minWidth)
        {
            var button = new Button
            {
                Command = command,
                Style = (Style)Application.Current.Resources["Action"]
            };

            if (minWidth > 0)
                button.MinWidth = minWidth;

            Loc.SetContent(button, key);
            Loc.SetTooltip(button, key + ".Tooltip");

            return button;
        }

        private static Binding OneWay(object source, string path) =>
            new() { Source = source, Path = new PropertyPath(path), Mode = BindingMode.OneWay };

        private static void InsertAfter<T>(IList<T> items, T neighbor, T inserted) =>
            items.Insert(items.IndexOf(neighbor) + 1, inserted);
    }
}
#endif
