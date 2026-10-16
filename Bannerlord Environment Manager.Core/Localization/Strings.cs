namespace BannerlordEnvironmentManager.Core.Localization;

// The ambient localizer every migrated call site reads. Assigned once at startup by the app, and by
// tests that need another language. It is a static rather than an injected dependency because the
// XAML attached properties that read it have no constructor to inject into. Code that already receives its dependencies should take ILocalizer instead.
public static class Strings
{
    private static ILocalizer current = English();

    public static ILocalizer Current => current;

    public static void Use(ILocalizer localizer) =>
        current = localizer ?? throw new ArgumentNullException(nameof(localizer));

    public static void Reset() => current = English();

    private static ILocalizer English() => Localizer.EnglishOnly(EmbeddedCatalogs.English());
}
