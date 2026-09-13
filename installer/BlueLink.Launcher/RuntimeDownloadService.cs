#if NET8_0_OR_GREATER
#nullable disable
#endif
namespace BlueLink.Launcher
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    public enum RuntimeStage { Required, Downloading, Verifying, Elevation, Installing, Completed, Failed }
    public sealed class RuntimeProgress
    {
        public RuntimeStage Stage { get; set; }
        public long Received { get; set; }
        public long Total { get; set; }
        public string Message { get; set; }
        public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Received * 100 / Total);
    }
    internal sealed class RuntimeDownloadService
    {
        private readonly HttpMessageHandler handler;
        private readonly Action<string> verifySignature;
        public RuntimeDownloadService(HttpMessageHandler handler = null, Action<string> verifySignature = null)
        { this.handler = handler; this.verifySignature = verifySignature ?? AuthenticodeVerifier.VerifyMicrosoft; }
        public async Task<RuntimePackageLease> DownloadAsync(RuntimePackageInfo package, string temporaryRoot, IProgress<RuntimeProgress> progress, CancellationToken token)
        {
            Validate(package);
            var root = Path.GetFullPath(temporaryRoot);
            Directory.CreateDirectory(root);
            CheckAncestors(root);
            var attempt = Path.Combine(root, "BlueLink-Runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            var target = Path.Combine(attempt, "runtime.exe");
            FileStream lease = null;
            try
            {
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, handler == null))
                {
                    stop.CancelAfter(TimeSpan.FromMinutes(10)); client.Timeout = TimeSpan.FromSeconds(30);
                    var uri = new Uri(package.Url);
                    HttpResponseMessage response = null;
                    try
                    {
                        for (var redirects = 0; redirects <= 3; redirects++)
                        {
                            ValidateUri(uri);
                            response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                            if ((int)response.StatusCode < 300 || (int)response.StatusCode >= 400) break;
                            var location = response.Headers.Location;
                            if (location == null || redirects == 3) throw new IOException("运行时下载重定向无效。");
                            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                            response.Dispose(); response = null;
                        }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != package.Size)
                            throw new IOException("运行时安装包大小与元数据不一致。");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
                        {
                            var buffer = new byte[65536]; long received = 0;
                            while (true)
                            {
                                stop.Token.ThrowIfCancellationRequested();
                                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                                {
                                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                                    using (idle.Token.Register(() => input.Dispose()))
                                    {
                                        var count = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        received += count;
                                        if (received > package.Size) throw new IOException("运行时安装包超出预期大小。");
                                        await output.WriteAsync(buffer, 0, count, stop.Token).ConfigureAwait(false);
                                        progress?.Report(new RuntimeProgress { Stage = RuntimeStage.Downloading, Received = received, Total = package.Size });
                                    }
                                }
                            }
                            if (received != package.Size) throw new IOException("运行时安装包下载不完整。");
                            await output.FlushAsync(stop.Token).ConfigureAwait(false);
                        }
                        progress?.Report(new RuntimeProgress { Stage = RuntimeStage.Verifying, Received = package.Size, Total = package.Size });
                        lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using (var sha = SHA512.Create())
                        {
                            var actual = await Task.Run(() => BitConverter.ToString(sha.ComputeHash(lease)).Replace("-", ""), stop.Token).ConfigureAwait(false);
                            if (!actual.Equals(package.Sha512, StringComparison.OrdinalIgnoreCase)) throw new IOException("运行时安装包 SHA-512 校验失败。");
                        }
                        await Task.Run(() => verifySignature(target), stop.Token).ConfigureAwait(false);
                        stop.Token.ThrowIfCancellationRequested();
                        return new RuntimePackageLease(attempt, target, lease);
                    }
                    finally { response?.Dispose(); }
                }
            }
            catch { lease?.Dispose(); if (File.Exists(target)) File.Delete(target); if (Directory.Exists(attempt)) Directory.Delete(attempt, false); throw; }
        }
        private static void CheckAncestors(string path)
        {
            for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("运行时下载目录不能包含链接。");
        }
        internal static void Validate(RuntimePackageInfo package)
        {
            Version version;
            if (!Version.TryParse(package.Version, out version) || version.Major != 8 || version.Build < 0 || package.Size <= 0 || package.Size > 256L * 1024 * 1024 ||
                package.Sha512 == null || !System.Text.RegularExpressions.Regex.IsMatch(package.Sha512, "\\A[0-9a-fA-F]{128}\\z"))
                throw new IOException("运行时包元数据无效，请使用 Microsoft 官方下载入口。");
            ValidateUri(new Uri(package.Url));
        }
        private static void ValidateUri(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                !(uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("运行时下载地址不是允许的 Microsoft 官方源。");
        }
    }
    internal sealed class RuntimePackageLease : IDisposable
    {
        private readonly string directory;
        private FileStream lease;
        public string Path { get; }
        public RuntimePackageLease(string directory, string path, FileStream lease) { this.directory = directory; Path = path; this.lease = lease; }
        public void Dispose()
        {
            if (lease == null) return;
            lease.Dispose(); lease = null;
            try { File.Delete(Path); Directory.Delete(directory, false); } catch { /* Only this private package may remain for cleanup. */ }
        }
    }
}
