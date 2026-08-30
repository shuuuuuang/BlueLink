namespace BlueLink.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Security.Cryptography;
    using System.Text;

    internal enum InstallDirectoryKind
    {
        Empty,
        UserDataOnly,
        LegacySingleFile,
        CurrentStructure,
        InvalidManifest,
        IncompleteProduct,
        ForeignContent,
    }

    internal sealed class InstallDirectoryInspection
    {
        internal InstallDirectoryInspection(InstallDirectoryKind kind, string message = null,
            InstallOwnershipManifest manifest = null)
        {
            Kind = kind;
            Message = message;
            Manifest = manifest;
        }

        internal InstallDirectoryKind Kind { get; }
        internal string Message { get; }
        internal InstallOwnershipManifest Manifest { get; }
        internal bool IsRecognizedInstall =>
            Kind == InstallDirectoryKind.LegacySingleFile || Kind == InstallDirectoryKind.CurrentStructure;
        internal bool IsInstallable => Kind == InstallDirectoryKind.Empty ||
            Kind == InstallDirectoryKind.UserDataOnly || IsRecognizedInstall;
    }

    [DataContract]
    internal sealed class InstallOwnershipManifest
    {
        [DataMember(Name = "ProductId", IsRequired = true)]
        public string ProductId { get; set; }

        [DataMember(Name = "StructureVersion", IsRequired = true)]
        public int StructureVersion { get; set; }

        [DataMember(Name = "ApplicationVersion", IsRequired = true)]
        public string ApplicationVersion { get; set; }

        [DataMember(Name = "OwnedPaths", IsRequired = true)]
        public List<string> OwnedPaths { get; set; }

        [DataMember(Name = "BuildId", IsRequired = false)]
        public string BuildId { get; set; }

        [DataMember(Name = "PayloadFingerprint", IsRequired = false)]
        public string PayloadFingerprint { get; set; }

        [DataMember(Name = "PayloadFiles", IsRequired = false)]
        public List<InstallPayloadFile> PayloadFiles { get; set; }
    }

    [DataContract]
    internal sealed class InstallPayloadFile
    {
        [DataMember(Name = "Path", IsRequired = true)]
        public string Path { get; set; }

        [DataMember(Name = "Length", IsRequired = true)]
        public long Length { get; set; }

        [DataMember(Name = "Sha256", IsRequired = true)]
        public string Sha256 { get; set; }
    }

    internal static class InstallDirectoryOwnership
    {
        internal const string ProductId = "BlueLink.Desktop";
        internal const int CurrentStructureVersion = 2;

        private static readonly HashSet<string> CurrentTopLevelNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BlueLink.exe", "BlueLink.exe.config", "Uninstall.exe", "Uninstall.exe.config",
                ".bluelink-install.json", "app", "bootstrap", "Download",
            };

        private static readonly string[] RequiredOwnedPaths =
        {
            "BlueLink.exe",
            "BlueLink.exe.config",
            "Uninstall.exe",
            "Uninstall.exe.config",
            ".bluelink-install.json",
            "app\\BlueLink.exe",
            "bootstrap\\runtime-package.json",
        };

        internal static InstallDirectoryInspection Inspect(string folder)
        {
            string root;
            try { root = Path.GetFullPath(folder); }
            catch (Exception failure)
            {
                return Invalid(InstallDirectoryKind.ForeignContent,
                    "安装路径无效：" + failure.Message);
            }

            if (!Directory.Exists(root)) return new InstallDirectoryInspection(InstallDirectoryKind.Empty);

            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(root).EnumerateFileSystemInfos().ToArray(); }
            catch (Exception failure)
            {
                return Invalid(InstallDirectoryKind.ForeignContent,
                    "无法读取所选安装目录：" + failure.Message);
            }
            if (entries.Length == 0) return new InstallDirectoryInspection(InstallDirectoryKind.Empty);
            if (entries.All(entry => entry is DirectoryInfo &&
                    entry.Name.Equals("Download", StringComparison.OrdinalIgnoreCase)))
                return new InstallDirectoryInspection(InstallDirectoryKind.UserDataOnly);

            var manifestPath = Path.Combine(root, ".bluelink-install.json");
            if (File.Exists(manifestPath)) return InspectCurrentStructure(root, entries, manifestPath);
            return InspectLegacyStructure(root, entries);
        }

        internal static void ValidateInstallable(string folder)
        {
            var inspection = Inspect(folder);
            if (!inspection.IsInstallable)
                throw new InvalidOperationException(inspection.Message ?? "所选安装目录无效。");
        }

        internal static bool IsBlueLinkExecutable(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var productName = FileVersionInfo.GetVersionInfo(path).ProductName;
                return String.Equals(productName, "BlueLink", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(productName, "蓝联 BlueLink", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool IsProductRollbackFile(FileSystemInfo entry)
        {
            var file = entry as FileInfo;
            if (file == null || !file.Extension.Equals(".rbf", StringComparison.OrdinalIgnoreCase)) return false;

            var stem = Path.GetFileNameWithoutExtension(file.Name);
            if (stem.Length != 7 || stem.Any(character => !Uri.IsHexDigit(character))) return false;
            try
            {
                var product = FileVersionInfo.GetVersionInfo(file.FullName);
                return String.Equals(product.ProductName, "BlueLink", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(product.FileDescription, "BlueLink", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool VerifyInstalledPayload(string root, string expectedVersion,
            string expectedFingerprint, out string error)
        {
            var inspection = Inspect(root);
            if (inspection.Kind != InstallDirectoryKind.CurrentStructure || inspection.Manifest == null)
            {
                error = inspection.Message ?? "安装完成后未检测到有效的蓝联程序结构。";
                return false;
            }

            var manifest = inspection.Manifest;
            if (!String.Equals(manifest.ApplicationVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                error = "安装完成后检测到的应用版本仍为 " + manifest.ApplicationVersion +
                    "，预期为 " + expectedVersion + "。程序文件未被正确替换。";
                return false;
            }
            if (String.IsNullOrWhiteSpace(expectedFingerprint) ||
                !String.Equals(manifest.PayloadFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                error = "安装完成后的程序载荷标识与当前安装包不一致。程序文件未被正确替换。";
                return false;
            }
            if (manifest.PayloadFiles == null || manifest.PayloadFiles.Count == 0)
            {
                error = "安装归属清单缺少程序文件校验信息，无法确认覆盖安装结果。";
                return false;
            }

            string invalidPath;
            var verificationPaths = NormalizeOwnedPaths(root,
                manifest.PayloadFiles.Select(file => file == null ? null : file.Path), out invalidPath);
            var ownedPaths = NormalizeOwnedPaths(root, manifest.OwnedPaths, out invalidPath);
            if (verificationPaths == null || ownedPaths == null)
            {
                error = "安装归属清单包含不安全或重复的程序校验路径：" + invalidPath + "。";
                return false;
            }

            foreach (var payload in manifest.PayloadFiles)
            {
                var relative = payload.Path.Replace('/', Path.DirectorySeparatorChar)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (!ownedPaths.Contains(relative) ||
                    relative.Equals(".bluelink-install.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("Download", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("Download\\", StringComparison.OrdinalIgnoreCase) ||
                    payload.Length < 0 || !IsSha256(payload.Sha256))
                {
                    error = "安装归属清单中的程序校验项无效：" + payload.Path + "。";
                    return false;
                }

                var fullPath = Path.Combine(root, relative);
                var file = new FileInfo(fullPath);
                if (!file.Exists || file.Length != payload.Length)
                {
                    error = "安装完成后的程序文件缺失或大小不一致：" + payload.Path + "。";
                    return false;
                }

                string actualHash;
                using (var stream = file.OpenRead())
                using (var sha = SHA256.Create())
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
                if (!actualHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = "安装完成后的程序文件校验失败：" + payload.Path + "。";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static InstallDirectoryInspection InspectCurrentStructure(string root,
            IEnumerable<FileSystemInfo> entries, string manifestPath)
        {
            InstallOwnershipManifest manifest;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(InstallOwnershipManifest));
                string json;
                using (var reader = new StreamReader(manifestPath, Encoding.UTF8, true))
                    json = reader.ReadToEnd().TrimStart('\uFEFF');
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    manifest = serializer.ReadObject(stream) as InstallOwnershipManifest;
            }
            catch (Exception failure)
            {
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单无法解析：" + failure.Message + " 已停止覆盖安装。");
            }

            if (manifest == null || !String.Equals(manifest.ProductId, ProductId, StringComparison.Ordinal) ||
                manifest.StructureVersion != CurrentStructureVersion ||
                String.IsNullOrWhiteSpace(manifest.ApplicationVersion) || manifest.OwnedPaths == null)
            {
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单的产品标识、结构版本或应用版本无效，已停止覆盖安装。");
            }

            Version parsedVersion;
            if (!Version.TryParse(manifest.ApplicationVersion, out parsedVersion))
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单中的应用版本无效，已停止覆盖安装。");

            string invalidOwnedPath;
            var ownedPaths = NormalizeOwnedPaths(root, manifest.OwnedPaths, out invalidOwnedPath);
            if (ownedPaths == null)
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单包含不安全或重复的路径：" + invalidOwnedPath + "。已停止覆盖安装。");

            var forbiddenOwnedPath = ownedPaths.FirstOrDefault(path =>
                path.Equals("Download", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Download\\", StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedOwnedPath(path));
            if (forbiddenOwnedPath != null)
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单声明了不允许由安装器拥有的路径：" + forbiddenOwnedPath + "。");

            var missingDeclaration = RequiredOwnedPaths.FirstOrDefault(path => !ownedPaths.Contains(path));
            if (missingDeclaration != null)
                return Invalid(InstallDirectoryKind.InvalidManifest,
                    "蓝联安装归属清单缺少必要路径：" + missingDeclaration + "。");

            var invalidTopLevel = entries.FirstOrDefault(entry =>
                !CurrentTopLevelNames.Contains(entry.Name) && !IsProductRollbackFile(entry));
            if (invalidTopLevel != null)
                return Foreign(invalidTopLevel.Name);

            var wrongEntryType = entries.FirstOrDefault(entry =>
                (entry.Name.Equals("Download", StringComparison.OrdinalIgnoreCase) ||
                 entry.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                 entry.Name.Equals("bootstrap", StringComparison.OrdinalIgnoreCase))
                    ? !(entry is DirectoryInfo)
                    : CurrentTopLevelNames.Contains(entry.Name) && !(entry is FileInfo));
            if (wrongEntryType != null) return Foreign(wrongEntryType.Name);

            if (!IsBlueLinkExecutable(Path.Combine(root, "BlueLink.exe")) ||
                !IsBlueLinkExecutable(Path.Combine(root, "app", "BlueLink.exe")))
            {
                return Invalid(InstallDirectoryKind.IncompleteProduct,
                    "所选目录中的蓝联启动程序缺失或产品身份无效，已停止覆盖安装。");
            }

            var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string unsafeEntry;
            if (!CollectProgramFiles(root, new DirectoryInfo(root), actualFiles, out unsafeEntry))
                return Foreign(unsafeEntry);

            var unownedFile = actualFiles.FirstOrDefault(path => !ownedPaths.Contains(path));
            if (unownedFile != null) return Foreign(unownedFile);

            var missingFile = ownedPaths.FirstOrDefault(path => !File.Exists(Path.Combine(root, path)));
            if (missingFile != null)
                return Invalid(InstallDirectoryKind.IncompleteProduct,
                    "蓝联安装目录不完整，清单记录的程序文件不存在：" + missingFile + "。");

            return new InstallDirectoryInspection(InstallDirectoryKind.CurrentStructure, null, manifest);
        }

        private static InstallDirectoryInspection InspectLegacyStructure(string root,
            IEnumerable<FileSystemInfo> entries)
        {
            var executable = Path.Combine(root, "BlueLink.exe");
            if (!IsBlueLinkExecutable(executable))
                return Foreign(entries.First().Name);

            var invalid = entries.FirstOrDefault(entry =>
                !(entry is FileInfo && entry.Name.Equals("BlueLink.exe", StringComparison.OrdinalIgnoreCase)) &&
                !(entry is DirectoryInfo && entry.Name.Equals("Download", StringComparison.OrdinalIgnoreCase)) &&
                !IsProductRollbackFile(entry));
            return invalid == null
                ? new InstallDirectoryInspection(InstallDirectoryKind.LegacySingleFile)
                : Foreign(invalid.Name);
        }

        private static HashSet<string> NormalizeOwnedPaths(string root, IEnumerable<string> paths,
            out string invalidPath)
        {
            invalidPath = null;
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (var value in paths)
            {
                if (String.IsNullOrWhiteSpace(value) || value != value.Trim() || Path.IsPathRooted(value))
                {
                    invalidPath = value ?? "<null>";
                    return null;
                }

                var candidate = value.Replace('/', Path.DirectorySeparatorChar)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var segments = candidate.Split(Path.DirectorySeparatorChar);
                if (segments.Any(segment => String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                {
                    invalidPath = value;
                    return null;
                }

                string full;
                try { full = Path.GetFullPath(Path.Combine(root, candidate)); }
                catch
                {
                    invalidPath = value;
                    return null;
                }
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !normalized.Add(candidate))
                {
                    invalidPath = value;
                    return null;
                }
            }
            return normalized;
        }

        private static bool IsSupportedOwnedPath(string path)
        {
            return path.Equals("BlueLink.exe", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("BlueLink.exe.config", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("Uninstall.exe.config", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(".bluelink-install.json", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("app\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("bootstrap\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256(string value) =>
            !String.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

        private static bool CollectProgramFiles(string root, DirectoryInfo directory,
            ISet<string> files, out string unsafeEntry)
        {
            unsafeEntry = null;
            FileSystemInfo[] entries;
            try { entries = directory.EnumerateFileSystemInfos().ToArray(); }
            catch (Exception failure)
            {
                unsafeEntry = directory.FullName + "（无法读取：" + failure.Message + "）";
                return false;
            }

            foreach (var entry in entries)
            {
                if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    entry is DirectoryInfo && entry.Name.Equals("Download", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsProductRollbackFile(entry)) continue;

                var relative = entry.FullName.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    unsafeEntry = relative;
                    return false;
                }
                var childDirectory = entry as DirectoryInfo;
                if (childDirectory != null)
                {
                    if (!CollectProgramFiles(root, childDirectory, files, out unsafeEntry)) return false;
                }
                else files.Add(relative);
            }
            return true;
        }

        private static InstallDirectoryInspection Foreign(string entry)
        {
            return Invalid(InstallDirectoryKind.ForeignContent,
                "所选目录包含不属于蓝联的项目：" + entry + "。请选择空目录或经过产品身份验证的蓝联目录；Download 中的用户文件不会被清理。");
        }

        private static InstallDirectoryInspection Invalid(InstallDirectoryKind kind, string message) =>
            new InstallDirectoryInspection(kind, message);
    }
}
