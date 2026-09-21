"""Validate preview identity and assemble only verified release assets (standard library)."""
import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ABIS = ("armeabi-v7a", "arm64-v8a", "x86", "x86_64", "universal")
ARCHES = ("x86", "x64", "arm64")


def validate_tag(tag, version):
    if not re.fullmatch(r"v" + re.escape(version) + r"-preview\.[1-9][0-9]*", tag):
        raise ValueError(f"Review packages require v{version}-preview.N (N >= 1).")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def collect(source, output, tag, version, commit):
    validate_tag(tag, version)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected the full source commit SHA.")
    expected = {}
    for arch in ARCHES:
        for extension in ("exe", "msi"):
            expected[f"BlueLink-Review-{version}-win-{arch}-Setup.{extension}"] = (f"windows-{arch}", arch, "ReviewInstaller")
            expected[f"BlueLink-Review-{version}-win-{arch}-NoRuntime-Setup.{extension}"] = (f"windows-{arch}", arch, "ReviewInstallerNoRuntime")
        expected[f"BlueLink-{version}-win-{arch}-Portable.zip"] = (f"windows-{arch}", arch, "Portable")
    for abi in ABIS:
        expected[f"BlueLink-{version}-android-{abi}-release.apk"] = ("android", abi, "release")
    records = []
    seen = set()
    certificates = set()
    # Validate everything before creating an output directory or copying any assets.
    for group in ("windows-x86", "windows-x64", "windows-arm64", "android"):
        folder = source / group
        entries = json.loads((folder / "build-manifest.json").read_text(encoding="utf-8-sig"))
        if not isinstance(entries, list):
            raise ValueError(f"Manifest must be an array: {group}")
        declared = {"build-manifest.json"}
        for entry in entries:
            name = entry["File"]
            if name not in expected or name in seen:
                raise ValueError(f"Unexpected or duplicate release asset: {name}")
            origin, architecture, kind = expected[name]
            if origin != group or entry.get("Version") != version:
                raise ValueError(f"Asset origin/version mismatch: {name}")
            if group == "android":
                if entry.get("Abi") != architecture or entry.get("Variant") != kind or entry.get("Signed") is not True:
                    raise ValueError(f"Unsigned or incorrect Android release: {name}")
                native = set(entry.get("NativeAbis", []))
                if native != (set(ABIS[:-1]) if architecture == "universal" else {architecture}):
                    raise ValueError(f"Wrong APK native libraries: {name}")
                cert = entry.get("CertificateSha256", "")
                if not re.fullmatch(r"[0-9a-f]{64}", cert):
                    raise ValueError("Missing Android signing certificate fingerprint.")
                certificates.add(cert)
            else:
                if entry.get("Architecture") != architecture or entry.get("Kind") != kind or entry.get("SelfContained") is not (kind != "ReviewInstallerNoRuntime"):
                    raise ValueError(f"Windows package metadata mismatch: {name}")
                if architecture != "arm64" and not entry.get("RuntimeVerification", "").startswith("Passed"):
                    raise ValueError(f"Native regression not verified: {name}")
            path = folder / name
            if not path.is_file() or path.is_symlink() or sha256(path) != entry.get("Sha256", "").lower():
                raise ValueError(f"Release asset hash mismatch: {name}")
            if "Size" in entry and entry["Size"] != path.stat().st_size:
                raise ValueError(f"Release asset size mismatch: {name}")
            records.append(dict(entry, Sha256=sha256(path), Size=path.stat().st_size, SourceGroup=group))
            seen.add(name)
            declared.add(name)
        if {p.name for p in folder.iterdir()} != declared:
            raise ValueError(f"Unexpected files in artifact: {group}")
    if seen != set(expected) or len(certificates) != 1:
        raise ValueError("Incomplete release or inconsistent Android signing identities.")
    output.mkdir(parents=True, exist_ok=False)
    records.sort(key=lambda entry: entry["File"])
    for entry in records:
        shutil.copyfile(source / entry["SourceGroup"] / entry["File"], output / entry["File"])
    manifest = {"Tag": tag, "Version": version, "SourceCommit": commit, "Prerelease": True, "Artifacts": records}
    (output / "release-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    sums = [f'{entry["Sha256"]}  {entry["File"]}' for entry in records]
    sums.append(f'{sha256(output / "release-manifest.json")}  release-manifest.json')
    (output / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="ascii")
    (output / "RELEASE_NOTES.md").write_text(
        f"""BlueLink {version} 预览版

- Windows：x86、x64、ARM64 均提供内置运行库 EXE/MSI、NoRuntime 精简 EXE/MSI 和自包含 Portable ZIP。
- NoRuntime 包不内置 .NET 8 Desktop Runtime；EXE 安装向导在缺少对应架构运行库时提示从 Microsoft 下载，直接使用 MSI 则在首次启动时引导补装。
- Android：ARM32、ARM64、x86、x86_64 及 universal 通用包，均以正式应用密钥签名。
- Portable：数据保存在程序目录的 Data，接收文件默认在 Download；更新请保留这两个目录。
本次更新：
- 消息与文件：完善多选、范围选择、复制、分享与操作菜单，优化聊天历史加载和滚动位置保持。
- 搜索与筛选：优化消息搜索布局、文本选择与命中高亮；修复日期范围冲突处理及重置后选择日期导致面板关闭的问题。
- 图片预览：双端支持连续浏览；Android 增加跟手切换和边界提示，Windows 增加靠边悬停箭头、预览右键菜单和标题栏图标优化。
- Android：统一会话与文件搜索框，优化页面间距、日期筛选区域和短暂提示位置。
- 传输与存储：完善断线恢复、消息草稿、文件分享、临时文件清理和设备备注等已完成改动。
- 更新：双端接入 GitHub Release 检查与下载校验，构建内嵌完整预览标签；Android versionCode 升至 13。
- 收藏功能仍处于设计阶段，明确不包含在本次版本中。

下载建议：Windows 常见电脑选择 win-x64-Setup.exe；免安装选相同架构的 Portable.zip。
电脑已安装对应架构 .NET 8 Desktop Runtime 时可选 NoRuntime-Setup.exe；首次补装运行库需要联网和管理员权限。
Android 不确定架构时选择 universal-release.apk；最低 Android 13。

已知限制：
- Windows 包尚无 Authenticode 签名，安装器沿用 Review 身份，本次不是正式稳定版。
- ARM64 原生实机及分架构完整安装/升级/卸载矩阵尚未验证。
- Android 正式签名与已有 Debug 安装不同，不能直接覆盖；卸载会删除应用数据。
- Portable 身份受 Windows 用户保护，跨账户/电脑需要重新核验信任。
- 双端“检查更新”读取 GitHub Releases（包含预览版），在应用内下载校验；Android 交给系统安装确认。Windows 未签名 Review 预览包经固定来源、SHA-256 和产品版本核验并明确提示后允许安装，正式包仍要求签名。Portable 下载后仍需手动退出并解压替换。
- Android 同签名候选已通过真机系统覆盖安装；完整公网下载、正式签名跨版本覆盖及 Windows 全安装矩阵仍未验证。

源码提交：{commit}
Android 签名证书 SHA256：{next(iter(certificates))}
完整附件与哈希见 release-manifest.json、SHA256SUMS.txt。
""", encoding="utf-8")
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-tag", "collect"))
    parser.add_argument("--tag", required=True)
    parser.add_argument("--commit")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    validate_tag(args.tag, version)
    if args.command == "collect":
        if not args.input or not args.output or not args.commit:
            parser.error("collect requires --input, --output and --commit")
        records = collect(args.input, args.output, args.tag, version, args.commit)
        print(f"Verified {len(records)} release packages for {args.tag}.")
    else:
        print(f"Validated preview tag: {args.tag}")


if __name__ == "__main__":
    main()
