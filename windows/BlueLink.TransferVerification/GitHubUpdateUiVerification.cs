using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Storage;
using BlueLink.Updates;

internal static class GitHubUpdateUiVerification
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                app = App.CreateResourceOnlyHost(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                foreach (var theme in new[] { "light", "dark" })
                foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
                foreach (var ready in new[] { false, true })
                {
                    AppearanceService.Apply(BlueLinkSettings.Defaults(output) with { Theme = theme, Language = language });
                    using var workflow = new UpdateWorkflow(new UpdateService(Path.Combine(output, "unused"), new Source(), new UpdateTestVerifier()));
                    Wait(workflow.CheckAsync());
                    if (workflow.Stage != UpdateStage.Available || workflow.Release?.IsUnsignedPreview != true)
                        throw new InvalidOperationException("Review release must expose in-app download");
                    if (workflow.PrimaryText.Contains("GitHub", StringComparison.Ordinal)) throw new InvalidOperationException("Browser update action must be absent");
                    checks += 2;
                    if (ready)
                    {
                        Wait(workflow.DownloadAsync());
                        if (workflow.Stage != UpdateStage.Ready || !workflow.CanUsePrimary ||
                            !workflow.NoteText.Contains("SHA-256", StringComparison.Ordinal))
                            throw new InvalidOperationException("Verified unsigned preview must disclose its policy before installation");
                        checks++;
                    }
                    var window = new UpdateWindow(workflow, () => false)
                    {
                        Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual,
                        ShowActivated = false, ShowInTaskbar = false
                    };
                    try
                    {
                        window.Show(); Drain(); window.UpdateLayout();
                        var primary = (Wpf.Ui.Controls.Button)window.FindName("UpdatePrimaryButton");
                        var name = new ButtonAutomationPeer(primary).GetName();
                        if (name != workflow.PrimaryText || !primary.IsEnabled || !primary.IsVisible)
                            throw new InvalidOperationException("Native UI Automation download button is not available");
                        var scroll = Descendants<ScrollViewer>(window).Single(view => view.Content is TextBlock text && text.Text == workflow.NoteText);
                        if (!ready && scroll.ScrollableHeight <= 0) throw new InvalidOperationException("Long release notes must scroll");
                        foreach (var button in Descendants<Button>(window).Where(button => button.IsVisible))
                        {
                            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                            if (bounds.Right > window.ActualWidth || bounds.Bottom > window.ActualHeight) throw new InvalidOperationException("Update button is clipped");
                        }
                        checks += 3;
                        var root = (FrameworkElement)window.Content;
                        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"github-{(ready ? "ready" : "available")}-{theme}-{language}.png")); png.Save(file);
                    }
                    finally { window.Close(); }
                }
                Console.WriteLine($"GitHub update native UI Automation passed: {checks} checks, 12 themed/localized screenshots.");
            }
            catch (Exception error) { failure = error; }
            finally { app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); Drain(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class Source : HttpMessageHandler
    {
        private readonly byte[] payload = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri != UpdateService.Feed)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            var version = new Version(UpdateService.CurrentVersion.Major + 1, 0, 0);
            var tag = $"v{version}-preview.10";
            var name = $"BlueLink-Review-{version}-{UpdateService.RuntimeIdentifier}-Setup.exe";
            var json = JsonSerializer.SerializeToUtf8Bytes(new[] { new {
                tag_name = tag, prerelease = true, draft = false,
                body = string.Join("\n", Enumerable.Repeat("Release notes / 更新说明：GitHub 预览发布测试。", 40)),
                assets = new[] { new { name, browser_download_url = $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/{name}",
                    size = payload.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)) } }
            } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(json) });
        }
    }
}
