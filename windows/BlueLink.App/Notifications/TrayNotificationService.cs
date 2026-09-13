using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using BlueLink.Localization;
using Forms = System.Windows.Forms;

namespace BlueLink.Notifications;

/// <summary>Created only by the production App startup. Tests exercise its center and view without the shell.</summary>
internal sealed class TrayNotificationService : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly NotificationCenter _center;
    private readonly TrayConversationView _view;
    private readonly Popup _popup;
    private readonly Icon _originalIcon;
    private Icon? _badgedIcon;
    private readonly DispatcherTimer _hover, _close;
    private int _count;
    private bool _disposed;
    private System.Drawing.Point _anchor;
    public TrayNotificationService(Forms.NotifyIcon tray, MainWindow window)
    {
        _tray = tray; _window = window; _center = window.ViewModel.Notifications;
        _originalIcon = (Icon)(tray.Icon ?? SystemIcons.Application).Clone();
        _view = new(_center);
        _popup = new Popup { Child = _view, Placement = PlacementMode.MousePoint, StaysOpen = true, AllowsTransparency = true,
            HorizontalOffset = -384, VerticalOffset = -280 };
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _hover.Tick += (_, _) => { _hover.Stop(); var point = Forms.Control.MousePosition; if (Math.Abs(point.X - _anchor.X) <= 16 && Math.Abs(point.Y - _anchor.Y) <= 16 && _center.UnreadCount > 0 && !_window.IsActive && !_disposed) { _popup.IsOpen = true; _close.Start(); } };
        _close.Tick += (_, _) => { _close.Stop(); if (!_view.IsMouseOver) _popup.IsOpen = false; };
        _view.MouseEnter += (_, _) => _close.Stop();
        _view.MouseLeave += (_, _) => _close.Start();
        _view.DismissRequested += Dismiss;
        _view.ConversationRequested += OpenConversation;
        _tray.MouseMove += TrayMouseMove;
        _center.Changed += Refresh;
        _window.ViewModel.SystemNotificationRequested += NotifyEvent;
        _window.Activated += Activated;
        Strings.Current.PropertyChanged += LanguageChanged;
        Refresh();
    }
    private void NotifyEvent(string title, string detail)
    {
        if (_disposed || _window.IsActive) return;
        _tray.ShowBalloonTip(5000, title, detail, Forms.ToolTipIcon.Info);
    }
    private void TrayMouseMove(object? sender, Forms.MouseEventArgs args) { _anchor = Forms.Control.MousePosition; if (_center.UnreadCount > 0 && !_popup.IsOpen && !_hover.IsEnabled) _hover.Start(); }
    private void Activated(object? sender, EventArgs args) { Dismiss(); Flash(false); }
    private void LanguageChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void Dismiss() { _hover.Stop(); _close.Stop(); _popup.IsOpen = false; }
    private async void OpenConversation(string peerId)
    {
        Dismiss();
        if (!_window.TryCloseSettings()) return;
        _window.Show(); if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate(); _window.ViewModel.ShowFiles = false;
        await _window.ViewModel.SelectConversationAsync(peerId);
    }
    private void Refresh()
    {
        if (_disposed) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.BeginInvoke(Refresh); return; }
        var count = _center.UnreadCount;
        var increasing = count > _count; _count = count;
        _view.RefreshText();
        var label = count > 0 ? _center.Title : Strings.Get("蓝联 · 设备间安全传输");
        _tray.Text = label.Length > 63 ? label[..63] : label;
        if (_tray.ContextMenuStrip is { Items.Count: >= 2 } menu)
        { menu.Items[0].Text = Strings.Get("打开蓝联"); menu.Items[1].Text = Strings.Get("退出"); }
        var previous = _badgedIcon;
        _badgedIcon = count > 0 ? CreateBadge(count, _originalIcon) : null;
        _tray.Icon = _badgedIcon ?? _originalIcon; previous?.Dispose();
        _window.TaskbarItemInfo ??= new TaskbarItemInfo();
        using var overlay = count > 0 ? CreateBadge(count) : null;
        _window.TaskbarItemInfo.Overlay = overlay is not null ? BadgeImage(overlay) : null;
        _window.TaskbarItemInfo.Description = label;
        if (count == 0) { Dismiss(); Flash(false); }
        else if (increasing && !_window.IsActive && _window.ViewModel.Settings.MessageNotifications) Flash(true);
    }
    private static Icon CreateBadge(int count, Icon? original = null)
    {
        // Only the count badge is drawn; the product icon remains the original resource.
        using var bitmap = new Bitmap(32, 32);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (original is not null) canvas.DrawIcon(original, new Rectangle(0, 0, 32, 32));
            var bounds = original is null ? new RectangleF(1, 1, 30, 30) : new RectangleF(15, 0, 17, 17);
            canvas.FillEllipse(System.Drawing.Brushes.Crimson, bounds);
            using var font = new Font("Segoe UI", original is null ? count > 9 ? 11 : 16 : count > 9 ? 7 : 10, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            canvas.DrawString(count > 99 ? "99+" : count.ToString(), font, System.Drawing.Brushes.White, bounds, format);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    private static ImageSource BadgeImage(Icon icon) => Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    private void Flash(bool enabled)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = enabled ? 2u : 0u, Count = enabled ? 3u : 0u, Timeout = 0 };
        FlashWindowEx(ref info);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Dismiss(); Flash(false);
        _window.ViewModel.SystemNotificationRequested -= NotifyEvent;
        _center.Changed -= Refresh; _window.Activated -= Activated; _tray.MouseMove -= TrayMouseMove;
        Strings.Current.PropertyChanged -= LanguageChanged;
        _view.DismissRequested -= Dismiss; _view.ConversationRequested -= OpenConversation;
        _tray.Icon = null; _badgedIcon?.Dispose(); _originalIcon.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
