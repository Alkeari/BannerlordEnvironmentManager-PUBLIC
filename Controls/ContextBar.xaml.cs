using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Controls;

// The context bar at the top of every page (redesign proposal 1.1). It reads the same view models
// Play always used (the version switcher and the launch state on EnvironmentViewModel), so a version
// picked here is the version every page reads, and Launch here is exactly Play's Launch.
public sealed partial class ContextBar : UserControl
{
    public static readonly DependencyProperty IsLaunchPrimaryProperty = DependencyProperty.Register(
        nameof(IsLaunchPrimary), typeof(bool), typeof(ContextBar), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(ContextBar), new PropertyMetadata(false, OnLayoutChanged));

    public ContextBar()
    {
        try
        {
            Construct();
        }
        catch (Exception ex)
        {
            // XAML reports a failure here only as "Cannot create instance of type ContextBar" and drops the
            // cause, so the cause is logged before it is rethrown.
            Services.LoggingService.Log($"ContextBar failed to construct: {ex}", Services.LogLevel.Critical);
            throw;
        }
    }

    private void Construct()
    {
        InitializeComponent();

        ViewModel.VersionSwitcher.PropertyChanged += OnSwitcherChanged;
        ViewModel.PropertyChanged += OnEnvironmentChanged;

        // Singletons that outlive the bar; the bar is built once with the shell, so these stay.
        ViewModel.VersionSwitcher.InstalledVersions.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(Apply);
        ViewModel.AvailableTargets.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(Apply);
        ShellViewModels.Instance.NavigationSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                DispatcherQueue.TryEnqueue(Apply);
        };
        Unloaded += (_, _) =>
        {
            ViewModel.VersionSwitcher.PropertyChanged -= OnSwitcherChanged;
            ViewModel.PropertyChanged -= OnEnvironmentChanged;
        };
        Loaded += (_, _) =>
        {
            ViewModel.VersionSwitcher.PropertyChanged -= OnSwitcherChanged;
            ViewModel.PropertyChanged -= OnEnvironmentChanged;
            ViewModel.VersionSwitcher.PropertyChanged += OnSwitcherChanged;
            ViewModel.PropertyChanged += OnEnvironmentChanged;
            Apply();
        };

        Apply();
    }

    // Raised when the user asks for the Versions page from the bar (nothing installed yet).
    public event EventHandler? OpenVersionsRequested;

    public EnvironmentViewModel ViewModel => ShellViewModels.Instance.Environment;

    // True on Play, where Launch Game is the page's primary action.
    public bool IsLaunchPrimary
    {
        get => (bool)GetValue(IsLaunchPrimaryProperty);
        set => SetValue(IsLaunchPrimaryProperty, value);
    }

    // True below the compact window width: the field labels go, the pickers narrow.
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public Visibility BusyVisibility(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarningVisibility(string? warning) =>
        string.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((ContextBar)d).Apply();
    }

    private void OnSwitcherChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;

        if (e.PropertyName == nameof(VersionSwitcherViewModel.HasInstalledVersions))
            DispatcherQueue.TryEnqueue(Apply);
    }

    private void OnEnvironmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;

        if (e.PropertyName == nameof(EnvironmentViewModel.LaunchTargetNote))
            DispatcherQueue.TryEnqueue(Apply);
    }

    private void OnOpenVersions(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenVersionsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Apply()
    {
        var hasVersions = ViewModel.VersionSwitcher.HasInstalledVersions;

        // Basic shows a picker only when there is a choice to make: one version installed or one
        // launch target means nothing to pick, the way the other managers leave it out.
        var advanced = ShellViewModels.Instance.NavigationSettings.IsAdvancedMode;
        var pickVersion = hasVersions && (advanced || ViewModel.VersionSwitcher.InstalledVersions.Count > 1);
        var pickTarget = advanced || ViewModel.AvailableTargets.Count > 1;

        VersionPicker.Visibility = pickVersion ? Visibility.Visible : Visibility.Collapsed;
        NoVersionsButton.Visibility = hasVersions ? Visibility.Collapsed : Visibility.Visible;

        VersionLabel.Visibility = !IsCompact && (pickVersion || !hasVersions) ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Visibility = !IsCompact && pickTarget ? Visibility.Visible : Visibility.Collapsed;
        TargetPicker.Visibility = pickTarget ? Visibility.Visible : Visibility.Collapsed;

        VersionPicker.MinWidth = IsCompact ? 160 : 200;
        TargetPicker.MinWidth = IsCompact ? 140 : 180;

        TargetNote.Visibility = !IsCompact && pickTarget && !string.IsNullOrEmpty(ViewModel.LaunchTargetNote)
            ? Visibility.Visible
            : Visibility.Collapsed;

        LaunchButton.Style = (Style)Application.Current.Resources[IsLaunchPrimary ? "RowPrimaryAction" : "RowAction"];
    }
}
