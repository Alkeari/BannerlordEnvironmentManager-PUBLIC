namespace BannerlordEnvironmentManager.Localization
{
    // JetBrains Mono, Cascadia Mono and Consolas carry no CJK glyphs, so Chinese, Japanese and Korean
    // render as empty boxes without a fallback appended. The palette and the type ladder are
    // untouched: this only extends the family list, and only for those languages.
    public static class LanguageFonts
    {
        private const string Latin = "JetBrains Mono, Cascadia Mono, Consolas";

        public static string FamilyFor(string languageCode) => languageCode switch
        {
            "zh-Hans" => Latin + ", Microsoft YaHei, Segoe UI",
            "ja" => Latin + ", Yu Gothic UI, Meiryo, Segoe UI",
            "ko" => Latin + ", Malgun Gothic, Segoe UI",
            _ => Latin,
        };
    }
}
