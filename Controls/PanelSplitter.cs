using BannerlordEnvironmentManager.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BannerlordEnvironmentManager.Controls
{
    // The left edge of a side column, in the gap beside it. Drag it, press the arrow keys, or set its
    // value through automation, and the column's width is remembered between sessions under SectionKey.
    // A column nobody has resized keeps the proportion its page gave it.
    public sealed partial class PanelSplitter : Grid
    {
        private const double MinimumWidth = 260;
        private const double MinimumNeighbour = 420;
        private const double KeyStep = 24;

        private static readonly SectionLayoutStore Store = new();

        private double startX;
        private double startWidth;
        private bool dragging;

        public PanelSplitter()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Width = 8;
            Margin = new Thickness(-8, 0, 0, 0);
            HorizontalAlignment = HorizontalAlignment.Left;
            IsTabStop = true;
            AutomationProperties.SetName(this, "Panel width");

            Loaded += (_, _) =>
            {
                // A left column's splitter sits on that column's right edge instead.
                if (SizesLeftColumn)
                {
                    Margin = new Thickness(0, 0, -8, 0);
                    HorizontalAlignment = HorizontalAlignment.Right;
                }

                Restore();

                if (Parent is Grid grid)
                {
                    grid.SizeChanged -= OnParentSizeChanged;
                    grid.SizeChanged += OnParentSizeChanged;
                }
            };
            Unloaded += (_, _) =>
            {
                if (Parent is Grid grid)
                    grid.SizeChanged -= OnParentSizeChanged;
            };
            PointerPressed += (_, e) =>
            {
                dragging = CapturePointer(e.Pointer);
                startX = e.GetCurrentPoint(null).Position.X;
                startWidth = ColumnWidth;
                e.Handled = true;
            };
            PointerMoved += (_, e) =>
            {
                // A side column's left edge follows the pointer, so dragged left is wider; a left
                // column's right edge does, so dragged right is wider.
                if (dragging)
                    SetColumnWidth(startWidth + Direction * (startX - e.GetCurrentPoint(null).Position.X), remember: false);
            };
            PointerReleased += (_, e) => EndDrag(e);
            PointerCanceled += (_, e) => EndDrag(e);
            KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Left or VirtualKey.Right)
                {
                    SetColumnWidth(ColumnWidth + Direction * (e.Key == VirtualKey.Left ? KeyStep : -KeyStep), remember: true);
                    e.Handled = true;
                }
            };
        }

        public string SectionKey { get; set; } = string.Empty;

        // True for a column on the left of its neighbour, such as Settings' group list.
        public bool SizesLeftColumn { get; set; }

        // The narrowest the column may go; a short list of names needs less than a side panel.
        public double MinimumColumnWidth { get; set; } = MinimumWidth;

        private double Direction => SizesLeftColumn ? -1 : 1;

        // Raised whenever this control changes the column, for a page with something sized to it.
        public event Action? ColumnWidthChanged;

        private ColumnDefinition? Column =>
            Parent is Grid grid && Grid.GetColumn(this) < grid.ColumnDefinitions.Count ? grid.ColumnDefinitions[Grid.GetColumn(this)] : null;

        private double ColumnWidth => Column?.ActualWidth ?? 0;

        private double MaximumColumnWidth =>
            Parent is Grid grid ? Math.Max(MinimumColumnWidth, grid.ActualWidth - MinimumNeighbour) : MinimumColumnWidth;

        // A page that folds its side column away calls this as it does, and the saved width returns
        // when the window is wide again.
        public void SetFolded(bool folded)
        {
            Visibility = folded ? Visibility.Collapsed : Visibility.Visible;

            if (!folded)
                Restore();
        }

        private void Restore()
        {
            if (Visibility == Visibility.Visible && SectionKey.Length > 0 && Store.Read(SectionKey) is { } saved)
                SetColumnWidth(saved.Size, remember: false);
        }

        // A width that fit a wide window must not squeeze the neighbour in a narrow one. The saved value
        // stays as it was, so the column grows back when the window does.
        private void OnParentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Visibility != Visibility.Visible || Column is not { Width.IsAbsolute: true } column)
                return;

            var wanted = SectionKey.Length > 0 && Store.Read(SectionKey) is { } saved ? saved.Size : column.Width.Value;

            SetColumnWidth(wanted, remember: false);
        }

        private void SetColumnWidth(double width, bool remember)
        {
            if (Column is not { } column)
                return;

            column.Width = new GridLength(Math.Clamp(width, MinimumColumnWidth, MaximumColumnWidth));
            ColumnWidthChanged?.Invoke();

            if (remember)
                Remember(column.Width.Value);
        }

        private void Remember(double width)
        {
            if (SectionKey.Length > 0)
                Store.Write(SectionKey, new SectionLayout(width, false));
        }

        private void EndDrag(PointerRoutedEventArgs e)
        {
            if (!dragging)
                return;

            dragging = false;
            ReleasePointerCapture(e.Pointer);

            if (Column is { } column)
                Remember(column.Width.Value);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new SplitterPeer(this);

        private sealed partial class SplitterPeer(PanelSplitter owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            public bool IsReadOnly => false;

            public double LargeChange => KeyStep * 4;

            public double SmallChange => KeyStep;

            public double Maximum => owner.MaximumColumnWidth;

            public double Minimum => owner.MinimumColumnWidth;

            public double Value => owner.ColumnWidth;

            public void SetValue(double value) => owner.SetColumnWidth(value, remember: true);

            protected override object GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        }
    }
}
