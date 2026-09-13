using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Storage;

internal static class DialogVerification
{
    internal static void Run(string directory, string? desktopScene = null, string theme = "light")
    {
        var output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (desktopScene is not null)
                {
                    AppearanceService.Apply(BlueLinkSettings.Defaults(output) with { Theme = theme });
                    var dialog = Create(desktopScene);
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    var owner = new Window { Title = "蓝联 · 隔离弹窗验收", Width = 880, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new Grid() };
                    owner.Show(); dialog.Owner = owner;
                    using (BlueLinkDialog.DimOwner(owner, "#3D0F172A", 0)) dialog.ShowDialog();
                    owner.Close(); app.Shutdown();
                    File.WriteAllText(Path.Combine(output, desktopScene + "-result.json"), JsonSerializer.Serialize(new { Result = dialog.Result.ToString() }));
                    return;
                }
                var checks = new List<string>();
                void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); checks.Add(name); }
                foreach (var appearance in new[] { "light", "dark" })
                foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
                foreach (var scene in new[] { "error", "information", "warning", "confirm", "unsaved", "long", "cancel-transfer", "incoming" })
                {
                    AppearanceService.Apply(BlueLinkSettings.Defaults(output) with { Theme = appearance, Language = language });
                    var dialog = Create(scene);
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -5000; dialog.Top = -5000; dialog.ShowActivated = false;
                    dialog.Show(); dialog.UpdateLayout(); Drain();
                    var label = appearance + "-" + language + "-" + scene;
                    var actions = new[] { "ConfirmationSecondaryButton", "ConfirmationCancelButton", "ConfirmationPrimaryButton" }
                        .Select(n => (Button)dialog.FindName(n)).Where(b => b.IsVisible).ToArray();
                    Check(actions.Length == (scene == "unsaved" ? 3 : scene is "confirm" or "cancel-transfer" or "incoming" ? 2 : 1), label + ": no empty actions");
                    double previousRight = 0;
                    foreach (var button in actions)
                    {
                        var bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                        Check(button.ActualHeight == 36 && bounds.Bottom <= dialog.ActualHeight && bounds.Left >= previousRight,
                            label + ": complete action, no overlap " + button.Name);
                        Check(button.Content is string caption && caption.Length > 0, label + ": action has a caption");
                        previousRight = bounds.Right;
                        InputControlGeometry.Verify(button, Check, new List<string>(), label);
                    }
                    double Center(string name)
                    {
                        var element = (FrameworkElement)dialog.FindName(name);
                        return element.TranslatePoint(new Point(0, element.ActualHeight / 2), dialog).Y;
                    }
                    Check(Math.Abs(Center("ConfirmationTitle") - Center("ConfirmationCloseButton")) < .8, label + ": title and close vertically aligned");
                    Check(Math.Abs(Center("ConfirmationIconTile") - Center("ConfirmationQuestion")) < .8, label + ": icon and message vertically aligned");
                    var scroll = (ScrollViewer)dialog.FindName("ConfirmationBodyScroll");
                    Check(dialog.ActualWidth == 460 && dialog.ActualHeight <= dialog.MaxHeight, label + ": bounded dialog");
                    Check((scroll.ScrollableHeight > 0) == (scene == "long"), label + ": overflow-only scrolling");
                    var content = (FrameworkElement)dialog.Content;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, label + ".png"))) png.Save(stream);
                    // Exercise the real WPF automation peers, never a production transfer or data action.
                    var chosen = scene == "unsaved" ? (Button)dialog.FindName("ConfirmationSecondaryButton") : actions[^1];
                    ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(chosen)!.GetPattern(PatternInterface.Invoke)!).Invoke(); Drain();
                    Check(dialog.Result == (scene == "unsaved" ? Wpf.Ui.Controls.MessageBoxResult.Secondary : Wpf.Ui.Controls.MessageBoxResult.Primary), label + ": action result");
                }
                File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks));
                Console.WriteLine($"Dialog verification passed: {checks.Count} checks; 48 themed/localized scenes.");
                app.Shutdown();
            }
            catch (Exception e) { failure = e; Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    private static ConfirmationWindow Create(string scene) => scene switch
    {
        "error" => BlueLinkDialog.CreateWindow("部分文件发送失败", "QA-report.pdf：设备会话已结束\nQA-photo.png：接收方已拒绝", BlueLinkDialogTone.Error),
        "information" => BlueLinkDialog.CreateWindow("消息详情", "QA 消息已送达。", BlueLinkDialogTone.Information),
        "warning" => BlueLinkDialog.CreateWindow("提示", "QA 文件暂时不可用，请稍后重试。", BlueLinkDialogTone.Warning),
        "confirm" => BlueLinkDialog.CreateWindow("确认", "是否继续此操作？", BlueLinkDialogTone.Warning, true),
        "unsaved" => BlueLinkDialog.CreateWindow("设置尚未保存", "是否保存本次设置修改后返回？", BlueLinkDialogTone.Warning, true, "保存并返回", "继续编辑", "放弃修改"),
        "long" => BlueLinkDialog.CreateWindow("部分文件发送失败", string.Join("\n", Enumerable.Range(1, 70).Select(i => $"QA-{i:D2}-报告文件.pdf：设备会话已结束，请重新连接后重试。")), BlueLinkDialogTone.Error),
        "cancel-transfer" => new ConfirmationWindow(ConfirmationDocument.CancelTransfer("QA-report.pdf", true)),
        "incoming" => new ConfirmationWindow(new ConfirmationDocument("接收文件", "是否接收 QA-report.pdf？", "文件将保存到已选择的接收目录。", "接收", Destructive: false)),
        _ => throw new ArgumentException("Unknown dialog fixture")
    };
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
