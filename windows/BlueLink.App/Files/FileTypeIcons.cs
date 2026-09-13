using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BlueLink.Files;

public sealed class FileTypeIcons : IValueConverter
{
    private static readonly Dictionary<string, DrawingImage> Images = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ForKey(value as string ?? "FileTypeIcon-file");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    public static ImageSource? ForFile(string? name, string? mimeType = null) =>
        ForKey("FileTypeIcon-" + FileTypeCatalog.Classify(name, mimeType));

    private static DrawingImage? ForKey(string key)
    {
        if (Application.Current is not { } app) return null;
        if (Images.TryGetValue(key, out var existing)) return existing;
        if (app.TryFindResource(key) is not DrawingImage source) return null;
        // A shared DrawingImage's nested DynamicResource may retain the original dictionary's
        // palette. Keep a mutable view-owned copy so already visible bindings follow theme changes.
        var image = source.CloneCurrentValue();
        ApplyColor(app, key, image);
        Images[key] = image;
        return image;
    }

    internal static void ApplyPalette(Application app)
    {
        foreach (var (key, image) in Images) ApplyColor(app, key, image);
    }

    private static void ApplyColor(Application app, string key, DrawingImage image)
    {
        var color = ((SolidColorBrush)app.FindResource(key.Replace("FileTypeIcon-", "FileTypeBrush-"))).Color;
        var drawing = (GeometryDrawing)((DrawingGroup)image.Drawing).Children[0];
        drawing.Brush = new SolidColorBrush(color);
    }
}
