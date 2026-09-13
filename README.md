# 蓝联 / BlueLink

BlueLink 是一个 Android 与 Windows 之间的端到端本地通信应用，以 Bluetooth 为基础通道。应用使用 BLE Presence 查找当前在附近的 BlueLink 节点，通过 GATT Rendezvous 协商 RFCOMM 参数，再在可靠双向流上运行统一的 BTX/1.1 安全会话、聊天和文件传输。

USB 配件通道已接入双端源码，默认关闭；使用同一 BTX/1.1 身份认证与加密，安全握手完成后优先使用 USB。同一设备的蓝牙通道仍可备用。Android 已完成部分真机页面验收，实际 AOA、驱动、双端文件传输与拔插回退尚未完成验收，详见 `docs/IMPLEMENTATION_STATUS.md`。

运行时不会申请或使用互联网、Wi-Fi、Wi-Fi Direct、局域网、HTTP 或 WebSocket。构建阶段仍需从 Google、NuGet 和 Gradle 仓库下载编译依赖。

## 已实现

- Android Compose 客户端：附近设备、离线历史会话、聊天附件、图片预览、全局传输、诊断、权限引导和四类设置页面。
- Windows WPF 客户端：已连接/离线/可连接会话栏、后台多会话、聊天附件、图片预览、可折叠传输栏、全局传输、设置、托盘驻留和安装/卸载向导。
- 多设备会话：默认同时保持 4 个端到端加密连接；切换前台会话不会断开后台会话，后台仍可接收消息和文件。
- 本地持久化：设备、信任、聊天、附件、传输、未读状态和设置写入本机数据库，离线设备仍可打开历史会话。
- 独立设备身份：Ed25519 身份、X25519 临时密钥、首次六位安全代码、已信任公钥固定。
- 加密记录层：ChaCha20-Poly1305、加密帧头、严格 sequence、重放/乱序拒绝、1 MiB 记录上限。
- 多路优先级：控制和聊天可以越过文件 extent；只有单一 writer 接触 socket。
- 文件安全：不可变发送快照、精确长度、64 KiB 块落盘确认、extent/whole-file SHA-256、按传输 ID 隔离的 `.part`、关闭校验句柄后原子改名及短暂占用重试、路径穿越和 Windows 保留名防护。
- 平台无关内核：角色选举、Android/Windows transport policy、resume bitmap、互操作测试向量。

当前实现状态和进入正式发布前必须补齐的项目见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

## 工程结构

```text
shared-spec/       BTX/1 与 rendezvous Protobuf、常量和测试向量
protocol-core/     无平台依赖的 Java 17 参考实现与自动校验
android/           Kotlin + Jetpack Compose Android 应用
windows/           C# + WPF Windows 驻留应用
scripts/           构建与验证入口
```

## 快速验证

只验证共享协议内核，不需要 Android 或 .NET SDK：

```powershell
.\gradlew.bat verify
```

当前校验覆盖密钥协商、加密 record、篡改拒绝、稳定 wire vector、role election、transport policy、优先级、路径安全、extent bitmap、断点补齐，以及 Windows 接收端句柄释放、同名传输隔离和原子落盘重试。

## 构建 Android

要求 Android SDK 34、JDK 17。设置 `ANDROID_SDK_ROOT` 后运行：

```powershell
.\scripts\build-android.ps1
```

APK 输出到 `android/app/build/outputs/apk/debug/app-debug.apk`。最低系统版本为 Android 13（API 33），这是为了使用平台内置 Ed25519/X25519/ChaCha20-Poly1305 能力。

## 构建 Windows

要求 Windows 11 和 .NET 8 SDK。普通编译：

```powershell
.\scripts\build-windows.ps1
```

构建产物位于 `windows/BlueLink.App/bin/Release/net8.0-windows10.0.19041.0/`。

生成无需预装 .NET 的 x64 单文件发布版和安装包：

```powershell
.\scripts\build-windows-release.ps1
```

发布目录为 `artifacts/windows/win-x64/`，安装包名称为 `artifacts/installer/BlueLink-Setup-<VERSION>-win-x64.exe`。版本号以仓库根目录 `VERSION` 为唯一基线。安装向导支持选择路径；默认安装到当前用户目录，默认接收目录为安装路径下的 `Download`。卸载时可分别保留或删除本地记录与已接收文件。

## 首次连接

1. 两端都打开 BlueLink，并授予附近设备/蓝牙权限。Android 与 Windows 都发布可连接 GATT Rendezvous、设备信息和固定 RFCOMM service UUID。
2. 在 Android“附近设备”中扫描，只会显示本轮收到 BlueLink Presence 的节点；历史配对设备不会进入附近列表。
3. Android 点击 Windows 时，会先通过 GATT 读取真实 Windows 设备名和 Classic 地址，再作为 RFCOMM dialer 发起连接。Windows 点击 Android 时，会向 Android GATT 写入 Connect Request，由 Android 回连 Windows RFCOMM；首次未配对时系统会显示配对提示。
4. 首次连接时核对两端六位安全代码并确认。之后设备身份公钥会被固定。
5. 在聊天区发送文字、图片或文件。附件会直接进入聊天记录，图片可点击预览，普通文件可用系统应用打开：
   - Android：默认公开到系统 `Download/BlueLink`，也可通过系统目录选择器指定位置。
   - Windows：默认保存到安装目录下的 `Download`，也可在设置中指定可写目录。

本仓库没有后端服务，也没有任何设备间网络 fallback。
