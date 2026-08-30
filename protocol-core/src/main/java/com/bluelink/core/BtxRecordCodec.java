package com.bluelink.core;

import javax.crypto.Cipher;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.security.GeneralSecurityException;

public final class BtxRecordCodec {
    private static final int HEADER_SIZE = 16;

    private BtxRecordCodec() {}

    public static void write(OutputStream output, BtxFrame frame, byte[] key, int noncePrefix)
            throws IOException, GeneralSecurityException {
        validateKey(key);
        byte[] plain = ByteBuffer.allocate(HEADER_SIZE + frame.payload().length)
                .order(ByteOrder.BIG_ENDIAN)
                .put((byte) BtxConstants.PROTOCOL_MAJOR)
                .put((byte) frame.type().code())
                .putShort((short) frame.flags())
                .putInt(frame.streamId())
                .putLong(frame.sequence())
                .put(frame.payload())
                .array();
        if (plain.length + 16 > BtxConstants.MAX_RECORD_SIZE) throw new IOException("Record exceeds max size");
        int encryptedLength = plain.length + 16;
        byte[] length = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(encryptedLength).array();
        Cipher cipher = cipher(Cipher.ENCRYPT_MODE, key, noncePrefix, frame.sequence());
        cipher.updateAAD(length);
        byte[] encrypted = cipher.doFinal(plain);
        DataOutputStream data = new DataOutputStream(output);
        data.write(length);
        data.write(encrypted);
        data.flush();
    }

    public static BtxFrame read(InputStream input, byte[] key, int noncePrefix, ReplayGuard replayGuard)
            throws IOException, GeneralSecurityException {
        validateKey(key);
        DataInputStream data = new DataInputStream(input);
        byte[] lengthBytes = data.readNBytes(4);
        if (lengthBytes.length == 0) throw new EOFException("Transport closed");
        if (lengthBytes.length != 4) throw new EOFException("Truncated record length");
        int encryptedLength = ByteBuffer.wrap(lengthBytes).order(ByteOrder.BIG_ENDIAN).getInt();
        if (encryptedLength < HEADER_SIZE + 16 || encryptedLength > BtxConstants.MAX_RECORD_SIZE) {
            throw new IOException("Invalid encrypted record length: " + encryptedLength);
        }
        byte[] encrypted = data.readNBytes(encryptedLength);
        if (encrypted.length != encryptedLength) throw new EOFException("Truncated record");

        long expectedSequence = replayGuard.expected();
        Cipher cipher = cipher(Cipher.DECRYPT_MODE, key, noncePrefix, expectedSequence);
        cipher.updateAAD(lengthBytes);
        byte[] plain = cipher.doFinal(encrypted);
        ByteBuffer buffer = ByteBuffer.wrap(plain).order(ByteOrder.BIG_ENDIAN);
        int version = Byte.toUnsignedInt(buffer.get());
        if (version != BtxConstants.PROTOCOL_MAJOR) throw new IOException("Unsupported BTX version: " + version);
        WireMessageType type = WireMessageType.fromCode(Byte.toUnsignedInt(buffer.get()));
        int flags = Short.toUnsignedInt(buffer.getShort());
        int streamId = buffer.getInt();
        long sequence = buffer.getLong();
        if (streamId < 0 || sequence < 0) throw new IOException("Invalid frame header");
        replayGuard.accept(sequence);
        byte[] payload = new byte[buffer.remaining()];
        buffer.get(payload);
        return new BtxFrame(type, flags, streamId, sequence, payload);
    }

    private static Cipher cipher(int mode, byte[] key, int noncePrefix, long sequence)
            throws GeneralSecurityException {
        byte[] nonce = ByteBuffer.allocate(12).order(ByteOrder.BIG_ENDIAN)
                .putInt(noncePrefix).putLong(sequence).array();
        Cipher cipher = CryptoProviders.chacha20Poly1305();
        cipher.init(mode, new SecretKeySpec(key, "ChaCha20"), new IvParameterSpec(nonce));
        return cipher;
    }

    private static void validateKey(byte[] key) {
        if (key == null || key.length != 32) throw new IllegalArgumentException("Record key must be 32 bytes");
    }
}
