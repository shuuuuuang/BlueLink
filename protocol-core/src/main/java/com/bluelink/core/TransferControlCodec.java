package com.bluelink.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;

public final class TransferControlCodec {
    private static final byte[] MAGIC = {'B', 'T'};
    private TransferControlCodec() {}

    public static byte[] encode(TransferControl value) throws IOException {
        byte[] reason = MessagePayloadCodec.utf8(value.reason(), 512, "transfer reason");
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream output = new DataOutputStream(bytes)) {
            output.write(MAGIC); output.writeByte(1); MessagePayloadCodec.writeUuid(output, value.transferId());
            output.writeByte(value.action().code()); output.writeLong(value.timestamp());
            output.writeShort(reason.length); output.write(reason);
        }
        return bytes.toByteArray();
    }

    public static TransferControl decode(byte[] payload) throws IOException {
        try (DataInputStream input = new DataInputStream(new ByteArrayInputStream(payload))) {
            byte[] magic = MessagePayloadCodec.readExact(input, 2);
            if (!java.util.Arrays.equals(magic, MAGIC) || input.readUnsignedByte() != 1)
                throw new IOException("Invalid transfer control payload");
            var id = MessagePayloadCodec.readUuid(input); TransferControlAction action;
            try { action = TransferControlAction.fromCode(input.readUnsignedByte()); }
            catch (IllegalArgumentException failure) { throw new IOException(failure.getMessage(), failure); }
            long timestamp = input.readLong();
            String reason = MessagePayloadCodec.readUtf8(input, input.readUnsignedShort(), 512, "transfer reason");
            MessagePayloadCodec.ensureEnd(input);
            return new TransferControl(id, action, timestamp, reason);
        }
    }
}
