using System.IO;
using BlueLink.Launcher;

internal static class RuntimePackageVerification
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkRuntimeVerification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var item in new[] { ("x86", (ushort)0x14c), ("x64", (ushort)0x8664), ("arm64", (ushort)0xaa64) })
            {
                var path = Path.Combine(root, item.Item1);
                WritePe(Path.Combine(path, "dotnet.exe"), item.Item2);
                WritePe(Path.Combine(path, "shared", "Microsoft.NETCore.App", "8.0.30", "coreclr.dll"), item.Item2);
                var desktop = Path.Combine(path, "shared", "Microsoft.WindowsDesktop.App", "8.0.30");
                WritePe(Path.Combine(desktop, "wpfgfx_cor3.dll"), item.Item2);
                File.WriteAllText(Path.Combine(desktop, "PresentationFramework.dll"), "fixture");
                Check(RuntimeDetector.HasDesktopRuntime8(path, item.Item1), "Complete matching runtime is detected.");
                Check(!RuntimeDetector.HasDesktopRuntime8(path, item.Item1 == "x64" ? "arm64" : "x64"), "Other architecture is rejected.");
                // A broken older runtime must not hide a valid newer installation.
                Directory.CreateDirectory(Path.Combine(path, "shared", "Microsoft.WindowsDesktop.App", "8.0.0"));
                Check(RuntimeDetector.HasDesktopRuntime8(path, item.Item1), "Broken old runtime does not hide the valid patch.");
                File.Delete(Path.Combine(desktop, "wpfgfx_cor3.dll"));
                Check(!RuntimeDetector.HasDesktopRuntime8(path, item.Item1), "Incomplete desktop runtime is rejected.");
                WritePe(Path.Combine(desktop, "wpfgfx_cor3.dll"), item.Item2);
                File.Delete(Path.Combine(path, "shared", "Microsoft.NETCore.App", "8.0.30", "coreclr.dll"));
                Check(!RuntimeDetector.HasDesktopRuntime8(path, item.Item1), "Desktop without base runtime is rejected.");

                var rid = "win-" + item.Item1;
                var package = RuntimePackageInfo.ParseInstallerManifest("\uFEFF{\"Version\":\"8.0.30\",\"Rid\":\"" + rid + "\"}");
                Check(package.Architecture == item.Item1, "Manifest targets the client rather than bootstrapper architecture.");
                package.Size = 1024; package.Sha512 = new string('a', 128);
                package.Url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.30/windowsdesktop-runtime-8.0.30-" + rid + ".exe";
                RuntimeDownloadService.Validate(package);
                checks++;
                package.Url = package.Url.Replace("-" + rid + ".exe", "-win-wrong.exe");
                try { RuntimeDownloadService.Validate(package); throw new Exception("Mismatched runtime URL was accepted."); }
                catch (IOException) { checks++; }
            }
            var legacy = RuntimePackageInfo.ParseInstallerManifest("{\"Version\":\"8.0.30\"}");
            Check(legacy.Architecture == "x64", "Existing manifests retain the legacy x64 default.");
            try { _ = new RuntimePackageInfo { Rid = "win-arm" }.Architecture; throw new Exception("ARM32 accepted."); }
            catch (InvalidDataException) { checks++; }
            Check(!RuntimeDetector.HasDesktopRuntime8(Path.Combine(root, "missing"), "x64"), "Missing installation returns false.");
            Console.WriteLine("Runtime architecture verification passed: " + checks + " checks.");
            await new InstallationVerification().RunAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WritePe(string path, ushort machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x5a4d);
        writer.BaseStream.Position = 0x3c; writer.Write(0x80);
        writer.BaseStream.Position = 0x80; writer.Write((uint)0x4550); writer.Write(machine);
    }
}
