namespace BlueLink.Appearance;

public static class AppearancePreferences
{
    public static string NormalizeTheme(string? value) => value is "light" or "dark" ? value : "system";
    public static string NormalizeLanguage(string? value) => value is "en" or "en-US" ? "en-US" : value == "zh-TW" ? "zh-TW" : "zh-CN";
}
