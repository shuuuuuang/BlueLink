<p align="center">
  <img src="design/brand/final/bluelink-final-logo.png" alt="蓝联 BlueLink LOGO" width="220">
</p>

<h1 align="center">蓝联 · BlueLink</h1>

<p align="center">
  让手机与电脑，通过蓝牙相连。<br>
  无需联网，在附近的 Android 与 Windows 设备之间发送消息、图片和文件。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x86%20%7C%20x64%20%7C%20ARM64-0078D4?style=flat-square" alt="Windows x86、x64 和 ARM64">
  <img src="https://img.shields.io/badge/Android-13%2B-3DDC84?style=flat-square" alt="Android 13 及以上">
  <img src="https://img.shields.io/badge/Bluetooth-USB-0082FC?style=flat-square" alt="蓝牙连接与 USB 文件传输">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License"></a>
</p>

<p align="center">
  <a href="#获取与安装">获取与安装</a> ·
  <a href="#开始使用">开始使用</a> ·
  <a href="#常见问题">常见问题</a> ·
  <a href="https://github.com/shuuuuuang/BlueLink/issues">问题反馈</a>
</p>

## 关于蓝联

BlueLink 是一款连接 Android 手机与 Windows 电脑的蓝牙通信应用。想把手机里的图片传到电脑，或把一段文字、一个文件发给身边的设备？打开两端的蓝联，连接后就能在聊天中发送。

设备之间通过蓝牙建立连接，也可使用 USB 加速文件传输。无需连接同一个 Wi-Fi，也无需登录账号或经过云端中转。

## 你可以做什么

| 功能 | 使用体验 |
| --- | --- |
| 💬 发送文字 | 在手机与电脑之间互发消息，保留本地聊天记录 |
| 📎 分享图片与文件 | 从聊天中发送附件，点击预览图片或用系统应用打开文件 |
| 🔎 查找聊天记录 | 搜索历史消息、按日期筛选，并对消息进行多选操作 |
| ⚡ USB 文件加速 | 连接数据线后优先通过 USB 发送文件，蓝牙继续保持消息与控制连接 |
| 📊 查看传输进度 | 在传输列表中查看进度、速度和剩余时间 |
| 🔒 确认安全连接 | 首次连接核对两端安全码，消息与文件采用端到端加密传输 |
| 🗂️ 找回历史会话 | 设备离线后，仍可查看已保存在本机的聊天与传输记录 |
| 🖥️ Windows 托盘驻留 | 支持托盘驻留，方便继续接收消息和文件 |

## 获取与安装

前往 [GitHub Releases](https://github.com/shuuuuuang/BlueLink/releases) 下载。当前预览版为 [v0.2.18-preview.2](https://github.com/shuuuuuang/BlueLink/releases/tag/v0.2.18-preview.2)。

| 设备 | 如何选择与安装 |
| --- | --- |
| Windows 电脑 | 常见电脑选择 `win-x64-Setup.exe`，也提供 x86 和 ARM64；需要免安装时选择同架构的 `Portable.zip`。普通包内置运行时；`NoRuntime` 包需要本机安装对应架构的 .NET 8 桌面运行时 |
| Android 手机 | 最低 Android 13；不确定架构时选择 `android-universal-release.apk`，按系统提示安装 |

> **当前为预览版。** Windows 包尚未签名；Android 使用正式签名，不能直接覆盖旧 Debug 安装，请勿通过卸载来绕过签名保护。下载页提供校验文件与版本说明，设备兼容性及已知限制见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

## 开始使用

1. **打开两端蓝联**：开启手机与电脑的蓝牙，在应用中按提示授予蓝牙、附近设备等所需权限。
2. **找到对方设备**：Android 在设备列表顶部下拉扫描；Windows 在附近设备标题或设备栏空白处右键选择「扫描附近设备」，也可按 `F5`。选择对方设备建立连接。首次连接如出现系统蓝牙配对提示，请按提示完成配对。
3. **核对安全码**：确认手机与电脑显示的六位安全码一致，再在两端确认连接。如果不一致，请取消连接。
4. **发送消息或文件**：连接完成后，在聊天区输入文字，或选择图片、文件发送；传输状态可在传输列表中查看。

### 收到的文件在哪里？

| 设备 | 默认保存位置 |
| --- | --- |
| Android | 系统下载目录下的 `Download/BlueLink` |
| Windows | 蓝联安装目录下的 `Download` 文件夹 |

两端都可以在设置中更改接收位置，请选择有写入权限的目录。聊天记录与设备信任信息保存在本机。

## 常见问题

### 需要联网或连接同一个 Wi-Fi 吗？

不需要。设备间消息与文件通过蓝牙或 USB 传输，不经过互联网、Wi-Fi、局域网或云端服务器。手动检查更新、下载更新包、获取 Windows 运行时或从源码构建时需要联网；更新功能不上传设备、消息或文件数据。

### 为什么找不到对方设备？

请先确认两端都打开了蓝联、蓝牙已开启、所需权限已允许，并让设备靠近后重新扫描。附近列表显示的是当前发现的蓝联设备，系统中曾经配对过的设备不一定会出现在这里。

### 为什么大文件传输比较慢？

蓝牙传输速度会受设备硬件、距离和干扰影响。**传输大文件时，建议使用 USB 高速传输**：先通过蓝牙连接并核对安全码，在两端「设置 → 连接与设备」中开启「USB 高速传输」，用支持数据传输的 USB 线连接手机与电脑，并在手机上选择「传输文件」。首次开启时，按提示选择并授权蓝联专用中转文件夹，例如 `Download/BlueLinkUSB`。设备名称旁出现闪电标识后，新发送的文件会优先使用 USB。

无需开启 USB 调试或替换驱动。若 USB 尚未就绪，请检查手机连接模式和文件夹授权；其他程序占用手机文件时，可能需要等待。旧版本没有此入口时，可先使用蓝牙传输；兼容性与已知限制以对应版本说明为准。

### 传输中断后怎么办？

先恢复设备连接，再查看传输列表。符合条件的失败发送任务可重试；USB 重试会重传当前整个文件，不保证传输中无缝切换到蓝牙。具体限制见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

### 支持 iPhone 或 Mac 吗？

目前面向 Android 与 Windows。iOS 和 macOS 已进入规划，尚无可用客户端。

### 如何反馈问题？

请在 [GitHub Issues](https://github.com/shuuuuuang/BlueLink/issues) 中描述遇到的问题，附上两端应用版本、系统版本、设备型号和复现步骤。如果提供截图或诊断信息，请先隐去私人消息、文件内容等个人信息。

## 开源与开发

欢迎反馈问题、提出建议或参与改进。协议和验证细节见 [BTX/1.1 协议](docs/BTX_1_1_PROTOCOL.md) 与 [实现状态](docs/IMPLEMENTATION_STATUS.md)。当前开发主分支为 **main**。

<details>
<summary>源码结构、构建与发布说明（开发者）</summary>

## 工程结构

```text
android/           Kotlin / Jetpack Compose 客户端
windows/           .NET 8 / WPF 客户端及验证程序
installer/         WiX 安装包、安装界面、启动器与卸载器
protocol-core/     Java 17 平台无关协议实现与校验
shared-spec/       协议定义、常量与测试向量
shared/            共用逻辑、文件图标和缩略图测试数据
design/            品牌资源、字体及 Figma 导出资产
docs/              协议、实施规范与验收状态
scripts/           构建、检查与设备验收入口
```

界面原型以 [BlueLink Figma](https://www.figma.com/design/Tna537kDQdpsVwDX0lXlCs/BlueLink) 中当前确认的画板为准。旧的 `design/prototypes` 已移除；运行时所需的图标、字体和品牌资产仍保留在仓库中。

## 构建与验证

以下命令在仓库根目录的 PowerShell 中执行。

### 开发环境

| 目标 | 环境 |
| --- | --- |
| 协议内核 | JDK 17，Gradle Wrapper 或 Gradle 8.5 |
| Android | JDK 17、Android SDK 34；最低 Android 13（API 33） |
| Windows 客户端 | Windows、.NET 8 SDK；客户端使用 WPF-UI 4.3 |
| Windows 安装器 | 上述环境及项目声明的 WiX / .NET Framework 引用程序集依赖 |

设置 `JAVA_HOME` 和 `ANDROID_SDK_ROOT`；可用 `BLUELINK_GRADLE` 指定 Gradle 可执行文件。部分 Windows 发布脚本仍使用开发机上的 `D:\Tool\dotnet-sdk-8\dotnet.exe`，在其他机器运行前需要调整路径；多架构打包脚本与 Review 脚本支持 `-DotnetPath` 参数。

### 协议与静态检查

```powershell
.\gradlew.bat verify
.\scripts\verify-ui-contract.ps1
.\scripts\verify-installer-contract.ps1
.\scripts\verify-version.ps1
```

首条命令只校验平台无关协议内核，不需要 Android SDK 或 .NET SDK。Windows 验证入口为 `scripts/verify.ps1`；UI 与真机验收脚本位于 `scripts/`，各次验收范围见实现状态，构建成功不代表全部场景已验收。

### Android

```powershell
.\scripts\build-android.ps1
```

脚本执行协议测试、应用单元测试、Debug lint，并构建 Debug 和未签名 Release APK。默认分别生成 `armeabi-v7a`（ARM32）、`arm64-v8a`（ARM64）、`x86`、`x86_64` 及通用 APK，输出到 `artifacts/android/`：

- `BlueLink-<VERSION>-android-<ABI>-debug.apk`
- `BlueLink-<VERSION>-android-<ABI>-release-unsigned.apk`
- 通用包使用 `universal`，同时保留不含 ABI 的旧文件名作为兼容副本。
- `build-manifest.json` 记录每个 APK 实际包含的原生 ABI；`SHA256SUMS.txt` 提供校验值。

只构建指定架构时可用 `-Architecture arm64-v8a` 或 `-Architecture x86_64`；`-Offline` 使用本地依赖缓存，`-SkipTests` 跳过测试与 Debug lint。直接从 IDE/Gradle 构建时沿用原有单个通用 APK；按 ABI 拆包需传入 `-PbluelinkSplitApks=true`，可用 `-PbluelinkAbis=arm64-v8a,x86_64` 指定范围。

Debug APK 可用于开发验收；未签名 Release APK 需要签名后才能安装。覆盖已有 Debug 安装时需使用同一签名，可通过 `BLUELINK_DEBUG_KEYSTORE` 指定本地调试密钥文件，密钥不应提交到仓库。

### Windows 客户端与多架构打包

| 架构 | 客户端 | 内置运行库安装包 | NoRuntime 精简安装包 | Portable 免安装包 |
| --- | --- | --- | --- | --- |
| x86（32 位） | 原生 x86 | EXE / MSI | EXE / MSI | ZIP |
| x64 | 原生 x64 | EXE / MSI | EXE / MSI | ZIP |
| ARM64 | 原生 ARM64 | EXE / MSI | EXE / MSI | ZIP |

Windows ARM 指 ARM64，不包含 ARM32。ARM64 安装包目前使用 x86 的 .NET Framework 安装引导界面，通过系统兼容层运行；客户端和内置 .NET 8 运行库为原生 ARM64。

```powershell
# 只编译客户端；Architecture 默认为 x64，也可传 x86、arm64 或 all。
.\scripts\build-windows.ps1 -Architecture all

# 一次生成三种架构的两类 Review EXE/MSI 与 portable ZIP。
.\scripts\build-windows-packages.ps1 -Architecture all

# 只生成三种架构的不内置 .NET 安装包。
.\scripts\build-windows-packages.ps1 -Architecture all -Format Installer -InstallerRuntime External

# 只生成某种架构的 portable 包。
.\scripts\build-windows-packages.ps1 -Architecture arm64 -Format Portable
```

普通编译输出位于 `windows/BlueLink.App/bin/Release/net8.0-windows10.0.19041.0/win-<架构>/`，需要对应架构的 .NET 8 Desktop Runtime。内置运行库安装包和 portable 使用自包含部署，无需另外安装 .NET 8。新增 NoRuntime EXE/MSI 不内置 .NET、WPF 和 WinForms 运行库，需要系统已安装对应架构的 .NET 8 Desktop Runtime；普通 .NET Runtime、其他架构或只有 .NET 9/10 都不能替代。

打包脚本会恢复依赖、校验客户端和关键运行库的 PE 架构，对当前主机可运行的架构执行 SQLite/portable/更新回归；安装包还会执行目录归属测试，并解包核对 MSI 平台、Burn 内嵌架构和载荷哈希。可用 `-SkipTests` 跳过原生运行回归，结构与包内容校验仍保留；可用 `-DotnetPath` 指定 SDK、`-Offline` 使用已缓存的依赖，或用 `-Format Installer` / `Portable` / `Both` 选择产物。输出到新的 `artifacts/windows/multiarch-<时间戳>/`，避免覆盖旧产物：

- `BlueLink-Review-<VERSION>-win-<架构>-Setup.exe`
- `BlueLink-Review-<VERSION>-win-<架构>-Setup.msi`
- `BlueLink-Review-<VERSION>-win-<架构>-NoRuntime-Setup.exe`
- `BlueLink-Review-<VERSION>-win-<架构>-NoRuntime-Setup.msi`
- `BlueLink-<VERSION>-win-<架构>-Portable.zip`
- `build-manifest.json`、`SHA256SUMS.txt`

`-InstallerRuntime Bundled` 只生成内置运行库安装包，`External` 只生成 NoRuntime 安装包，默认 `Both` 生成两种。该选项不改变 portable：portable 始终内置运行库。配合默认 `-Format Both` 使用 `External` 时，仍会额外生成自包含 portable。

NoRuntime EXE 检测到缺少运行库时，会由现有向导提示从 Microsoft 下载并安装；直接安装 MSI 后，首次启动由启动器提供同样的补装入口。首次补装需要联网和管理员权限；已有运行库时可离线使用。两种安装包共用同一 Review 安装身份，不是可并排安装的两个产品，切换时保留既有用户数据。运行库下载信息固定在 `installer/runtime-packages.json`，构建校验 SHA512 和 Microsoft 签名，EXE 中只记录外部下载地址，不包含运行库安装文件。

原有 `scripts/build-windows-review.ps1` 已接入同一流程，可继续使用 `-CompileInstaller`，并支持 `-Architecture` 选择架构。`scripts/build-windows.ps1 -Package Both` 也可直接进入打包流程。

这些包是未签名的开发验收产物。Review 安装包共用独立于正式版的安装身份，不用于与其他架构的 Review 版并排安装。脚本只生成产物，不自动执行安装。

### GitHub 检查更新

Windows 设置与 Android 关于页已接入固定仓库 `shuuuuuang/BlueLink` 的公开 Release 列表，当前开发阶段包含预览版。按完整标签比较版本（如 `v0.2.17-preview.10` 晚于 `preview.9`），忽略草稿，校验附件名称、大小、SHA-256 元数据和仓库下载地址。

发布工作流将标签通过 `BLUELINK_RELEASE_TAG` 写入双端；普通本地构建只使用 VERSION，不猜测它对应哪个已发布预览版。Windows 按进程架构选择完整安装包或 Portable ZIP。安装版在应用内下载并校验，用户确认后启动安装程序；未签名 Review 预览包按上述例外处理，正式包签名门禁保留。Portable ZIP 在应用内下载，仍需退出后解压替换并保留 Data、Download，尚不支持自动替换。

Android 在应用内下载 universal release APK，显示进度，支持取消和重试。安装前再次核验完整性及签名，系统权限和安装确认仍由系统界面完成，全流程不打开浏览器。正式签名 APK 与 Debug 安装签名不同，应用会拒绝覆盖；不要通过卸载来绕过该保护。

无线真机已完成同签名候选包从应用内校验到系统覆盖安装，回到首页后原设备记录保留。真实 GitHub 下载已看到进度并验证取消清理；公网完整下载受网络影响未完成，Windows 完整公网下载超时，真实签名 Windows 覆盖更新和完整安装矩阵仍为 **Not verified**。本轮源码接入不等于发布了新 Release，既有 APK/EXE 不会自动获得这些改动。


### Portable 数据与更新

将 ZIP 解压到可写目录，直接运行 `BlueLink.exe`。`BlueLink.portable` 标记使数据库、设备身份、设置、缓存和日志保存在程序目录内的 `Data`，默认接收文件放入 `Download`。

退出程序后可以整体移动该目录。下次启动会修正数据库中原程序目录内的下载、附件、预览和传输路径；用户选择的外部目录保持原路径，外部文件需自行搬移。跨 Windows 账户或电脑时，身份私钥仍受 Windows 用户保护，需要重新核验设备信任。

更新时退出程序，用同架构的 portable 包更新程序文件，并保留 `BlueLink.portable`、`Data` 和 `Download`。Portable 模式不会自动运行 EXE 安装包更新，以免转换成安装版或覆盖错误目录。

### 正式发布

原有 `scripts/build-windows-release.ps1` 保留 x64 的框架依赖正式发布流程，与新的多架构 Review/portable 构建分开。产物位于 `artifacts/windows/win-x64/` 和 `artifacts/installer/BlueLink-Setup-<VERSION>-win-x64.exe`；流程含运行库获取及 UI 验收。

正式版本受 `installer/release-payload-lock.json` 约束，同版本内容变化时必须升级 `VERSION`，不能覆盖已锁定的发布内容。多架构产物完成构建不等于正式发布完成，分架构的签名、实际安装/升级/卸载及目标硬件验收仍须分别完成。

### GitHub Releases 自动发布

仓库的 `.github/workflows/release.yml` 在推送 `v<VERSION>-preview.N` 标签时执行自动发布，例如 `v0.2.18-preview.2`。当前多架构安装器仍为 Review 身份，因此此工作流只发布预览版；正式稳定发布继续遵守上面的发布锁和验收要求。

```powershell
git push origin main
git tag -a v0.2.18-preview.2 -m "BlueLink 0.2.18 preview 2"
git push origin v0.2.18-preview.2
```

标签必须与根目录 `VERSION` 一致，指向远程 `main` 历史中的提交。每次发布使用新的标签；升级 Android 版本时还须递增 `android/app/build.gradle.kts` 的 `versionCode`。Actions 页面也可手动运行 **Publish preview release**，填写已有标签；手动运行默认保留草稿。

工作流使用 Windows runner 分别构建 x86/x64/ARM64，并完成 Android 四 ABI 和通用包的测试、构建、签名。上传前检查全部 20 个安装附件的文件名、架构、版本、哈希和 Android 签名指纹，再生成 `release-manifest.json` 与 `SHA256SUMS.txt`。先上传到草稿，全部上传成功才公开。上传失败会保留草稿；排除故障并删除失败草稿后可以重跑，流程不会替换已发布版本。

Android 签名需要在 **Settings → Secrets and variables → Actions** 配置以下 repository Secrets：

| Secret | 内容 |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | 正式 JKS 密钥库的 Base64 内容 |
| `ANDROID_KEYSTORE_PASSWORD` | 密钥库密码 |
| `ANDROID_KEY_ALIAS` | 应用签名别名 |
| `ANDROID_KEY_PASSWORD` | 私钥密码 |

密钥和密码必须在仓库外备份，未来版本持续使用同一密钥。CI 仅在签名步骤将 JKS 写入临时目录，结束后删除；Release 不包含密钥、Debug APK 或 unsigned APK。正式密钥与 Debug 密钥不同，不能覆盖原有 Debug 安装。

本地也可在设置三个密码/别名环境变量后，通过 `scripts/sign-android-packages.ps1 -KeystorePath <JKS路径> -InputDirectory <未签名产物目录> -OutputDirectory <新输出目录>` 签名；密码不要写进命令参数或源码。脚本校验原始 APK 哈希，执行 zipalign/apksigner，并检查五个 APK 的证书一致。

GitHub CI 使用 `NuGet.CI.Config` 和官方 Maven 仓库恢复依赖；本地沿用原镜像配置。工作流固定 Actions 完整提交 SHA，构建任务仅有源码读取权限，只有最终上传任务拥有 Release 写入权限。Windows 当前没有 Authenticode 签名，ARM64 实机与完整安装矩阵尚未验证，发布说明会明确这些限制。

</details>

## 许可证

BlueLink 原创代码采用 [MIT License](LICENSE)，Copyright (c) 2026 shuuuuuang。
第三方依赖及素材保留各自的许可证和版权声明。
