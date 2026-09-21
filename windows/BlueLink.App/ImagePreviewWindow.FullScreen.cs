using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class ImagePreviewWindow
{
    private readonly Appearance.WindowSizePersistence _windowSizing;
    private FullScreenRestore? _fullScreenRestore;
    internal bool IsFullScreen => _fullScreenRestore is not null;

    private sealed record FullScreenRestore(double Left, double Top, double Width, double Height,
        double MinWidth, double MinHeight, WindowState State, WindowStyle Style, ResizeMode Resize,
        Thickness Border, WindowCornerPreference Corners, bool Extended, WindowChrome? Chrome);

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    internal void ToggleFullScreen()
    {
        if (IsFullScreen) { ExitFullScreen(); return; }
        var handle = new WindowInteropHelper(this).Handle;
        System.Drawing.Rectangle? monitor = handle == IntPtr.Zero ? null : System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        _windowSizing.Suspend();
        _fullScreenRestore = new(Left, Top, Width, Height, MinWidth, MinHeight, WindowState,
            WindowStyle, ResizeMode, BorderThickness, WindowCornerPreference,
            ExtendsContentIntoTitleBar, (WindowChrome?)WindowChrome.GetWindowChrome(this)?.CloneCurrentValue());
        try
        {
            WindowState = WindowState.Normal;
            ExtendsContentIntoTitleBar = false;
            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            BorderThickness = new Thickness(0);
            WindowCornerPreference = WindowCornerPreference.DoNotRound;
            MinWidth = MinHeight = 0;
            ApplyFullScreenBounds(monitor);
            UpdateFullScreenButton();
        }
        catch
        {
            ExitFullScreen();
            PreviewToasts.Show(Localization.Strings.Get("无法进入全屏，请重试"), ToastLevel.Error);
        }
    }

    private void ApplyFullScreenBounds(System.Drawing.Rectangle? monitor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            // Offscreen verification must never create an HWND.
            Left = Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            return;
        }
        // Physical monitor bounds include the taskbar and avoid mixed-DPI coordinate conversion.
        var bounds = monitor ?? System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        if (!SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            0x0020 | 0x0040)) // FRAMECHANGED | SHOWWINDOW; raise within the non-topmost band.
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void ExitFullScreen()
    {
        if (_fullScreenRestore is not { } saved) return;
        try
        {
            WindowState = WindowState.Normal;
            WindowStyle = saved.Style;
            ResizeMode = saved.Resize;
            ExtendsContentIntoTitleBar = saved.Extended;
            WindowChrome.SetWindowChrome(this, saved.Chrome);
            BorderThickness = saved.Border;
            WindowCornerPreference = saved.Corners;
            MinWidth = saved.MinWidth; MinHeight = saved.MinHeight;
            Left = saved.Left; Top = saved.Top;
            Width = saved.Width; Height = saved.Height;
            WindowState = saved.State;
        }
        finally
        {
            _fullScreenRestore = null;
            _windowSizing.Resume();
            UpdateFullScreenButton();
        }
    }

    private void UpdateFullScreenButton()
    {
        FullScreenIcon.Symbol = IsFullScreen ? SymbolRegular.FullScreenMinimize24 : SymbolRegular.FullScreenMaximize24;
        var title = Localization.Strings.Get(IsFullScreen ? "退出全屏" : "全屏");
        FullScreenButton.ToolTip = title + " (F11)";
        AutomationProperties.SetName(FullScreenButton, title);
    }

    private void PreviewWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleFullScreenKey(e.Key, Keyboard.Modifiers, e.IsRepeat)) e.Handled = true;
    }

    internal bool HandleFullScreenKey(Key key, ModifierKeys modifiers, bool repeat = false)
    {
        if (modifiers != ModifierKeys.None) return false;
        if (key == Key.F11)
        {
            if (!repeat) ToggleFullScreen();
            return true;
        }
        if (key == Key.Escape && IsFullScreen)
        {
            ExitFullScreen();
            return true;
        }
        return false;
    }

    private void PreviewTitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsFullScreen) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        e.Handled = true; // Keep title dragging/double-click from moving the full-screen window.
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
