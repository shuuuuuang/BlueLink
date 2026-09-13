namespace BlueLink.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.Win32;

    internal static class RuntimeDetector
    {
        public static bool HasBundledDesktopRuntime()
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
            return File.Exists(Path.Combine(root, "coreclr.dll")) && File.Exists(Path.Combine(root, "hostfxr.dll")) &&
                File.Exists(Path.Combine(root, "PresentationFramework.dll"));
        }

        // Keep the old entry point for existing installer verification callers.
        public static bool IsDesktopRuntime8X64Installed() => IsDesktopRuntime8Installed("x64");

        public static bool IsDesktopRuntime8Installed(string architecture)
        {
            foreach (var root in CandidateRoots(architecture))
                if (HasDesktopRuntime8(root, architecture)) return true;
            return false;
        }

        private static IEnumerable<string> CandidateRoots(string architecture)
        {
            ExpectedMachine(architecture);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = value => { if (!String.IsNullOrWhiteSpace(value)) roots.Add(value); };
            add(Environment.GetEnvironmentVariable("DOTNET_ROOT_" + architecture.ToUpperInvariant()));
            if (architecture == "x86") add(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"));
            add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            // SDK/host registrations can be in either view; the key names identify the target architecture.
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\" + architecture))
                        add(key?.GetValue("InstallLocation") as string);
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            foreach (var programFiles in new[] {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (String.IsNullOrWhiteSpace(programFiles)) continue;
                add(Path.Combine(programFiles, "dotnet"));
                if (architecture == "x64") add(Path.Combine(programFiles, "dotnet", "x64"));
            }
            return roots;
        }

        internal static bool HasDesktopRuntime8(string root, string architecture)
        {
            var machine = ExpectedMachine(architecture);
            try
            {
                // A directory called "dotnet" is not enough: avoid x86/ARM64 false positives.
                if (!MatchesMachine(Path.Combine(root, "dotnet.exe"), machine)) return false;
                var desktop = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                var core = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(desktop) || !Directory.Exists(core)) return false;
                foreach (var folder in Directory.EnumerateDirectories(desktop))
                {
                    Version version;
                    if (!Version.TryParse(Path.GetFileName(folder), out version) || version.Major != 8 ||
                        !File.Exists(Path.Combine(folder, "PresentationFramework.dll")) ||
                        !MatchesMachine(Path.Combine(folder, "wpfgfx_cor3.dll"), machine)) continue;
                    foreach (var coreFolder in Directory.EnumerateDirectories(core))
                    {
                        Version coreVersion;
                        if (Version.TryParse(Path.GetFileName(coreFolder), out coreVersion) &&
                            coreVersion.Major == 8 && coreVersion >= version &&
                            MatchesMachine(Path.Combine(coreFolder, "coreclr.dll"), machine)) return true;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (ArgumentException) { }
            return false;
        }

        private static ushort ExpectedMachine(string architecture)
        {
            switch (architecture)
            {
                case "x86": return 0x14c;
                case "x64": return 0x8664;
                case "arm64": return 0xaa64;
                default: throw new ArgumentException("Unsupported .NET runtime architecture.", nameof(architecture));
            }
        }

        private static bool MatchesMachine(string path, ushort expected)
        {
            if (!File.Exists(path)) return false;
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadUInt16() != 0x5a4d) return false;
                reader.BaseStream.Position = 0x3c;
                var offset = reader.ReadInt32();
                if (offset < 0 || offset > reader.BaseStream.Length - 6) return false;
                reader.BaseStream.Position = offset;
                return reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == expected;
            }
        }
    }
}
