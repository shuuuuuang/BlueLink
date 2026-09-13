using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using BlueLink.Updates;

internal sealed class UpdateVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlueLinkUpdateVerification-" + Guid.NewGuid().ToString("N")));
        try
        {
            var source = new UpdateTestSource();
            using var service = new UpdateService(root, source, new UpdateTestVerifier());
            var release = await service.CheckAsync(UpdateService.CurrentVersion, CancellationToken.None);
            Check(release?.Version == source.Version && release.Sha256 == Convert.ToHexString(SHA256.HashData(source.Payload)), "feed selects exact Windows package with its digest");
            Check(UpdateService.ParseRelease(source.Metadata(), source.Version) is null, "same version does not produce an update");
            Check(UpdateService.ParseRelease(source.Metadata(), new Version(99, 0, 0)) is null, "older releases cannot cause downgrades");
            foreach (var mode in new[] { "draft", "prerelease", "missing-asset", "missing-digest", "foreign-url", "huge" })
                await Fails(() => Task.FromResult(UpdateService.ParseRelease(source.Metadata(mode), UpdateService.CurrentVersion)), "invalid release: " + mode);
            source.Mode = "404";
            await Fails(() => service.CheckAsync(UpdateService.CurrentVersion, CancellationToken.None), "missing release is a failure, not up to date");
            source.Mode = "normal";
            var package = await service.DownloadAsync(release!, null, CancellationToken.None);
            Check(File.Exists(package.Path) && (await File.ReadAllBytesAsync(package.Path)).SequenceEqual(source.Payload), "only complete verified bytes become a package");
            await using (var lease = await service.AcquireVerifiedPackageAsync(package, CancellationToken.None))
                await Fails(() => File.WriteAllBytesAsync(package.Path, [0]), "install lease prevents concurrent replacement");
            foreach (var mode in new[] { "corrupt", "truncated", "foreign-redirect", "http-redirect" })
            {
                source.Mode = mode;
                await Fails(() => service.DownloadAsync(release!, null, CancellationToken.None), "invalid download: " + mode);
                Check(File.Exists(package.Path) && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "failed attempt preserves previous package and removes its partial file");
            }
            source.Mode = "normal";
            await File.WriteAllBytesAsync(package.Path, new byte[source.Payload.Length]);
            await Fails(() => service.AcquireVerifiedPackageAsync(package, CancellationToken.None), "modified staged package cannot install");
            using (var unsigned = new UpdateService(Path.Combine(root, "unsigned"), new UpdateTestSource()))
                await Fails(() => unsigned.DownloadAsync(release!, null, CancellationToken.None), "production signature policy blocks unsigned payload without UI");
            await VerifyWorkflow(root);
            Console.WriteLine($"BlueLink update verification passed: {_checks} checks");
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("BlueLinkUpdateVerification-", StringComparison.Ordinal))
                throw new IOException("Invalid update verification cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private async Task VerifyWorkflow(string root)
    {
        var source = new UpdateTestSource();
        using var workflow = new UpdateWorkflow(new UpdateService(Path.Combine(root, "workflow"), source, new UpdateTestVerifier()));
        await workflow.CheckAsync();
        Check(workflow.Stage == UpdateStage.Available, "feed result drives available state");
        source.Mode = "hold"; var download = workflow.DownloadAsync();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(workflow.Stage == UpdateStage.Downloading && workflow.Busy, "streaming drives busy state");
        await workflow.CheckAsync();
        Check(workflow.Stage == UpdateStage.Downloading, "duplicate operation cannot replace active download");
        workflow.Cancel(); await download;
        Check(workflow.Stage == UpdateStage.Available && !workflow.Busy && workflow.Package is null, "cancel returns to available without a ready package");
        source.Mode = "corrupt"; await workflow.DownloadAsync();
        Check(workflow.Stage == UpdateStage.DownloadFailed && workflow.Error.Length > 0, "invalid bytes produce retryable failure");
        source.Mode = "normal"; await workflow.DownloadAsync();
        Check(workflow.Stage == UpdateStage.Ready && workflow.Percent == 100, "retry succeeds only after verification");
        var launches = 0;
        Check(!await workflow.InstallAsync(() => false, _ => launches++) && launches == 0 && workflow.Stage == UpdateStage.Ready, "active transfers block installer and preserve the verified package");
        await File.WriteAllBytesAsync(workflow.Package!.Path, new byte[source.Payload.Length]);
        Check(!await workflow.InstallAsync(() => true, _ => launches++) && launches == 0, "tampering blocks install after Ready");
        await workflow.DownloadAsync();
        Check(await workflow.InstallAsync(() => true, _ => launches++) && workflow.Stage == UpdateStage.HandedOff && launches == 1, "verified package handed off once to test launcher");
        Check(!await workflow.InstallAsync(() => true, _ => launches++) && launches == 1, "duplicate install cannot launch twice");
    }
    private async Task Fails(Func<Task> operation, string label)
    {
        try { await operation(); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or CryptographicException) { Check(true, label); return; }
        throw new InvalidOperationException("Update verification expected failure: " + label);
    }
    private void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException("Update verification failed: " + label); _checks++; }
}

// Test-only network/PE verifier. Production uses HTTPS and SignedUpdateVerifier.
internal sealed class UpdateTestVerifier : IUpdatePackageVerifier
{
    public void Verify(string path, Version version) { if (!File.Exists(path)) throw new IOException("Missing fixture package"); }
}
internal sealed class UpdateTestSource : HttpMessageHandler
{
    public Version Version { get; } = new(UpdateService.CurrentVersion.Major + 1, 1, 0);
    public byte[] Payload { get; } = Enumerable.Range(0, 1000).Select(x => (byte)x).ToArray();
    public string Mode { get; set; } = "normal";
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public byte[] Metadata(string mode = "normal") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        tag_name = "v" + Version, draft = mode == "draft", prerelease = mode == "prerelease", body = "QA 更新说明：实际下载与校验状态。",
        assets = new[] { new { name = mode == "missing-asset" ? "Android.apk" : $"BlueLink-Setup-{Version}-win-x64.exe",
            browser_download_url = mode == "foreign-url" ? "https://example.com/installer.exe" : $"https://github.com/{UpdateService.Repository}/releases/download/v{Version}/BlueLink-Setup-{Version}-win-x64.exe",
            size = mode == "huge" ? UpdateService.MaximumPackageBytes + 1 : Payload.Length,
            digest = mode == "missing-digest" ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(Payload)) } }
    });
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Mode == "404") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (request.RequestUri == UpdateService.Feed) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata()) });
        if (Mode is "foreign-redirect" or "http-redirect")
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new(Mode == "foreign-redirect" ? "https://example.com/setup.exe" : "http://github.com/setup.exe");
            return Task.FromResult(redirect);
        }
        HttpContent content = Mode == "hold" ? new StreamContent(new HeldStream(Payload, Waiting)) :
            new ByteArrayContent(Mode == "corrupt" ? new byte[Payload.Length] : Mode == "truncated" ? Payload[..500] : Payload);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private sealed class HeldStream(byte[] payload, TaskCompletionSource waiting) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..Math.Min(380, buffer.Length)], token);
            waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0;
        }
    }
}
