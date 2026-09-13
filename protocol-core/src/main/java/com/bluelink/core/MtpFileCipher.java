package com.bluelink.core;

import javax.crypto.Cipher;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.io.*;
import java.nio.ByteBuffer;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import java.util.UUID;

/** BLM1 interoperable authenticated spool, never stores a key in the public directory. */
public final class MtpFileCipher {
    public static final int CHUNK_SIZE = 1024 * 1024;
    @FunctionalInterface public interface Checkpoint { void run() throws IOException; }
    @FunctionalInterface public interface Sink { void accept(byte[] bytes, int length) throws IOException; }
    public static long encodedSize(long size) {
        if (size < 0 || size > (long)CHUNK_SIZE * 0xffffffffL)
            throw new IllegalArgumentException("Invalid USB file size");
        return Math.addExact(size, 16 * Math.max(1, (size + CHUNK_SIZE - 1) / CHUNK_SIZE));
    }
    public static void encrypt(InputStream input, OutputStream output, UUID id, long size, byte[] key,
                               Checkpoint checkpoint) throws IOException, GeneralSecurityException {
        validate(size, key);
        long offset = 0;
        byte[] buffer = new byte[CHUNK_SIZE];
        try {
            for (long index = 0; index < Math.max(1, (size + CHUNK_SIZE - 1) / CHUNK_SIZE); index++) {
                checkpoint.run();
                int length = (int)Math.min(CHUNK_SIZE, size - offset);
                new DataInputStream(input).readFully(buffer, 0, length);
                Cipher cipher = cipher(Cipher.ENCRYPT_MODE, key, id, size, index, length);
                output.write(cipher.doFinal(buffer, 0, length));
                offset += length;
            }
            if (input.read() != -1) throw new IOException("USB source size changed");
        } finally { Arrays.fill(buffer, (byte)0); }
    }
    public static void decrypt(InputStream input, UUID id, long size, byte[] key, Sink sink,
                               Checkpoint checkpoint) throws IOException, GeneralSecurityException {
        validate(size, key);
        long offset = 0;
        DataInputStream data = new DataInputStream(input);
        for (long index = 0; index < Math.max(1, (size + CHUNK_SIZE - 1) / CHUNK_SIZE); index++) {
            checkpoint.run();
            int length = (int)Math.min(CHUNK_SIZE, size - offset);
            byte[] encrypted = new byte[length + 16];
            data.readFully(encrypted);
            byte[] plain = cipher(Cipher.DECRYPT_MODE, key, id, size, index, length).doFinal(encrypted);
            try { sink.accept(plain, length); } finally { Arrays.fill(plain, (byte)0); }
            offset += length;
        }
        if (data.read() != -1) throw new IOException("Unexpected USB file suffix");
    }
    private static Cipher cipher(int mode, byte[] key, UUID id, long size, long index, int length)
            throws GeneralSecurityException {
        byte[] nonce = ByteBuffer.allocate(12).putInt(0x424c4d31).putLong(index).array();
        byte[] aad = ByteBuffer.allocate(40).putInt(0x424c4d31)
                .putLong(id.getMostSignificantBits()).putLong(id.getLeastSignificantBits())
                .putLong(size).putLong(index).putInt(length).array();
        Cipher cipher = CryptoProviders.chacha20Poly1305();
        cipher.init(mode, new SecretKeySpec(key, "ChaCha20"), new IvParameterSpec(nonce));
        cipher.updateAAD(aad);
        return cipher;
    }
    private static void validate(long size, byte[] key) throws IOException {
        if (size < 0 || size > (long)CHUNK_SIZE * 0xffffffffL || key.length != 32)
            throw new IOException("Invalid USB file parameters");
    }
    private MtpFileCipher() {}
}
