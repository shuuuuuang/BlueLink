# BlueLink 实现状态

更新日期：2026-08-28。版本基线：0.2.9。本文只记录可由源码、测试、真机或发布产物证明的状态。

状态定义：`源码`＝代码已接入；`自动`＝自动验证通过；`真机`＝真实设备验收通过；`发布`＝已经进入同版本安装包/APK。四项不得互相替代。

## 证据状态

| 模块 | 状态 | 说明 |
|---|---|---|
| BTX/1.1 framing / record | 源码、自动、发布 | 共享参考实现、Windows BTX/1.1 互操作 12 项于 0.2.9 发布前通过 |
| Identity / trust / E2EE | 源码、自动、单机真机 | 首次安全码和身份公钥固定已完成；仍需多设备和异常身份测试 |
| BLE Presence / GATT rendezvous | 源码、单机真机 | 双端互相发现已由用户验证；仍需多设备及系统恢复测试 |
| RFCOMM Android ↔ Windows | 源码、单机真机 | 单设备双向连接和基本自动重连已验证 |
| 多设备连接 | 源码、自动接线 | 默认最多 4 个活动 Peer；可信设备按顺序自动恢复并有 5 秒后台重试；尚未完成 2/4 台真实设备压力验收 |
| 会话排序与离线历史 | 源码、自动渲染 | 已连接、离线、附近新设备分组及离线历史入口存在；已按传输地址/身份键去重，待多会话真机验收 |
| Chat / receipts | 源码、自动、单机真机 | 基本文本已验收；未读、后台与排队补发仍需矩阵测试 |
| 图片与文件消息 | 源码、自动、单机真机、发布 | 聊天附件、传输中心、进度/速度/ETA、限制尺寸缩略图与图片预览已接入；新 0.2.9 Android 真机 UI 尚待复验 |
| 文件可靠传输 | 源码、自动、单机真机、发布 | 块/整文件校验、原子提交、不可变发送快照、持久化 `.part.resume` 与同 ID 重试均有自动回归 |
| 本地持久化 | 源码、Windows 自动 | SQLite/Room 结构存在；Android 记录开关刚接入，待重启行为测试 |
| 设置与数据管理 | 源码、静态接线验证 | Windows 4 项、Android 5 项运行时策略已接线；待双端行为测试 |
| Android UI | 源码、静态合约、Lint、发布 | 15 个页面入口/系统入口已核对；无在线真机，逐屏视觉及长按手势仍待复验 |
| Windows UI | 源码、自动渲染、发布 | 展开、收起、1180×720 与图片预览四种状态化渲染冒烟通过；人工逐页与 125%/150% DPI 仍待复验 |
| Android 发布产物 | 发布（内部 Debug） | `BlueLink-0.2.9-android-debug.apk` 已通过 assemble、Lint、badging、签名和无 INTERNET 检查 |
| Windows 安装程序 | 发布、实际升级/修复验收 | `BlueLink-Setup-0.2.9-win-x64.exe` 已验证 0.2.8→0.2.9 升级、Download、已安装启动、哈希和修复；五页 UI 自动截图通过 |
| Runtime 网络边界 | 源码、自动、发布 | 发布 APK 的最终权限清单确认无 `INTERNET`，只包含蓝牙/前台服务/通知权限 |

## 已知限制

1. 当前 Android 产物使用 debug 签名，Windows EXE/安装包没有商业代码签名证书；仅适合内部验收。
2. 断点数据可跨进程恢复，但发起方仍需在重连后点击重试；同 ID 重试会从接收方连续已提交 extent 继续。超限 Offer 在活动会话内保留约 30 秒，过期需对端重试。
3. 流控仍为逐 64 KiB 块确认；大文件吞吐量受经典蓝牙和 stop-and-wait 往返影响。
4. 当前安全协议为项目自有 BTX/1.1；正式高安全等级发布前仍需独立密码协议审计。
5. 当前主机没有在线 Android 设备，因此新 0.2.9 APK 的安装、Android 逐屏、长按、后台、多设备与蓝牙中断矩阵尚未在本轮执行。
6. Windows 已完全使用 WiX Burn + 自定义 WPF 安装 UI；欢迎、路径、进度、完成、维护五页自动截图通过，125%/150% DPI 仍需用户桌面复验。

## 发布前真机矩阵

- Windows 11 × Android 13/14/15，至少覆盖高通、联发科与三星蓝牙栈。
- 1、2、4 个并发 Peer：后台消息、文件接收、切换会话、自动重连和重复连接竞争。
- 0 B、1 KiB、10 MiB、500 MiB 文件；同名、长文件名、中文名、磁盘不足、文件占用、主动取消和蓝牙中断。
- Windows 睡眠/唤醒、Android 锁屏/省电、蓝牙开关、进程终止与重启后的历史恢复。
- 100%/125%/150% Windows DPI 与 Android 360–600 dp、字体缩放 1.0–1.3。

## 自动验证命令

```powershell
# Android 与共享协议
$env:ANDROID_SDK_ROOT='D:\Tool\Android\Sdk'
$env:JAVA_HOME='D:\Tool\jdk-17.0.20'
D:\Tool\gradle-8.5\bin\gradle.bat --no-daemon -p .\android :protocol-core:verify :app:assembleDebug
D:\Tool\gradle-8.5\bin\gradle.bat --no-daemon -p .\android :app:lintDebug

# Windows 协议、SQLite 与文件接收器
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget-mirror-test"
D:\Tool\dotnet-sdk-8\dotnet.exe build .\windows\BlueLink.TransferVerification\BlueLink.TransferVerification.csproj -c Release --no-restore
D:\Tool\dotnet-sdk-8\dotnet.exe .\windows\BlueLink.TransferVerification\bin\Release\net8.0-windows10.0.19041.0\BlueLink.TransferVerification.dll
```
