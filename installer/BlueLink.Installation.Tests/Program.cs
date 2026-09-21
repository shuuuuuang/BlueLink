namespace BlueLink.Installation.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using BlueLink.Shared;

    internal static class Program
    {
        private static int assertions;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0].Equals("--execution-policy", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    TestInstallerExecutionPolicy();
                    Console.WriteLine("Installer execution policy tests passed: " + assertions + " assertions.");
                    return 0;
                }
                catch (Exception failure)
                {
                    Console.Error.WriteLine(failure);
                    return 1;
                }
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: BlueLink.Installation.Tests <valid-stage-root> <isolated-test-root> [installed-fixture-root]");
                return 2;
            }

            var source = Path.GetFullPath(args[0]);
            var testRoot = Path.GetFullPath(args[1]);
            var expectedBoundary = Path.Combine(Path.GetFullPath(Environment.CurrentDirectory), ".acceptance") + Path.DirectorySeparatorChar;
            if (!testRoot.StartsWith(expectedBoundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(testRoot).StartsWith("install-ownership-", StringComparison.OrdinalIgnoreCase) ||
                testRoot.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(testRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Installation tests require an isolated .acceptance/install-ownership-* directory.");
                return 2;
            }
            try
            {
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
                ResetDirectory(testRoot);

                AssertKind(source, InstallDirectoryKind.CurrentStructure, "generated stage");
                if (args.Length >= 3)
                    AssertKind(Path.GetFullPath(args[2]), InstallDirectoryKind.CurrentStructure,
                        "deployed double-space manifest fixture");

                TestWhitespaceVariants(source, testRoot);
                TestPayloadVerification(source, testRoot);
                TestDownloadIsUserOwned(source, testRoot);
                TestForeignContentIsRejected(source, testRoot);
                TestUnsafeManifestIsRejected(source, testRoot);
                TestIncompleteProductIsRejected(source, testRoot);
                TestRegisteredRecovery(source, testRoot);
                TestLegacySingleFile(source, testRoot);
                TestUserDataOnly(testRoot);
                TestInstallerExecutionPolicy();

                Console.WriteLine("Install ownership tests passed: " + assertions + " assertions.");
                return 0;
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine(failure);
                return 1;
            }
            finally
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
        }

        private static void TestWhitespaceVariants(string source, string testRoot)
        {
            var variants = new[]
            {
                new { Name = "single-space", Transform = new Func<string, string>(raw =>
                    Regex.Replace(raw, "\"ProductId\"\\s*:\\s*", "\"ProductId\": ", RegexOptions.None)) },
                new { Name = "double-space", Transform = new Func<string, string>(raw =>
                    Regex.Replace(raw, "\"ProductId\"\\s*:\\s*", "\"ProductId\":  ", RegexOptions.None)) },
                new { Name = "tabs", Transform = new Func<string, string>(raw =>
                    Regex.Replace(raw, "\"ProductId\"\\s*:\\s*", "\"ProductId\":\t", RegexOptions.None)) },
                new { Name = "lf", Transform = new Func<string, string>(raw => raw.Replace("\r\n", "\n")) },
                new { Name = "compact-lines", Transform = new Func<string, string>(raw =>
                    raw.Replace("\r", String.Empty).Replace("\n", String.Empty)) },
            };

            foreach (var variant in variants)
            {
                var root = CreateFixture(source, Path.Combine(testRoot, variant.Name));
                var manifest = Path.Combine(root, ".bluelink-install.json");
                var transformed = variant.Transform(File.ReadAllText(manifest));
                File.WriteAllText(manifest, transformed, new UTF8Encoding(false));
                AssertKind(root, InstallDirectoryKind.CurrentStructure, variant.Name);
            }

            var bomRoot = CreateFixture(source, Path.Combine(testRoot, "utf8-bom"));
            var bomManifest = Path.Combine(bomRoot, ".bluelink-install.json");
            File.WriteAllText(bomManifest, File.ReadAllText(bomManifest), new UTF8Encoding(true));
            AssertKind(bomRoot, InstallDirectoryKind.CurrentStructure, "UTF-8 BOM");
        }

        private static void TestDownloadIsUserOwned(string source, string testRoot)
        {
            var root = CreateFixture(source, Path.Combine(testRoot, "download-user-content"));
            var nested = Path.Combine(root, "Download", "用户文件", "payload.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(nested));
            File.WriteAllBytes(nested, new byte[] { 1, 2, 3, 4 });
            AssertKind(root, InstallDirectoryKind.CurrentStructure, "arbitrary Download content");
        }

        private static void TestPayloadVerification(string source, string testRoot)
        {
            var inspection = InstallDirectoryOwnership.Inspect(source);
            var manifest = inspection.Manifest;
            Assert(manifest != null && manifest.PayloadFiles != null && manifest.PayloadFiles.Count > 0,
                "generated manifest contains payload verification entries");

            string error;
            Assert(InstallDirectoryOwnership.VerifyInstalledPayload(source,
                    manifest.ApplicationVersion, manifest.PayloadFingerprint, out error),
                "generated stage payload hashes verify: " + error);
            Assert(!InstallDirectoryOwnership.VerifyInstalledPayload(source,
                    "99.99.99", manifest.PayloadFingerprint, out error) && error.Contains("应用版本"),
                "version mismatch is rejected");
            Assert(!InstallDirectoryOwnership.VerifyInstalledPayload(source,
                    manifest.ApplicationVersion, new string('0', 64), out error) && error.Contains("载荷标识"),
                "payload fingerprint mismatch is rejected");

            var tamperedRoot = CreateFixture(source, Path.Combine(testRoot, "tampered-payload"));
            var tamperedRelative = manifest.PayloadFiles.First().Path.Replace('/', Path.DirectorySeparatorChar);
            using (var stream = new FileStream(Path.Combine(tamperedRoot, tamperedRelative),
                       FileMode.Append, FileAccess.Write, FileShare.None))
                stream.WriteByte(0x5A);
            Assert(!InstallDirectoryOwnership.VerifyInstalledPayload(tamperedRoot,
                    manifest.ApplicationVersion, manifest.PayloadFingerprint, out error) &&
                    (error.Contains("大小") || error.Contains("校验失败")),
                "tampered installed payload is rejected");
        }

        private static void TestForeignContentIsRejected(string source, string testRoot)
        {
            var root = CreateFixture(source, Path.Combine(testRoot, "foreign"));
            File.WriteAllText(Path.Combine(root, "foreign-file.txt"), "not owned");
            var inspection = InstallDirectoryOwnership.Inspect(root);
            Assert(inspection.Kind == InstallDirectoryKind.ForeignContent, "foreign content kind");
            Assert(inspection.Message.Contains("foreign-file.txt"), "foreign content names rejected entry");
        }

        private static void TestUnsafeManifestIsRejected(string source, string testRoot)
        {
            var root = CreateFixture(source, Path.Combine(testRoot, "traversal"));
            var manifest = Path.Combine(root, ".bluelink-install.json");
            var raw = File.ReadAllText(manifest);
            var pattern = new Regex("\"OwnedPaths\"\\s*:\\s*\\[");
            var malicious = pattern.Replace(raw, "$0\r\n    \"../escape.dll\",", 1);
            Assert(!String.Equals(raw, malicious, StringComparison.Ordinal), "traversal fixture injection");
            raw = malicious;
            File.WriteAllText(manifest, raw, new UTF8Encoding(false));
            AssertKind(root, InstallDirectoryKind.InvalidManifest, "manifest traversal");
        }

        private static void TestIncompleteProductIsRejected(string source, string testRoot)
        {
            var root = CreateFixture(source, Path.Combine(testRoot, "incomplete"));
            File.Delete(Path.Combine(root, "app", "BlueLink.exe"));
            AssertKind(root, InstallDirectoryKind.IncompleteProduct, "missing app executable");
        }

        private static void TestRegisteredRecovery(string source, string testRoot)
        {
            var root = CreateFixture(source, Path.Combine(testRoot, "registered-recovery"));
            var manifest = Path.Combine(source, ".bluelink-install.json");
            foreach (var file in new[] { "BlueLink.exe", "BlueLink.exe.config", "Uninstall.exe",
                         "Uninstall.exe.config", ".bluelink-install.json" })
                File.Delete(Path.Combine(root, file));
            Assert(!InstallDirectoryOwnership.Inspect(root).IsInstallable, "unregistered partial install remains rejected");
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, null, manifest), "recovery requires MSI registration");
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root + "-other", manifest), "recovery rejects another registered path");
            Assert(InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root + "\\", manifest), "exact MSI partial install can recover");
            var download = Path.Combine(root, "Download", "keep.txt");
            File.WriteAllText(download, "user data");
            Assert(InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root, manifest) &&
                File.ReadAllText(download) == "user data", "recovery validation preserves Download");
            var foreign = Path.Combine(root, "bootstrap", "foreign.txt");
            File.WriteAllText(foreign, "foreign");
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root, manifest), "registered recovery rejects foreign nested file");
            File.Delete(foreign);
            File.WriteAllText(Path.Combine(root, ".bluelink-install.json"), "invalid");
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root, manifest), "registered recovery does not bypass invalid local manifest");
            File.Delete(Path.Combine(root, ".bluelink-install.json"));
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root, manifest + ".missing"), "recovery requires bundled manifest");
            File.WriteAllText(Path.Combine(root, "app", "BlueLink.exe"), "foreign executable");
            Assert(!InstallDirectoryOwnership.CanRecoverRegisteredInstall(root, root, manifest), "registered recovery rejects foreign app identity");
        }

        private static void TestLegacySingleFile(string source, string testRoot)
        {
            var root = Path.Combine(testRoot, "legacy");
            Directory.CreateDirectory(root);
            File.Copy(Path.Combine(source, "BlueLink.exe"), Path.Combine(root, "BlueLink.exe"));
            Directory.CreateDirectory(Path.Combine(root, "Download"));
            File.WriteAllText(Path.Combine(root, "Download", "user.txt"), "preserve");
            AssertKind(root, InstallDirectoryKind.LegacySingleFile, "legacy single-file install");

            File.WriteAllText(Path.Combine(root, "foreign.txt"), "not owned");
            AssertKind(root, InstallDirectoryKind.ForeignContent, "legacy foreign content");
        }

        private static void TestUserDataOnly(string testRoot)
        {
            var root = Path.Combine(testRoot, "download-only");
            var download = Path.Combine(root, "Download", "历史接收文件");
            Directory.CreateDirectory(download);
            File.WriteAllText(Path.Combine(download, "preserved.txt"), "user owned");
            AssertKind(root, InstallDirectoryKind.UserDataOnly, "preserved Download after uninstall");
        }

        private static void TestInstallerExecutionPolicy()
        {
            Assert(InstallerExecutionPolicy.IsEmbeddedRelatedExecution(true, false),
                "embedded display is a related execution boundary");
            Assert(InstallerExecutionPolicy.IsEmbeddedRelatedExecution(false, true),
                "non-none relation is a related execution boundary");
            Assert(!InstallerExecutionPolicy.IsEmbeddedRelatedExecution(false, false),
                "standalone execution is not embedded");
            Assert(!InstallerExecutionPolicy.ShouldVerifyStandaloneUninstall(true, true),
                "embedded related uninstall skips standalone file postconditions");
            Assert(InstallerExecutionPolicy.ShouldVerifyStandaloneUninstall(true, false),
                "standalone uninstall keeps strict postconditions");

            Assert(!InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(false, "0.2.11", "0.2.12"),
                "legacy 0.2.11 BA is not launched inside the new transaction");
            Assert(InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(false, "0.2.12.0", "0.2.12"),
                "first embedded-safe BA can use the recommended related-bundle plan");
            Assert(InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(false, "0.3.0+release", "0.2.12"),
                "newer semantic version can use the recommended related-bundle plan");
            Assert(!InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(true, "0.3.0", "0.2.12"),
                "standalone uninstall never removes a different related bundle");
            Assert(!InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(false, "unknown", "0.2.12"),
                "unknown legacy version fails safe without invoking its BA");
            Assert(InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan(true, true),
                "first safe related-bundle plan is executed");
            Assert(!InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan(true, false),
                "duplicate related-bundle plan is suppressed");
            Assert(!InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan(false, true),
                "unsafe related-bundle plan remains suppressed");

            Assert(InstallerExecutionPolicy.GetExecutePhase("BlueLinkMsi", false, false, false)
                    .Contains("新版程序文件"),
                "MSI execution has an accurate progress phase");
            Assert(InstallerExecutionPolicy.GetExecutePhase("{OLD-BUNDLE}", false, false, true)
                    .Contains("旧安装注册"),
                "related bundle execution is not labelled as file copy");

            Assert(InstallerExecutionPolicy.GetDisplayVersion("0.2.13+0424e45") == "0.2.13",
                "display version strips source revision metadata");
            Assert(InstallerExecutionPolicy.GetDisplayVersion("0.2.13-preview") == "0.2.13",
                "display version strips prerelease metadata");
            Assert(InstallerExecutionPolicy.GetDisplayVersion("not-a-version") == "未知",
                "invalid display version fails closed");

            Assert(InstallerExecutionPolicy.IsMsiExecutionPlanValid(true, true, true, "Install"),
                "install plan with executable MSI is accepted");
            Assert(InstallerExecutionPolicy.IsMsiExecutionPlanValid(true, true, true, "Repair"),
                "repair plan with executable MSI is accepted");
            Assert(!InstallerExecutionPolicy.IsMsiExecutionPlanValid(true, true, false, "None"),
                "no-op MSI plan is rejected");
            Assert(!InstallerExecutionPolicy.IsMsiExecutionPlanValid(true, false, false, null),
                "missing MSI plan is rejected");
            Assert(InstallerExecutionPolicy.IsMsiExecutionPlanValid(false, false, false, null),
                "runtime-only plan does not require application MSI execution");
            Assert(InstallerExecutionPolicy.ShouldRepairPresentMsi(false, false, "Present", "Install"),
                "standalone MSI followed by new bundle requests real repair");
            Assert(InstallerExecutionPolicy.ShouldRepairPresentMsi(false, false, "Present", "Repair"),
                "exact bundle repair retains package repair");
            foreach (var state in new[] { "Absent", "Obsolete", "Superseded", "Unknown", null })
                Assert(!InstallerExecutionPolicy.ShouldRepairPresentMsi(false, false, state, "Install"),
                    "only the exact present MSI may be repaired: " + state);
            Assert(!InstallerExecutionPolicy.ShouldRepairPresentMsi(true, false, "Present", "Install"),
                "runtime-only does not repair application MSI");
            Assert(!InstallerExecutionPolicy.ShouldRepairPresentMsi(false, true, "Present", "Install"),
                "uninstall context never becomes repair");
            Assert(!InstallerExecutionPolicy.ShouldRepairPresentMsi(false, false, "Present", "Uninstall"),
                "uninstall action never becomes repair");
            Assert(InstallerExecutionPolicy.ShouldConvertInstallToRepair(true, "Install"),
                "installed bundle converts command-line install to repair");
            Assert(InstallerExecutionPolicy.ShouldConvertInstallToRepair(true, "Unknown"),
                "installed bundle converts unknown default action to repair");
            Assert(!InstallerExecutionPolicy.ShouldConvertInstallToRepair(false, "Install"),
                "fresh install remains install");
            Assert(!InstallerExecutionPolicy.ShouldConvertInstallToRepair(true, "Uninstall"),
                "uninstall is never converted to repair");
            Assert(!InstallerExecutionPolicy.ShouldPlanRuntimePackage(false, true, false),
                "normal install/repair does not plan an already available runtime");
            Assert(InstallerExecutionPolicy.ShouldPlanRuntimePackage(false, false, false),
                "normal install plans a missing runtime");
            Assert(InstallerExecutionPolicy.ShouldPlanRuntimePackage(true, false, false),
                "runtime-only flow plans a missing runtime");
            Assert(!InstallerExecutionPolicy.ShouldPlanRuntimePackage(false, false, true),
                "BlueLink uninstall never plans the shared runtime");

            Assert(InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                    false, true, "0.2.11", "0.2.12"),
                "successful upgrade cleans a pre-embedded-safe legacy bundle registration");
            Assert(!InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                    false, false, "0.2.11", "0.2.12"),
                "failed upgrade preserves the legacy bundle for rollback");
            Assert(!InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                    true, true, "0.2.11", "0.2.12"),
                "standalone uninstall does not start a second legacy cleanup");
            Assert(!InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                    false, true, "0.2.12", "0.2.12"),
                "embedded-safe bundles remain owned by the Burn related plan");
            Assert(!InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                    false, true, "unknown", "0.2.12"),
                "unknown bundle versions are never launched by fallback cleanup");
        }

        private static string CreateFixture(string source, string destination)
        {
            CopyDirectory(source, destination);
            return destination;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void ResetDirectory(string directory)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
        }

        private static void AssertKind(string root, InstallDirectoryKind expected, string scenario)
        {
            var actual = InstallDirectoryOwnership.Inspect(root);
            Assert(actual.Kind == expected,
                scenario + ": expected " + expected + ", got " + actual.Kind + ". " + actual.Message);
        }

        private static void Assert(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Assertion failed: " + description);
        }
    }
}
