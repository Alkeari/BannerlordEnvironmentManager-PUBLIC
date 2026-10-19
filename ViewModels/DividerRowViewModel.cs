using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class DividerRowViewModel : ObservableObject, ILoadOrderRowViewModel
    {
        public DividerRowViewModel(LoadOrderDivider divider, int hiddenCount, int dividerIndex)
        {
            ArgumentNullException.ThrowIfNull(divider);

            Divider = divider;
            DividerIndex = dividerIndex;
            Label = divider.Label;
            Collapsed = divider.Collapsed;
            HiddenCount = hiddenCount;
        }

        public LoadOrderDivider Divider { get; }

        // Which entry of EnvironmentViewModel.Dividers this row is. LoadOrderDivider is a record, so
        // two sections that happen to hold the same label, anchor and state are equal to each other:
        // a right-click menu that looked its target up by value would act on whichever came first,
        // not the one under the pointer. The position is what tells them apart.
        public int DividerIndex { get; }

        public bool IsDivider => true;

        [ObservableProperty]
        public partial string Label { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HiddenCountText))]
        [NotifyPropertyChangedFor(nameof(CollapseExpandLabel))]
        public partial bool Collapsed { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HiddenCountText))]
        public partial int HiddenCount { get; set; }

        public string HiddenCountText => HiddenCount == 1 ? "1 module hidden" : $"{HiddenCount} modules hidden";

        public string CollapseExpandLabel => Collapsed ? "Expand" : "Collapse";

        [ObservableProperty]
        public partial bool IsUiSelected { get; set; }

        public override string ToString() => Label;
    }
}
