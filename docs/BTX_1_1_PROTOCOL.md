# BTX/1.1 应用层协议

状态：基础 Record 冻结；可选能力按双方交集启用。所有整数使用网络字节序（Big Endian），UUID 使用 RFC 4122 的 16 字节顺序，文本使用 UTF-8。

BTX/1.1 不改变 BTX/1 的加密 Record 头和 ChaCha20-Poly1305 封装，因此旧版加密向量保持有效。

## 1. 协议协商

`PROTOCOL_HELLO`：

| 字段 | 长度 |
|---|---:|
| major | 1 |
| minor | 1 |
| reserved | 2 |
| capabilities | 4（BTX/1.0 可省略） |

本版发送 `major=1, minor=1`。能力位为：结构化聊天 `0x01`、消息回执 `0x02`、附件元数据 `0x04`、传输控制 `0x08`、断点状态 `0x10`、WPD/MTP 文件通道 `0x20`。当前能力合计 `0x3f`；未协商 `0x20` 的旧版本继续使用蓝牙文件流。

收到旧的 `01 00 00 00` 时进入 BTX/1.0 兼容模式：聊天仍为纯 UTF-8，文件 Offer 使用旧格式，不发送新增消息类型。

## 2. 结构化聊天

`CHAT` payload：`BM` + schema(1) + kind + messageId + createdAtMs + bodyLength + body + attachmentCount + attachments。

每个附件包含 attachmentId、transferId、role、size、文件名、MIME 与可选 SHA-256。role 为普通文件、图片预览或图片原图。单条消息最多 2 个附件；正文最多 64 KiB。

`CHAT_RECEIPT` payload：`BR` + schema(1) + messageId + state + timestampMs。state 为 delivered、read 或 failed。

## 3. 文件 Offer

旧格式保持不变。增强格式以 `BO` + schema(1) 开始，包含 transferId、messageId、attachmentId、role、size、extentSize、SHA-256、文件名与 MIME。只有双方都声明附件元数据能力时才发送增强格式。

## 4. 传输控制

消息类型 `TRANSFER_CONTROL(32)`，payload 为 `BT` + schema(1) + transferId + action + timestampMs + UTF-8 reason。action 为 pause、resume、cancel 或 retry。

双端已实现 pause/resume/cancel/retry；本地暂停与远端暂停互不覆盖。`RESUME_QUERY/RESUME_STATE` 和 `WINDOW_UPDATE` 继续保留用于断点与流控。

## 5. 兼容规则

- major 不一致：拒绝建立应用会话。
- minor 取双方最小值，能力取按位交集。
- 未协商的新增格式不得发送。
- 未知能力位忽略；未知消息类型仍按 BTX/1 规则拒绝。
- 结构化消息 ID、附件 ID 和传输 ID 在本地数据库中保持稳定，不因重连改变。

## 6. WPD/MTP 文件通道（2026-09-13）

生产文件通道以 Windows 系统 WPD 访问手机的 MTP 存储，Android 使用明确授权的 SAF 本地专用目录。保留 MTP，不执行 AOA START、驱动绑定或 USB 用途切换。蓝牙仍承担认证、聊天、控制、Offer 和最终确认；只有已认证蓝牙会话协商 `0x20` 后才允许 `MTP_CONTROL(33)`。

控制载荷为至多 16 KiB 的 UTF-8 JSON，字段使用 camelCase：`op`、`epoch`、`id`（transfer UUID）、`path`（路径分段）、`proof`（Base64）、`offer`（原 TransferWire 编码的 Base64）、`blob`、`key`（Base64）、`bytes`。字段按操作使用，不记录文件密钥。

1. Android 每个会话新建 `bluelink-usb-<epoch>` 子目录并写入 32 字节随机 `peer-proof`；epoch 为不带连字符的 UUID。`announce` 在加密蓝牙通道发送相对路径、证明和 epoch。Windows 逐级匹配目录并比对证明；多重匹配拒绝绑定。WPD 名称、USB 地址和 MAC 不能建立信任。
2. `hello`/`announce`/`ready` 维持可用状态；`disabled` 撤销通道并结束此通道任务。`ready` 属于特定 epoch 与 Peer，只能点亮该会话的闪电。
3. 双向发送先发 `queue`（原始 Offer）。Windows 按物理 WPD 设备统一 FIFO 调度，发 `start` 后才进入原有 Offer/Accept 流程，因此后面的文件不会提前触发接收询问超时。不同设备独立排队。Android 本地准备顺序、Windows 批量提交顺序保持稳定。
4. `start` 后发送方将稳定快照加密为随机 `<uuidN>.blm`。Windows 发起时经 WPD Upload 写入手机；Android 发起时写入 SAF，Windows Download 读取。`blob` 携带随机文件名及本次密钥；接收端验证后以 `data` 确认。原 `TRANSFER_FINISH`/整文件 SHA-256/原子发布/`TRANSFER_COMPLETE` 保留；`end` 释放队列槽位。只有最终发布完成才显示完成。
5. `progress` 由 Windows 报告 WPD 数据进度。暂停队首保留槽位；取消等待项不打断当前流，取消当前项要等本地读写关闭后才能开始下一项。普通消息和控制不排在大文件之后。
6. 设备忙只在尚未创建/打开数据流前有界退避（100 ms 起、最多 1 s 间隔、45 s 上限），部分写入不自动重放。WPD 目录探测、清理和文件流共用每设备访问门，防止自身竞争。COM 调用期间的物理设备卡死仍依赖系统驱动返回，不承诺可强制中断任何原生阻塞。

### BLM1 加密中转格式

每次尝试生成独立 32 字节密钥，仅经已认证的 BTX 通道发送。文件按 1 MiB 明文记录划分，每条写入 ChaCha20-Poly1305 密文及 16 字节 tag，无额外明文头。nonce 为 ASCII `BLM1` 的 4 字节加 8 字节大端记录序号；AAD 为 `BLM1`、RFC4122 UUID（16 字节）、总明文大小（int64）、记录序号（int64）、记录明文长度（int32），均为大端。

零字节文件仍有一条零长度记录和 16 字节 tag。接收端核对每条 tag、文件 ID、大小及结尾，不接受截断、额外尾部或错误 ID。加密内容只在私有接收目录解密，最终仍校验 Offer 的 SHA-256；公共 SAF 中转目录不存密钥或明文。清理限定本次随机文件和明确创建的会话对象，绝不递归清理用户授权树。

此版 WPD 重试从完整文件重新加密、传送；不会把旧密文的部分偏移当成新密钥下的续传点。蓝牙原有断点逻辑不变。转存会增加本地临时空间需求，空间不足按单个任务失败处理；没有持久化 USB 排队任务跨进程自动恢复保证。
