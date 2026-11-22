using System.Windows.Input;
using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BannerlordEnvironmentManager.Controls;

// How a finding reads. Error and warning are both Alert Amber and differ by glyph and wording; a note
// is Steel Gray and carries no Accept the Risk, because it never counts against anything.
public enum FindingSeverity
{
    Error,
    Warning,
    Note,
}

// The one finding row (redesign proposal 1.6 as amended by the owner at 23:32 on 2026-10-29). It is
// presentation only: whether a finding is accepted, and what accepting or undoing does, belong to the
// view model behind it and to AcceptedFindingStore. The row raises AcceptConfirmed after its inline
// two-step confirmation and UndoRequested from the Accepted state, and runs AcceptCommand and
// UndoCommand with CommandParameter when those are bound instead.
[ContentProperty(Name = nameof(FixContent))]
public sealed partial class FindingRow : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(FindingSeverity), typeof(FindingRow), new PropertyMetadata(FindingSeverity.Warning, OnChanged));

    public static readonly DependencyProperty HeadlineProperty = DependencyProperty.Register(
        nameof(Headline), typeof(string), typeof(FindingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(FindingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty DetailMaxHeightProperty = DependencyProperty.Register(
        nameof(DetailMaxHeight), typeof(double), typeof(FindingRow),
        new PropertyMetadata(double.PositiveInfinity));

    // The evidence text Advanced Mode reveals under a finding (a path, a method, a log line).
    public static readonly DependencyProperty EvidenceProperty = DependencyProperty.Register(
        nameof(Evidence), typeof(string), typeof(FindingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty ShowEvidenceProperty = DependencyProperty.Register(
        nameof(ShowEvidence), typeof(bool), typeof(FindingRow), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty IsAcceptedProperty = DependencyProperty.Register(
        nameof(IsAccepted), typeof(bool), typeof(FindingRow), new PropertyMetadata(false, OnAcceptedChanged));

    // Every error and warning can be accepted; a source that genuinely cannot record an acceptance
    // sets this False and the button is absent rather than dead.
    public static readonly DependencyProperty CanAcceptProperty = DependencyProperty.Register(
        nameof(CanAccept), typeof(bool), typeof(FindingRow), new PropertyMetadata(true, OnChanged));

    // Offered only where a launch can pass the finding for one run.
    public static readonly DependencyProperty CanIgnoreOnceProperty = DependencyProperty.Register(
        nameof(CanIgnoreOnce), typeof(bool), typeof(FindingRow), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty CanUndoProperty = DependencyProperty.Register(
        nameof(CanUndo), typeof(bool), typeof(FindingRow), new PropertyMetadata(true, OnChanged));

    // When it was accepted, said on the Accepted row in place of the fix line.
    public static readonly DependencyProperty AcceptedDetailProperty = DependencyProperty.Register(
        nameof(AcceptedDetail), typeof(string), typeof(FindingRow), new PropertyMetadata(string.Empty, OnChanged));

    // False inside a list that selects or is clicked: a selectable TextBlock takes the
    // pointer and the click never reaches the row.
    public static readonly DependencyProperty IsTextSelectableProperty = DependencyProperty.Register(
        nameof(IsTextSelectable), typeof(bool), typeof(FindingRow), new PropertyMetadata(true, OnChanged));

    public static readonly DependencyProperty FixContentProperty = DependencyProperty.Register(
        nameof(FixContent), typeof(object), typeof(FindingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty AcceptCommandProperty = DependencyProperty.Register(
        nameof(AcceptCommand), typeof(ICommand), typeof(FindingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty UndoCommandProperty = DependencyProperty.Register(
        nameof(UndoCommand), typeof(ICommand), typeof(FindingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(FindingRow), new PropertyMetadata(null));

    private bool confirming;

    public FindingRow()
    {
        InitializeComponent();
        Apply();
    }

    // A row narrower than this cannot hold its text beside its actions: the actions take what they
    // need first and the text column drops to nothing, so the row showed buttons and no words.
    private const double StackBelowWidth = 1300;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < StackBelowWidth;

        foreach (var actions in new FrameworkElement[] { OpenActions, ConfirmActions, AcceptedActions })
        {
            Grid.SetRow(actions, stacked ? 1 : 0);
            Grid.SetColumn(actions, stacked ? 1 : 2);
            Grid.SetColumnSpan(actions, stacked ? 2 : 1);
            actions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
    }
    public event TypedEventHandler<FindingRow, object?>? AcceptConfirmed;

    public event TypedEventHandler<FindingRow, object?>? UndoRequested;

    public event TypedEventHandler<FindingRow, object?>? IgnoreOnceRequested;

    public bool CanIgnoreOnce
    {
        get => (bool)GetValue(CanIgnoreOnceProperty);
        set => SetValue(CanIgnoreOnceProperty, value);
    }

    public FindingSeverity Severity
    {
        get => (FindingSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double DetailMaxHeight
    {
        get => (double)GetValue(DetailMaxHeightProperty);
        set => SetValue(DetailMaxHeightProperty, value);
    }

    public string Evidence
    {
        get => (string)GetValue(EvidenceProperty);
        set => SetValue(EvidenceProperty, value);
    }

    public bool ShowEvidence
    {
        get => (bool)GetValue(ShowEvidenceProperty);
        set => SetValue(ShowEvidenceProperty, value);
    }

    public bool IsAccepted
    {
        get => (bool)GetValue(IsAcceptedProperty);
        set => SetValue(IsAcceptedProperty, value);
    }

    public bool CanAccept
    {
        get => (bool)GetValue(CanAcceptProperty);
        set => SetValue(CanAcceptProperty, value);
    }

    public bool CanUndo
    {
        get => (bool)GetValue(CanUndoProperty);
        set => SetValue(CanUndoProperty, value);
    }

    public string AcceptedDetail
    {
        get => (string)GetValue(AcceptedDetailProperty);
        set => SetValue(AcceptedDetailProperty, value);
    }

    public bool IsTextSelectable
    {
        get => (bool)GetValue(IsTextSelectableProperty);
        set => SetValue(IsTextSelectableProperty, value);
    }

    public object? FixContent
    {
        get => GetValue(FixContentProperty);
        set => SetValue(FixContentProperty, value);
    }

    public ICommand? AcceptCommand
    {
        get => (ICommand?)GetValue(AcceptCommandProperty);
        set => SetValue(AcceptCommandProperty, value);
    }

    public ICommand? UndoCommand
    {
        get => (ICommand?)GetValue(UndoCommandProperty);
        set => SetValue(UndoCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((FindingRow)d).Apply();
    }

    private static void OnAcceptedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        var row = (FindingRow)d;
        row.confirming = false;
        row.Apply();
    }

    private void OnAcceptClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        confirming = true;
        Apply();
        ConfirmButton.Focus(FocusState.Programmatic);
    }

    private void OnIgnoreOnceClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        IgnoreOnceRequested?.Invoke(this, CommandParameter);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        confirming = false;
        Apply();
        AcceptButton.Focus(FocusState.Programmatic);
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        confirming = false;

        AcceptConfirmed?.Invoke(this, CommandParameter);

        if (AcceptCommand is { } command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);

        Apply();
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        UndoRequested?.Invoke(this, CommandParameter);

        if (UndoCommand is { } command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void Apply()
    {
        var accepted = IsAccepted && Severity != FindingSeverity.Note;
        var acceptable = CanAccept && Severity != FindingSeverity.Note && !accepted;

        GlyphText.Text = Severity switch
        {
            FindingSeverity.Error => "\u2715",
            FindingSeverity.Warning => "\u25B2",
            _ => "\u25CB",
        };

        GlyphText.Foreground = accepted || Severity == FindingSeverity.Note
            ? Resource("AlkSteelGrayBrush")
            : Resource("AlkAlertAmberBrush");

        HeadlineText.IsTextSelectionEnabled = IsTextSelectable;
        DetailText.IsTextSelectionEnabled = IsTextSelectable;
        EvidenceText.IsTextSelectionEnabled = IsTextSelectable;

        HeadlineText.Text = Headline;
        HeadlineText.Foreground = accepted ? Resource("AlkSteelGrayBrush") : Resource("AlkSoftChampagneBrush");
        ToolTipService.SetToolTip(HeadlineText, string.IsNullOrEmpty(Headline) ? null : Headline);

        var detail = confirming
            ? Strings.Current["Controls.FindingRow.ConfirmPrompt"]
            : accepted && !string.IsNullOrEmpty(AcceptedDetail) ? AcceptedDetail : Detail;

        DetailText.Text = detail;
        DetailText.Foreground = accepted ? Resource("AlkMutedTextBrush") : Resource("AlkSteelGrayBrush");
        DetailViewer.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(DetailText, string.IsNullOrEmpty(detail) ? null : detail);

        EvidenceText.Text = Evidence;
        ToolTipService.SetToolTip(EvidenceText, string.IsNullOrEmpty(Evidence) ? null : Evidence);
        EvidenceText.Visibility = ShowEvidence && !string.IsNullOrEmpty(Evidence) ? Visibility.Visible : Visibility.Collapsed;

        Root.Background = confirming ? Resource("AlkRaisedBrush") : Resource("AlkTransparentBrush");

        OpenActions.Visibility = !accepted && !confirming ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Visibility = acceptable ? Visibility.Visible : Visibility.Collapsed;
        IgnoreOnceButton.Visibility = acceptable && CanIgnoreOnce ? Visibility.Visible : Visibility.Collapsed;
        ConfirmActions.Visibility = confirming && acceptable ? Visibility.Visible : Visibility.Collapsed;
        AcceptedActions.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.Visibility = CanUndo ? Visibility.Visible : Visibility.Collapsed;
    }
}
