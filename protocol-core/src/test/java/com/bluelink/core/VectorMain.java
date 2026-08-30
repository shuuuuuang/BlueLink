package com.bluelink.core;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.UUID;
import java.util.HexFormat;

public final class VectorMain {
    public static void main(String[] args) throws Exception {
        byte[] key = new byte[32];
        for (int index = 0; index < key.length; index++) key[index] = (byte) index;
        var output = new ByteArrayOutputStream();
        BtxRecordCodec.write(output, new BtxFrame(WireMessageType.CHAT, 0, 1, 7,
                "BTX vector".getBytes(StandardCharsets.UTF_8)), key, 0x11223344);
        System.out.println(HexFormat.of().formatHex(output.toByteArray()));

        byte[] hash = new byte[32];
        for (int index = 0; index < hash.length; index++) hash[index] = (byte) index;
        var envelope = new ChatEnvelope(
                UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"), ChatPayloadKind.FILE,
                1_700_000_000_123L, "BTX 1.1",
                List.of(new AttachmentDescriptor(
                        UUID.fromString("11111111-2222-3333-4444-555555555555"),
                        UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        AttachmentRole.FILE, "report.pdf", "application/pdf", 123_456L, hash)));
        System.out.println("MESSAGE=" + HexFormat.of().formatHex(MessagePayloadCodec.encode(envelope)));
        var control = new TransferControl(UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                TransferControlAction.CANCEL, 1_700_000_000_456L, "user canceled");
        System.out.println("CONTROL=" + HexFormat.of().formatHex(TransferControlCodec.encode(control)));
    }
}
