namespace BlueLink.Launcher
{
    using System;
    using System.IO;
    using Microsoft.Win32;

    internal static class RuntimeDetector
    {
        public static bool IsDesktopRuntime8X64Installed()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                {
                    if (key != null)
                        foreach (var name in key.GetValueNames())
                            if (Version.TryParse(name, out var version) && version.Major == 8) return true;
                }
            }
            catch { }
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var shared = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            try
            {
                if (Directory.Exists(shared))
                    foreach (var folder in Directory.EnumerateDirectories(shared))
                        if (Version.TryParse(Path.GetFileName(folder), out var version) && version.Major == 8) return true;
            }
            catch { }
            return false;
        }
    }
}
