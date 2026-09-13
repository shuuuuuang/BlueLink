using System.Buffers.Binary;
using System.Text;

namespace BlueLink.Protocol;

/// <summary>Optional display metadata after the unchanged eight-byte authenticated greeting.</summary>
public static class DeviceNameGreeting
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(string? name)
    {
        var clean = Normalize(name);
        var greeting = ProtocolGreeting.Current.Encode();
        if (clean.Length == 0) return greeting;
        var text = Utf8.GetBytes(clean);
        var value = new byte[14 + text.Length];
        greeting.CopyTo(value, 0);
        "BLDN"u8.CopyTo(value.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(12), (ushort)text.Length);
        text.CopyTo(value, 14);
        return value;
    }

    public static string Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length < 14 || !value.Slice(8, 4).SequenceEqual("BLDN"u8)) return "";
        var length = BinaryPrimitives.ReadUInt16BigEndian(value[12..14]);
        if (length > 384 || value.Length != 14 + length) return "";
        try { return Normalize(Utf8.GetString(value[14..])); }
        catch (DecoderFallbackException) { return ""; }
    }

    private static string Normalize(string? name)
    {
        var value = name?.Trim() ?? "";
        return value.Length <= 128 && !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= 384 ? value : "";
    }
}
