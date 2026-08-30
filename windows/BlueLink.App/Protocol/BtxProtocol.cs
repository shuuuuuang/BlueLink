using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlueLink.Protocol;

public enum WireMessageType : byte
{
    ProtocolHello = 1, SessionReady = 2, Ping = 3, Pong = 4, GoAway = 5,
    Chat = 10, ChatReceipt = 11, TransferOffer = 20, TransferAccept = 21,
    TransferReject = 22, TransferExtent = 23, TransferFinish = 24,
    ResumeQuery = 25, ResumeState = 26, TransferExtentAck = 27,
    TransferComplete = 28, TransferFailed = 29, WindowUpdate = 30, StreamCancel = 31,
    TransferControl = 32
}

public static class WireMessagePriority
{
    public static int Of(WireMessageType type) => type switch
    {
        WireMessageType.ProtocolHello or WireMessageType.SessionReady or WireMessageType.GoAway
            or WireMessageType.WindowUpdate or WireMessageType.StreamCancel or WireMessageType.TransferControl => 0,
        WireMessageType.Ping or WireMessageType.Pong or WireMessageType.Chat or WireMessageType.ChatReceipt => 1,
        WireMessageType.TransferOffer or WireMessageType.TransferAccept or WireMessageType.TransferReject
            or WireMessageType.ResumeQuery or WireMessageType.ResumeState or WireMessageType.TransferExtentAck
            or WireMessageType.TransferComplete or WireMessageType.TransferFailed => 2,
        WireMessageType.TransferExtent or WireMessageType.TransferFinish => 5,
        _ => 6
    };
}

public sealed record BtxFrame(WireMessageType Type, ushort Flags, int StreamId, long Sequence, byte[] Payload);

public sealed class ReplayGuard(long initialSequence = 0)
{
    public long Expected { get; private set; } = initialSequence;
    public void Accept(long sequence)
    {
        if (sequence != Expected) throw new CryptographicException($"Unexpected sequence {sequence}; expected {Expected}");
        Expected++;
    }
}

public static class BtxRecordCodec
{
    public const int MaxRecordSize = 1024 * 1024;
    private const int HeaderSize = 16;

    public static async Task WriteAsync(Stream output, BtxFrame frame, byte[] key, int noncePrefix, CancellationToken token)
    {
        if (key.Length != 32) throw new ArgumentException("Record key must be 32 bytes", nameof(key));
        var plain = new byte[HeaderSize + frame.Payload.Length];
        plain[0] = 1;
        plain[1] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2, 2), frame.Flags);
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(4, 4), frame.StreamId);
        BinaryPrimitives.WriteInt64BigEndian(plain.AsSpan(8, 8), frame.Sequence);
        frame.Payload.CopyTo(plain, HeaderSize);
        var encryptedLength = plain.Length + 16;
        if (encryptedLength > MaxRecordSize) throw new InvalidDataException("BTX record exceeds maximum");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, encryptedLength);
        var nonce = Nonce(noncePrefix, frame.Sequence);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using (var cipher = new ChaCha20Poly1305(key)) cipher.Encrypt(nonce, plain, ciphertext, tag, length);
        await output.WriteAsync(length, token);
        await output.WriteAsync(ciphertext, token);
        await output.WriteAsync(tag, token);
        await output.FlushAsync(token);
    }

    public static async Task<BtxFrame> ReadAsync(Stream input, byte[] key, int noncePrefix, ReplayGuard replay, CancellationToken token)
    {
        var length = new byte[4];
        await input.ReadExactlyAsync(length, token);
        var encryptedLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (encryptedLength < HeaderSize + 16 || encryptedLength > MaxRecordSize)
            throw new InvalidDataException("Invalid BTX record length");
        var encrypted = new byte[encryptedLength];
        await input.ReadExactlyAsync(encrypted, token);
        var ciphertext = encrypted[..(encryptedLength - 16)];
        var tag = encrypted[(encryptedLength - 16)..];
        var plain = new byte[ciphertext.Length];
        using (var cipher = new ChaCha20Poly1305(key))
            cipher.Decrypt(Nonce(noncePrefix, replay.Expected), ciphertext, tag, plain, length);
        if (plain[0] != 1) throw new InvalidDataException("Unsupported BTX protocol version");
        var type = (WireMessageType)plain[1];
        if (!Enum.IsDefined(type)) throw new InvalidDataException("Unknown BTX message type");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2, 2));
        var stream = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(4, 4));
        var sequence = BinaryPrimitives.ReadInt64BigEndian(plain.AsSpan(8, 8));
        if (stream < 0 || sequence < 0) throw new InvalidDataException("Invalid BTX frame header");
        replay.Accept(sequence);
        return new(type, flags, stream, sequence, plain[HeaderSize..]);
    }

    private static byte[] Nonce(int prefix, long sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(nonce, prefix);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }
}
