# BlueLink 0.2.9 发布验收记录

日期：2026-08-28。本文只记录本轮最终产物实际执行过的检查。

## 已通过

- 共享 Java BTX 验证通过。
- Windows 传输验证 16 项、SQLite 5 项、身份恢复 4 项、BTX/1.1 互操作 12 项、文件拖放 5 项、不可变发送快照 2 项全部通过。
- Windows 聊天时间分组验证通过，覆盖当天、昨天、本周、本年和跨年格式。
- Windows Release 发布、SetupUI、MSI 与 Burn Bundle 均 0 warning、0 error。
- Windows 状态化 UI 冒烟通过：传输栏展开、收起、1180×720 紧凑窗口三种状态均生成最终渲染截图。
- Windows 图片预览实图冒烟通过：透明棋盘格、等比适应、缩放、旋转及底部操作栏均生成最终渲染截图。
- 自定义安装器五页实图冒烟通过：欢迎、安装位置、安装进度、完成、维护；四角裁剪由最终窗口截图确认。
- 安装路径规范化验证通过：根目录和普通目录自动追加 `BlueLink`，已名为 `BlueLink` 的目录不重复追加。
- 0.2.9 MSI 对本机 0.2.8 安装执行升级成功；最终 Bundle 不再调用旧缓存 Bundle，注册、非破坏性修复和已安装程序启动冒烟通过。
- 已安装 `D:\BlueLink\BlueLink.exe` 与最终 Windows 发布物 SHA-256 完全一致，版本为 0.2.9，`D:\BlueLink\Download` 存在。
- Android `protocol-core:test`、`app:assembleDebug`、`app:lintDebug` 全部成功。
- APK versionCode 11、versionName 0.2.9；签名、应用标签、产品图标、无 INTERNET 权限由发布校验脚本检查。

## 发布产物

| 产物 | 大小 | SHA-256 |
|---|---:|---|
| `artifacts/installer/BlueLink-Setup-0.2.9-win-x64.exe` | 68,097,915 | `B68198E81B7B6D28F8B55A811331A562F73F234311DB76B757B498774CB70CD7` |
| `artifacts/windows/win-x64/BlueLink.exe` | 195,153,790 | `072D6686F9434FC6FA34E981577979D3B6C79DAE5C11D7A1231E0127EB5AA8ED` |
| `artifacts/android/BlueLink-0.2.9-android-debug.apk` | 66,902,942 | `FBA182A155BCA7C1EA905D3D6FE082BBAE3E98262EE9EF4F65C59CC5C7562A57` |

## 硬件或签名限制

- `adb devices -l` 无在线 Android 真机，因此新 APK 的真机安装启动、蓝牙发现、长按菜单和 Android 视觉逐屏验收不能伪装为已通过。
- 双设备并发、30 分钟稳定性、系统蓝牙开关、睡眠/唤醒和厂商省电策略仍需真实硬件矩阵。
- 为保留当前已升级的 `D:\BlueLink` 安装，本轮没有再次执行最终 Bundle 的卸载清理；安装、升级、修复和启动链路已实际通过。
- Android APK 为 debug 签名，Windows 产物未代码签名，不能作为公开商店/企业分发的正式签名包。
