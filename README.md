# 蓝联 / BlueLink

BlueLink 是 Android 与 Windows 之间的本地聊天和文件传输应用。设备通过 Bluetooth 发现、连接并建立端到端加密会话；连接 USB 后，可使用 Windows WPD / Android MTP 文件通道加速传输。

当前源码版本为 **0.2.17**，主分支为 **main**。项目仍在开发与验收中，已验证范围和已知限制见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

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

设置 `JAVA_HOME` 和 `ANDROID_SDK_ROOT`；可用 `BLUELINK_GRADLE` 指定 Gradle 可执行文件。部分 Windows 发布脚本仍使用开发机上的 `D:\Tool\dotnet-sdk-8\dotnet.exe`，在其他机器运行前需要调整路径；Review 脚本支持 `-DotnetPath` 参数。

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

脚本执行协议测试、应用单元测试、Debug lint，并构建 Debug 和未签名 Release APK。输出到 `artifacts/android/`：

- `BlueLink-<VERSION>-android-debug.apk`
- `BlueLink-<VERSION>-android-release-unsigned.apk`

Debug APK 可用于开发验收；未签名 Release APK 需要签名后才能安装。覆盖已有 Debug 安装时需使用同一签名，可通过 `BLUELINK_DEBUG_KEYSTORE` 指定本地调试密钥文件，密钥不应提交到仓库。

### Windows 客户端

```powershell
.\scripts\build-windows.ps1
```

Release 输出位于 `windows/BlueLink.App/bin/Release/net8.0-windows10.0.19041.0/`。客户端采用框架依赖部署，需要兼容的 .NET 8 Desktop Runtime；它不是内置运行库的单文件程序。

### Windows Review 安装包

Review 包用于开发验收，具有独立安装身份，不作为正式签名发布包。该脚本使用 `--no-restore`，首次运行前需恢复客户端和安装组件依赖：

```powershell
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
& $dotnetPath restore windows/BlueLink.App/BlueLink.App.csproj --configfile NuGet.config
if ($LASTEXITCODE -ne 0) { throw "Client restore failed" }
foreach ($component in @('Launcher', 'Uninstall', 'SetupUI', 'Installation.Tests', 'Package', 'Bundle')) {
    $extension = if ($component -in @('Package', 'Bundle')) { 'wixproj' } else { 'csproj' }
    & $dotnetPath restore "installer/BlueLink.$component/BlueLink.$component.$extension" --configfile NuGet.config
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $component" }
}
.\scripts\build-windows-review.ps1 -DotnetPath $dotnetPath -CompileInstaller
```

默认输出目录为 `artifacts/windows/review-<时间戳>/`，包含 `BlueLink-Review-DO-NOT-DISTRIBUTE.exe`、对应 MSI 和 `BlueLink-Windows-review.zip`。Review 包未签名、不内置 .NET 运行库；脚本生成产物，不自动执行安装。

正式发布入口为 `scripts/build-windows-release.ps1`，产物位于 `artifacts/windows/win-x64/` 和 `artifacts/installer/BlueLink-Setup-<VERSION>-win-x64.exe`。该流程含运行库获取及 UI 验收；正式版本受 `installer/release-payload-lock.json` 约束，同版本内容变化时必须升级 `VERSION`，不能覆盖已锁定的发布内容。正式发布还需完成签名及安装/升级/卸载验收。

## 当前验证边界

双端已完成多轮协议、单元测试、界面及指定手机的实际传输验收，但多机/Hub、其他手机型号、长期运行和完整发布矩阵仍需继续验证。近期 Windows 完整 UI 回归存在剪贴板或前台焦点干扰；Review 包完整性检查不能替代实际安装验收。

具体能力、限制和各次验证结果见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。构建产物、验收输出和本地工作文档不纳入源码仓库。
