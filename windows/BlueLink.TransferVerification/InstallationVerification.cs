using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using BlueLink.Launcher;
using BlueLink.Security;
using BlueLink.Shared;
using BlueLink.Storage;

internal sealed class InstallationVerification
{
    private int _checks;
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkInstallationVerification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var local = Path.Combine(root, "local"); var userRoot = Path.Combine(local, "BlueLink");
            var installRoot = Path.Combine(root, "install"); var download = Path.Combine(installRoot, "Download");
            Directory.CreateDirectory(download); await File.WriteAllTextAsync(Path.Combine(download, "keep.txt"), "received");
            var identity = new IdentityStore(userRoot);
            var database = new BlueLinkDatabase(Path.Combine(userRoot, "Data"), installRoot);
            await database.InitializeAsync(identity);
            var custom = Path.Combine(userRoot, "Cache", "CustomReceived"); Directory.CreateDirectory(custom);
            await File.WriteAllTextAsync(Path.Combine(custom, "keep.txt"), "custom received");
            var temp = Path.Combine(userRoot, "Cache", "temporary.bin"); await File.WriteAllTextAsync(temp, "temporary");
            var unrelated = Path.Combine(userRoot, "my-notes.txt"); await File.WriteAllTextAsync(unrelated, "unrelated");
            await database.SaveSettingsAsync((await database.LoadSettingsAsync()) with { DownloadDirectory = custom });
            using (var handle = new FileStream(Path.Combine(userRoot, "identity.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Fails(() => Task.Run(() => UserDataCleanup.DeleteSelectedData(local, installRoot)), "locked identity prevents cleanup");
                Check(File.Exists(database.DatabasePath) && File.Exists(temp), "cleanup preflight preserves all data when a file is locked");
            }
            var removed = UserDataCleanup.DeleteSelectedData(local, installRoot);
            Check(removed >= 3 && !File.Exists(database.DatabasePath) && !File.Exists(Path.Combine(userRoot, "identity.json")), "selected database and identity are removed");
            Check(!File.Exists(temp), "unprotected application cache removed");
            Check(await File.ReadAllTextAsync(Path.Combine(download, "keep.txt")) == "received", "installation Download files preserved");
            Check(await File.ReadAllTextAsync(Path.Combine(custom, "keep.txt")) == "custom received", "custom received folder inside Cache preserved");
            Check(await File.ReadAllTextAsync(unrelated) == "unrelated", "unknown user files preserved");
            UserDataCleanup.DeleteSelectedData(local, installRoot);
            Check(File.Exists(Path.Combine(custom, "keep.txt")), "repeated cleanup preserves cached receive folders when the database is already gone");
            Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
            await File.WriteAllTextAsync(database.DatabasePath, "invalid sqlite");
            await File.WriteAllTextAsync(Path.Combine(userRoot, "identity.json"), "keep identity");
            await Fails(() => Task.Run(() => UserDataCleanup.DeleteSelectedData(local, installRoot)), "unreadable received-path index prevents all cleanup");
            Check(File.Exists(Path.Combine(custom, "keep.txt")) && await File.ReadAllTextAsync(Path.Combine(userRoot, "identity.json")) == "keep identity", "failed cleanup preserves identity and received content");

            var bytes = new byte[131072]; RandomNumberGenerator.Fill(bytes);
            var package = new RuntimePackageInfo { Version = "8.0.30", Size = bytes.Length, Sha512 = Convert.ToHexString(SHA512.HashData(bytes)), Url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.30/windowsdesktop-runtime-8.0.30-win-x64.exe", FileName = "../../escape.exe" };
            var handler = new RuntimeFixtureHandler(bytes); var signatures = 0;
            var service = new RuntimeDownloadService(handler, _ => signatures++);
            var downloads = Path.Combine(root, "downloads"); var reports = new List<RuntimeProgress>();
            string path;
            using (var lease = await service.DownloadAsync(package, downloads, new InlineProgress(reports.Add), CancellationToken.None))
            {
                path = lease.Path;
                Check(path.StartsWith(downloads + Path.DirectorySeparatorChar) && Path.GetFileName(path) == "runtime.exe", "manifest filename cannot escape the private package directory");
                Check(File.ReadAllBytes(path).SequenceEqual(bytes) && signatures == 1, "complete runtime package hash and verifier checked");
                await Fails(() => File.WriteAllTextAsync(path, "tamper"), "verified runtime lease prevents writes before installer launch");
                Check(reports.Any(p => p.Stage == RuntimeStage.Downloading && p.Received == bytes.Length) && reports.Last().Stage == RuntimeStage.Verifying, "progress is based on actual bytes and verification stage");
            }
            Check(!File.Exists(path), "closing verified package removes only its private attempt");
            handler.Mode = "corrupt";
            await Fails(() => service.DownloadAsync(package, downloads, null, CancellationToken.None), "corrupt runtime hash rejected");
            Check(signatures == 1 && !Directory.EnumerateFileSystemEntries(downloads).Any(), "failed hash never reaches signature verification and leaves no package");
            handler.Mode = "truncated";
            await Fails(() => service.DownloadAsync(package, downloads, null, CancellationToken.None), "truncated runtime package rejected");
            handler.Mode = "redirect";
            await Fails(() => service.DownloadAsync(package, downloads, null, CancellationToken.None), "external runtime redirect rejected");
            handler.Mode = "hold";
            using (var stop = new CancellationTokenSource())
            {
                var pending = service.DownloadAsync(package, downloads, null, stop.Token);
                await handler.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
                await Fails(() => pending, "runtime download cancels without launching anything");
            }
            Check(!Directory.EnumerateFileSystemEntries(downloads).Any(), "cancellation leaves no executable");
            handler.Mode = "normal";
            using (var retry = await service.DownloadAsync(package, downloads, null, CancellationToken.None)) Check(File.Exists(retry.Path), "runtime download can retry after cancellation");
            package.Size = 0;
            await Fails(() => service.DownloadAsync(package, downloads, null, CancellationToken.None), "unknown package size rejected before download");
            Console.WriteLine($"BlueLink installation safety verification passed: {_checks} checks");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("BlueLinkInstallationVerification-")) throw new IOException("Invalid test cleanup root");
            Directory.Delete(root, true);
        }
    }
    private void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); _checks++; }
    private async Task Fails(Func<Task> operation, string label) { try { await operation(); } catch (Exception error) when (error is IOException or OperationCanceledException or HttpRequestException) { _checks++; return; } throw new InvalidOperationException("Expected failure: " + label); }
    private sealed class InlineProgress(Action<RuntimeProgress> report) : IProgress<RuntimeProgress> { public void Report(RuntimeProgress value) => report(value); }
    private sealed class RuntimeFixtureHandler(byte[] bytes) : HttpMessageHandler
    {
        public string Mode = "normal";
        public TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Mode == "hold") { Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (Mode == "redirect") return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/runtime.exe") } };
            var payload = (byte[])bytes.Clone(); if (Mode == "corrupt") payload[0] ^= 1; if (Mode == "truncated") payload = payload[..^1];
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }
    }
}
