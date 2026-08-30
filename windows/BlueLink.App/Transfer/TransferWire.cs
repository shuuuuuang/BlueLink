using System.Buffers.Binary;
using System.Text;
using BlueLink.Protocol;

namespace BlueLink.Transfer;

public sealed record FileOffer(Guid Id, string Name, long Size, int ExtentSize, byte[] Hash,
    Guid? MessageId = null, Guid? AttachmentId = null,
    string MimeType = "application/octet-stream", AttachmentRole Role = AttachmentRole.File)
{
    public bool HasAttachmentMetadata => MessageId is not null && AttachmentId is not null;
}
public sealed record FileExtent(Guid Id, int Index, byte[] Hash, byte[] Data);
public sealed record FileExtentAck(Guid Id, int Index);
public sealed record FileTransferAccept(Guid Id, int NextExtent);
public sealed record FileTransferFailure(Guid Id, string Reason);

public static class TransferWire
{
    public static byte[] EncodeOffer(FileOffer offer)
    {
        return offer.HasAttachmentMetadata ? EncodeEnhancedOffer(offer) : EncodeLegacyOffer(offer);
    }

    public static byte[] EncodeLegacyOffer(FileOffer offer)
    {
        var name = Encoding.UTF8.GetBytes(offer.Name);
        if (name.Length > 512 || offer.Hash.Length != 32) throw new InvalidDataException("Invalid file offer");
        var result = new byte[16 + 2 + name.Length + 8 + 4 + 32];
        WriteGuid(result, offer.Id);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(16, 2), (ushort)name.Length);
        name.CopyTo(result, 18);
        var offset = 18 + name.Length;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(offset, 8), offer.Size);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset + 8, 4), offer.ExtentSize);
        offer.Hash.CopyTo(result, offset + 12);
        return result;
    }

    public static FileOffer DecodeOffer(byte[] value)
    {
        if (value.Length >= 3 && value[0] == 'B' && value[1] == 'O') return DecodeEnhancedOffer(value);
        if (value.Length < 62) throw new InvalidDataException("Truncated file offer");
        var id = ReadGuid(value);
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(16, 2));
        if (nameLength > 512 || value.Length != 16 + 2 + nameLength + 8 + 4 + 32) throw new InvalidDataException("Invalid file offer");
        var name = Encoding.UTF8.GetString(value, 18, nameLength);
        var offset = 18 + nameLength;
        var size = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(offset, 8));
        var extent = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(offset + 8, 4));
        if (size < 0 || extent is < 1 or > 786432) throw new InvalidDataException("Invalid file sizes");
        return new(id, name, size, extent, value[(offset + 12)..]);
    }

    private static byte[] EncodeEnhancedOffer(FileOffer offer)
    {
        if (offer.MessageId is null || offer.AttachmentId is null || offer.Size < 0 ||
            offer.ExtentSize is < 1 or > 786432 || offer.Hash.Length != 32)
            throw new InvalidDataException("Invalid enhanced file offer");
        var name = MessageWire.Utf8(offer.Name, 512, "file name");
        var mime = MessageWire.Utf8(offer.MimeType, 255, "MIME type");
        using var output = new MemoryStream();
        output.Write([(byte)'B', (byte)'O', 1]);
        MessageWire.WriteGuid(output, offer.Id); MessageWire.WriteGuid(output, offer.MessageId.Value);
        MessageWire.WriteGuid(output, offer.AttachmentId.Value); output.WriteByte((byte)offer.Role);
        MessageWire.WriteInt64(output, offer.Size); MessageWire.WriteInt32(output, offer.ExtentSize);
        output.Write(offer.Hash); MessageWire.WriteUInt16(output, name.Length); output.Write(name);
        MessageWire.WriteUInt16(output, mime.Length); output.Write(mime);
        return output.ToArray();
    }

    private static FileOffer DecodeEnhancedOffer(byte[] value)
    {
        using var input = new MemoryStream(value, false);
        if (!MessageWire.ReadExact(input, 2).SequenceEqual([(byte)'B', (byte)'O']) || MessageWire.ReadByte(input) != 1)
            throw new InvalidDataException("Invalid enhanced file offer");
        var id = MessageWire.ReadGuid(input); var messageId = MessageWire.ReadGuid(input);
        var attachmentId = MessageWire.ReadGuid(input); var role = (AttachmentRole)MessageWire.ReadByte(input);
        if (!Enum.IsDefined(role)) throw new InvalidDataException("Unknown attachment role");
        var size = MessageWire.ReadInt64(input); var extent = MessageWire.ReadInt32(input);
        var hash = MessageWire.ReadExact(input, 32);
        var name = MessageWire.ReadUtf8(input, MessageWire.ReadUInt16(input), 512, "file name");
        var mime = MessageWire.ReadUtf8(input, MessageWire.ReadUInt16(input), 255, "MIME type");
        MessageWire.EnsureEnd(input);
        if (size < 0 || extent is < 1 or > 786432) throw new InvalidDataException("Invalid file sizes");
        return new(id, name, size, extent, hash, messageId, attachmentId, mime, role);
    }

    public static byte[] EncodeId(Guid id) { var value = new byte[16]; WriteGuid(value, id); return value; }
    public static Guid DecodeId(byte[] value) => value.Length >= 16 ? ReadGuid(value) : throw new InvalidDataException("Missing transfer id");

    public static byte[] EncodeAccept(FileTransferAccept accept)
    {
        if (accept.NextExtent < 0) throw new InvalidDataException("Invalid resume extent");
        var value = new byte[20];
        WriteGuid(value, accept.Id);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(16), accept.NextExtent);
        return value;
    }

    public static FileTransferAccept DecodeAccept(byte[] value)
    {
        if (value.Length is not (16 or 20)) throw new InvalidDataException("Invalid transfer accept");
        var next = value.Length == 20 ? BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(16)) : 0;
        if (next < 0) throw new InvalidDataException("Invalid resume extent");
        return new(ReadGuid(value), next);
    }

    public static byte[] EncodeExtentAck(FileExtentAck ack)
    {
        var value = new byte[20];
        WriteGuid(value, ack.Id);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(16), ack.Index);
        return value;
    }

    public static FileExtentAck DecodeExtentAck(byte[] value)
    {
        if (value.Length != 20) throw new InvalidDataException("Invalid extent acknowledgement");
        var index = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(16));
        if (index < 0) throw new InvalidDataException("Invalid acknowledged extent index");
        return new(ReadGuid(value), index);
    }

    public static byte[] EncodeFailure(FileTransferFailure failure)
    {
        var reason = Encoding.UTF8.GetBytes(failure.Reason);
        if (reason.Length > 512) reason = reason[..512];
        var value = new byte[18 + reason.Length];
        WriteGuid(value, failure.Id);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(16), (ushort)reason.Length);
        reason.CopyTo(value, 18);
        return value;
    }

    public static FileTransferFailure DecodeFailure(byte[] value)
    {
        if (value.Length < 18) throw new InvalidDataException("Truncated transfer failure");
        var length = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(16));
        if (length > 512 || value.Length != 18 + length) throw new InvalidDataException("Invalid transfer failure");
        return new(ReadGuid(value), Encoding.UTF8.GetString(value, 18, length));
    }

    public static byte[] EncodeExtent(FileExtent extent)
    {
        if (extent.Hash.Length != 32) throw new InvalidDataException("Invalid extent hash");
        var value = new byte[16 + 4 + 32 + extent.Data.Length];
        WriteGuid(value, extent.Id);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(16, 4), extent.Index);
        extent.Hash.CopyTo(value, 20);
        extent.Data.CopyTo(value, 52);
        return value;
    }

    public static FileExtent DecodeExtent(byte[] value)
    {
        if (value.Length < 52) throw new InvalidDataException("Truncated extent");
        var index = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(16, 4));
        if (index < 0) throw new InvalidDataException("Invalid extent index");
        return new(ReadGuid(value), index, value[20..52], value[52..]);
    }

    // Java/Kotlin UUID serialization writes the two RFC 4122 64-bit halves in network order.
    private static void WriteGuid(Span<byte> target, Guid id)
    {
        var text = id.ToString("N");
        Convert.FromHexString(text).CopyTo(target);
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source)
    {
        var hex = Convert.ToHexString(source[..16]);
        return Guid.ParseExact(hex, "N");
    }
}
