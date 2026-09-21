using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using BlueLink.Updates;

internal static class GitHubReleaseVerification
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        ReleaseVersion Parse(string tag) => ReleaseVersion.Parse(tag) ?? throw new InvalidOperationException(tag);
        Check(Parse("v0.2.17-preview.10").CompareTo(Parse("v0.2.17-preview.9")) > 0, "numeric preview ordering");
        Check(Parse("v0.2.17").CompareTo(Parse("v0.2.17-preview.2147483647")) > 0, "stable follows all previews");
        Check(Parse("v0.2.18-preview.1").CompareTo(Parse("v0.2.17")) > 0, "next-version preview");
        foreach (var tag in new[] { "vv0.2.17", "v0.2", "v0.2.17.0", "v0.2.17-preview.0", "v00.2.17", "v0.2.17-beta.1", "v0.2.17-preview.999999999999" })
            Check(ReleaseVersion.Parse(tag) is null, "reject malformed tag " + tag);
        object Entry(string tag, string file, bool draft = false, string? url = null, string? digest = null) => new
        {
            tag_name = tag, draft, prerelease = tag.Contains("preview"), body = "GitHub release notes",
            assets = new[] { new { name = file, browser_download_url = url ?? $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/{file}",
                size = 1024, digest = digest ?? "sha256:" + new string('a', 64) } }
        };
        byte[] Feed(params object[] entries) => JsonSerializer.SerializeToUtf8Bytes(entries);
        foreach (var rid in new[] { "win-x86", "win-x64", "win-arm64" })
        {
            var name = $"BlueLink-Review-0.2.17-{rid}-Setup.exe";
            var bytes = Feed(Entry("v0.2.17-preview.2", name), Entry("v0.2.17-preview.10", name), Entry("v9.0.0", name, true));
            var release = UpdateService.ParseFeed(bytes, Parse("v0.2.17-preview.1"), runtimeIdentifier: rid, portable: false);
            Check(release?.Tag == "v0.2.17-preview.10" && release.IsUnsignedPreview && release.FileName == name, "preview installer " + rid);
            Check(UpdateService.ParseFeed(bytes, Parse("v0.2.17-preview.10"), runtimeIdentifier: rid, portable: false) is null, "same preview");
            Check(UpdateService.ParseFeed(bytes, Parse("v0.2.17"), runtimeIdentifier: rid, portable: false) is null, "stable cannot downgrade to preview");
            Check(UpdateService.ParseFeed(bytes, Parse("v0.2.16"), includePrereleases: false, runtimeIdentifier: rid, portable: false) is null, "stable-only empty feed");
            var zip = $"BlueLink-0.2.17-{rid}-Portable.zip";
            var portable = UpdateService.ParseFeed(Feed(Entry("v0.2.17-preview.10", zip)), Parse("v0.2.16"), runtimeIdentifier: rid, portable: true);
            Check(portable?.FileName == zip && portable.IsPortable, "portable selects ZIP instead of installer");
        }
        var stableName = $"BlueLink-Setup-0.3.0-{UpdateService.RuntimeIdentifier}.exe";
        var stable = UpdateService.ParseFeed(Feed(Entry("v0.3.0", stableName)), Parse("v0.2.17"), portable: false);
        Check(stable?.Version == new Version(0, 3, 0) && !stable.IsUnsignedPreview, "signed stable format retains automatic verification flow");
        Check(UpdateService.ParseFeed(Feed(), Parse("v0.2.17")) is null, "empty release feed");
        foreach (var entry in new[] {
            Entry("v0.3.0", stableName, url: "https://example.com/setup.exe"),
            Entry("v0.3.0", stableName, digest: "bad") })
        {
            try { UpdateService.ParseFeed(Feed(entry), Parse("v0.2.17"), portable: false); throw new InvalidOperationException("unsafe asset accepted"); }
            catch (InvalidDataException) { checks++; }
        }
        Console.WriteLine($"GitHub release selection verification passed: {checks} checks.");
    }

    internal static async Task VerifyLocalPreviewAsync(string installer, string output)
    {
        var bytes = await File.ReadAllBytesAsync(installer);
        var version = new Version(0, 2, 17);
        var tag = "v0.2.17-preview.3";
        var name = $"BlueLink-Review-{version}-{UpdateService.RuntimeIdentifier}-Setup.exe";
        var release = new UpdateRelease(version, "Local transport fixture using a real Review installer", name,
            new Uri($"https://github.com/{UpdateService.Repository}/releases/download/{tag}/{name}"), bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes))) { Tag = tag };
        using var service = new UpdateService(output, new PackageSource(bytes));
        var package = await service.DownloadAsync(release, null, CancellationToken.None);
        await using (var lease = await service.AcquireVerifiedPackageAsync(package, CancellationToken.None)) { }
        var checks = 1;
        foreach (var invalid in new[] {
            release with { Tag = "v0.2.17", DownloadUrl = new Uri($"https://github.com/{UpdateService.Repository}/releases/download/v0.2.17/{name}") },
            release with { DownloadUrl = new Uri("https://example.com/" + name) },
            release with { Sha256 = new string('0', 64) },
            release with { Version = new Version(0, 2, 18) } })
        {
            try { await service.DownloadAsync(invalid, null, CancellationToken.None); }
            catch (InvalidDataException) { checks++; continue; }
            throw new InvalidOperationException("Unsigned preview exception accepted an invalid package.");
        }
        using (var stream = new FileStream(package.Path, FileMode.Open, FileAccess.Write)) stream.WriteByte(0);
        try { await using var lease = await service.AcquireVerifiedPackageAsync(package, CancellationToken.None); }
        catch (InvalidDataException) { checks++; }
        if (checks != 6) throw new InvalidOperationException("Modified preview was accepted.");
        Console.WriteLine($"Production unsigned preview verification passed: {checks} checks; real PE with local HTTP fixture, no installer launched.");
    }
    private sealed class PackageSource(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    internal static async Task DownloadLiveAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var service = new UpdateService(Path.Combine(output, "packages"));
        var release = await service.CheckAsync(new Version(0, 0, 0), CancellationToken.None)
            ?? throw new InvalidOperationException("Expected a public update.");
        var package = await service.DownloadAsync(release, null, CancellationToken.None);
        if (!File.Exists(package.Path) || new FileInfo(package.Path).Length != release.Size) throw new InvalidOperationException("Download was not preserved.");
        await File.WriteAllTextAsync(Path.Combine(output, "downloaded.json"), JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Public in-app Windows download passed: {release.Tag}, {release.FileName}, {release.Size} bytes. No installer launched.");
        try
        {
            await using var lease = await service.AcquireVerifiedPackageAsync(package, CancellationToken.None);
            if (release.IsUnsignedPreview && !UpdateService.AllowUnsignedPreviewInstallation) throw new InvalidOperationException("Unsigned preview installation gate was bypassed.");
            Console.WriteLine("Installation preflight passed; no installer launched.");
        }
        catch (InvalidDataException error) when (release.IsUnsignedPreview && !UpdateService.AllowUnsignedPreviewInstallation)
        {
            Console.WriteLine("Unsigned preview installation remains blocked: " + error.Message);
        }
    }

    internal static async Task LiveAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var service = new UpdateService(Path.Combine(output, "unused-download-cache"));
        var release = await service.CheckAsync(new Version(0, 0, 0), CancellationToken.None);
        if (release is null) throw new InvalidOperationException("Expected a public release for the simulated older client.");
        await File.WriteAllTextAsync(Path.Combine(output, "github-release.json"), JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Public GitHub check passed: {release.Tag}, {release.FileName}, {release.Size} bytes; unsignedPreview={release.IsUnsignedPreview}. No download or installer launch.");
    }
}
