using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using BlueLink.Updates;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    internal static readonly string[] UpdateScenes =
    [ "update-checking", "update-check-failed", "update-available", "update-downloading", "update-download-failed", "update-ready" ];

    private static async Task ApplyUpdateFixtureAsync(MainWindow window, string directory, string scene)
    {
        var source = new AcceptanceUpdateSource { Mode = scene };
        window.ViewModel.UseAcceptanceUpdates(new UpdateService(Path.Combine(directory, "QAUpdates"), source,
            new AcceptanceUpdateVerifier()));
        var workflow = window.ViewModel.Updates;
        window.OpenSettings();
        window.ActiveSettingsPage!.ShowAcceptanceAbout();
        if (scene == "update-checking") { _ = workflow.CheckAsync(); return; }
        await workflow.CheckAsync();
        if (scene == "update-check-failed") { source.Mode = "update-available"; return; }
        if (scene == "update-downloading")
        {
            _ = workflow.DownloadAsync();
            await source.Waiting.Task;
        }
        else if (scene is "update-download-failed" or "update-ready") await workflow.DownloadAsync();
        // A retry can exercise a complete local download; this source never makes a network request.
        if (scene == "update-download-failed") source.Mode = "update-ready";
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = new UpdateWindow(workflow, () => false) { Owner = window };
            using var dim = BlueLinkDialog.DimOwner(window, "#4D0D1729", 0);
            dialog.ShowDialog();
        }));
    }

    // Startup-only fixtures. The payload is not an executable; installation is also blocked by the model.
    private sealed class AcceptanceUpdateVerifier : IUpdatePackageVerifier
    {
        public void Verify(string path, Version version)
        {
            using var file = File.OpenRead(path);
            if (version != AcceptanceUpdateSource.Version || file.Length != AcceptanceUpdateSource.Payload.Length ||
                !SHA256.HashData(file).SequenceEqual(SHA256.HashData(AcceptanceUpdateSource.Payload)))
                throw new InvalidDataException("QA 更新样本不匹配。");
        }
    }

    private sealed class AcceptanceUpdateSource : HttpMessageHandler
    {
        internal static readonly Version Version = new(UpdateService.CurrentVersion.Major + 1, 1, 0);
        internal static readonly byte[] Payload = Enumerable.Repeat((byte)'Q', 1_000_000).ToArray();
        internal string Mode { get; set; } = "update-available";
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri == UpdateService.Feed)
            {
                if (Mode == "update-checking") await Task.Delay(Timeout.Infinite, token);
                if (Mode == "update-check-failed") throw new HttpRequestException("QA：模拟检查失败。");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tag_name = "v" + Version, draft = false, prerelease = false,
                    body = "QA 本地验收样本：用于核对下载、取消和重试界面，不会连接发布服务器或启动安装。",
                    assets = new[] { new { name = $"BlueLink-Setup-{Version}-win-x64.exe",
                        browser_download_url = $"https://github.com/{UpdateService.Repository}/releases/download/v{Version}/BlueLink-Setup-{Version}-win-x64.exe",
                        size = Payload.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Payload)) } }
                })) };
            }
            if (Mode == "update-download-failed") throw new HttpRequestException("QA：模拟下载失败。");
            return new(HttpStatusCode.OK) { Content = Mode == "update-downloading"
                ? new StreamContent(new HeldStream(Waiting)) : new ByteArrayContent(Payload) };
        }

        private sealed class HeldStream(TaskCompletionSource waiting) : MemoryStream(Payload)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (Position < 380_000)
                {
                    var count = (int)Math.Min(buffer.Length, 380_000 - Position);
                    if (Position + count == 380_000) await Task.Delay(180, token);
                    return await base.ReadAsync(buffer[..count], token);
                }
                waiting.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                return await base.ReadAsync(buffer[..1], token);
            }
        }
    }
}

public sealed partial class MainViewModel
{
    private bool _acceptanceUpdates;
    internal void UseAcceptanceUpdates(UpdateService service)
    {
        if (_runtimeStarted) throw new InvalidOperationException("Update visual fixtures require an isolated runtime.");
        Updates.Dispose();
        Updates = new(service);
        _acceptanceUpdates = true;
        Raise(nameof(Updates));
    }
}

public partial class SettingsPage
{
    internal void ShowAcceptanceAbout() => SetPage("about");
}
