# 蓝联 / BlueLink

BlueLink 是 Android 与 Windows 之间的本地聊天和文件传输应用。设备通过 Bluetooth 发现、连接并建立端到端加密会话；连接 USB 后，可使用 Windows WPD / Android MTP 文件通道加速传输。

当前源码版本为 **0.2.17**，主分支为 **main**。项目仍在开发与验收中，已验证范围和已知限制见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

## 下载

安装包见 [GitHub Releases](https://github.com/shuuuuuang/BlueLink/releases)。首个预览版为 [v0.2.17-preview.1](https://github.com/shuuuuuang/BlueLink/releases/tag/v0.2.17-preview.1)。

- Windows 常见电脑选择 `win-x64-Setup.exe`；需要免安装时选择同架构的 `Portable.zip`，也提供 x86 与 ARM64。
- Android 不确定架构时选择 `android-universal-release.apk`，最低 Android 13；正式签名 APK 无法直接覆盖旧 Debug 安装。
- 附件包含 `SHA256SUMS.txt` 与源码/架构清单。Windows 当前未签名，仍属 Review 预览包，具体限制见版本说明。

## 主要功能

- **设备与会话**：已连接、离线和附近新设备分类；保留离线聊天记录，支持多个设备会话与后台接收。
- **聊天与搜索**：文字、图片、文件附件、历史记录搜索；Android 从会话底部操作抽屉中的“搜索聊天记录”进入，Windows 从会话区域搜索按钮进入。
- **文件管理**：查看全部设备或当前会话的文件，支持搜索、筛选及按任务状态提供暂停、继续、取消和重试等操作。Windows 未选中会话时仍显示会话区域标题栏，可点击“文件”进入全局文件管理。
- **图片预览**：查看原图、缩放、旋转与重置；双端使用统一的旋转与重置图标。
- **USB 文件加速**：保留系统 MTP 模式，通过加密中转文件传输；同一手机的双向文件任务按队列执行，蓝牙继续承担身份核验、消息和控制。
- **外观与设置**：简体中文、繁体中文、英文，浅色与深色主题；连接、文件接收、隐私、设备信任和诊断等设置。
- **Windows 集成**：托盘驻留、文件拖入/拖出、安装路径选择，以及卸载时的数据保留选项。

## 连接与使用

1. 在两端开启蓝牙并启动 BlueLink，授予所需的蓝牙/附近设备权限。
2. 查找附近设备：Android 在设备列表顶部下拉扫描；Windows 在附近设备标题或设备栏空白处打开右键菜单，选择“扫描附近设备”，也可按 `F5`。启动扫描由设置中的开关控制。
3. 选择设备建立连接，首次连接核对两端显示的六位安全代码并确认信任。
4. 在会话中发送文字或附件。离线时仍可查看已保存的历史；重新连接后，符合条件的失败发送任务可重试。
5. 默认接收位置为 Android 的 `Download/BlueLink`，Windows 的安装目录下 `Download`；可在设置中调整。

### 使用 USB 文件加速

先建立可信蓝牙会话，再用支持数据传输的 USB 线连接手机与电脑：

1. 在手机系统 USB 选项中选择“传输文件”。
2. 开启 BlueLink 的 USB 功能，并通过 Android 系统目录选择器授权专用中转目录（例如 `Download/BlueLinkUSB`）。
3. 等待设备卡片显示 USB 就绪的闪电标记，再发送文件。

生产通道使用 Windows 系统 WPD 和 Android SAF 授权目录，不要求开启 USB 调试或替换 MTP 驱动。USB 未就绪时，新文件可使用蓝牙；USB 在传输中失效时，任务可能失败并需要重试，不保证无缝切换。WPD 重试会重传当前完整文件，不提供密文断点续传或跨进程持久队列保证。

## 通信与隐私

设备间聊天和文件传输使用 Bluetooth 或 USB，不依赖云端服务，也不使用 Wi-Fi、局域网或互联网作为传输回退。聊天、文件记录和信任关系保存在本机。

- 身份与会话安全采用 Ed25519、X25519 和 ChaCha20-Poly1305，首次连接需要人工核对安全代码。
- 蓝牙使用 BTX/1.1 加密记录及顺序校验；USB 中转目录保存加密文件，传输密钥通过已加密的蓝牙会话交换。
- 接收端执行文件完整性校验，并处理接收大小限制、同名文件和最终发布。

Android 当前不申请 `INTERNET` 权限。Windows 的更新检查与安装包下载会访问配置的 GitHub 发布源，运行库获取也可能联网；这些功能与设备间传输分开。构建时需要下载 Gradle、Android 和 NuGet 依赖。

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

| 架构 | 客户端 | Review 安装包 | Portable 免安装包 |
| --- | --- | --- | --- |
| x86（32 位） | 原生 x86 | EXE / MSI | ZIP |
| x64 | 原生 x64 | EXE / MSI | ZIP |
| ARM64 | 原生 ARM64 | EXE / MSI | ZIP |

Windows ARM 指 ARM64，不包含 ARM32。ARM64 安装包目前使用 x86 的 .NET Framework 安装引导界面，通过系统兼容层运行；客户端和内置 .NET 8 运行库为原生 ARM64。

```powershell
# 只编译客户端；Architecture 默认为 x64，也可传 x86、arm64 或 all。
.\scripts\build-windows.ps1 -Architecture all

# 一次生成三种架构的 Review EXE/MSI 与 portable ZIP。
.\scripts\build-windows-packages.ps1 -Architecture all

# 只生成某种架构的 portable 包。
.\scripts\build-windows-packages.ps1 -Architecture arm64 -Format Portable
```

普通编译输出位于 `windows/BlueLink.App/bin/Release/net8.0-windows10.0.19041.0/win-<架构>/`，需要对应架构的 .NET 8 Desktop Runtime。打包脚本则使用自包含部署，将对应架构的运行库一并打入包中，无需另外安装 .NET 8。

打包脚本会恢复依赖、校验客户端和关键运行库的 PE 架构，对当前主机可运行的架构执行 SQLite/portable/更新回归；安装包还会执行目录归属测试，并解包核对 MSI 平台、Burn 内嵌架构和载荷哈希。可用 `-SkipTests` 跳过原生运行回归，结构与包内容校验仍保留；可用 `-DotnetPath` 指定 SDK、`-Offline` 使用已缓存的依赖，或用 `-Format Installer` / `Portable` / `Both` 选择产物。输出到新的 `artifacts/windows/multiarch-<时间戳>/`，避免覆盖旧产物：

- `BlueLink-Review-<VERSION>-win-<架构>-Setup.exe`
- `BlueLink-Review-<VERSION>-win-<架构>-Setup.msi`
- `BlueLink-<VERSION>-win-<架构>-Portable.zip`
- `build-manifest.json`、`SHA256SUMS.txt`

原有 `scripts/build-windows-review.ps1` 已接入同一流程，可继续使用 `-CompileInstaller`，并支持 `-Architecture` 选择架构。`scripts/build-windows.ps1 -Package Both` 也可直接进入打包流程。

这些包是未签名的开发验收产物。Review 安装包共用独立于正式版的安装身份，不用于与其他架构的 Review 版并排安装。脚本只生成产物，不自动执行安装。

### Portable 数据与更新

将 ZIP 解压到可写目录，直接运行 `BlueLink.exe`。`BlueLink.portable` 标记使数据库、设备身份、设置、缓存和日志保存在程序目录内的 `Data`，默认接收文件放入 `Download`。

退出程序后可以整体移动该目录。下次启动会修正数据库中原程序目录内的下载、附件、预览和传输路径；用户选择的外部目录保持原路径，外部文件需自行搬移。跨 Windows 账户或电脑时，身份私钥仍受 Windows 用户保护，需要重新核验设备信任。

更新时退出程序，用同架构的 portable 包更新程序文件，并保留 `BlueLink.portable`、`Data` 和 `Download`。Portable 模式不会自动运行 EXE 安装包更新，以免转换成安装版或覆盖错误目录。

### 正式发布

原有 `scripts/build-windows-release.ps1` 保留 x64 的框架依赖正式发布流程，与新的多架构 Review/portable 构建分开。产物位于 `artifacts/windows/win-x64/` 和 `artifacts/installer/BlueLink-Setup-<VERSION>-win-x64.exe`；流程含运行库获取及 UI 验收。

正式版本受 `installer/release-payload-lock.json` 约束，同版本内容变化时必须升级 `VERSION`，不能覆盖已锁定的发布内容。多架构产物完成构建不等于正式发布完成，分架构的签名、实际安装/升级/卸载及目标硬件验收仍须分别完成。

### GitHub Releases 自动发布

仓库的 `.github/workflows/release.yml` 在推送 `v<VERSION>-preview.N` 标签时执行自动发布，例如 `v0.2.17-preview.1`。当前多架构安装器仍为 Review 身份，因此此工作流只发布预览版；正式稳定发布继续遵守上面的发布锁和验收要求。

```powershell
git push origin main
git tag -a v0.2.17-preview.1 -m "BlueLink 0.2.17 preview 1"
git push origin v0.2.17-preview.1
```

标签必须与根目录 `VERSION` 一致，指向远程 `main` 历史中的提交。每次发布使用新的标签；升级 Android 版本时还须递增 `android/app/build.gradle.kts` 的 `versionCode`。Actions 页面也可手动运行 **Publish preview release**，填写已有标签；手动运行默认保留草稿。

工作流使用 Windows runner 分别构建 x86/x64/ARM64，并完成 Android 四 ABI 和通用包的测试、构建、签名。上传前检查全部 14 个安装附件的文件名、架构、版本、哈希和 Android 签名指纹，再生成 `release-manifest.json` 与 `SHA256SUMS.txt`。先上传到草稿，全部上传成功才公开。上传失败会保留草稿；排除故障并删除失败草稿后可以重跑，流程不会替换已发布版本。

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

## 当前验证边界

双端已完成多轮协议、单元测试、界面及指定手机的实际传输验收，但多机/Hub、其他手机型号、长期运行和完整发布矩阵仍需继续验证。近期 Windows 完整 UI 回归存在剪贴板或前台焦点干扰；Review 包完整性检查不能替代实际安装验收。

具体能力、限制和各次验证结果见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。构建产物、验收输出和本地工作文档不纳入源码仓库。

## 许可证

BlueLink 原创代码采用 [MIT License](LICENSE)，Copyright (c) 2026 shuuuuuang。
第三方依赖及素材保留各自的许可证和版权声明。
