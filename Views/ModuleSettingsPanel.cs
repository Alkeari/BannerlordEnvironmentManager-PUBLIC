using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Settings;
using BannerlordEnvironmentManager.Services;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Views
{
    // One mod's settings, shown from the mod's own row on Play and Library: the settings folders BEM
    // attributes to that module. A file whose settings class BEM can read from the mod's assemblies
    // shows as grouped controls the way MCM does; any other file falls back to a raw text editor
    // under an "Edit file" expander. The scan, attribution and cleanup of every folder at once lives
    // in Troubleshoot's Mod Settings tab, one button below.
    public static class ModuleSettingsPanel
    {
        public static void Show(ModuleRowViewModel row)
        {
            ArgumentNullException.ThrowIfNull(row);

            var strings = Strings.Current;
            var settings = ShellViewModels.Instance.ModSettings;
            settings.RefreshCommand.Execute(null);

            var folders = ModuleSettingsFolders.For(settings.Matched.Select(match => match.Review), row.Entry.Id);
            var schemas = ReadSchemas(row);
            var body = new StackPanel { Spacing = 12 };
            var rowStyle = (Style)Application.Current.Resources["RowAction"];

            if (folders.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = strings["ModSettings.Row.None"],
                    Style = (Style)Application.Current.Resources["BodyText"],
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            foreach (var review in folders)
            {
                var path = review.Folder.FullPath;
                var open = new Button { Content = strings["ModSettings.Row.OpenFolder"], Style = rowStyle };
                open.Click += (_, _) => OpenFolder(path);

                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock
                {
                    Text = review.Folder.RelativePath,
                    Style = (Style)Application.Current.Resources["SubHeadingText"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
                text.Children.Add(new TextBlock
                {
                    Text = path,
                    Style = (Style)Application.Current.Resources["MonoText"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var line = new StackPanel { Spacing = 6 };
                line.Children.Add(text);
                line.Children.Add(open);

                foreach (var file in ModuleSettingsFolders.EditableFiles(path))
                    line.Children.Add(BuildFileEditor(file, ModuleSettingsFolders.SchemaFor(file, schemas), strings));

                body.Children.Add(line);
            }

            var manage = new Button { Content = strings["ModSettings.Row.ManageAll"], Style = rowStyle };
            manage.Click += (_, _) => ShellPage.Open(typeof(ModSettingsPage));
            body.Children.Add(manage);

            ShellPage.ShowDetail(strings.Format("ModSettings.Row.Title", row.DisplayName), body);
        }

        private static IReadOnlyList<McmSettingsSchema> ReadSchemas(ModuleRowViewModel row)
        {
            var folder = row.Entry.Manifest?.FolderPath;
            if (string.IsNullOrWhiteSpace(folder))
                return [];

            try
            {
                return McmSchemaReader.ReadModule(folder);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not read a mod's settings schema");
                return [];
            }
        }

        private static UIElement BuildFileEditor(string file, McmSettingsSchema? schema, ILocalizer strings)
        {
            IReadOnlyDictionary<string, string> values = new Dictionary<string, string>();
            if (schema is not null)
            {
                try
                {
                    values = McmSettingsFile.Read(file, schema.Properties.Select(p => p.PropertyName));
                }
                catch (Exception ex)
                {
                    LoggingService.LogException(ex, "Could not read a mod's settings file");
                }
            }

            // Only properties the file actually holds get a control; one absent from the file has
            // no value to edit and writing it would change the file's shape.
            var shown = schema?.Properties.Where(p => values.ContainsKey(p.PropertyName)).ToList() ?? [];
            if (shown.Count == 0)
                return BuildRawEditor(file, strings);

            return BuildSchemaEditor(file, shown, values, strings);
        }

        private static UIElement BuildSchemaEditor(
            string file, List<McmSettingProperty> properties, IReadOnlyDictionary<string, string> values, ILocalizer strings)
        {
            var panel = new StackPanel { Spacing = 8 };
            var readers = new Dictionary<string, (Func<string> Read, string Original)>(StringComparer.Ordinal);
            var status = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                TextWrapping = TextWrapping.Wrap,
            };

            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(file),
                Style = (Style)Application.Current.Resources["MonoText"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            string? currentGroup = null;
            foreach (var property in properties)
            {
                if (property.Group != currentGroup && property.Group.Length > 0)
                {
                    currentGroup = property.Group;
                    panel.Children.Add(new TextBlock
                    {
                        Text = property.Group,
                        Style = (Style)Application.Current.Resources["SubHeadingText"],
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                }

                var original = values[property.PropertyName];
                var control = BuildControl(property, original, out var read);
                readers[property.PropertyName] = (read, original);

                var block = new StackPanel { Spacing = 2 };
                block.Children.Add(new TextBlock
                {
                    Text = property.DisplayName,
                    Style = (Style)Application.Current.Resources["BodyText"],
                    TextWrapping = TextWrapping.Wrap,
                });
                block.Children.Add(control);

                var hint = property.HintText;
                if (property.RequireRestart)
                    hint = (hint + " " + strings["ModSettings.Panel.RequiresRestart"]).Trim();
                if (hint.Length > 0)
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = hint,
                        Style = (Style)Application.Current.Resources["MonoText"],
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                panel.Children.Add(block);
            }

            var save = new Button
            {
                Content = strings["ModSettings.Panel.Save"],
                Style = (Style)Application.Current.Resources["RowAction"],
            };
            save.Click += (_, _) =>
            {
                try
                {
                    var edits = readers
                        .Select(pair => (pair.Key, Value: pair.Value.Read(), pair.Value.Original))
                        .Where(item => item.Value != item.Original)
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    ModuleSettingsFolders.Save(file, edits);
                    foreach (var key in edits.Keys.ToList())
                        readers[key] = (readers[key].Read, edits[key]);
                    status.Text = strings.Format("ModSettings.Panel.Saved", Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    LoggingService.LogException(ex, "Could not save a mod's settings file");
                    status.Text = strings.Format("ModSettings.Panel.SaveFailed", Path.GetFileName(file), ex.Message);
                }
            };
            panel.Children.Add(save);
            panel.Children.Add(status);
            panel.Children.Add(BuildRawExpander(file, strings));
            return panel;
        }

        private static FrameworkElement BuildControl(McmSettingProperty property, string original, out Func<string> read)
        {
            switch (property.Kind)
            {
                case McmSettingKind.Bool:
                {
                    var toggle = new ToggleSwitch
                    {
                        IsOn = bool.TryParse(original, out var on) && on,
                    };
                    read = () => toggle.IsOn ? "true" : "false";
                    return toggle;
                }
                case McmSettingKind.Integer:
                case McmSettingKind.Float:
                case McmSettingKind.Dropdown:
                {
                    var isInteger = property.Kind != McmSettingKind.Float;
                    double.TryParse(original, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    var box = new NumberBox
                    {
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        SmallChange = isInteger ? 1 : 0.1,
                        LargeChange = isInteger ? 10 : 1,
                    };
                    if (property.Minimum is { } min) box.Minimum = min;
                    if (property.Maximum is { } max) box.Maximum = max;
                    box.Value = number;
                    read = () => double.IsNaN(box.Value)
                        ? original
                        : box.Value.ToString(isInteger ? "0" : "R", CultureInfo.InvariantCulture);
                    return box;
                }
                default:
                {
                    // AcceptsReturn before Text: setting Text first drops its line breaks.
                    var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                    text.Text = original;
                    read = () => text.Text;
                    return text;
                }
            }
        }

        private static UIElement BuildRawEditor(string file, ILocalizer strings)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(file),
                Style = (Style)Application.Current.Resources["MonoText"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            panel.Children.Add(BuildRawExpander(file, strings));
            return panel;
        }

        // The raw editor: shown only for a file BEM cannot lay out, and kept as an expander under a
        // laid-out file for the setting no attribute describes. It saves the whole text it shows.
        private static Expander BuildRawExpander(string file, ILocalizer strings)
        {
            var editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 160 };
            try
            {
                editor.Text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not read a mod's settings file");
            }

            var status = new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyText"],
                TextWrapping = TextWrapping.Wrap,
            };
            var save = new Button
            {
                Content = strings["ModSettings.Panel.Save"],
                Style = (Style)Application.Current.Resources["RowAction"],
            };
            save.Click += (_, _) =>
            {
                try
                {
                    ModuleSettingsFolders.Save(file, editor.Text);
                    status.Text = strings.Format("ModSettings.Panel.Saved", Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    LoggingService.LogException(ex, "Could not save a mod's settings file");
                    status.Text = strings.Format("ModSettings.Panel.SaveFailed", Path.GetFileName(file), ex.Message);
                }
            };

            var inner = new StackPanel { Spacing = 6 };
            inner.Children.Add(new TextBlock
            {
                Text = strings["ModSettings.Panel.EditFile.Note"],
                Style = (Style)Application.Current.Resources["MonoText"],
                TextWrapping = TextWrapping.Wrap,
            });
            inner.Children.Add(editor);
            inner.Children.Add(save);
            inner.Children.Add(status);

            return new Expander
            {
                Header = strings["ModSettings.Panel.EditFile"],
                Content = inner,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        private static void OpenFolder(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not open a mod's settings folder");
            }
        }
    }
}
