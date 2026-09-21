using System.Buffers.Binary;
using System.Text;

namespace BlueLink.Protocol;

[Flags]
public enum BtxCapability : uint
{
    None = 0,
    StructuredMessages = 1 << 0,
    MessageReceipts = 1 << 1,
    AttachmentMetadata = 1 << 2,
    TransferControl = 1 << 3,
    ResumeState = 1 << 4,
    MtpFiles = 1 << 5,
    TransferAttemptStreams = 1 << 6,
}

public sealed record ProtocolGreeting(byte Major, byte Minor, BtxCapability Capabilities)
{
    public const byte CurrentMajor = 1;
    public const byte CurrentMinor = 1;
    public const BtxCapability CurrentCapabilities = BtxCapability.StructuredMessages |
        BtxCapability.MessageReceipts | BtxCapability.AttachmentMetadata |
        BtxCapability.TransferControl | BtxCapability.ResumeState | BtxCapability.MtpFiles | BtxCapability.TransferAttemptStreams;

    public static ProtocolGreeting Current { get; } = new(CurrentMajor, CurrentMinor, CurrentCapabilities);
    public static ProtocolGreeting Legacy { get; } = new(1, 0, BtxCapability.None);

    public byte[] Encode()
    {
        var value = new byte[8];
        value[0] = Major;
        value[1] = Minor;
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4), (uint)Capabilities);
        return value;
    }

    public static ProtocolGreeting Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length is not (4 or >= 8)) throw new InvalidDataException("Invalid PROTOCOL_HELLO payload");
        if (value[2] != 0 || value[3] != 0) throw new InvalidDataException("Unsupported PROTOCOL_HELLO flags");
        return new(value[0], value[1], value.Length >= 8
            ? (BtxCapability)BinaryPrimitives.ReadUInt32BigEndian(value[4..8])
            : BtxCapability.None);
    }

    public BtxNegotiation Negotiate(ProtocolGreeting remote)
    {
        if (Major != remote.Major) throw new InvalidDataException($"Unsupported BTX major version {remote.Major}");
        return new(Major, Math.Min(Minor, remote.Minor), Capabilities & remote.Capabilities);
    }
}

public sealed record BtxNegotiation(byte Major, byte Minor, BtxCapability Capabilities)
{
    public bool Supports(BtxCapability value) => (Capabilities & value) == value;
}

public enum ChatPayloadKind : byte { Text = 1, Image = 2, File = 3, System = 4 }
public enum AttachmentRole : byte { File = 0, ImagePreview = 1, ImageOriginal = 2 }
public enum ReceiptState : byte { Delivered = 1, Read = 2, Failed = 3 }

public sealed record AttachmentDescriptor(Guid AttachmentId, Guid TransferId, AttachmentRole Role,
    string FileName, string MimeType, long Size, byte[]? Sha256 = null);
public sealed record ChatEnvelope(Guid MessageId, ChatPayloadKind Kind, long CreatedAt,
    string Body, IReadOnlyList<AttachmentDescriptor> Attachments);
public sealed record ChatReceipt(Guid MessageId, ReceiptState State, long Timestamp);

public static class MessageWire
{
    private static readonly byte[] MessageMagic = [(byte)'B', (byte)'M'];
    private static readonly byte[] ReceiptMagic = [(byte)'B', (byte)'R'];
    private const byte Schema = 1;
    private const int MaxBody = 64 * 1024;
    private const int MaxAttachments = 2;

    public static byte[] Encode(ChatEnvelope value)
    {
        if (value.Attachments.Count > MaxAttachments) throw new InvalidDataException("Too many attachments");
        var body = Utf8(value.Body, MaxBody, "message body");
        using var output = new MemoryStream();
        output.Write(MessageMagic); output.WriteByte(Schema); output.WriteByte((byte)value.Kind);
        WriteGuid(output, value.MessageId); WriteInt64(output, value.CreatedAt); WriteInt32(output, body.Length);
        output.Write(body); output.WriteByte((byte)value.Attachments.Count);
        foreach (var item in value.Attachments) WriteAttachment(output, item);
        return output.ToArray();
    }

    public static ChatEnvelope Decode(ReadOnlySpan<byte> value)
    {
        using var input = new MemoryStream(value.ToArray(), false);
        Expect(input, MessageMagic, "message");
        if (ReadByte(input) != Schema) throw new InvalidDataException("Unsupported message schema");
        var kind = (ChatPayloadKind)ReadByte(input);
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown message kind");
        var id = ReadGuid(input); var createdAt = ReadInt64(input);
        var body = ReadUtf8(input, ReadInt32(input), MaxBody, "message body");
        var count = ReadByte(input);
        if (count > MaxAttachments) throw new InvalidDataException("Too many attachments");
        var attachments = new List<AttachmentDescriptor>(count);
        for (var index = 0; index < count; index++) attachments.Add(ReadAttachment(input));
        EnsureEnd(input);
        return new(id, kind, createdAt, body, attachments);
    }

    public static byte[] EncodeReceipt(ChatReceipt value)
    {
        using var output = new MemoryStream();
        output.Write(ReceiptMagic); output.WriteByte(Schema); WriteGuid(output, value.MessageId);
        output.WriteByte((byte)value.State); WriteInt64(output, value.Timestamp);
        return output.ToArray();
    }

    public static ChatReceipt DecodeReceipt(ReadOnlySpan<byte> value)
    {
        using var input = new MemoryStream(value.ToArray(), false);
        Expect(input, ReceiptMagic, "receipt");
        if (ReadByte(input) != Schema) throw new InvalidDataException("Unsupported receipt schema");
        var id = ReadGuid(input); var state = (ReceiptState)ReadByte(input);
        if (!Enum.IsDefined(state)) throw new InvalidDataException("Unknown receipt state");
        var timestamp = ReadInt64(input); EnsureEnd(input);
        return new(id, state, timestamp);
    }

    public static bool IsStructured(ReadOnlySpan<byte> value) => value.Length >= 3 && value[0] == 'B' && value[1] == 'M';

    internal static void WriteGuid(Stream output, Guid value) => output.Write(Convert.FromHexString(value.ToString("N")));
    internal static Guid ReadGuid(Stream input) => Guid.ParseExact(Convert.ToHexString(ReadExact(input, 16)), "N");
    internal static void WriteInt64(Stream output, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); output.Write(bytes); }
    internal static long ReadInt64(Stream input) => BinaryPrimitives.ReadInt64BigEndian(ReadExact(input, 8));
    internal static void WriteInt32(Stream output, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); output.Write(bytes); }
    internal static int ReadInt32(Stream input) => BinaryPrimitives.ReadInt32BigEndian(ReadExact(input, 4));
    internal static void WriteUInt16(Stream output, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); output.Write(bytes); }
    internal static int ReadUInt16(Stream input) => BinaryPrimitives.ReadUInt16BigEndian(ReadExact(input, 2));
    internal static byte ReadByte(Stream input) { var value = input.ReadByte(); if (value < 0) throw new EndOfStreamException(); return (byte)value; }
    internal static byte[] ReadExact(Stream input, int length) { if (length < 0) throw new InvalidDataException("Negative field length"); var value = new byte[length]; input.ReadExactly(value); return value; }
    internal static string ReadUtf8(Stream input, int length, int max, string field) => Encoding.UTF8.GetString(ReadBounded(input, length, max, field));
    internal static byte[] Utf8(string value, int max, string field) { var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length > max) throw new InvalidDataException($"{field} is too long"); return bytes; }
    internal static void EnsureEnd(Stream input) { if (input.Position != input.Length) throw new InvalidDataException("Unexpected trailing payload"); }

    private static void WriteAttachment(Stream output, AttachmentDescriptor value)
    {
        if (value.Size < 0 || value.Sha256 is { Length: not 32 }) throw new InvalidDataException("Invalid attachment metadata");
        var name = Utf8(value.FileName, 512, "file name"); var mime = Utf8(value.MimeType, 255, "MIME type");
        WriteGuid(output, value.AttachmentId); WriteGuid(output, value.TransferId); output.WriteByte((byte)value.Role);
        WriteInt64(output, value.Size); WriteUInt16(output, name.Length); output.Write(name);
        WriteUInt16(output, mime.Length); output.Write(mime); output.WriteByte((byte)(value.Sha256?.Length ?? 0));
        if (value.Sha256 is not null) output.Write(value.Sha256);
    }

    private static AttachmentDescriptor ReadAttachment(Stream input)
    {
        var attachmentId = ReadGuid(input); var transferId = ReadGuid(input); var role = (AttachmentRole)ReadByte(input);
        if (!Enum.IsDefined(role)) throw new InvalidDataException("Unknown attachment role");
        var size = ReadInt64(input); if (size < 0) throw new InvalidDataException("Invalid attachment size");
        var name = ReadUtf8(input, ReadUInt16(input), 512, "file name");
        var mime = ReadUtf8(input, ReadUInt16(input), 255, "MIME type");
        var hashLength = ReadByte(input); if (hashLength is not (0 or 32)) throw new InvalidDataException("Invalid attachment hash");
        return new(attachmentId, transferId, role, name, mime, size, hashLength == 0 ? null : ReadExact(input, hashLength));
    }

    private static byte[] ReadBounded(Stream input, int length, int max, string field)
    {
        if (length < 0 || length > max) throw new InvalidDataException($"Invalid {field} length");
        return ReadExact(input, length);
    }

    private static void Expect(Stream input, byte[] magic, string field)
    {
        if (!ReadExact(input, magic.Length).SequenceEqual(magic)) throw new InvalidDataException($"Invalid {field} payload");
    }
}

public enum TransferControlAction : byte { Pause = 1, Resume = 2, Cancel = 3, Retry = 4 }
public sealed record TransferControl(Guid TransferId, TransferControlAction Action, long Timestamp, string Reason = "");

public static class TransferControlWire
{
    private static readonly byte[] Magic = [(byte)'B', (byte)'T'];
    public static byte[] Encode(TransferControl value)
    {
        var reason = MessageWire.Utf8(value.Reason, 512, "transfer reason");
        using var output = new MemoryStream();
        output.Write(Magic); output.WriteByte(1); MessageWire.WriteGuid(output, value.TransferId);
        output.WriteByte((byte)value.Action); MessageWire.WriteInt64(output, value.Timestamp);
        MessageWire.WriteUInt16(output, reason.Length); output.Write(reason);
        return output.ToArray();
    }

    public static TransferControl Decode(ReadOnlySpan<byte> value)
    {
        using var input = new MemoryStream(value.ToArray(), false);
        if (!MessageWire.ReadExact(input, 2).SequenceEqual(Magic) || MessageWire.ReadByte(input) != 1)
            throw new InvalidDataException("Invalid transfer control payload");
        var id = MessageWire.ReadGuid(input); var action = (TransferControlAction)MessageWire.ReadByte(input);
        if (!Enum.IsDefined(action)) throw new InvalidDataException("Unknown transfer control action");
        var timestamp = MessageWire.ReadInt64(input);
        var reason = MessageWire.ReadUtf8(input, MessageWire.ReadUInt16(input), 512, "transfer reason");
        MessageWire.EnsureEnd(input);
        return new(id, action, timestamp, reason);
    }
}
