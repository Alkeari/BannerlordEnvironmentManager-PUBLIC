namespace BannerlordEnvironmentManager.Localization
{
    using System.Globalization;
    using BannerlordEnvironmentManager.Core.Localization;
    using BannerlordEnvironmentManager.Services;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Media;
    using Microsoft.Windows.AppLifecycle;

    public sealed record LanguageOption(string Code, string NativeName, string EnglishName);

    public static class LocalizationService
    {
        private static readonly LanguageSettingsStore Store = new();

        public static IReadOnlyList<LanguageOption> Available { get; private set; } = [];

        public static string CurrentCode { get; private set; } = LanguageChoice.Fallback;

        public static void Start()
        {
            var overrides = CatalogOverrides.Load(OverrideFolder());

            foreach (var problem in overrides.Problems)
                LoggingService.Log($"Ignored a language file: {problem}", LogLevel.Warn);

            var codes = EmbeddedCatalogs.Codes.Concat(overrides.Catalogs.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            CurrentCode = LanguageChoice.Resolve(Store.Read(), codes, CultureInfo.CurrentUICulture);

            var english = EmbeddedCatalogs.English();

            var active = EmbeddedCatalogs.Load(CurrentCode) ?? LanguageCatalog.Empty(CurrentCode);

            if (overrides.Catalogs.TryGetValue(CurrentCode, out var community))
                active = CatalogMerge.Overlay(active, community);

            Strings.Use(new Localizer(active, english));

            Available =
            [
                .. codes.Select(code =>
                {
                    // A Languages file may be a correction of a few lines with no header at all, so
                    // its names are only used when it actually declares them.
                    var over = overrides.Catalogs.GetValueOrDefault(code);
                    var shipped = EmbeddedCatalogs.Load(code);

                    return new LanguageOption(
                        code,
                        Named(over?.NativeName, shipped?.NativeName, code),
                        Named(over?.EnglishName, shipped?.EnglishName, code));
                }).OrderBy(option => option.EnglishName, StringComparer.Ordinal),
            ];

            CultureInfo.CurrentCulture = Strings.Current.Culture;
            CultureInfo.CurrentUICulture = Strings.Current.Culture;
            CultureInfo.DefaultThreadCurrentCulture = Strings.Current.Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Strings.Current.Culture;

            Application.Current.Resources["AlkUiFontFamily"] =
                new FontFamily(LanguageFonts.FamilyFor(CurrentCode));
            Application.Current.Resources["ContentControlThemeFontFamily"] =
                new FontFamily(LanguageFonts.FamilyFor(CurrentCode));

            LoggingService.Log($"Language: {CurrentCode}");
        }

        // Saves the choice and restarts. BEM registers a single-instance key, so launching a second
        // process and exiting would have the new one redirect into the dying old one; AppInstance
        // .Restart is the supported way through that. Returns false when Windows refused, which
        // leaves the choice saved and applied on the next manual start.
        public static bool Choose(string code)
        {
            Store.Write(new LanguagePreference(code));

            var reason = AppInstance.Restart(string.Empty);

            LoggingService.Log($"Restart for language {code} was refused: {reason}", LogLevel.Warn);
            return false;
        }

        private static string Named(string? preferred, string? shipped, string code) =>
            string.IsNullOrWhiteSpace(preferred)
                ? string.IsNullOrWhiteSpace(shipped) ? code : shipped
                : preferred;

        private static string OverrideFolder()
        {
            var exe = Environment.ProcessPath;

            return string.IsNullOrEmpty(exe)
                ? CatalogOverrides.FolderName
                : Path.Combine(Path.GetDirectoryName(exe)!, CatalogOverrides.FolderName);
        }
    }
}
