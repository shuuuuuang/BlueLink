using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlueLink.Updates;

public sealed record UpdateRelease(Version Version, string Notes, string FileName, Uri DownloadUrl, long Size, string Sha256);
public sealed record DownloadProgress(long Received, long Total);
public sealed record UpdatePackage(UpdateRelease Release, string Path);
public interface IUpdatePackageVerifier { void Verify(string path, Version version); }

/// <summary>Only the configured repository's public release feed is queried. No device or chat data is uploaded.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "shuuuuuang/BlueLink";
    public static readonly Uri Feed = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static string RuntimeIdentifier => GetRuntimeIdentifier(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
    public static string GetRuntimeIdentifier(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "win-x86",
        System.Runtime.InteropServices.Architecture.X64 => "win-x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("Unsupported Windows architecture.")
    };
    public const string PortableUpdateNotice = "免安装版本请下载对应架构的 Portable ZIP，退出程序后解压更新并保留 Data 和 Download 文件夹。";
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly string _directory;
    private readonly IUpdatePackageVerifier _verifier;

    public UpdateService(string directory, HttpMessageHandler? handler = null, IUpdatePackageVerifier? verifier = null)
    {
        _directory = Path.GetFullPath(directory);
        _verifier = verifier ?? new SignedUpdateVerifier();
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueLink-Windows/" + CurrentVersion);
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion
    {
        get { var value = typeof(UpdateService).Assembly.GetName().Version!; return new(value.Major, value.Minor, Math.Max(0, value.Build)); }
    }

    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        if (Storage.AppStoragePaths.IsPortable) throw new InvalidOperationException(PortableUpdateNotice);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await GetAsync(Feed, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("暂时无法取得公开发布信息，请稍后重试。");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), 256 * 1024, deadline.Token);
        return ParseRelease(bytes, current);
    }

    public static UpdateRelease? ParseRelease(byte[] json, Version current)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("发布信息不是正式版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var rawVersion = tag.TrimStart('v', 'V');
        if (!Version.TryParse(rawVersion, out var parsed) || parsed.Build < 0 || parsed.Revision > 0 || rawVersion.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new InvalidDataException("发布版本格式无效。");
        var version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        var name = $"BlueLink-Setup-{version}-{RuntimeIdentifier}.exe";
        var assets = root.GetProperty("assets").EnumerateArray().Where(x => x.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("此版本尚未提供当前架构的 Windows 安装包。");
        var asset = assets[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (size is <= 0 or > MaximumPackageBytes || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 || digest[7..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("发布包缺少有效的大小或 SHA-256 校验信息。");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        if (!url.AbsoluteUri.Equals(expected, StringComparison.Ordinal)) throw new InvalidDataException("安装包地址不属于已配置的发布源。");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        notes = notes.Replace("\0", "");
        return new(version, notes.Length > 4000 ? notes[..4000] : notes, name, url, size, digest[7..].ToUpperInvariant());
    }

    public async Task<UpdatePackage> DownloadAsync(UpdateRelease release, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        ValidateRelease(release);
        if (Storage.AppStoragePaths.IsPortable) throw new InvalidOperationException(PortableUpdateNotice);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        EnsureSafeDirectory(_directory);
        var attempt = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        EnsureSafeDirectory(attempt);
        var partial = Path.Combine(attempt, release.FileName + ".part");
        var complete = Path.Combine(attempt, release.FileName);
        try
        {
            using var response = await GetAsync(release.DownloadUrl, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size)
                throw new InvalidDataException("下载内容大小与发布信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long received = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var lastReport = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, idle.Token);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Size) throw new InvalidDataException("下载内容超过发布包大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    if (received == count || lastReport.ElapsedMilliseconds >= 150 || received == release.Size)
                    {
                        progress?.Report(new(received, release.Size));
                        lastReport.Restart();
                    }
                }
                if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("安装包校验失败，请重新下载。");
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => _verifier.Verify(partial, release.Version), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, complete);
            return new(release, complete);
        }
        catch
        {
            // Delete only this attempt's incomplete package; never touch an earlier verified package.
            if (File.Exists(partial)) File.Delete(partial);
            if (!Directory.EnumerateFileSystemEntries(attempt).Any()) Directory.Delete(attempt);
            throw;
        }
    }

    public async Task<FileStream> AcquireVerifiedPackageAsync(UpdatePackage package, CancellationToken token)
    {
        ValidateRelease(package.Release);
        var path = Path.GetFullPath(package.Path);
        if (!path.StartsWith(_directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path) != package.Release.FileName) throw new InvalidDataException("安装包路径无效。");
        EnsureSafeDirectory(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("安装包路径不能是链接。");
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        try
        {
            if (lease.Length != package.Release.Size || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(lease, token), Convert.FromHexString(package.Release.Sha256)))
                throw new InvalidDataException("本地安装包已变化，请重新下载。");
            await Task.Run(() => _verifier.Verify(path, package.Release.Version), token).ConfigureAwait(false);
            return lease; // Keep writes/deletion blocked until the installer process has opened the package.
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private static void ValidateRelease(UpdateRelease value)
    {
        var prefix = $"https://github.com/{Repository}/releases/download/";
        if (value.Size is <= 0 or > MaximumPackageBytes || value.FileName != $"BlueLink-Setup-{value.Version}-{RuntimeIdentifier}.exe" ||
            value.Sha256.Length != 64 || value.Sha256.Any(c => !Uri.IsHexDigit(c)) || !value.DownloadUrl.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.DownloadUrl.AbsoluteUri.EndsWith("/" + value.FileName, StringComparison.Ordinal)) throw new InvalidDataException("更新包元数据无效。");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("更新服务器重定向地址无效。");
            var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null) throw new InvalidDataException("更新服务器未提供下载地址。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("更新服务器重定向次数过多。");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        await using (stream)
        {
            using var result = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token); if (count == 0) return result.ToArray();
                if (result.Length + count > limit) throw new InvalidDataException("发布信息超过允许大小。");
                result.Write(buffer, 0, count);
            }
        }
    }

    private static void EnsureSafeDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        for (var parent = current; parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("更新缓存目录不能使用链接。");
        Directory.CreateDirectory(directory);
    }
    public void Dispose() => _client.Dispose();
}
