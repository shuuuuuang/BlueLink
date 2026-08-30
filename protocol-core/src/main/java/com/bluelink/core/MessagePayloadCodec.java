package com.bluelink.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;

public final class MessagePayloadCodec {
    private static final byte[] MESSAGE_MAGIC = {'B', 'M'};
    private static final byte[] RECEIPT_MAGIC = {'B', 'R'};
    private static final int SCHEMA = 1;
    private static final int MAX_BODY = 64 * 1024;
    private static final int MAX_ATTACHMENTS = 2;

    private MessagePayloadCodec() {}

    public static byte[] encode(ChatEnvelope value) throws IOException {
        if (value.attachments().size() > MAX_ATTACHMENTS) throw new IOException("Too many attachments");
        byte[] body = utf8(value.body(), MAX_BODY, "message body");
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream output = new DataOutputStream(bytes)) {
            output.write(MESSAGE_MAGIC); output.writeByte(SCHEMA); output.writeByte(value.kind().code());
            writeUuid(output, value.messageId()); output.writeLong(value.createdAt());
            output.writeInt(body.length); output.write(body); output.writeByte(value.attachments().size());
            for (AttachmentDescriptor item : value.attachments()) writeAttachment(output, item);
        }
        return bytes.toByteArray();
    }

    public static ChatEnvelope decode(byte[] payload) throws IOException {
        try (DataInputStream input = new DataInputStream(new ByteArrayInputStream(payload))) {
            expect(input, MESSAGE_MAGIC, "message");
            if (input.readUnsignedByte() != SCHEMA) throw new IOException("Unsupported message schema");
            ChatPayloadKind kind;
            try { kind = ChatPayloadKind.fromCode(input.readUnsignedByte()); }
            catch (IllegalArgumentException failure) { throw new IOException(failure.getMessage(), failure); }
            var messageId = readUuid(input); long createdAt = input.readLong();
            String body = readUtf8(input, input.readInt(), MAX_BODY, "message body");
            int count = input.readUnsignedByte();
            if (count > MAX_ATTACHMENTS) throw new IOException("Too many attachments");
            var attachments = new ArrayList<AttachmentDescriptor>(count);
            for (int index = 0; index < count; index++) attachments.add(readAttachment(input));
            ensureEnd(input);
            return new ChatEnvelope(messageId, kind, createdAt, body, java.util.List.copyOf(attachments));
        }
    }

    public static byte[] encodeReceipt(ChatReceipt value) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream output = new DataOutputStream(bytes)) {
            output.write(RECEIPT_MAGIC); output.writeByte(SCHEMA); writeUuid(output, value.messageId());
            output.writeByte(value.state().code()); output.writeLong(value.timestamp());
        }
        return bytes.toByteArray();
    }

    public static ChatReceipt decodeReceipt(byte[] payload) throws IOException {
        try (DataInputStream input = new DataInputStream(new ByteArrayInputStream(payload))) {
            expect(input, RECEIPT_MAGIC, "receipt");
            if (input.readUnsignedByte() != SCHEMA) throw new IOException("Unsupported receipt schema");
            var id = readUuid(input); ReceiptState state;
            try { state = ReceiptState.fromCode(input.readUnsignedByte()); }
            catch (IllegalArgumentException failure) { throw new IOException(failure.getMessage(), failure); }
            var result = new ChatReceipt(id, state, input.readLong()); ensureEnd(input); return result;
        }
    }

    public static boolean isStructured(byte[] payload) {
        return payload.length >= 3 && payload[0] == 'B' && payload[1] == 'M';
    }

    static void writeUuid(DataOutputStream output, java.util.UUID value) throws IOException {
        output.writeLong(value.getMostSignificantBits()); output.writeLong(value.getLeastSignificantBits());
    }

    static java.util.UUID readUuid(DataInputStream input) throws IOException {
        return new java.util.UUID(input.readLong(), input.readLong());
    }

    static byte[] utf8(String value, int max, String field) throws IOException {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        if (bytes.length > max) throw new IOException(field + " is too long");
        return bytes;
    }

    static String readUtf8(DataInputStream input, int length, int max, String field) throws IOException {
        if (length < 0 || length > max) throw new IOException("Invalid " + field + " length");
        return new String(readExact(input, length), StandardCharsets.UTF_8);
    }

    static byte[] readExact(DataInputStream input, int length) throws IOException {
        byte[] value = input.readNBytes(length);
        if (value.length != length) throw new EOFException("Truncated payload");
        return value;
    }

    static void ensureEnd(DataInputStream input) throws IOException {
        if (input.available() != 0) throw new IOException("Unexpected trailing payload");
    }

    private static void writeAttachment(DataOutputStream output, AttachmentDescriptor value) throws IOException {
        if (value.size() < 0 || value.sha256() != null && value.sha256().length != 32)
            throw new IOException("Invalid attachment metadata");
        byte[] name = utf8(value.fileName(), 512, "file name");
        byte[] mime = utf8(value.mimeType(), 255, "MIME type");
        writeUuid(output, value.attachmentId()); writeUuid(output, value.transferId());
        output.writeByte(value.role().code()); output.writeLong(value.size());
        output.writeShort(name.length); output.write(name); output.writeShort(mime.length); output.write(mime);
        output.writeByte(value.sha256() == null ? 0 : value.sha256().length);
        if (value.sha256() != null) output.write(value.sha256());
    }

    private static AttachmentDescriptor readAttachment(DataInputStream input) throws IOException {
        var attachmentId = readUuid(input); var transferId = readUuid(input); AttachmentRole role;
        try { role = AttachmentRole.fromCode(input.readUnsignedByte()); }
        catch (IllegalArgumentException failure) { throw new IOException(failure.getMessage(), failure); }
        long size = input.readLong(); if (size < 0) throw new IOException("Invalid attachment size");
        String name = readUtf8(input, input.readUnsignedShort(), 512, "file name");
        String mime = readUtf8(input, input.readUnsignedShort(), 255, "MIME type");
        int hashLength = input.readUnsignedByte();
        if (hashLength != 0 && hashLength != 32) throw new IOException("Invalid attachment hash");
        return new AttachmentDescriptor(attachmentId, transferId, role, name, mime, size,
                hashLength == 0 ? null : readExact(input, hashLength));
    }

    private static void expect(DataInputStream input, byte[] expected, String field) throws IOException {
        if (!java.util.Arrays.equals(readExact(input, expected.length), expected))
            throw new IOException("Invalid " + field + " payload");
    }
}
