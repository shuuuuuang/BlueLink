# BlueLink BTX/1 developer preview

BTX/1 runs only after a reliable ordered full-duplex Bluetooth transport is
established. Runtime HTTP, WebSocket, Wi-Fi and Internet fallbacks are forbidden.

## Session sequence

1. Exchange signed identity hello records in plaintext on the newly connected
   transport. Each hello contains an Ed25519 identity key, an ephemeral X25519
   key, a random nonce and a signature over the hello fields.
2. Validate that `peer_id = SHA-256(identity_public_key)[0..15]`.
3. Derive two directional record keys using X25519 and HKDF-SHA-256 over the
   ordered transcript. Display the same six-digit safety code on both peers.
4. For a new peer, do not mark the peer trusted until both users confirm the
   safety code. Existing trusted peers must present the pinned identity key.
5. Send encrypted `PROTOCOL_HELLO`; application traffic is legal only after both
   sides agree on the protocol version and feature intersection.

The developer-preview handshake is deliberately isolated behind `SessionCrypto`
so it can be replaced by an audited Noise XX/IK implementation without changing
the BTX frames or application domain.

## Streams

- stream `0`: connection control, security, ping/pong
- stream `1`: chat and receipts
- stream `>= 2`: dynamically allocated attachment/file streams

Only the session writer touches the transport. It services frames by priority and
observes both connection and per-stream credit. A receiver grants new credit only
after decrypted content has been validated and persisted.

## File resume

Files are split into 4 MiB extents by default. Every extent and the completed file
use SHA-256. The receiver writes to a managed `.part` path, atomically renames only
after whole-file verification, and persists a completed-extent bitmap independent
of any Bluetooth session. A reconnect sends `RESUME_QUERY` / `RESUME_STATE` and
only missing extents are transmitted.

The developer-preview application currently maps each 64 KiB transport segment to
one transfer extent. A receiver sends `TRANSFER_EXTENT_ACK` only after that segment
has passed SHA-256 validation, has been written, and has been flushed. The sender
may send `TRANSFER_FINISH` only after all segment acknowledgements have arrived and
must remain in `VERIFYING` until the receiver validates the whole-file hash, performs
the atomic rename, and responds with `TRANSFER_COMPLETE`. `TRANSFER_FAILED` aborts
only the affected transfer stream; it does not close the BTX session.

Reliable transfer message additions used by application version 0.2.4 and later are:

| Type | Code | Payload |
|---|---:|---|
| `TRANSFER_EXTENT_ACK` | 27 | transfer UUID (16 bytes), extent index (big-endian int32) |
| `TRANSFER_COMPLETE` | 28 | transfer UUID (16 bytes) |
| `TRANSFER_FAILED` | 29 | transfer UUID, UTF-8 reason length (uint16), reason |

### Optional device display name in the authenticated greeting

PROTOCOL_HELLO retains its original 8-byte version/capability prefix. An optional `BLDN` ASCII marker at offset 8, followed by a 2-byte big-endian UTF-8 byte length and the name, communicates the app-configured display name. Maximum 384 UTF-8 bytes / 128 UTF-16 code units; control characters are not accepted. The metadata is inside the existing encrypted greeting and is applied only after trust confirmation succeeds. It never identifies, merges, or trusts a peer.

Existing 4/8-byte peers remain supported; the old decoders ignore trailing bytes. Missing, unknown, malformed, or invalid name extensions use the existing display-name fallback. A new configured name is advertised to the remote session on its next handshake.

## Optional WPD file transport

The production Windows–Android USB file path now uses WPD/MTP instead of AOA.
Authenticated Bluetooth remains the control and message connection. Capability
`0x20` gates `MTP_CONTROL(33)`; peers without it retain ordinary Bluetooth files.
Per-device bidirectional FIFO scheduling, SAF proof binding, BLM1 encrypted spool
records, failure boundaries and full-file retry are specified in
[`docs/BTX_1_1_PROTOCOL.md`](../docs/BTX_1_1_PROTOCOL.md#6-wpdmtp-文件通道2026-09-13).
No network or driver-switch fallback is added.
