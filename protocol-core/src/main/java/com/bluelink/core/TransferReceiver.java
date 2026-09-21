package com.bluelink.core;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;

public final class TransferReceiver implements AutoCloseable {
    private static final java.util.Set<Path> PARTIAL_OWNERS = java.util.concurrent.ConcurrentHashMap.newKeySet();
    private boolean ownsPartial;
    private final Path target;
    private final Path partial;
    private final Path metadata;
    private final long fileSize;
    private final int extentSize;
    private final byte[] wholeFileHash;
    private final ExtentMap extents;
    private final FileChannel channel;

    public TransferReceiver(Path managedRoot, String relativePath, long fileSize, int extentSize,
                            byte[] wholeFileHash, byte[] resumeBitmap) throws IOException {
        this(managedRoot, relativePath, fileSize, extentSize, wholeFileHash, resumeBitmap, null);
    }

    public TransferReceiver(Path managedRoot, String relativePath, long fileSize, int extentSize,
                            byte[] wholeFileHash, byte[] resumeBitmap, String resumeKey) throws IOException {
        if (fileSize < 0 || extentSize <= 0) throw new IllegalArgumentException("Invalid transfer size");
        this.target = SafePaths.resolve(managedRoot, relativePath);
        String normalizedKey = resumeKey == null ? null : resumeKey.replaceAll("[^A-Za-z0-9_-]", "");
        if (resumeKey != null && normalizedKey.isBlank()) throw new IllegalArgumentException("Invalid resume key");
        String suffix = normalizedKey == null ? ".part" : "." + normalizedKey + ".part";
        this.partial = target.resolveSibling(target.getFileName() + suffix);
        this.metadata = normalizedKey == null ? null : partial.resolveSibling(partial.getFileName() + ".resume");
        this.fileSize = fileSize;
        this.extentSize = extentSize;
        this.wholeFileHash = wholeFileHash.clone();
        int count = Math.toIntExact((fileSize + extentSize - 1) / extentSize);
        Files.createDirectories(target.getParent());
        if (!PARTIAL_OWNERS.add(partial)) throw new IOException("Previous receiver is still closing");
        ownsPartial = true;
        FileChannel opened = null;
        try {
            byte[] restored = resumeBitmap != null ? resumeBitmap : loadResume(count);
            this.extents = restored == null ? new ExtentMap(count) : ExtentMap.decode(count, restored);
            opened = restored == null
                    ? FileChannel.open(partial, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING,
                        StandardOpenOption.READ, StandardOpenOption.WRITE)
                    : FileChannel.open(partial, StandardOpenOption.CREATE, StandardOpenOption.READ, StandardOpenOption.WRITE);
            if (opened.size() > fileSize) opened.truncate(fileSize);
            this.channel = opened;
        } catch (IOException | RuntimeException error) {
            if (opened != null) try { opened.close(); } catch (IOException closeError) { error.addSuppressed(closeError); }
            PARTIAL_OWNERS.remove(partial); ownsPartial = false;
            throw error;
        }
    }

    public synchronized void accept(int index, byte[] data, byte[] expectedHash) throws IOException {
        if (extents.isComplete(index)) return;
        long offset = Math.multiplyExact((long) index, extentSize);
        int expectedLength = (int) Math.min(extentSize, fileSize - offset);
        if (data.length != expectedLength)
            throw new IOException("Extent length mismatch: index=" + index + ", expected=" + expectedLength
                    + ", actual=" + data.length);
        byte[] actualHash = sha256(data);
        if (!MessageDigest.isEqual(actualHash, expectedHash))
            throw new IOException("Extent hash mismatch: index=" + index + ", expected="
                    + shortHex(expectedHash) + ", actual=" + shortHex(actualHash));
        ByteBuffer buffer = ByteBuffer.wrap(data);
        while (buffer.hasRemaining()) channel.write(buffer, offset + buffer.position());
        channel.force(false);
        extents.complete(index);
        persistResume();
    }

    public synchronized Path finish() throws IOException {
        if (!extents.allComplete()) throw new IOException("Transfer still has missing extents");
        channel.force(true);
        if (channel.size() != fileSize)
            throw new IOException("Whole-file length mismatch: expected=" + fileSize + ", actual=" + channel.size());
        channel.close();
        byte[] actualHash = hashFile(partial);
        if (!MessageDigest.isEqual(actualHash, wholeFileHash)) {
            deleteResumeFiles();
            throw new IOException("Whole-file hash mismatch: expected=" + shortHex(wholeFileHash)
                    + ", actual=" + shortHex(actualHash));
        }
        Path result;
        try {
            result = Files.move(partial, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (java.nio.file.AtomicMoveNotSupportedException ex) {
            result = Files.move(partial, target, StandardCopyOption.REPLACE_EXISTING);
        }
        if (metadata != null) Files.deleteIfExists(metadata);
        return result;
    }

    /** One durable checkpoint per authenticated USB record; final whole-file hash is still mandatory. */
    public synchronized void importChunk(long offset, byte[] data, int length) throws IOException {
        if (offset < 0 || offset % extentSize != 0 || length < 0 || length > data.length || offset + length > fileSize ||
                (length % extentSize != 0 && offset + length != fileSize)) throw new IOException("Invalid USB import range");
        ByteBuffer buffer = ByteBuffer.wrap(data, 0, length);
        while (buffer.hasRemaining()) channel.write(buffer, offset + buffer.position());
        channel.force(false);
        for (long i = offset / extentSize; i < (offset + length + extentSize - 1) / extentSize; i++) extents.complete(Math.toIntExact(i));
        persistResume();
    }

    public ExtentMap extentMap() { return extents; }
    public long contiguousCommittedOffset() { return Math.min(fileSize, (long) extents.contiguousCount() * extentSize); }
    @Override public synchronized void close() throws IOException {
        if (channel.isOpen()) channel.close();
        if (ownsPartial) { PARTIAL_OWNERS.remove(partial); ownsPartial = false; }
    }

    public static byte[] sha256(byte[] data) {
        try { return MessageDigest.getInstance("SHA-256").digest(data); }
        catch (NoSuchAlgorithmException impossible) { throw new IllegalStateException(impossible); }
    }

    private static byte[] hashFile(Path path) throws IOException {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            try (InputStream input = Files.newInputStream(path)) {
                byte[] buffer = new byte[128 * 1024];
                for (int read; (read = input.read(buffer)) != -1;) digest.update(buffer, 0, read);
            }
            return digest.digest();
        } catch (NoSuchAlgorithmException impossible) {
            throw new IllegalStateException(impossible);
        }
    }

    private byte[] loadResume(int extentCount) throws IOException {
        if (metadata == null) return null;
        if (!Files.exists(partial) || !Files.exists(metadata)) {
            deleteResumeFiles();
            return null;
        }
        try (DataInputStream input = new DataInputStream(Files.newInputStream(metadata))) {
            if (input.readInt() != 0x42545231 || input.readLong() != fileSize || input.readInt() != extentSize)
                throw new IOException("Resume metadata does not match the offer");
            int hashLength = input.readInt();
            byte[] hash = input.readNBytes(hashLength);
            if (hashLength != wholeFileHash.length || !MessageDigest.isEqual(hash, wholeFileHash))
                throw new IOException("Resume hash does not match the offer");
            int bitmapLength = input.readInt();
            byte[] bitmap = input.readNBytes(bitmapLength);
            if (bitmap.length != bitmapLength || bitmapLength > (extentCount + 7) / 8 || input.read() != -1)
                throw new IOException("Resume metadata is invalid");
            ExtentMap restored = ExtentMap.decode(extentCount, bitmap);
            int last = -1;
            for (int index = 0; index < extentCount; index++) if (restored.isComplete(index)) last = index;
            long requiredLength = last < 0 ? 0 : Math.min(fileSize, (long) (last + 1) * extentSize);
            if (Files.size(partial) < requiredLength) throw new IOException("Partial file is shorter than its bitmap");
            return bitmap;
        } catch (IOException | IllegalArgumentException failure) {
            deleteResumeFiles();
            return null;
        }
    }

    private void persistResume() throws IOException {
        if (metadata == null) return;
        Path temporary = metadata.resolveSibling(metadata.getFileName() + ".tmp");
        try (DataOutputStream output = new DataOutputStream(Files.newOutputStream(temporary,
                StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING, StandardOpenOption.WRITE))) {
            output.writeInt(0x42545231); // BTR1
            output.writeLong(fileSize);
            output.writeInt(extentSize);
            output.writeInt(wholeFileHash.length);
            output.write(wholeFileHash);
            byte[] bitmap = extents.encode();
            output.writeInt(bitmap.length);
            output.write(bitmap);
            output.flush();
        }
        try {
            Files.move(temporary, metadata, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (java.nio.file.AtomicMoveNotSupportedException ex) {
            Files.move(temporary, metadata, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private void deleteResumeFiles() throws IOException {
        if (metadata != null) {
            Files.deleteIfExists(metadata);
            Files.deleteIfExists(metadata.resolveSibling(metadata.getFileName() + ".tmp"));
        }
        Files.deleteIfExists(partial);
    }

    private static String shortHex(byte[] value) {
        return java.util.HexFormat.of().formatHex(value, 0, Math.min(6, value.length));
    }
}
