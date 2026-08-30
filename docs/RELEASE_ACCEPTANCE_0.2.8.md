# BlueLink 0.2.8 发布验收记录

日期：2026-08-28。本文记录本轮实际执行结果，不以源码存在代替测试结果。

## 已通过

- 共享 Java BTX 验证：41 项。
- Windows 传输验证：16 项；SQLite：5 项；身份恢复：4 项；BTX/1.1 互操作：12 项；文件拖放：5 项；不可变发送快照：2 项。
- Windows Release 编译：0 warning、0 error。
- Windows 状态化 UI 冒烟：传输栏展开、收起、1180×720 最小窗口三种渲染成功；测试中发现并修复只读 `Progress` 的双向绑定崩溃。
- Windows 安装器：Inno 标准向导已完全移除，改为 WiX Burn + 自定义 WPF 安装 UI；严格按原型采用“欢迎 → 安装位置 → 进度 → 完成”顺序，并为已安装状态提供维护页；五个页面均由最终安装包截图验证。
- Windows 安装闭环：最终安装包实际安装到隔离自定义路径；验证 0.2.8、`Download` 目录、已安装 EXE 启动、产品图标、修复后再次启动、卸载清理，且安装目录不存在旧 Inno `unins000.exe`。
- Android：protocol-core tests、assembleDebug、lintDebug 均成功；Lint 0 error（剩余 2 个工具链/性能类 warning）。
- Android API 33 兼容：以自实现精确跳过替换 API 34 的 `InputStream.skipNBytes`。
- APK：`com.bluelink.android`、versionCode 10、versionName 0.2.8、minSdk 33、targetSdk 34；v2 签名验证通过。
- APK 最终权限清单无 `android.permission.INTERNET`；产品自适应、圆形及 monochrome 图标已打包。

## 发布产物

| 产物 | 大小 | SHA-256 |
|---|---:|---|
| `artifacts/installer/BlueLink-Setup-0.2.8-win-x64.exe` | 68,096,547 | `1CCEA5DE555C1144D790CF2CF275D3299C402C24C0A47F2E507DF52127093423` |
| `artifacts/windows/win-x64/BlueLink.exe` | 195,153,790 | `30528E56B00A2E277C7769D78563BFC5C4E6B4E8E66FF495EF4D8C1D3F5052A3` |
| `artifacts/android/BlueLink-0.2.8-android-debug.apk` | 66,898,846 | `399375E21269C008892A797734318C073BAF18FA0BEC6DC824ED6978DE6A16CD` |

## 本轮无法执行，禁止标记为通过

- `adb devices -l` 返回空列表；没有在线 Android 真机。
- 新 APK 的安装启动、412×915 dp 逐屏视觉、长按菜单、系统 chooser、后台/锁屏仍需真机。
- 2 台和 4 台设备并发、30 分钟稳定性、系统蓝牙开关、睡眠/唤醒、厂商省电策略仍需硬件矩阵。
- Windows 125%/150% DPI 与自定义安装向导的真实鼠标/键盘交互仍需用户桌面人工确认；自动截图已覆盖默认 DPI 的五个页面与控件状态。
- Android APK 为 debug 签名，Windows 产物未代码签名，不能视为公开正式发行签名。
