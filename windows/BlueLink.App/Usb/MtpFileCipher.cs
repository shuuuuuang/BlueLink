using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlueLink.Usb;

/// <summary>BLM1: bounded, independently authenticated records. Keys are fresh per attempt and sent only over BTX.</summary>
public static class MtpFileCipher
{
    public const int ChunkSize = 1024 * 1024;
    public static long EncodedSize(long size)
    {
        ValidateSize(size);
        return checked(size + 16 * Math.Max(1, (size + ChunkSize - 1) / ChunkSize));
    }

    public static async Task EncryptAsync(Stream input, Stream output, Guid id, long size, byte[] key,
        Func<Task>? checkpoint = null, CancellationToken token = default)
    {
        Validate(size, key);
        using var cipher = new ChaCha20Poly1305(key);
        var plain = new byte[ChunkSize];
        var encrypted = new byte[ChunkSize];
        var tag = new byte[16];
        long offset = 0;
        try
        {
        for (long index = 0; index < Math.Max(1, (size + ChunkSize - 1) / ChunkSize); index++)
        {
            token.ThrowIfCancellationRequested();
            if (checkpoint is not null) await checkpoint();
            var length = (int)Math.Min(ChunkSize, size - offset);
            await input.ReadExactlyAsync(plain.AsMemory(0, length), token);
            cipher.Encrypt(Nonce(index), plain.AsSpan(0, length), encrypted.AsSpan(0, length), tag,
                Aad(id, size, index, length));
            await output.WriteAsync(encrypted.AsMemory(0, length), token);
            await output.WriteAsync(tag, token);
            offset += length;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("USB source size changed");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static async Task DecryptAsync(Stream input, Guid id, long size, byte[] key,
        Func<ReadOnlyMemory<byte>, Task> accept, Func<Task>? checkpoint = null, CancellationToken token = default)
    {
        Validate(size, key);
        using var cipher = new ChaCha20Poly1305(key);
        var plain = new byte[ChunkSize];
        var encrypted = new byte[ChunkSize];
        var tag = new byte[16];
        long offset = 0;
        try
        {
            for (long index = 0; index < Math.Max(1, (size + ChunkSize - 1) / ChunkSize); index++)
            {
                token.ThrowIfCancellationRequested();
                if (checkpoint is not null) await checkpoint();
                var length = (int)Math.Min(ChunkSize, size - offset);
                await input.ReadExactlyAsync(encrypted.AsMemory(0, length), token);
                await input.ReadExactlyAsync(tag, token);
                cipher.Decrypt(Nonce(index), encrypted.AsSpan(0, length), tag, plain.AsSpan(0, length),
                    Aad(id, size, index, length));
                await accept(plain.AsMemory(0, length));
                offset += length;
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("Unexpected USB file suffix");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void Validate(long size, byte[] key)
    {
        ValidateSize(size);
        if (key.Length != 32)
            throw new InvalidDataException("Invalid USB file parameters");
    }
    private static void ValidateSize(long size)
    {
        if (size < 0 || size > (long)ChunkSize * uint.MaxValue)
            throw new InvalidDataException("Invalid USB file size");
    }
    private static byte[] Nonce(long index)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 0x424C4D31);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), index);
        return bytes;
    }
    private static byte[] Aad(Guid id, long size, long index, int length)
    {
        var bytes = new byte[40];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 0x424C4D31);
        Convert.FromHexString(id.ToString("N")).CopyTo(bytes, 4);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(20), size);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(28), index);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(36), length);
        return bytes;
    }
}
