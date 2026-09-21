using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlueLink.Updates;

public sealed record UpdateRelease(Version Version, string Notes, string FileName, Uri DownloadUrl, long Size, string Sha256)
{
    public string Tag { get; init; } = "v" + Version;
    public bool IsPortable => FileName.EndsWith("-Portable.zip", StringComparison.Ordinal);
    public bool IsUnsignedPreview => Tag.Contains("-preview.", StringComparison.Ordinal) && FileName.StartsWith("BlueLink-Review-", StringComparison.Ordinal);
    public string DisplayVersion => Tag.TrimStart('v');
    public Uri ReleaseUrl => new($"https://github.com/{UpdateService.Repository}/releases/tag/{Uri.EscapeDataString(Tag)}");
}
public sealed record DownloadProgress(long Received, long Total);
public sealed record UpdatePackage(UpdateRelease Release, string Path);
public interface IUpdatePackageVerifier { void Verify(string path, Version version); }

/// <summary>Only the configured repository's public release feed is queried. No device or chat data is uploaded.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "shuuuuuang/BlueLink";
    public static readonly Uri Feed = new($"https://api.github.com/repos/{Repository}/releases?per_page=100");
    public static ReleaseVersion CurrentRelease
    {
        get
        {
            var tag = typeof(UpdateService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "ReleaseTag")?.Value;
            var identity = ReleaseVersion.Parse(tag);
            return identity?.Version == CurrentVersion ? identity : new(CurrentVersion, null);
        }
    }
    public static string RuntimeIdentifier => GetRuntimeIdentifier(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
    public static string GetRuntimeIdentifier(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "win-x86",
        System.Runtime.InteropServices.Architecture.X64 => "win-x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("Unsupported Windows architecture.")
    };
    public const string PortableUpdateNotice = "免安装更新包已在应用内下载；退出程序后解压替换程序文件，并保留 Data 和 Download 文件夹。";
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await GetAsync(Feed, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("暂时无法取得公开发布信息，请稍后重试。");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), 2 * 1024 * 1024, deadline.Token);
        var identity = current == CurrentVersion ? CurrentRelease : new ReleaseVersion(current, null);
        var release = ParseFeed(bytes, identity);
        return release;
    }

    public static UpdateRelease? ParseRelease(byte[] json, Version current) =>
        ParseFeed(json, new(current, null), includePrereleases: false);

    public static UpdateRelease? ParseFeed(byte[] json, ReleaseVersion current, bool includePrereleases = true,
        string? runtimeIdentifier = null, bool? portable = null)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
        var root = document.RootElement;
        var releases = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        if (releases.Length > 100) throw new InvalidDataException("发布信息超过允许大小。");
        var candidates = new List<(JsonElement Json, ReleaseVersion Identity)>();
        foreach (var item in releases)
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            var identity = ReleaseVersion.Parse(item.GetProperty("tag_name").GetString());
            if (identity is null) continue;
            var preview = item.GetProperty("prerelease").GetBoolean();
            if (preview != identity.Preview.HasValue || (preview && !includePrereleases)) continue;
            candidates.Add((item, identity));
        }
        var selected = candidates.OrderByDescending(value => value.Identity).FirstOrDefault();
        if (selected.Identity is null || selected.Identity.CompareTo(current) <= 0) return null;
        var release = selected.Json;
        var tag = release.GetProperty("tag_name").GetString()!;
        var version = selected.Identity.Version;
        var rid = runtimeIdentifier ?? RuntimeIdentifier;
        if (rid is not ("win-x86" or "win-x64" or "win-arm64")) throw new InvalidDataException("不支持的更新架构。");
        var isPortable = portable ?? Storage.AppStoragePaths.IsPortable;
        var names = isPortable ? new[] { $"BlueLink-{version}-{rid}-Portable.zip" }
            : new[] { $"BlueLink-Setup-{version}-{rid}.exe", $"BlueLink-Review-{version}-{rid}-Setup.exe" };
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement? selectedAsset = null;
        foreach (var name in names)
        {
            var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("发布包名称重复。");
            if (matches.Length == 1) { selectedAsset = matches[0]; break; }
        }
        if (selectedAsset is not { } asset) throw new InvalidDataException("此版本尚未提供当前架构的 Windows 安装包。");
        var fileName = asset.GetProperty("name").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (size is <= 0 or > MaximumPackageBytes || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 || digest[7..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("发布包缺少有效的大小或 SHA-256 校验信息。");
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{fileName}";
        if (asset.GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidDataException("安装包地址不属于已配置的发布源。");
        var notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        notes = notes.Replace("\0", "");
        return new(version, notes.Length > 4000 ? notes[..4000] : notes, fileName, new(expected), size, digest[7..].ToUpperInvariant())
        { Tag = tag };
    }

    public async Task<UpdatePackage> DownloadAsync(UpdateRelease release, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        ValidateRelease(release);
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
            await Task.Run(() => VerifyPackage(partial, release, installing: false), ct).ConfigureAwait(false);
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
            await Task.Run(() => VerifyPackage(path, package.Release, installing: true), token).ConfigureAwait(false);
            return lease; // Keep writes/deletion blocked until the installer process has opened the package.
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    // User-approved preview policy: only exact repository preview assets may use integrity/product checks instead of Authenticode.
    public const bool AllowUnsignedPreviewInstallation = true;
    private void VerifyPackage(string path, UpdateRelease release, bool installing)
    {
        if (release.IsPortable)
        {
            if (installing) throw new InvalidOperationException(PortableUpdateNotice);
            return; // Exact repository URL, byte count and SHA-256 were already checked.
        }
        if (release.IsUnsignedPreview && _verifier is SignedUpdateVerifier)
        {
            SignedUpdateVerifier.VerifyProduct(path, release.Version);
            if (!installing || AllowUnsignedPreviewInstallation) return;
        }
        _verifier.Verify(path, release.Version);
    }

    private static void ValidateRelease(UpdateRelease value)
    {
        var identity = ReleaseVersion.Parse(value.Tag);
        var names = new[] { $"BlueLink-Setup-{value.Version}-{RuntimeIdentifier}.exe", $"BlueLink-Review-{value.Version}-{RuntimeIdentifier}-Setup.exe", $"BlueLink-{value.Version}-{RuntimeIdentifier}-Portable.zip" };
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(value.Tag)}/{value.FileName}";
        if (identity?.Version != value.Version || value.Size is <= 0 or > MaximumPackageBytes || !names.Contains(value.FileName, StringComparer.Ordinal) ||
            value.Sha256.Length != 64 || value.Sha256.Any(c => !Uri.IsHexDigit(c)) || value.DownloadUrl.AbsoluteUri != expected)
            throw new InvalidDataException("更新包元数据无效。");
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
