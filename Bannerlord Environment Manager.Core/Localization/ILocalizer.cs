using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Localization;

public interface ILocalizer
{
    string LanguageCode { get; }

    CultureInfo Culture { get; }

    string this[string key] { get; }

    string Format(string key, params object?[] args);

    string Plural(string key, long count, params object?[] args);
}
