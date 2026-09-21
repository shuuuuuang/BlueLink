using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BlueLink;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyPreviewNavigation(ImagePreviewWindow window, FrameworkElement root, string first, string directory, string output, string language, string theme)
    {
        var second = Path.Combine(directory, "gallery-second.png");
        var stale = Path.Combine(directory, "gallery-stale.png");
        File.Copy(first, second, true); File.Copy(first, stale, true);
        var title = (TextBlock)window.FindName("TitleBarFileName");
        var image = (Image)window.FindName("PreviewImage");
        var fit = (Button)window.FindName("FitButton");
        var previous = (Button)window.FindName("PreviousImageButton");
        var next = (Button)window.FindName("NextImageButton");
        var zone = (Border)window.FindName("NextImageZone");
        Check(zone.Visibility == Visibility.Collapsed, "standalone image has no gallery hotspots");
        window.SetGallery([new(first, title.Text), new(stale, "stale.png"), new(second, "gallery-second.png")]);
        File.Delete(stale);
        Layout(root, 1000, 700);
        Check(zone.Visibility == Visibility.Visible && previous.Opacity == 0 && next.Opacity == 0 && !previous.Focusable && !next.Focusable, "gallery arrows are initially invisible");
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        zone.SetValue(hoverKey, true); DrainDispatcher();
        Check(next.Opacity == 1 && previous.Opacity == 0, "only the approached edge reveals its arrow");
        Capture(root, output, $"preview-gallery-hover-{language}-{theme}", 1000, 700);
        if (language == "zh-CN" && theme == "light") CaptureArrowBackdrops(window, root, output);
        window.LoadZoomedVisualFixture();
        ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke(); DrainDispatcher();
        Check(title.Text == "gallery-second.png" && ((BitmapImage)image.Source).UriSource.LocalPath == second && fit.Tag is true,
            "UIA next skips a removed file, replaces pixels and title, and resets zoom");
        var menu = image.ContextMenu;
        Check(image.IsHitTestVisible && menu is not null, "actual preview image exposes its right-click menu");
        menu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        var menuItems = menu.Items.OfType<MenuItem>().ToArray();
        Check(menuItems.Select(item => item.Tag as string).SequenceEqual(new[] { "copy", "copy-name", "save", "locate" }), "preview menu contains exactly the four requested operations in order");
        Check(menuItems.All(item => item.IsEnabled && !string.IsNullOrEmpty(new MenuItemAutomationPeer(item).GetName())), "preview actions have usable localized UI Automation names");
        Check(menu.DataContext is BlueLink.Domain.ChatAttachment current && current.LocalPath == second && current.FileName == "gallery-second.png", "preview file actions follow the navigated image path and filename");
        menu.Width = 320; menu.Height = 230;
        Capture(menu, output, $"preview-file-menu-{language}-{theme}", 320, 230);
        File.Delete(second);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(menuItems.Single(item => item.Tag as string == "copy-name").IsEnabled && menuItems.Where(item => item.Tag as string != "copy-name").All(item => !item.IsEnabled), "removed preview file disables disk operations while keeping filename copy");
        File.Copy(first, second);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(menuItems.All(item => item.IsEnabled), "available preview file restores its actions");
        var finalImage = image.Source;
        Check(!window.NavigateImage(1) && ReferenceEquals(image.Source, finalImage), "last image does not wrap or lose its bitmap");
        ((IInvokeProvider)new ButtonAutomationPeer(previous).GetPattern(PatternInterface.Invoke)).Invoke(); DrainDispatcher();
        Check(((BitmapImage)image.Source).UriSource.LocalPath == first, "previous returns through the same gallery ordering");
        Check(menu.DataContext is BlueLink.Domain.ChatAttachment restored && restored.LocalPath == first, "previous-image navigation also refreshes file actions");
        Check(!window.NavigateImage(-1), "first image remains at the first boundary");
        zone.SetValue(hoverKey, false); DrainDispatcher();
        Check(next.Opacity == 0, "arrow hides again when pointer leaves its zone");
        window.SetGallery([new(first, title.Text)]);
        Check(zone.Visibility == Visibility.Collapsed, "one-item gallery hides navigation controls");
    }
    private void CaptureArrowBackdrops(ImagePreviewWindow window, FrameworkElement root, string output)
    {
        var arrow = (FrameworkElement)((Button)window.FindName("NextImageButton")).Content;
        var tiles = new System.Windows.Media.DrawingGroup();
        tiles.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.White, null,
            new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 28, 28))));
        var gray = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200));
        foreach (var rect in new[] { new Rect(0, 0, 14, 14), new Rect(14, 14, 14, 14) })
            tiles.Children.Add(new System.Windows.Media.GeometryDrawing(gray, null, new System.Windows.Media.RectangleGeometry(rect)));
        var checker = new System.Windows.Media.DrawingBrush(tiles) {
            TileMode = System.Windows.Media.TileMode.Tile, ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewport = new Rect(-2.0 / 1.5, -17.0 / 1.5, 28, 28), Stretch = System.Windows.Media.Stretch.None
        };
        foreach (var (name, brush, width, height, x, y) in new (string, System.Windows.Media.Brush, int, int, double, double)[] {
            ("arrow-reference-checker", checker, 103, 200, 27, 41),
            ("arrow-reference-black", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)), 90, 156, 22, 22) })
        {
            // Render the actual button content, at reference scale and position, without image post-processing.
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(brush, null, new Rect(0, 0, width / 1.5, height / 1.5));
                var content = new System.Windows.Media.VisualBrush(arrow) {
                    ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                    Viewbox = new Rect(-2, -2, 28, 64), Stretch = System.Windows.Media.Stretch.Fill
                };
                context.DrawRectangle(content, null, new Rect(x - 2, y - 2, 28, 64));
            }
            var bitmap = new RenderTargetBitmap(width, height, 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            _images.Add(new { name, width, height, dpi = 144 });
        }
    }
}
