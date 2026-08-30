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
