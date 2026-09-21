using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Localization;
using BlueLink.Storage;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyImagePreviewDesign(string dataRoot, string output)
    {
        var directory = Path.Combine(dataRoot, "image-preview");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "transparent.png");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 73, 145, 214)), null, new Point(800, 500), 500, 330);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(160, 60, 199, 166)), null,
                new Rect(260, 550, 1080, 240), 70, 70);
        }
        var bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(sourcePath)) encoder.Save(stream);
        var originalLanguage = Strings.Language;
        var originalTheme = AppearanceService.CurrentTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark ? "dark" : "light";
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            foreach (var theme in new[] { "light", "dark" })
            {
                var settings = BlueLinkSettings.Defaults(directory) with { Language = language, Theme = theme };
                AppearanceService.Apply(settings);
                var title = language == "zh-CN" ? "蓝联_带透明背景的图片_用于验证长文件名在窄窗口下省略但保留完整提示.png"
                    : "BlueLink_transparent_image_with_a_long_filename_for_preview_layout_verification.png";
                var window = new ImagePreviewWindow(sourcePath, title, directory);
                try
                {
                    var root = DetachForRendering(window);
                    Layout(root, 1000, 700);
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    VerifyFocusAndPointer(root, "preview-" + language + "-" + theme);
                    var tools = (StackPanel)window.FindName("PreviewTools");
                    var footer = (Border)window.FindName("PreviewFooter");
                    var close = (Button)window.FindName("PreviewCloseButton");
                    var viewport = (Border)window.FindName("ImageViewport");
                    var image = (Image)window.FindName("PreviewImage");
                    var zoom = (TextBlock)window.FindName("ZoomText");
                    var fit = (Button)window.FindName("FitButton");
                    var actual = (Button)window.FindName("ActualSizeButton");
                    var left = (Button)window.FindName("RotateLeftButton");
                    var right = (Button)window.FindName("RotateRightButton");
                    var reset = (Button)window.FindName("ResetButton");
                    var transform = (MatrixTransform)image.RenderTransform;
                    var actions = new[] { fit, actual, left, right, reset };
                    var names = new[] { "适应窗口", "一比一显示", "向左旋转", "向右旋转", "重置图片预览" };
                    foreach (var (width, height) in new[] { (1000, 700), (900, 480), (899, 480), (720, 480), (1320, 820), (720, 480), (1000, 700) })
                    {
                        Layout(root, width, height);
                        var scene = $"preview-{language}-{theme}-{width}";
                        var compact = width < 900;
                        Check(Equals(tools.Tag, compact) && actions.All(b => b.ActualHeight == 32 && (!compact || b.ActualWidth == 32)),
                            "image preview: all five tools become square below the breakpoint: " + scene);
                        Check(new[] { "FitLabel", "RotateLeftLabel", "RotateRightLabel", "ResetLabel" }
                                .Select(name => (TextBlock)window.FindName(name)).All(label => label.Visibility == (compact ? Visibility.Collapsed : Visibility.Visible)),
                            "image preview: labels restore after repeated width changes: " + scene);
                        var bounds = actions.Select(b => b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize))).ToArray();
                        var groupBounds = tools.TransformToAncestor(root).TransformBounds(new Rect(tools.RenderSize));
                        Check(footer.ActualHeight == 56 && groupBounds.Left >= 14 && groupBounds.Right <= width - 14 &&
                            Math.Abs(groupBounds.Left + groupBounds.Width / 2 - width / 2d) < 1 && bounds.All(b => Math.Abs(b.Top - bounds[0].Top) < 1),
                            "image preview: toolbar remains one centered row with a fixed footer: " + scene);
                        Check(bounds.Zip(bounds.Skip(1)).All(pair => pair.Second.Left >= pair.First.Right + 7),
                            "image preview: neighboring buttons retain space without overlap: " + scene);
                        var closeBounds = close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize));
                        var titleText = (TextBlock)window.FindName("TitleBarFileName");
                        var titleBounds = titleText.TransformToAncestor(root).TransformBounds(new Rect(titleText.RenderSize));
                        Check(close.ActualWidth == 32 && close.ActualHeight == 32 && Math.Abs(width - closeBounds.Right - 20) < 1 &&
                            Math.Abs(closeBounds.Top + closeBounds.Height / 2 - 32) < 1 && titleBounds.Right <= ((Button)window.FindName("FullScreenButton")).TransformToAncestor(root).TransformBounds(new Rect(0, 0, 32, 32)).Left - 11 &&
                            titleText.Text == title && titleText.ToolTip as string == title && titleText.TextTrimming == TextTrimming.CharacterEllipsis,
                            "image preview: close button is centered and inset, long filename cannot cover it: " + scene);
                        Check(actions.Select((b, i) => AutomationProperties.GetName(b) == Strings.Get(names[i]) && b.ToolTip is string tip && tip.Length > 0).All(v => v),
                            "image preview: icon buttons retain localized tooltips and accessible names: " + scene);
                        var extent = transform.TransformBounds(new Rect(0, 0, image.Width, image.Height));
                        Check(fit.Tag is true && extent.Left >= 27 && extent.Top >= 27 && extent.Right <= viewport.ActualWidth - 27 && extent.Bottom <= viewport.ActualHeight - 27,
                            "image preview: fit mode updates image bounds when viewport changes: " + scene);
                        Check(!Descendants<TextBlock>(root).Any(t => t.Visibility == Visibility.Visible && t.Text.Contains("Esc")) &&
                            !Descendants<Button>(root).Any(b => new[] { "另存为", "在资源管理器中打开", "复制文件名", "Copy file name", "複製檔案名稱", "Save as", "Show in Explorer" }.Contains(AutomationProperties.GetName(b))),
                            "image preview: Escape hint and duplicate file actions are absent: " + scene);
                    }
                    Capture(root, output, $"preview-design-{language}-{theme}-wide", 1000, 700);
                    Capture(root, output, $"preview-design-{language}-{theme}-compact", 720, 480);
                    actual.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    left.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Layout(root, 720, 480);
                    Check(zoom.Text == "100%" && actual.Tag is true && Math.Abs(transform.Matrix.M12 + 1) < .001,
                        "image preview: left rotation turns the image anticlockwise at actual size");
                    var rotation = (RotateTransform)window.FindName("NavigatorRotation");
                    var matrixBefore = transform.Matrix;
                    foreach (var width in new[] { 900, 1000, 899, 720 }) Layout(root, width, 480);
                    Check(zoom.Text == "100%" && actual.Tag is true && Math.Abs(transform.Matrix.M12 - matrixBefore.M12) < .001 &&
                        Math.Abs(transform.Matrix.OffsetX - matrixBefore.OffsetX) < 1 && Math.Abs(transform.Matrix.OffsetY - matrixBefore.OffsetY) < 1,
                        "image preview: crossing breakpoint preserves scale, angle and image position when space returns");
                    right.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Check(Math.Abs(transform.Matrix.M12) < .001 && Math.Abs(transform.Matrix.M11 - 1) < .001,
                        "image preview: right rotation reverses left rotation without changing actual size");
                    var navigator = (Border)window.FindName("Navigator");
                    var navigatorCanvas = (Canvas)window.FindName("NavigatorCanvas");
                    var crop = (Border)window.FindName("NavigatorCrop");
                    Check(navigator.Visibility == Visibility.Visible && navigator.Width == 176 && navigator.Height == 120 &&
                        ReferenceEquals(viewport.Background, navigatorCanvas.Background) && ((SolidColorBrush)crop.Background).Color.A == 31,
                        "image preview: main view and navigator share checkerboard and the crop tint stays translucent");
                    VerifyPreviewChecker(viewport, theme);
                    Capture(root, output, $"preview-design-{language}-{theme}-zoomed", 720, 480);
                    AppearanceService.Apply(settings with { Theme = theme == "dark" ? "light" : "dark" });
                    Layout(root, 720, 480);
                    VerifyPreviewChecker(viewport, theme == "dark" ? "light" : "dark");
                    Check(zoom.Text == "100%" && navigator.Visibility == Visibility.Visible,
                        "image preview: changing theme does not reset viewport state");
                    AppearanceService.Apply(settings);
                    reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Check(fit.Tag is true && actual.Tag is false && navigator.Visibility == Visibility.Collapsed && rotation.Angle == 0,
                        "image preview: reset restores upright fit mode and hides the navigator");
                    Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && PresentationSource.FromVisual(root) is null,
                        "image preview: rendering and command checks never create a native window");
                    VerifyPreviewNavigation(window, root, sourcePath, directory, output, language, theme);
                    VerifyPreviewFullScreen(window, root, directory, output, language, theme);
                    var escaped = false;
                    window.Closed += (_, _) => escaped = true;
                    var escape = window.InputBindings.OfType<KeyBinding>().Single();
                    Check(escape.Key == Key.Escape && escape.Command == close.Command,
                        "image preview: Escape remains bound to the same command as the close button");
                    CommandManager.InvalidateRequerySuggested();
                    DrainDispatcher();
                    var closePeer = FrameworkElementAutomationPeer.CreatePeerForElement(close);
                    Check(close.IsEnabled && closePeer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
                        "image preview: close is enabled and exposes UI Automation Invoke without keyboard focus");
                    ((IInvokeProvider)closePeer!.GetPattern(PatternInterface.Invoke)).Invoke();
                    DrainDispatcher();
                    Check(escaped, "image preview: UI Automation invokes the close button and dismisses the window");
                    var closingPreview = new ImagePreviewWindow(sourcePath, title, directory);
                    try
                    {
                        var closingRoot = DetachForRendering(closingPreview);
                        Layout(closingRoot, 1000, 700);
                        closingPreview.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        closingPreview.ToggleFullScreen();
                        var preferences = new WindowPreferencesStore(directory, "preview");
                        var beforeClose = preferences.Read();
                        closingPreview.Close();
                        Check(beforeClose is not null && preferences.Read() == beforeClose,
                            "closing directly from fullscreen preserves the pre-fullscreen size preference");
                        var reopened = new ImagePreviewWindow(sourcePath, title, directory);
                        Check(!reopened.IsFullScreen && reopened.WindowStyle != WindowStyle.None && reopened.ResizeMode != ResizeMode.NoResize,
                            "reopened preview uses ordinary window mode after a fullscreen close");
                        reopened.Close();
                    }
                    finally { closingPreview.Close(); }
                }
                finally { window.Close(); }
            }
        }
        finally { AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Language = originalLanguage, Theme = originalTheme }); }
    }

    private void VerifyPreviewChecker(Border viewport, string theme)
    {
        var brush = (DrawingBrush)viewport.Background;
        var drawings = ((DrawingGroup)brush.Drawing).Children.OfType<GeometryDrawing>().ToArray();
        var colors = drawings.Select(d => ((SolidColorBrush)d.Brush).Color).ToArray();
        var expected = theme == "dark" ? new[] { "#FF232B38", "#FF2B3544" } : new[] { "#FFF6F8FB", "#FFE4E9F0" };
        Check(colors.Select(c => c.ToString()).SequenceEqual(expected) && brush.Viewport == new Rect(0, 0, 56, 56) && brush.TileMode == TileMode.Tile,
            "image preview: 28 DIP checkerboard resolves both theme colors: " + theme);
    }
}
