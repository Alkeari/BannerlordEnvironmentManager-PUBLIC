namespace BannerlordEnvironmentManager.Localization
{
    using BannerlordEnvironmentManager.Core.Localization;
    using BannerlordEnvironmentManager.Services;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Automation;
    using Microsoft.UI.Xaml.Controls;
    using Microsoft.UI.Xaml.Documents;

    // Attached properties rather than a markup extension: a scratch markup extension built to settle
    // this compiled, but WinUI resolves a custom markup extension through the reflection-based XAML
    // metadata provider and assigns whatever ProvideValue returns directly to the target property, with
    // no special handling for a returned Binding. A Binding assigned to a string-typed property such as
    // TextBlock.Text cannot bind live; x:Uid would tie translations to a rebuild, which is exactly what
    // the Languages override folder exists to avoid.
    //
    // The language cannot change without an app restart, so a key resolves once when it is applied
    // and never needs to re-resolve.
    public static class Loc
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text", typeof(string), typeof(Loc), new PropertyMetadata(null, OnTextChanged));

        public static readonly DependencyProperty ContentProperty =
            DependencyProperty.RegisterAttached(
                "Content", typeof(string), typeof(Loc), new PropertyMetadata(null, OnContentChanged));

        public static readonly DependencyProperty TooltipProperty =
            DependencyProperty.RegisterAttached(
                "Tooltip", typeof(string), typeof(Loc), new PropertyMetadata(null, OnTooltipChanged));

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.RegisterAttached(
                "Placeholder", typeof(string), typeof(Loc), new PropertyMetadata(null, OnPlaceholderChanged));

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.RegisterAttached(
                "Header", typeof(string), typeof(Loc), new PropertyMetadata(null, OnHeaderChanged));

        // The screen-reader name, separate from Text/Content: a control's automation name is only set
        // explicitly when its visible content is not itself readable text (an Expander whose header is
        // a compound layout, a ToggleButton whose Content is short but the accessible name wants more
        // context), so this property is applied deliberately rather than on every element.
        public static readonly DependencyProperty AutomationNameProperty =
            DependencyProperty.RegisterAttached(
                "AutomationName", typeof(string), typeof(Loc), new PropertyMetadata(null, OnAutomationNameChanged));

        public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);

        public static void SetText(DependencyObject target, string value) =>
            target.SetValue(TextProperty, value);

        public static string? GetContent(DependencyObject target) => (string?)target.GetValue(ContentProperty);

        public static void SetContent(DependencyObject target, string value) =>
            target.SetValue(ContentProperty, value);

        public static string? GetTooltip(DependencyObject target) => (string?)target.GetValue(TooltipProperty);

        public static void SetTooltip(DependencyObject target, string value) =>
            target.SetValue(TooltipProperty, value);

        public static string? GetPlaceholder(DependencyObject target) =>
            (string?)target.GetValue(PlaceholderProperty);

        public static void SetPlaceholder(DependencyObject target, string value) =>
            target.SetValue(PlaceholderProperty, value);

        public static string? GetHeader(DependencyObject target) => (string?)target.GetValue(HeaderProperty);

        public static void SetHeader(DependencyObject target, string value) =>
            target.SetValue(HeaderProperty, value);

        public static string? GetAutomationName(DependencyObject target) =>
            (string?)target.GetValue(AutomationNameProperty);

        public static void SetAutomationName(DependencyObject target, string value) =>
            target.SetValue(AutomationNameProperty, value);

        private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is not { } key)
                return;

            switch (target)
            {
                case TextBlock block:
                    block.Text = Strings.Current[key];
                    break;
                case TextBox box:
                    box.Text = Strings.Current[key];
                    break;
                // ToggleMenuFlyoutItem and RadioMenuFlyoutItem both derive from MenuFlyoutItem, so
                // this case also catches them.
                case MenuFlyoutItem menuItem:
                    menuItem.Text = Strings.Current[key];
                    break;
                // MenuFlyoutSubItem derives from MenuFlyoutItemBase rather than MenuFlyoutItem, so
                // it needs its own case or a submenu header falls through to the warning and renders
                // blank.
                case MenuFlyoutSubItem subItem:
                    subItem.Text = Strings.Current[key];
                    break;
                case Run run:
                    run.Text = Strings.Current[key];
                    break;
                case SelectorBarItem item:
                    item.Text = Strings.Current[key];
                    break;
                // InfoBar's body text is Message, not Content or Text; its Title is handled as a
                // header, in OnHeaderChanged, because that is the role it plays on the control.
                case InfoBar bar:
                    bar.Message = Strings.Current[key];
                    break;
                default:
                    LoggingService.Log(
                        $"Loc.Text: key '{key}' applied to unhandled control type '{target.GetType()}'.",
                        LogLevel.Warn);
                    break;
            }
        }

        private static void OnContentChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is not { } key)
                return;

            if (target is ContentControl control)
            {
                control.Content = Strings.Current[key];
                return;
            }

            LoggingService.Log(
                $"Loc.Content: key '{key}' applied to unhandled control type '{target.GetType()}'.",
                LogLevel.Warn);
        }

        private static void OnTooltipChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is { } key)
                ToolTipService.SetToolTip(target, Strings.Current[key]);
        }

        private static void OnPlaceholderChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is not { } key)
                return;

            switch (target)
            {
                case TextBox box:
                    box.PlaceholderText = Strings.Current[key];
                    break;
                case AutoSuggestBox suggest:
                    suggest.PlaceholderText = Strings.Current[key];
                    break;
                case PasswordBox password:
                    password.PlaceholderText = Strings.Current[key];
                    break;
                case ComboBox combo:
                    combo.PlaceholderText = Strings.Current[key];
                    break;
                default:
                    LoggingService.Log(
                        $"Loc.Placeholder: key '{key}' applied to unhandled control type '{target.GetType()}'.",
                        LogLevel.Warn);
                    break;
            }
        }

        private static void OnHeaderChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is not { } key)
                return;

            switch (target)
            {
                case ComboBox combo:
                    combo.Header = Strings.Current[key];
                    break;
                case TextBox box:
                    box.Header = Strings.Current[key];
                    break;
                case NumberBox numberBox:
                    numberBox.Header = Strings.Current[key];
                    break;
                case ToggleSwitch toggle:
                    toggle.Header = Strings.Current[key];
                    break;
                case Expander expander:
                    expander.Header = Strings.Current[key];
                    break;
                // InfoBar's Title reads as the control's header; its body text is Message, handled
                // in OnTextChanged. The same control appears in both switches because it plays both
                // roles.
                case InfoBar bar:
                    bar.Title = Strings.Current[key];
                    break;
                default:
                    LoggingService.Log(
                        $"Loc.Header: key '{key}' applied to unhandled control type '{target.GetType()}'.",
                        LogLevel.Warn);
                    break;
            }
        }

        private static void OnAutomationNameChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (Key(e) is not { } key)
                return;

            if (target is UIElement element)
            {
                AutomationProperties.SetName(element, Strings.Current[key]);
                return;
            }

            LoggingService.Log(
                $"Loc.AutomationName: key '{key}' applied to unhandled control type '{target.GetType()}'.",
                LogLevel.Warn);
        }

        private static string? Key(DependencyPropertyChangedEventArgs e) =>
            e.NewValue as string is { Length: > 0 } key ? key : null;
    }
}
