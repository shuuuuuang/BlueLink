# BlueLink 0.2.11 实施与发布验收记录

日期：2026-08-29。项目不是 Git 仓库；本文只记录本轮工作区中实际完成且有命令、文件或运行证据的结果。任何没有在当前主机真实执行的项目均标记为“未验证”。生产安装 `D:\BlueLink` 未参与破坏性验收。

## 结果摘要

- 修复了托盘退出、覆盖安装和卸载共用的进程控制：命名事件与单实例锁现在按“当前用户 + 精确安装根目录”隔离；精确进程路径通过 `QueryFullProcessImageName` 读取；正常关闭超时后只终止目标根目录的进程。
- 修复了 WPF 进程退出竞态：进程可能先报告 `HasExited`、稍后才从系统快照消失，控制器现在在强制截止时间内轮询精确路径。静默关闭失败会返回 1603，不再把不可见失败窗口永久挂起。
- 独立 `Uninstall.exe` 只读取随包配置的精确注册表键、Bundle UpgradeCode、ProviderKey 和 MSI ProductCode；Burn 缓存不可用时才按准确 ProductCode 回退 MSI；只有进程、注册和程序文件全部消失后才显示成功。
- 已彻底移除安装器“蓝联已安装/修复/卸载”组合维护页。检测到现有安装时直接回填原目录并进入安装位置/覆盖流程；卸载仍使用独立卸载页和根目录 `Uninstall.exe`。
- Windows 主应用及安装器继续使用 WPF-UI 4.3.0。基础控件手写 `ControlTemplate` 和旧 `Themes\Controls.xaml` 已移除；现有业务布局恢复后使用官方控件/样式。
- 缺少运行时页和运行中覆盖弹窗按两张冻结原型重新渲染；运行时页显示实际固定包 8.0.30、x64、55.8 MB、Microsoft 官方，并使用纯下划线“我已安装，重新检测”。覆盖弹窗由官方 WPF UI MessageBox 渲染并由 UI Automation 点击可见按钮。
- Windows 发布已改为框架依赖、多文件结构；根目录无 DLL，`app` 中无 `coreclr.dll`、`hostfxr.dll`、`hostpolicy.dll`、`clrjit.dll`。
- Android `NearbyDeviceTracker`、稳定身份迁移、45 秒/两扫描窗淘汰、能力过期保留、状态投影、稳定 Compose key、RSSI 节流和隐私化崩溃诊断已实现；10 个 JVM 测试全部通过。

## 新增文件

- `shared\WindowsAppControlChannel.cs`
- `shared\InstalledApplicationController.cs`
- `installer\dotnet-runtime-lock.json`
- `scripts\build-windows-acceptance-installer.ps1`
- `scripts\test-root-uninstaller.ps1`
- `android\app\src\main\java\com\bluelink\android\bluetooth\NearbyDeviceTracker.kt`
- `android\app\src\main\java\com\bluelink\android\domain\DeviceProjectionPolicy.kt`
- `android\app\src\test\java\com\bluelink\android\bluetooth\NearbyDeviceTrackerTest.kt`
- `installer\BlueLink.Uninstall\App.config`, `App.xaml`, `App.xaml.cs`
- `installer\BlueLink.Uninstall\BlueLink.Uninstall.csproj`
- `installer\BlueLink.Uninstall\UninstallWindow.xaml`, `UninstallWindow.xaml.cs`

## 修改文件

### Windows 主程序

- `windows\BlueLink.App\App.xaml`, `App.xaml.cs`, `BlueLink.App.csproj`
- `windows\BlueLink.App\MainWindow.xaml`, `MainWindow.xaml.cs`, `MainViewModel.cs`
- `windows\BlueLink.App\SettingsWindow.xaml`, `SettingsWindow.xaml.cs`
- `windows\BlueLink.App\AllTransfersWindow.xaml`, `AllTransfersWindow.xaml.cs`
- `windows\BlueLink.App\ImagePreviewWindow.xaml`, `ImagePreviewWindow.xaml.cs`
- `windows\BlueLink.App\BlueLinkDialog.xaml.cs`
- `windows\BlueLink.App\TrustConfirmationWindow.xaml`, `TrustConfirmationWindow.xaml.cs`
- `windows\BlueLink.App\Themes\Components.xaml`
- `windows\BlueLink.App\Bluetooth\BlePresenceService.cs`, `RfcommBluetoothService.cs`
- `windows\BlueLink.App\Domain\Models.cs`
- `windows\BlueLink.App\Files\FileInteractionService.cs`
- `windows\BlueLink.TransferVerification\Program.cs`

### 安装、启动、卸载与打包

- `installer\BlueLink.Launcher\App.config`, `App.xaml`, `App.xaml.cs`, `BlueLink.Launcher.csproj`
- `installer\BlueLink.Launcher\AuthenticodeVerifier.cs`, `RuntimeDetector.cs`, `RuntimePackageInfo.cs`, `RuntimeWindow.xaml`, `RuntimeWindow.xaml.cs`
- `installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj`, `BlueLinkBootstrapper.cs`, `InstallerPromptWindow.xaml.cs`, `InstallerWindow.xaml`, `InstallerWindow.xaml.cs`
- `installer\BlueLink.Package\BlueLink.Package.wixproj`, `Package.wxs`, `Package.Generated.wxs`
- `installer\BlueLink.Bundle\BlueLink.Bundle.wixproj`, `Bundle.wxs`
- `scripts\build-windows-release.ps1`, `generate-wix-payload.ps1`, `test-windows-installer.ps1`
- `scripts\verify-installer-contract.ps1`, `verify-release-artifacts.ps1`, `verify-ui-contract.ps1`

### Android

- `android\app\build.gradle.kts`
- `android\app\src\main\java\com\bluelink\android\BlueLinkApplication.kt`, `MainActivity.kt`, `MainViewModel.kt`
- `android\app\src\main\java\com\bluelink\android\bluetooth\BluetoothRepository.kt`
- `android\app\src\main\java\com\bluelink\android\data\local\BlueLinkRepository.kt`
- `android\app\src\main\java\com\bluelink\android\diagnostics\CrashReporter.kt`
- `android\app\src\main\java\com\bluelink\android\domain\Models.kt`
- `android\app\src\main\java\com\bluelink\android\files\FileInteraction.kt`
- `android\app\src\main\java\com\bluelink\android\runtime\BlueLinkRuntime.kt`
- `android\app\src\main\java\com\bluelink\android\session\PeerSession.kt`
- `android\app\src\main\res\xml\file_paths.xml`
- `scripts\build-android.ps1`

## 删除内容

- 删除 `windows\BlueLink.App\Themes\Controls.xaml`，不再提供 Button、TextBox、ComboBox、Tab、ScrollBar 等基础控件的手写模板。
- 删除安装器维护页的 XAML 分支、事件和快照入口；当前源码无 `MaintenancePage`，产物目录中的过期 `artifacts\acceptance\installer-maintenance.png` 也已删除。
- 删除派生 `TestableMessageBox` 和内部关闭调用；覆盖弹窗测试改为官方 MessageBox + UI Automation 可见按钮。
- 删除文件传输栏“清除已完成”、图片预览“打开原图”等已确认不需要的入口。

## 实际命令与结果

以下命令均在 `E:\BlueLink` 执行。

| 命令 | 结果 |
|---|---|
| `D:\Tool\dotnet-sdk-8\dotnet.exe build windows\BlueLink.sln -c Debug --no-restore` | 退出 0，0 warning，0 error |
| `D:\Tool\dotnet-sdk-8\dotnet.exe build windows\BlueLink.sln -c Release --no-restore` | 退出 0，0 warning，0 error |
| Launcher、SetupUI、Uninstall 三个 net472 项目 Debug/Release build | 全部退出 0，0 warning，0 error |
| `D:\Tool\dotnet-sdk-8\dotnet.exe run --project windows\BlueLink.TransferVerification\BlueLink.TransferVerification.csproj -c Release --no-build` | 退出 0；传输 16、SQLite 5、身份恢复 4、BTX 12、拖放 5、发送快照 2，时间格式通过 |
| `powershell -File scripts\build-windows-release.ps1 -Configuration Release` | 退出 0；框架依赖 publish、MSI、Burn、启动烟测及截图完成 |
| `scripts\verify-ui-contract.ps1` | 退出 0 |
| `scripts\verify-installer-contract.ps1` | 退出 0 |
| `scripts\verify-release-artifacts.ps1` | 退出 0 |
| `scripts\test-windows-installer.ps1 ... -IsolatedAcceptance` | 退出 0；非法目录、全新安装、取消覆盖、运行中修复、哈希不变、`.rbf` 清理、启动、Burn 卸载、Download 保留通过 |
| `scripts\test-root-uninstaller.ps1 ...` | 退出 0；真实 UI Automation 点击根 `Uninstall.exe` 和官方确认按钮，运行进程关闭、程序与注册删除、Download 保留 |
| 两个不同安装根目录的 control-channel smoke | 两进程退出码均 0；第一个退出时另一根目录实例保持运行 |
| `powershell -File scripts\build-android.ps1` | 退出 0；protocol-core 41、Debug/Release unit test、lintDebug、assembleDebug、assembleRelease 完成，113 tasks |
| `NearbyDeviceTrackerTest` JUnit XML | tests=10，failures=0，errors=0，skipped=0 |

Android lint 有 2 条非阻断 warning：Compose lint registry 版本提示、kapt 建议迁移 KSP；无 lint error。

构建时 Microsoft release metadata 的最后一次在线读取因 SSL 连接失败；脚本只在缓存包仍同时满足锁定版本/URL/SHA-512/大小和 Microsoft Authenticode 有效签名时允许离线复用。本次复用的 Runtime 为 8.0.30，58,510,672 字节，SHA-512：

`B4498F93CD6817E28ACD5C2AAA80FB7271F5EA62D7F3E2171B2B30D36A15BA950311208CEA55FC0903FC962FBC3184D457885DFE7C4AB19C022B52FFF6FCACEB`

## 最终产物

| 产物 | 大小 | SHA-256 |
|---|---:|---|
| `E:\BlueLink\artifacts\installer\BlueLink-Setup-0.2.11-win-x64.exe` | 19,944,581 | `A4C2A2FA823E8A9E6773940BF9AD6E8D419EC5270DF1651E0C99FE00D8B7F1A6` |
| `E:\BlueLink\artifacts\windows\win-x64\BlueLink.exe` | 487,424 | `F2A181C8A4A58F43DE5AE435C7F7C6BF49190511134C78FCA3B0B0543E1A3CED` |
| `E:\BlueLink\artifacts\windows\win-x64\app\BlueLink.exe` | 211,456 | `7148B6368FCBD5703C3F517D930D993CA28E7F038D5E312DB591F6CA287FD249` |
| `E:\BlueLink\artifacts\windows\win-x64\Uninstall.exe` | 490,496 | `008A82633812611E984961A5E4994B6F990837D3ACCE4345BEFB43A948432552` |
| `E:\BlueLink\artifacts\android\BlueLink-0.2.11-android-debug.apk` | 66,948,097 | `09A91C78005818A5063C38FC6C4B388E9631333B20A98BD8DC55DE6AC6E2203D` |
| `E:\BlueLink\artifacts\android\BlueLink-0.2.11-android-release-unsigned.apk` | 51,258,830 | `6FC96130BA7AC23389C9609568325978F71BBFC6CA430576695B89181508DED3` |

## 隔离安装后的实际目录树

```text
.bluelink-install.json (841 bytes)
BlueLink.exe (487424 bytes)
BlueLink.exe.config (376 bytes)
Uninstall.exe (490496 bytes)
Uninstall.exe.config (572 bytes)
app\
  BlueLink.exe (211456 bytes)
  BlueLink.dll (1004544 bytes)
  BlueLink.deps.json (2903 bytes)
  BlueLink.runtimeconfig.json (458 bytes)
  BouncyCastle.Cryptography.dll (7072536 bytes)
  Microsoft.Windows.SDK.NET.dll (24877600 bytes)
  WinRT.Runtime.dll (528944 bytes)
  Wpf.Ui.Abstractions.dll (7168 bytes)
  Wpf.Ui.dll (6565888 bytes)
bootstrap\
  runtime-package.json (444 bytes)
  System.Buffers.dll (23816 bytes)
  System.Memory.dll (145200 bytes)
  System.Numerics.Vectors.dll (110344 bytes)
  System.Runtime.CompilerServices.Unsafe.dll (19256 bytes)
  Wpf.Ui.Abstractions.dll (16896 bytes)
  Wpf.Ui.dll (6569984 bytes)
Download\
  acceptance-user-file.txt (34 bytes)
```

根目录 DLL 数量为 0；`Download` 不在 `.bluelink-install.json` 的 OwnedPaths 中。

## 最终运行截图

- `E:\BlueLink\artifacts\acceptance\windows-ui-expanded.png`
- `E:\BlueLink\artifacts\acceptance\windows-ui-collapsed.png`
- `E:\BlueLink\artifacts\acceptance\windows-ui-compact.png`
- `E:\BlueLink\artifacts\acceptance\windows-settings.png`
- `E:\BlueLink\artifacts\acceptance\windows-dialog.png`
- `E:\BlueLink\artifacts\acceptance\windows-image-preview.png`
- `E:\BlueLink\artifacts\acceptance\installer-welcome.png`
- `E:\BlueLink\artifacts\acceptance\installer-location.png`
- `E:\BlueLink\artifacts\acceptance\installer-runtime-required-current.png`
- `E:\BlueLink\artifacts\acceptance\installer-progress.png`
- `E:\BlueLink\artifacts\acceptance\installer-complete.png`
- `E:\BlueLink\artifacts\acceptance\installer-uninstall.png`
- `E:\BlueLink\artifacts\acceptance\installer-overwrite-wpfui-uia.png`
- `E:\BlueLink\artifacts\acceptance\uninstaller-main.png`

## 未通过或未验证

- **未通过：Windows 数字签名。** 最终 Setup、根启动器、实际客户端和 `Uninstall.exe` 的 Authenticode 状态均为 `NotSigned`；本机/工作区没有发布证书。
- **未通过：Android 正式签名。** release APK 为 unsigned；debug APK 使用 debug 签名。
- **未验证：Android 真机连续扫描 10 分钟及遮挡、远离、前后台、熄屏、手动重扫、蓝牙开关、重新配对。** 当前没有在线 adb 设备，代码推断不能替代真机日志。
- **未验证：真实蓝牙双端聊天/文件收发、系统拖入/拖出与厂商广播丢失时序。** JVM/桌面验证已通过，但无本轮硬件证据。
- **未验证：生产 `D:\BlueLink` 的正式覆盖/卸载。** 为保护现有业务数据和用户修改，本轮只使用独立 ProductCode、UpgradeCode、ProviderKey、注册表键和 `E:\BlueLink\.acceptance` 目录。
- **当前已安装副本仍是旧状态：** `D:\BlueLink` 的根启动器为 486,912 字节、`Uninstall.exe` 为 478,720 字节，与本轮最终产物不同；`HKCU\Software\BlueLink` 当前缺失，系统还留有 3 条隐藏的旧 0.2.11 MSI 注册：`{A211A001-8C34-4D70-A441-90F2A8347101}`、`{C0DB2F4A-3C83-4604-9377-B058ABA031D5}`、`{F1CDECF6-7D45-4666-BE68-3825A93EC24B}`。为避免猜测归属或误删用户安装，本轮未擅自修复/清理这些生产注册。
- **未验证：旧单文件 0.2.11 的真实覆盖、故意制造 MSI 事务失败后的旧版回滚启动。** 新结构到新结构、运行中覆盖、取消哈希不变已经通过。
- **未验证：缺少 x64 Runtime 的真实自动下载/安装、仅 x86 Runtime、离线/下载中断、签名错误、UAC 取消、需要重启。** 页面、检测代码、固定包哈希与 Microsoft 签名已验证；本机已安装 x64 Desktop Runtime，未卸载共享运行时做破坏性测试。
- **未验证：从 Windows“程序和功能”实际点击卸载。** 精确 Burn 静默卸载和根 `Uninstall.exe` UI 流程均已通过；系统面板入口未人工点击。
- **未验证：100%、125%、150% 三档 DPI 的完整人工交互矩阵。** 当前截图无裁切，但未在三个系统缩放设置下逐档重启验证。
- **未验证：所有设置、下拉、滚动条、右键菜单、焦点状态的逐控件人工点击。** 契约检查与页面截图通过，不等同于完整手工可用性验收。
