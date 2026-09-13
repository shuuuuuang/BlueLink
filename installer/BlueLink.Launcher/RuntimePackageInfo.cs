#if NET8_0_OR_GREATER
#nullable disable
#endif
namespace BlueLink.Launcher
{
    using System;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Globalization;
    using System.Text.RegularExpressions;

    [DataContract]
    public sealed class RuntimePackageInfo
    {
        [DataMember(Name = "Version")]
        public string Version { get; set; } = "8.0.x";
        [DataMember(Name = "Url")]
        public string Url { get; set; } = "https://dotnet.microsoft.com/download/dotnet/8.0";
        [DataMember(Name = "Sha512")]
        public string Sha512 { get; set; } = "";
        [DataMember(Name = "Size")]
        public long Size { get; set; }
        [DataMember(Name = "FileName")]
        public string FileName { get; set; } = "windowsdesktop-runtime-win-x64.exe";
        [DataMember(Name = "ManualUrl")]
        public string ManualUrl { get; set; } = "https://dotnet.microsoft.com/download/dotnet/8.0/runtime";
        public string DisplaySize => Size <= 0 ? "正在读取实际包大小" : Format(Size);

        public static RuntimePackageInfo Load()
        {
            foreach (var path in new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bootstrap", "runtime-package.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime-package.json"),
            })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        // Windows PowerShell 5 writes UTF-8 build metadata with a BOM and
                        // some .NET Framework JSON readers silently retain initializer values.
                        // This manifest has a fixed installer-owned schema, so parse only its
                        // known scalar fields.
                        return ParseInstallerManifest(File.ReadAllText(path));
                    }
                }
                catch { }
            }
            return new RuntimePackageInfo();
        }

        private static RuntimePackageInfo ParseInstallerManifest(string json)
        {
            var value = new RuntimePackageInfo
            {
                Version = ReadString(json, "Version", "8.0.x"),
                Url = ReadString(json, "Url", String.Empty),
                Sha512 = ReadString(json, "Sha512", String.Empty),
                FileName = ReadString(json, "FileName", "windowsdesktop-runtime-win-x64.exe"),
                ManualUrl = ReadString(json, "ManualUrl", "https://dotnet.microsoft.com/download/dotnet/8.0/runtime")
            };
            var size = Regex.Match(json, "\\\"Size\\\"\\s*:\\s*(?<value>[0-9]+)", RegexOptions.IgnoreCase);
            long parsed = 0;
            if (size.Success) Int64.TryParse(size.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
            value.Size = parsed;
            return value;
        }

        private static string ReadString(string json, string name, string fallback)
        {
            var match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : fallback;
        }

        private static string Format(long value) => value >= 1024L * 1024L
            ? (value / 1024d / 1024d).ToString("0.0") + " MB"
            : (value / 1024d).ToString("0.0") + " KB";
    }
}
