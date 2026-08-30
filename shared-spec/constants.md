# BTX/1 constants

This directory is the cross-platform source of truth. Android and Windows must
not infer protocol behavior from application versions.

| Constant | Value |
|---|---:|
| Protocol major | `1` |
| RFCOMM service UUID | `9c6c51a8-8f9a-4f67-96aa-2df57177b101` |
| BLE presence UUID | `9c6c51a8-8f9a-4f67-96aa-2df57177b102` |
| BLE rendezvous UUID | `9c6c51a8-8f9a-4f67-96aa-2df57177b103` |
| Transport offer characteristic UUID | `9c6c51a8-8f9a-4f67-96aa-2df57177b104` |
| Connect request characteristic UUID | `9c6c51a8-8f9a-4f67-96aa-2df57177b105` |
| Default max encrypted record | 1 MiB |
| Preferred segment | 64 KiB |
| Default extent | 4 MiB |
| Default connection window | 8 MiB |
| Default stream window | 4 MiB |

All integers on the BTX record wire are big-endian. A record is a four-byte
encrypted-body length followed by one ChaCha20-Poly1305 ciphertext. The frame
header, type, stream id, sequence and payload are all encrypted.

## BLE rendezvous wire profile

Android advertises a non-connectable manufacturer frame so Windows can identify
only a currently nearby BlueLink process. The company id is `0xFFFF` and the
12-byte payload is:

```text
42 4C | protocol-major | platform | ephemeral-presence-id[8]
```

Windows advertises only the connectable/discoverable GATT rendezvous service.
Its compact service data is:

```text
protocol-major | platform=2 | ephemeral-presence-id[0..5]
```

The `Transport offer` characteristic is read before RFCOMM setup:

```text
protocol-major | classic-address[6] | utf8-name-length | utf8-windows-name
```

The classic address is big-endian and the name is limited to 80 UTF-8 bytes.
Windows supplies a non-zero Classic address for direct Android dialing. Android
uses an all-zero address to advertise callback capability because ordinary apps
cannot depend on access to the local Android Classic address.

Windows initiates an Android connection by writing the same compact payload,
with its own non-zero Classic address and name, to the `Connect request`
characteristic. Android then dials that RFCOMM endpoint. The advertising address
from a GATT result must never be reused as an inferred Classic address.
