using System.Windows;
using BlueLink.Notifications;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    // Startup-only component host. This does not claim to exercise Explorer's hover trigger.
    private static void ShowTrayFixture(MainWindow owner)
    {
        var center = owner.ViewModel.Notifications;
        center.Clear();
        center.Receive("00000000000000000000000000000002", "QA SURFACE-LAPTOP", Guid.NewGuid(), "文件接收完成", false);
        center.Receive("00000000000000000000000000000001", "QA REDMI K80 Pro", Guid.NewGuid(), "收到，我把需求文档发给你。", false);
        center.Receive("00000000000000000000000000000001", "QA REDMI K80 Pro", Guid.NewGuid(), "收到，我把需求文档发给你。", false);
        var view = new TrayConversationView(center);
        var preview = new Window
        {
            Title = "蓝联 · QA 托盘卡片", Content = view, Width = 400,
            MinWidth = 400, MaxWidth = 400, Height = 274, MinHeight = 274, MaxHeight = 274,
            Style = null, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        center.Changed += view.RefreshText;
        view.DismissRequested += preview.Close;
        view.ConversationRequested += async peerId =>
        {
            preview.Close(); owner.Show(); owner.WindowState = WindowState.Normal;
            owner.Activate(); owner.ViewModel.ShowFiles = false;
            await owner.ViewModel.SelectConversationAsync(peerId);
        };
        preview.Closed += (_, _) => center.Changed -= view.RefreshText;
        owner.Closed += (_, _) => preview.Close();
        owner.WindowState = WindowState.Minimized;
        preview.Show();
    }
}
