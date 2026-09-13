using System.Windows;
using System.Windows.Controls;
using BlueLink.Localization;
using BlueLink.Transfer;

namespace BlueLink;

internal static class IncomingFilePrompt
{
    // Queue time and the visible dialog share the receive decision deadline.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static ConfirmationWindow CreateWindow(IncomingFileDecision request, out ComboBox choice)
    {
        var conflict = request.NameConflict && request.DuplicatePolicy == "ask";
        choice = new ComboBox
        {
            Margin = new Thickness(0, 10, 0, 0), MinHeight = 34,
            ItemsSource = new[] { Strings.Get("自动重命名"), Strings.Get("覆盖同名文件") }, SelectedIndex = 0
        };
        System.Windows.Automation.AutomationProperties.SetName(choice, Strings.Get("同名文件处理"));
        var document = new ConfirmationDocument(Strings.Get(conflict ? "同名文件处理" : "接收文件"),
            Strings.Format($"{request.PeerName} 请求发送：{request.Offer.Name}（{request.Offer.Size} B）"),
            Strings.Get("30 秒内确认；取消或超时将拒绝接收。"), Strings.Get("接收"),
            Destructive: false, CancelText: Strings.Get("拒绝"));
        return new ConfirmationWindow(document, conflict ? choice : null);
    }

    public static async Task<string?> DecideAsync(IncomingFileDecision request, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            return await dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                var dialog = CreateWindow(request, out var choice);
                var owner = Application.Current.MainWindow is { IsVisible: true } visible ? visible : null;
                dialog.Owner = owner;
                dialog.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
                if (owner is not null) dialog.MaxHeight = Math.Max(250, owner.ActualHeight - 80);
                using var overlay = owner is null ? null : BlueLinkDialog.DimOwner(owner, "#3D0F172A", owner is MainWindow ? 52 : 0);
                using var registration = token.Register(() => dispatcher.BeginInvoke(new Action(dialog.Close)));
                // Modal WPF pumping does not block the encrypted session read loop.
                dialog.ShowDialog();
                token.ThrowIfCancellationRequested();
                return !dialog.Confirmed ? null : request.NameConflict && request.DuplicatePolicy == "ask"
                    ? choice.SelectedIndex == 1 ? "overwrite" : "rename" : request.DuplicatePolicy;
            });
        }
        finally { Gate.Release(); }
    }
}
