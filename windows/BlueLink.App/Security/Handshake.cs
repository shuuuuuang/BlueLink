using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace BlueLink.Security;

public sealed record SessionKeys(byte[] SendKey, byte[] ReceiveKey, int SendNoncePrefix,
    int ReceiveNoncePrefix, int SafetyCode, byte[] RemotePeerId, byte[] RemoteIdentityPublicKey)
{
    public string FormattedSafetyCode => $"{SafetyCode / 1000:000} {SafetyCode % 1000:000}";
}

public sealed class HandshakeHello
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("BLUELINK-HS1");
    private static readonly byte[] X25519X509Prefix = Convert.FromHexString("302A300506032B656E032100");
    private readonly X25519PrivateKeyParameters? _ephemeralPrivate;
    public byte[] PeerId { get; }
    public byte[] IdentityPublicKey { get; }
    public byte[] EphemeralPublicKey { get; }
    public byte[] Nonce { get; }
    public byte[] Signature { get; }

    private HandshakeHello(byte[] peerId, byte[] identityPublicKey, byte[] ephemeralPublicKey,
        byte[] nonce, byte[] signature, X25519PrivateKeyParameters? ephemeralPrivate)
    {
        PeerId = peerId;
        IdentityPublicKey = identityPublicKey;
        EphemeralPublicKey = ephemeralPublicKey;
        Nonce = nonce;
        Signature = signature;
        _ephemeralPrivate = ephemeralPrivate;
    }

    public static HandshakeHello Create(DeviceIdentity identity)
    {
        var ephemeral = new X25519PrivateKeyParameters(new SecureRandom());
        var ephemeralPublic = Concat(X25519X509Prefix, ephemeral.GeneratePublicKey().GetEncoded());
        var nonce = RandomNumberGenerator.GetBytes(32);
        var unsigned = Unsigned(identity.PeerId, identity.PublicKey, ephemeralPublic, nonce);
        return new(identity.PeerId, identity.PublicKey, ephemeralPublic, nonce, identity.Sign(unsigned), ephemeral);
    }

    public byte[] Encode()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        WriteField(writer, PeerId); WriteField(writer, IdentityPublicKey); WriteField(writer, EphemeralPublicKey);
        WriteField(writer, Nonce); WriteField(writer, Signature);
        return bytes.ToArray();
    }

    public static HandshakeHello Decode(byte[] encoded)
    {
        using var input = new MemoryStream(encoded, false);
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        var peer = ReadField(reader); var identity = ReadField(reader); var ephemeral = ReadField(reader);
        var nonce = ReadField(reader); var signature = ReadField(reader);
        if (input.Position != input.Length || peer.Length != 16 || nonce.Length != 32)
            throw new InvalidDataException("Malformed handshake hello");
        DeviceIdentity.Verify(identity, peer, Unsigned(peer, identity, ephemeral, nonce), signature);
        return new(peer, identity, ephemeral, nonce, signature, null);
    }

    public SessionKeys Derive(HandshakeHello remote)
    {
        if (_ephemeralPrivate is null) throw new InvalidOperationException("Remote hello cannot derive keys");
        DeviceIdentity.Verify(remote.IdentityPublicKey, remote.PeerId,
            Unsigned(remote.PeerId, remote.IdentityPublicKey, remote.EphemeralPublicKey, remote.Nonce), remote.Signature);
        if (remote.EphemeralPublicKey.Length != 44 || !remote.EphemeralPublicKey.AsSpan(0, 12).SequenceEqual(X25519X509Prefix))
            throw new CryptographicException("Unsupported X25519 public key encoding");
        var remoteKey = new X25519PublicKeyParameters(remote.EphemeralPublicKey, 12);
        var shared = new byte[32];
        _ephemeralPrivate.GenerateSecret(remoteKey, shared, 0);

        var comparison = PeerId.AsSpan().SequenceCompareTo(remote.PeerId);
        if (comparison == 0) throw new CryptographicException("Duplicate device identity");
        var first = comparison < 0 ? this : remote;
        var second = comparison < 0 ? remote : this;
        var transcript = Concat(first.SignedTranscript(), second.SignedTranscript());
        var salt = SHA256.HashData(transcript);
        var material = Hkdf(salt, shared, Encoding.ASCII.GetBytes("BlueLink BTX/1 session"), 72);
        var firstToSecond = material[..32];
        var secondToFirst = material[32..64];
        var firstPrefix = BinaryPrimitives.ReadInt32BigEndian(material.AsSpan(64, 4));
        var secondPrefix = BinaryPrimitives.ReadInt32BigEndian(material.AsSpan(68, 4));
        var safety = SHA256.HashData(Concat(salt, shared));
        var safetyCode = (((safety[0] & 0xff) << 16) | ((safety[1] & 0xff) << 8) | (safety[2] & 0xff)) % 1_000_000;
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(material);
        return comparison < 0
            ? new(firstToSecond, secondToFirst, firstPrefix, secondPrefix, safetyCode, remote.PeerId, remote.IdentityPublicKey)
            : new(secondToFirst, firstToSecond, secondPrefix, firstPrefix, safetyCode, remote.PeerId, remote.IdentityPublicKey);
    }

    private byte[] SignedTranscript() => Concat(Unsigned(PeerId, IdentityPublicKey, EphemeralPublicKey, Nonce), Signature);
    private static byte[] Unsigned(byte[] peer, byte[] identity, byte[] ephemeral, byte[] nonce) => Concat(Domain, peer, identity, ephemeral, nonce);

    private static byte[] Hkdf(byte[] salt, byte[] ikm, byte[] info, int length)
    {
        using var extract = new HMACSHA256(salt);
        var prk = extract.ComputeHash(ikm);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        for (byte counter = 1; offset < length; counter++)
        {
            using var expand = new HMACSHA256(prk);
            previous = expand.ComputeHash(Concat(previous, info, [counter]));
            var copy = Math.Min(previous.Length, length - offset);
            Buffer.BlockCopy(previous, 0, output, offset, copy);
            offset += copy;
        }
        CryptographicOperations.ZeroMemory(prk);
        return output;
    }

    private static void WriteField(BinaryWriter writer, byte[] field)
    {
        if (field.Length > 512) throw new InvalidDataException("Handshake field too large");
        Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)field.Length);
        writer.Write(length); writer.Write(field);
    }

    private static byte[] ReadField(BinaryReader reader)
    {
        var lengthBytes = reader.ReadBytes(2);
        if (lengthBytes.Length != 2) throw new EndOfStreamException();
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (length > 512) throw new InvalidDataException("Handshake field too large");
        var value = reader.ReadBytes(length);
        if (value.Length != length) throw new EndOfStreamException();
        return value;
    }

    private static byte[] Concat(params byte[][] values)
    {
        var result = new byte[values.Sum(value => value.Length)];
        var offset = 0;
        foreach (var value in values) { Buffer.BlockCopy(value, 0, result, offset, value.Length); offset += value.Length; }
        return result;
    }
}
