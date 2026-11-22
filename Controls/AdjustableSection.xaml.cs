using BannerlordEnvironmentManager.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace BannerlordEnvironmentManager.Controls
{
    [ContentProperty(Name = nameof(Body))]
    public sealed partial class AdjustableSection : UserControl
    {
        private const double MinimumHeight = 80;
        private const double DefaultHeight = 240;

        private static readonly SectionLayoutStore Store = new();

        public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
            nameof(Heading), typeof(string), typeof(AdjustableSection), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty MetaProperty = DependencyProperty.Register(
            nameof(Meta), typeof(string), typeof(AdjustableSection), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body), typeof(object), typeof(AdjustableSection), new PropertyMetadata(null));

        private bool collapsed = true;
        private bool defaultsApplied;

        public AdjustableSection()
        {
            InitializeComponent();
            Grip.Section = this;
            Loaded += (_, _) => Restore();
        }

        public string SectionKey { get; set; } = string.Empty;

        // A page's main content opens by default; an accepted-items list starts folded away.
        public bool StartsCollapsed { get; set; } = true;

        // The height the body opens at until he drags it.
        public double DefaultBodyHeight { get; set; } = DefaultHeight;

        // A catalog key for the heading, so a page names its sections without a new string of its own.
        public string HeadingKey
        {
            get => headingKey;
            set
            {
                headingKey = value;
                Heading = Core.Localization.Strings.Current.Format(value);
            }
        }

        private string headingKey = string.Empty;

        public string Heading
        {
            get => (string)GetValue(HeadingProperty);
            set => SetValue(HeadingProperty, value);
        }

        public string Meta
        {
            get => (string)GetValue(MetaProperty);
            set => SetValue(MetaProperty, value);
        }

        public object? Body
        {
            get => GetValue(BodyProperty);
            set => SetValue(BodyProperty, value);
        }

        internal double BodyHeight { get; private set; } = DefaultHeight;

        // Never so tall that the list above it has nowhere to go.
        // How much of the window the body may take, so a section in a short card stays inside it.
        public double MaximumWindowShare { get; set; } = 0.6;

        internal double MaximumBodyHeight => Math.Max(MinimumHeight, (XamlRoot?.Size.Height ?? 800) * MaximumWindowShare);

        internal double MinimumBodyHeight => MinimumHeight;

        internal void SetBodyHeight(double height, bool remember)
        {
            BodyHeight = Math.Clamp(height, MinimumHeight, MaximumBodyHeight);
            BodyHost.Height = BodyHeight;

            if (remember)
                Remember();
        }

        internal void Remember()
        {
            if (SectionKey.Length > 0)
                Store.Write(SectionKey, new SectionLayout(BodyHeight, collapsed));
        }

        private void Restore()
        {
            if (!defaultsApplied)
            {
                defaultsApplied = true;
                collapsed = StartsCollapsed;
                BodyHeight = DefaultBodyHeight;
            }

            if (SectionKey.Length > 0 && Store.Read(SectionKey) is { } saved)
            {
                collapsed = saved.Collapsed;
                BodyHeight = saved.Size;
            }

            SetBodyHeight(BodyHeight, remember: false);
            Apply();
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            collapsed = !collapsed;
            Apply();
            Remember();
        }

        private void Apply()
        {
            BodyHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            Grip.Visibility = BodyHost.Visibility;
            Chevron.Glyph = collapsed ? "" : "";
            AutomationProperties.SetName(Toggle, Heading);
        }
    }

    // The bar above an open section. Drag it, press the arrow keys, or set its value through automation.
    public sealed partial class SectionResizeGrip : Grid
    {
        private const double KeyStep = 24;

        private double startY;
        private double startHeight;
        private bool dragging;

        public SectionResizeGrip()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            IsTabStop = true;
            AutomationProperties.SetName(this, "Section height");

            PointerPressed += (_, e) =>
            {
                dragging = CapturePointer(e.Pointer);
                startY = e.GetCurrentPoint(null).Position.Y;
                startHeight = Section?.BodyHeight ?? 0;
                e.Handled = true;
            };
            PointerMoved += (_, e) =>
            {
                // Dragged up is taller: the section's top edge follows the pointer.
                if (dragging)
                    Section?.SetBodyHeight(startHeight + startY - e.GetCurrentPoint(null).Position.Y, remember: false);
            };
            PointerReleased += (_, e) => EndDrag(e);
            PointerCanceled += (_, e) => EndDrag(e);
            KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Up or VirtualKey.Down && Section is { } section)
                {
                    section.SetBodyHeight(section.BodyHeight + (e.Key == VirtualKey.Up ? KeyStep : -KeyStep), remember: true);
                    e.Handled = true;
                }
            };
        }

        internal AdjustableSection? Section { get; set; }

        protected override AutomationPeer OnCreateAutomationPeer() => new GripPeer(this);

        private void EndDrag(PointerRoutedEventArgs e)
        {
            if (!dragging)
                return;

            dragging = false;
            ReleasePointerCapture(e.Pointer);
            Section?.Remember();
        }

        private sealed partial class GripPeer(SectionResizeGrip owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            private AdjustableSection? Section => owner.Section;

            public bool IsReadOnly => false;

            public double LargeChange => KeyStep * 4;

            public double SmallChange => KeyStep;

            public double Maximum => Section?.MaximumBodyHeight ?? 0;

            public double Minimum => Section?.MinimumBodyHeight ?? 0;

            public double Value => Section?.BodyHeight ?? 0;

            public void SetValue(double value) => Section?.SetBodyHeight(value, remember: true);

            protected override object GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        }
    }
}
