"""Release gates: reject incomplete, unsigned, corrupted and mislabelled artifacts."""
import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("prepare_release", Path(__file__).with_name("prepare-release.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "input"
        self.output = self.root / "output"
        self.version = "0.2.17"
        self.tag = "v0.2.17-preview.1"
        self.commit = "a" * 40
        self.entries = {}
        for arch in release.ARCHES:
            group = f"windows-{arch}"
            rows = []
            for kind, name in [
                ("ReviewInstaller", f"BlueLink-Review-{self.version}-win-{arch}-Setup.exe"),
                ("ReviewInstaller", f"BlueLink-Review-{self.version}-win-{arch}-Setup.msi"),
                ("Portable", f"BlueLink-{self.version}-win-{arch}-Portable.zip"),
                ("ReviewInstallerNoRuntime", f"BlueLink-Review-{self.version}-win-{arch}-NoRuntime-Setup.exe"),
                ("ReviewInstallerNoRuntime", f"BlueLink-Review-{self.version}-win-{arch}-NoRuntime-Setup.msi"),
            ]:
                rows.append(dict(File=name, Architecture=arch, Kind=kind, Version=self.version,
                                 SelfContained=kind != "ReviewInstallerNoRuntime", RuntimeVerification="Passed: fixture", Signed=False))
            self.entries[group] = rows
        self.entries["android"] = [
            dict(File=f"BlueLink-{self.version}-android-{abi}-release.apk", Abi=abi,
                 Version=self.version, Variant="release", Signed=True, CertificateSha256="b" * 64,
                 NativeAbis=list(release.ABIS[:-1]) if abi == "universal" else [abi])
            for abi in release.ABIS
        ]
        for group, rows in self.entries.items():
            folder = self.source / group
            folder.mkdir(parents=True)
            for row in rows:
                path = folder / row["File"]
                path.write_bytes(("fixture: " + row["File"]).encode())
                row["Sha256"] = release.sha256(path)
        self.save()

    def save(self):
        for group, rows in self.entries.items():
            (self.source / group / "build-manifest.json").write_text(json.dumps(rows), encoding="utf-8-sig")

    def collect(self):
        return release.collect(self.source, self.output, self.tag, self.version, self.commit)

    def reject(self):
        self.save()
        with self.assertRaises((ValueError, FileNotFoundError)):
            self.collect()
        self.assertFalse(self.output.exists())

    def test_complete_release(self):
        self.assertEqual(len(self.collect()), 20)
        self.assertEqual(len(list(self.output.iterdir())), 23)
        for line in (self.output / "SHA256SUMS.txt").read_text().splitlines():
            digest, name = line.split("  ")
            self.assertEqual(digest, release.sha256(self.output / name))

    def test_wrong_runtime_variant(self):
        self.entries["windows-x64"][-1]["SelfContained"] = True
        self.reject()

    def test_missing_no_runtime_installer(self):
        self.entries["windows-x86"].pop()
        self.reject()

    def test_missing_architecture(self):
        self.entries["windows-arm64"] = []
        self.reject()

    def test_unsigned_android(self):
        self.entries["android"][0]["Signed"] = False
        self.reject()

    def test_certificate_mismatch(self):
        self.entries["android"][0]["CertificateSha256"] = "c" * 64
        self.reject()

    def test_corrupted_package(self):
        row = self.entries["windows-x86"][0]
        (self.source / "windows-x86" / row["File"]).write_bytes(b"corrupted")
        self.reject()

    def test_wrong_abi(self):
        self.entries["android"][0]["NativeAbis"] = ["x86"]
        self.reject()

    def test_unexpected_file(self):
        (self.source / "android" / "credentials.json").write_text("fixture")
        self.reject()

    def test_duplicate(self):
        self.entries["android"].append(copy.deepcopy(self.entries["android"][0]))
        self.reject()

    def test_path_traversal(self):
        self.entries["android"][0]["File"] = "../outside.apk"
        self.reject()

    def test_runtime_tests_skipped(self):
        self.entries["windows-x86"][0]["RuntimeVerification"] = "Not verified"
        self.reject()

    def test_stable_tag_rejected(self):
        self.tag = "v0.2.17"
        self.reject()

    def test_version_mismatch(self):
        self.entries["android"][0]["Version"] = "0.2.16"
        self.reject()


if __name__ == "__main__":
    unittest.main()
