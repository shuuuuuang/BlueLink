# BTX/1.1 应用层协议

状态：冻结。所有整数使用网络字节序（Big Endian），UUID 使用 RFC 4122 的 16 字节顺序，文本使用 UTF-8。

BTX/1.1 不改变 BTX/1 的加密 Record 头和 ChaCha20-Poly1305 封装，因此旧版加密向量保持有效。

## 1. 协议协商

`PROTOCOL_HELLO`：

| 字段 | 长度 |
|---|---:|
| major | 1 |
| minor | 1 |
| reserved | 2 |
| capabilities | 4（BTX/1.0 可省略） |

本版发送 `major=1, minor=1`。能力位为：结构化聊天 `0x01`、消息回执 `0x02`、附件元数据 `0x04`、传输控制 `0x08`、断点状态 `0x10`。

收到旧的 `01 00 00 00` 时进入 BTX/1.0 兼容模式：聊天仍为纯 UTF-8，文件 Offer 使用旧格式，不发送新增消息类型。

## 2. 结构化聊天

`CHAT` payload：`BM` + schema(1) + kind + messageId + createdAtMs + bodyLength + body + attachmentCount + attachments。

每个附件包含 attachmentId、transferId、role、size、文件名、MIME 与可选 SHA-256。role 为普通文件、图片预览或图片原图。单条消息最多 2 个附件；正文最多 64 KiB。

`CHAT_RECEIPT` payload：`BR` + schema(1) + messageId + state + timestampMs。state 为 delivered、read 或 failed。

## 3. 文件 Offer

旧格式保持不变。增强格式以 `BO` + schema(1) 开始，包含 transferId、messageId、attachmentId、role、size、extentSize、SHA-256、文件名与 MIME。只有双方都声明附件元数据能力时才发送增强格式。

## 4. 传输控制

消息类型 `TRANSFER_CONTROL(32)`，payload 为 `BT` + schema(1) + transferId + action + timestampMs + UTF-8 reason。action 为 pause、resume、cancel 或 retry。

本阶段实现 cancel 的端到端语义；pause/resume/retry 在传输调度阶段接入相同格式。`RESUME_QUERY/RESUME_STATE` 和 `WINDOW_UPDATE` 继续保留用于断点与流控。

## 5. 兼容规则

- major 不一致：拒绝建立应用会话。
- minor 取双方最小值，能力取按位交集。
- 未协商的新增格式不得发送。
- 未知能力位忽略；未知消息类型仍按 BTX/1 规则拒绝。
- 结构化消息 ID、附件 ID 和传输 ID 在本地数据库中保持稳定，不因重连改变。
