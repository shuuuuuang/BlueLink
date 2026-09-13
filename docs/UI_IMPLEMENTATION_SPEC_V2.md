# BlueLink 产品与界面实施规范 V2

状态：保留 V2 实施规范记录；界面原型以 Figma 中当前确认的设计为准。

## 1. 基准资产

- 品牌标志：`design/brand/final/bluelink-final-logo.png`
- Windows / Android 原型：[BlueLink Figma](https://www.figma.com/design/Tna537kDQdpsVwDX0lXlCs/BlueLink)
- 仓库不再保存旧原型图片；实现与验收使用 Figma 中对应的已确认画板。

旧版原型已从仓库当前版本删除，不再作为实现依据。实现中不得恢复旧的蓝底 `B` 图标。

## 2. 产品边界

- 运行时只允许 Bluetooth，不申请 `INTERNET` 权限，不增加 Wi-Fi、局域网或云端回退。
- 聊天、文件、信任、设置和诊断数据只保存在本机。
- 每个已信任 Peer 最多保留一个 Active DataSession；默认最多同时连接 4 个 Peer。
- 关闭 Android Activity 或隐藏 Windows 窗口后，活动会话和传输继续运行。
- 图片属于带 preview 与 original 的附件消息；其他文件属于普通附件消息。
- 接收文件在大小限制和磁盘空间允许时自动接受、自动下载。

## 3. 统一设计语言

### 3.1 颜色

| Token | 值 | 用途 |
|---|---:|---|
| `BrandBlue` | `#176BFF` | 主操作、选中状态、进度 |
| `BrandCyan` | `#12C8E8` | LOGO 与少量品牌渐变 |
| `Ink` | `#162033` | 主文字、标题 |
| `Muted` | `#687386` | 次要信息 |
| `Canvas` | `#F5F7FB` | 页面背景 |
| `Surface` | `#FFFFFF` | 卡片与面板 |
| `Lavender` | `#F0ECFF` | 选中、提示背景 |
| `Success` | `#13A663` | 已连接、完成、已加密 |
| `Warning` | `#B06A00` | 警告、等待 |
| `Danger` | `#D92D20` | 失败、拒绝、破坏性操作 |
| `Border` | `#DCE3EF` | 分割线与描边 |

禁止使用纯黑大面积背景；图片预览遮罩除外。

### 3.2 尺寸与布局

- Windows 设计视口：1600 × 1000，100% DPI；必须同时适配 125%/150% DPI。
- Windows 最小窗口：1180 × 720；面板使用 Grid，不允许内容区域出现横向滚动条。
- Android 基准视口：412 × 915 dp；适配 360–600 dp 宽度和系统字体 1.0–1.3 倍。
- Windows 卡片圆角 14–20 px；Android 卡片圆角 16–24 dp。
- 长设备名和文件名必须单行省略；完整值通过 Tooltip、详情或预览呈现。
- 所有可点击区域最小 44 × 44 px/dp。

### 3.3 状态语义

设备显示顺序固定为：

1. 已连接
2. 离线
3. 可连接

连接状态统一为：

`OFFLINE → NEARBY → RENDEZVOUS → TRANSPORT_CONNECTING → SECURE_HANDSHAKE → TRUST_REQUIRED → CONNECTED → DISCONNECTED`

重复连接不得产生两个设备卡片；同一 Peer 只能对应一个会话和一个聊天历史。

## 4. 通用交互规则

### 4.1 多会话

- 切换会话只切换 UI 选中项，不关闭后台 Session。
- 后台会话收到消息时增加未读数；收到文件时同步更新聊天与传输中心。
- 已连接设备显示绿点；离线设备显示灰点；正在连接显示蓝色进度。
- 左侧/设备页显示的连接数必须来自真实 Active Session 数量。

### 4.2 离线会话

- 可以进入并查看本地历史消息和附件。
- 输入区不可发送新消息；断线前已进入 `LOCAL_QUEUED` 的消息保留并在重连后继续发送。
- 历史附件若本地文件已删除，卡片仍保留并显示“文件已移动或删除”。

### 4.3 图片消息

- 先发送 100–300 KiB preview，再发送 original。
- preview 可用后立即显示缩略图；original 未完成时显示进度遮罩。
- 点击缩略图打开原图预览；原图未完成时只预览已验证的 preview。
- 预览支持关闭、保存副本、分享/打开方式。

### 4.4 普通文件消息

- 显示类型图标、文件名、文件大小、方向和状态。
- Windows 点击后使用资源管理器选中文件。
- Android 点击后通过系统 chooser 选择应用。
- 文件传输完成必须以整文件 SHA-256 校验及原子提交成功为准。

### 4.5 文件限制

- 默认单文件接收上限 500 MiB，可关闭或修改。
- 超限 Offer 不写入数据块，显示“未接收”和超限原因。
- 用户提高限制后可接受仍有效的 Offer；Offer 已过期时提示对端重试。
- 磁盘空间不足、权限失效和路径不可写均使用结构化失败原因。

## 5. Windows 页面规范

| 编号 | 原型 | 必须实现的功能 |
|---:|---|---|
| 01 | `01-main-multi-session-expanded.png` | 三组设备、两条活动会话、会话切换、消息附件、展开传输栏、未读数 |
| 02 | `02-main-transfer-collapsed.png` | 传输栏折叠为窄轨道，聊天区扩展；折叠状态持久化 |
| 03 | `03-offline-conversation.png` | 离线历史、最后在线时间、不可发送状态、历史文件定位 |
| 04 | `04-auto-connecting.png` | 自动连接步骤、多个已信任设备恢复、取消/错误状态 |
| 05 | `05-settings-files-storage.png` | 文件大小限制、默认/自定义保存路径、自动下载、缩略图、空间统计 |
| 06 | `06-trust-confirmation-dialog.png` | Peer 名称、安全码、本机/对端指纹、取消和确认信任 |
| 07 | `07-image-preview-dialog.png` | 原图适配、文件信息、保存副本、资源管理器定位 |
| 08 | `08-installer-welcome.png` | 安装器欢迎、版本和纯蓝牙说明 |
| 09 | `09-installer-install-path.png` | 自定义安装路径、空间校验、默认 Download 说明 |
| 10 | `10-settings-connection.png` | 自动连接、启动扫描、后台连接、最大连接数、信任管理 |
| 11 | `11-settings-privacy-data.png` | 记录开关、保留周期、日志导出、清除数据、身份指纹 |
| 12 | `12-installer-progress.png` | 安装步骤和进度、禁止重复启动安装 |
| 13 | `13-installer-finish.png` | 安装完成、立即启动、快捷方式结果 |
| 14 | `14-all-transfers-view.png` | 全部/进行中/完成/失败筛选，按 Peer 分组，暂停、重试、定位、清理 |
| 15 | `15-uninstaller-data-choice.png` | 分项删除聊天、传输、信任设置及接收文件；默认保留数据 |

Windows 默认每用户安装路径：`%LOCALAPPDATA%\Programs\BlueLink`。

Windows 默认接收路径：`<InstallDir>\Download`。自定义安装目录必须可写；不可写路径不得继续安装。

## 6. Android 页面规范

| 编号 | 原型 | 必须实现的功能 |
|---:|---|---|
| 01 | `01-sessions-and-devices.png` | 已连接/离线/可连接分组，多会话、未读数、实时传输进度 |
| 02 | `02-chat-connected-files.png` | 会话切换、后台连接提示、文本、图片和文件消息 |
| 03 | `03-chat-offline-history.png` | 离线历史、最后在线、禁用输入、历史附件打开 |
| 04 | `04-auto-connecting.png` | 自动连接步骤，三组状态无矛盾 |
| 05 | `05-transfers.png` | 筛选、按 Peer 分组、速度、进度、排队、完成、失败、重试 |
| 06 | `06-settings-connection.png` | 自动连接、启动扫描、后台会话、最大连接数、信任管理 |
| 07 | `07-settings-files-storage.png` | 500 MiB 限制、SAF 路径、自动下载、缩略图、空间统计 |
| 08 | `08-settings-privacy-data.png` | 本地记录开关、保留期、导出/清除、身份指纹 |
| 09 | `09-diagnostics.png` | Peer/Session 状态、分类筛选、复制、清空、脱敏说明 |
| 10 | `10-trust-confirmation-dialog.png` | 安全码、指纹、确认并信任 |
| 11 | `11-image-preview-dialog.png` | 原图预览、分享、保存副本、打开方式 |
| 12 | `12-open-with-chooser.png` | `ACTION_VIEW` chooser 与临时读权限 |
| 13 | `13-file-size-limit-state.png` | 超限提示、调整限制、拒绝、不落盘 |
| 14 | `14-first-run-permissions.png` | 附近设备、广播、通知权限逐项引导 |
| 15 | `15-settings-about.png` | 版本、协议、加密、指纹、隐私、许可、本地更新说明 |

Android 底部导航固定为：设备、聊天、传输、诊断。设置通过右上角齿轮进入，不作为第五个底部 Tab。

Android 默认接收路径：公共 `Download/BlueLink`。自定义位置使用 SAF 并持久化 URI 权限。

## 7. 设置默认值

| 设置 | 默认值 |
|---|---|
| 自动连接已信任设备 | 开启 |
| 启动时扫描附近设备 | 开启 |
| 保持后台会话连接 | 开启 |
| 最大同时连接数 | 4 |
| 自动下载接收文件 | 开启 |
| 单文件接收上限 | 开启，500 MiB |
| 在聊天中显示图片缩略图 | 开启 |
| 保存聊天记录 | 开启 |
| 保存文件传输记录 | 开启 |
| 诊断日志 | 开启 |
| 数据保留期限 | 永久 |
| Windows 传输栏 | 展开 |

## 8. 诊断与隐私

- 诊断日志可记录阶段、耗时、脱敏 Peer、传输 ID 和错误码。
- 不记录消息文本、文件内容、私钥、会话密钥、安全码或完整蓝牙地址。
- 导出前再次执行脱敏。
- “检查更新”不得访问网络；V2 实现为检查用户选择的本地签名安装包。

## 9. 视觉验收

- 每个编号页面必须存在对应可导航状态或真实系统弹窗入口。
- Windows 在 1600×1000、125% DPI 截图；Android 在 412×915 dp 截图。
- 对照原型进行布局、颜色、层级、圆角和内容密度检查。
- 不要求复现 ImageGen 的字形伪影，但必须复现信息架构、组件和交互。
- 自动化截图像素差用于回归，最终视觉由人工确认。

## 10. 功能验收

- 4 台设备同时连接 30 分钟，切换会话不关闭后台连接。
- 任一后台会话可正常接收文字、图片和普通文件。
- 应用重启后历史、未读数、传输记录、设置和信任不丢失。
- 已信任设备双方启动且 Presence 可见后自动恢复连接。
- 文件完成状态只在整文件校验和原子提交后出现。
- 超限文件不创建有效载荷文件。
- Windows 文件卡片可定位文件；Android 文件卡片可打开系统 chooser。
- 运行期保持无 Internet/Wi-Fi 回退。
