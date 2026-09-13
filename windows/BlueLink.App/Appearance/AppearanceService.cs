using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink.Storage;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace BlueLink.Appearance;

public static class AppearanceService
{
    private static string _preference = "system";
    private static bool _watching;
    public static ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Light;

    public static void Apply(BlueLinkSettings settings)
    {
        _preference = AppearancePreferences.NormalizeTheme(settings.Theme);
        Localization.Strings.Apply(settings.Language);
        ApplyTheme();
    }

    public static void StartWatching()
    {
        if (_watching) return;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        _watching = true;
    }

    public static void StopWatching()
    {
        if (!_watching) return;
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        _watching = false;
    }

    private static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (_preference != "system" || Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher) return;
        dispatcher.BeginInvoke(new Action(() => { if (_preference == "system") ApplyTheme(); }));
    }

    private static void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.Dispatcher.VerifyAccess();
        SystemThemeManager.UpdateSystemThemeCache();
        CurrentTheme = _preference switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
                SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
                _ => ApplicationTheme.Light,
            },
        };
        // Change the official resource dictionary without creating or activating a window.
        var themes = app.Resources.MergedDictionaries.OfType<ThemesDictionary>().Single();
        themes.Theme = CurrentTheme;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/BlueLink;component/Themes/Palette.{(CurrentTheme == ApplicationTheme.Dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        foreach (System.Collections.DictionaryEntry entry in palette) app.Resources[entry.Key] = entry.Value;
        if (CurrentTheme == ApplicationTheme.HighContrast) ApplyHighContrast(app.Resources);
        ApplyIconPalette(app);
        Files.FileTypeIcons.ApplyPalette(app);
    }

    private static void ApplyIconPalette(Application app)
    {
        var dictionary = app.Resources.MergedDictionaries.Single(value => value.Source?.OriginalString.EndsWith("ThemedIcons.xaml", StringComparison.Ordinal) == true);
        foreach (var key in dictionary.Keys.Cast<string>())
        {
            if (key == "FigmaIcon-usb-ready")
            {
                // This asset is the original Figma vector, not a PNG opacity mask.
                var vector = (DrawingGroup)((DrawingImage)dictionary[key]).Drawing.CloneCurrentValue();
                ((GeometryDrawing)vector.Children[1]).Brush = (Brush)app.FindResource("BlueBrush");
                dictionary[key] = new DrawingImage(vector);
                continue;
            }
            var active = key.EndsWith("-active", StringComparison.Ordinal);
            var name = key["FigmaIcon-".Length..];
            if (active) name = name[..^"-active".Length];
            var assetName = name == "file" ? "file-outline" : name;
            var brushName = active ? "SettingsBlueBrush"
                : name is "message-sending" or "message-read" or "drop-send" or "file-empty" or "file-filter-empty" ? "BlueBrush"
                : name == "drop-blocked" ? "WarningBrush"
                : name is "message-waiting" or "message-delivered" or "file-search-empty" or "message-search-empty" or "device-search-empty" ? "MutedBrush"
                : name is "menu-trash" or "menu-shieldx" or "menu-cancel" or "message-failed" ? "DangerBrush" : name.StartsWith("menu-", StringComparison.Ordinal) ? "MutedBrush" : "InkBrush";
            var bitmap = new BitmapImage(new Uri($"pack://application:,,,/BlueLink;component/Assets/Figma/{assetName}.png"));
            var drawing = new DrawingGroup { OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Fill } };
            drawing.Children.Add(new GeometryDrawing((Brush)app.FindResource(brushName), null,
                new RectangleGeometry(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight))));
            var image = new DrawingImage(drawing);
            image.Freeze();
            app.Resources[key] = image;
        }
    }

    private static void ApplyHighContrast(ResourceDictionary resources)
    {
        foreach (var key in resources.Keys.Cast<object>().OfType<string>().Where(key => key.EndsWith("Brush", StringComparison.Ordinal)).ToArray())
        {
            var color = key == "OnAccentBrush" ? SystemColors.HighlightTextColor
                : key == "PrimaryBrush" || key.Contains("Blue", StringComparison.Ordinal) && !key.Contains("Soft", StringComparison.Ordinal)
                ? SystemColors.HighlightColor
                : key.StartsWith("Text", StringComparison.Ordinal) || key.Contains("Ink", StringComparison.Ordinal) || key.Contains("Muted", StringComparison.Ordinal) ||
                  key.Contains("Border", StringComparison.Ordinal) || key.Contains("Danger", StringComparison.Ordinal) ||
                  key.Contains("Success", StringComparison.Ordinal) || key.Contains("Warning", StringComparison.Ordinal)
                    ? SystemColors.WindowTextColor : SystemColors.WindowColor;
            resources[key] = new SolidColorBrush(color);
        }
    }
}
