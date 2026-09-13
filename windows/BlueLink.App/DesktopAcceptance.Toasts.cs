namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal static readonly (string Scene, ToastLevel Level, string Message)[] ToastFixtures =
    [
        ("toast-success", ToastLevel.Success, "已复制文件"),
        ("toast-info", ToastLevel.Info, "已取消文件传输"),
        ("toast-warning", ToastLevel.Warning, "蓝牙信号较弱，请靠近设备"),
        ("toast-error", ToastLevel.Error, "删除本机记录失败")
    ];

    private static void ApplyToastFixture(MainWindow window, string scene)
    {
        var host = (ToastHost)window.FindName("Toasts");
        foreach (var fixture in ToastFixtures.Where(item => scene == "toast-stacked" || item.Scene == scene))
            // Static visual fixture: allow time for a native screenshot; production feedback retains its 3/5 second lifetime.
            host.Show(fixture.Message, fixture.Level, TimeSpan.FromSeconds(30));
    }
}
