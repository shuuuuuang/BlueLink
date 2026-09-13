using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace BlueLink.Localization;

public sealed class Strings : INotifyPropertyChanged
{
    public static Strings Current { get; } = new();
    private static readonly IReadOnlyDictionary<string, string> English = LoadCatalog("en-US");
    private static readonly IReadOnlyDictionary<string, string> Traditional = LoadCatalog("zh-TW");
    public static string Language { get; private set; } = "zh-CN";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Get(key);

    public static string Get(string key) => (Language == "en-US" ? English : Language == "zh-TW" ? Traditional : null)?.GetValueOrDefault(key) ?? key;
    public static string Format(FormattableString text) => string.Format(
        CultureInfo.GetCultureInfo(Language), Get(text.Format), text.GetArguments());

    public static void Apply(string language)
    {
        var normalized = Appearance.AppearancePreferences.NormalizeLanguage(language);
        if (Language == normalized) return;
        Language = normalized;
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public static IReadOnlyDictionary<string, string> EnglishCatalog => English;

    private static IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream($"BlueLink.Localization.{language}.json")
            ?? throw new InvalidOperationException("The English language resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The English language resource is invalid.");
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string key) => Key = key;
    public string Key { get; }
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = Strings.Current,
        Path = new System.Windows.PropertyPath("[(0)]", Key),
        Mode = BindingMode.OneWay,
    }.ProvideValue(serviceProvider);
}
