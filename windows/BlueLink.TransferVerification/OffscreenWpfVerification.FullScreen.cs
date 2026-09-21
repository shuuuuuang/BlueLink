using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Localization;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyPreviewFullScreen(ImagePreviewWindow window, FrameworkElement root,
        string directory, string output, string language, string theme)
    {
        var full = (Button)window.FindName("FullScreenButton");
        var close = (Button)window.FindName("PreviewCloseButton");
        var icon = (SymbolIcon)window.FindName("FullScreenIcon");
        var fullBounds = full.TransformToAncestor(root).TransformBounds(new Rect(full.RenderSize));
        var closeBounds = close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize));
        Check(full.ActualWidth == 32 && full.ActualHeight == 32 &&
            System.Math.Abs(closeBounds.Left - fullBounds.Right - 8) < 1 && System.Math.Abs(fullBounds.Top - closeBounds.Top) < 1,
            "fullscreen sits immediately left of close, with equal size and eight DIP gap");
        Check(AutomationProperties.GetName(full) == Strings.Get("全屏") && full.ToolTip?.ToString()?.Contains("F11") == true &&
            icon.Symbol == SymbolRegular.FullScreenMaximize24, "fullscreen entry icon and localized accessible label");
        if (language == "zh-CN") CapturePreviewHeaderIcons(root, full, close, output, "normal-" + theme);
        var state = window.WindowState; var style = window.WindowStyle; var resize = window.ResizeMode;
        window.Left = 73; window.Top = 91;
        var width = window.Width; var height = window.Height;
        var corners = window.WindowCornerPreference; var border = window.BorderThickness;
        var scale = ((System.Windows.Controls.TextBlock)window.FindName("ZoomText")).Text;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(full);
        Check(peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "fullscreen exposes UI Automation Invoke");
        ((IInvokeProvider)peer!.GetPattern(PatternInterface.Invoke)).Invoke(); DrainDispatcher();
        Check(window.IsFullScreen && window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.NoResize &&
            WindowChrome.GetWindowChrome(window) is null && window.BorderThickness == new Thickness(0), "fullscreen removes native resize frame");
        Check(window.Width == SystemParameters.PrimaryScreenWidth && window.Height == SystemParameters.PrimaryScreenHeight &&
            window.Left == 0 && window.Top == 0, "offscreen fullscreen uses the complete monitor bounds");
        Check(AutomationProperties.GetName(full) == Strings.Get("退出全屏") && icon.Symbol == SymbolRegular.FullScreenMinimize24,
            "fullscreen control switches to exit icon and localized label");
        Capture(root, output, $"preview-fullscreen-{language}-{theme}", 1280, 800);
        if (language == "zh-CN") CapturePreviewHeaderIcons(root, full, close, output, "fullscreen-" + theme);
        var store = new WindowPreferencesStore(directory, "preview");
        var savedBefore = store.Read();
        var sizing = (WindowSizePersistence)typeof(ImagePreviewWindow).GetField("_windowSizing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        typeof(WindowSizePersistence).GetMethod("Flush", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(sizing, null);
        Check(store.Read() == savedBefore && savedBefore?.Width != window.Width, "fullscreen dimensions never overwrite remembered normal size");
        Check(!window.HandleFullScreenKey(Key.F11, ModifierKeys.Control) && window.IsFullScreen,
            "modified F11 is left to its original handler");
        Check(window.HandleFullScreenKey(Key.F11, ModifierKeys.None, repeat: true) && window.IsFullScreen,
            "holding F11 does not oscillate fullscreen state");
        Check(window.HandleFullScreenKey(Key.Escape, ModifierKeys.None) && !window.IsFullScreen,
            "first Escape exits fullscreen without closing the preview");
        Check(window.Left == 73 && window.Top == 91 && window.Width == width && window.Height == height &&
            window.WindowState == state && window.WindowStyle == style && window.ResizeMode == resize &&
            window.WindowCornerPreference == corners && window.BorderThickness == border,
            "exit restores placement, dimensions, state and window decoration");
        Check(!window.HandleFullScreenKey(Key.Escape, ModifierKeys.None), "normal Escape continues to existing close command");
        Check(window.HandleFullScreenKey(Key.F11, ModifierKeys.None) && window.IsFullScreen, "F11 enters fullscreen");
        full.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!window.IsFullScreen && icon.Symbol == SymbolRegular.FullScreenMaximize24, "exit button restores normal mode");
        window.WindowState = WindowState.Maximized;
        full.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(window.IsFullScreen, "an already maximized preview can enter fullscreen");
        full.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!window.IsFullScreen && window.WindowState == WindowState.Maximized, "exit restores previously maximized state");
        window.WindowState = state;
        Layout(root, 720, 480);
        Check(AutomationProperties.GetName(full) == Strings.Get("全屏") && close.Visibility == Visibility.Visible,
            "fullscreen toggle and close remain accessible after repeated transitions");
        Check(new WindowInteropHelper(window).Handle == System.IntPtr.Zero, "fullscreen verification never creates a native window");
    }
    private void CapturePreviewHeaderIcons(FrameworkElement root, Button full, Button close, string output, string state)
    {
        var bounds = full.TransformToAncestor(root).TransformBounds(new Rect(full.RenderSize));
        bounds.Union(close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize)));
        bounds.Inflate(8, 8);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * 1.5),
            (int)(root.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var crop = new System.Windows.Media.Imaging.CroppedBitmap(bitmap,
            new Int32Rect((int)(bounds.X * 1.5), (int)(bounds.Y * 1.5), (int)(bounds.Width * 1.5), (int)(bounds.Height * 1.5)));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
        var name = "preview-header-icons-" + state;
        using var stream = System.IO.File.Create(System.IO.Path.Combine(output, name + ".png"));
        encoder.Save(stream);
        _images.Add(new { name, width = crop.PixelWidth, height = crop.PixelHeight, dpi = 144 });
    }

}
