package com.bluelink.core;

import javax.crypto.KeyAgreement;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.MessageDigest;
import java.security.PrivateKey;
import java.security.Provider;
import java.security.SecureRandom;
import java.util.Arrays;

public final class HandshakeHello {
    private static final int MAX_FIELD = 512;

    private final byte[] peerId;
    private final byte[] identityPublicKey;
    private final byte[] ephemeralPublicKey;
    private final byte[] nonce;
    private final byte[] signature;
    private final PrivateKey ephemeralPrivateKey;
    private final Provider ephemeralProvider;

    private HandshakeHello(byte[] peerId, byte[] identityPublicKey, byte[] ephemeralPublicKey,
                           byte[] nonce, byte[] signature, PrivateKey ephemeralPrivateKey,
                           Provider ephemeralProvider) {
        this.peerId = peerId.clone();
        this.identityPublicKey = identityPublicKey.clone();
        this.ephemeralPublicKey = ephemeralPublicKey.clone();
        this.nonce = nonce.clone();
        this.signature = signature.clone();
        this.ephemeralPrivateKey = ephemeralPrivateKey;
        this.ephemeralProvider = ephemeralProvider;
    }

    public static HandshakeHello create(DeviceIdentity identity) throws GeneralSecurityException {
        CryptoProviders.GeneratedKeyPair generated = CryptoProviders.generateX25519();
        KeyPair ephemeral = generated.keyPair();
        byte[] nonce = new byte[32];
        new SecureRandom().nextBytes(nonce);
        byte[] unsigned = unsigned(identity.peerId(), identity.publicKey(), ephemeral.getPublic().getEncoded(), nonce);
        return new HandshakeHello(identity.peerId(), identity.publicKey(), ephemeral.getPublic().getEncoded(),
                nonce, identity.sign(unsigned), ephemeral.getPrivate(), generated.provider());
    }

    public byte[] encode() throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        DataOutputStream output = new DataOutputStream(bytes);
        writeField(output, peerId);
        writeField(output, identityPublicKey);
        writeField(output, ephemeralPublicKey);
        writeField(output, nonce);
        writeField(output, signature);
        return bytes.toByteArray();
    }

    public static HandshakeHello decode(byte[] encoded) throws IOException, GeneralSecurityException {
        DataInputStream input = new DataInputStream(new ByteArrayInputStream(encoded));
        byte[] peerId = readField(input);
        byte[] identity = readField(input);
        byte[] ephemeral = readField(input);
        byte[] nonce = readField(input);
        byte[] signature = readField(input);
        if (input.available() != 0 || peerId.length != 16 || nonce.length != 32) {
            throw new IOException("Malformed handshake hello");
        }
        byte[] unsigned = unsigned(peerId, identity, ephemeral, nonce);
        DeviceIdentity.verifyPeer(identity, peerId, unsigned, signature);
        return new HandshakeHello(peerId, identity, ephemeral, nonce, signature, null, null);
    }

    public SessionKeys derive(HandshakeHello remote) throws GeneralSecurityException, IOException {
        if (ephemeralPrivateKey == null) throw new IllegalStateException("Only a locally created hello can derive keys");
        if (Arrays.equals(peerId, remote.peerId)) throw new GeneralSecurityException("Duplicate device identity");
        DeviceIdentity.verifyPeer(remote.identityPublicKey, remote.peerId,
                unsigned(remote.peerId, remote.identityPublicKey, remote.ephemeralPublicKey, remote.nonce), remote.signature);

        java.security.PublicKey remoteEphemeral = CryptoProviders.decodeX25519(
                remote.ephemeralPublicKey, ephemeralProvider);
        KeyAgreement agreement = CryptoProviders.x25519Agreement(ephemeralPrivateKey, ephemeralProvider);
        agreement.doPhase(remoteEphemeral, true);
        byte[] shared = agreement.generateSecret();

        int comparison = compare(peerId, remote.peerId);
        HandshakeHello first = comparison < 0 ? this : remote;
        HandshakeHello second = comparison < 0 ? remote : this;
        byte[] transcript = concat(first.signedTranscript(), second.signedTranscript());
        byte[] salt = MessageDigest.getInstance("SHA-256").digest(transcript);
        byte[] material = Hkdf.derive(salt, shared, "BlueLink BTX/1 session".getBytes(StandardCharsets.US_ASCII), 72);
        byte[] firstToSecond = Arrays.copyOfRange(material, 0, 32);
        byte[] secondToFirst = Arrays.copyOfRange(material, 32, 64);
        int firstPrefix = readInt(material, 64);
        int secondPrefix = readInt(material, 68);
        byte[] safetyHash = MessageDigest.getInstance("SHA-256").digest(concat(salt, shared));
        int safetyCode = ((safetyHash[0] & 0xff) << 16 | (safetyHash[1] & 0xff) << 8 | safetyHash[2] & 0xff) % 1_000_000;
        Arrays.fill(shared, (byte) 0);
        Arrays.fill(material, (byte) 0);
        return comparison < 0
                ? new SessionKeys(firstToSecond, secondToFirst, firstPrefix, secondPrefix, safetyCode, remote.peerId, remote.identityPublicKey)
                : new SessionKeys(secondToFirst, firstToSecond, secondPrefix, firstPrefix, safetyCode, remote.peerId, remote.identityPublicKey);
    }

    public byte[] peerId() { return peerId.clone(); }
    public byte[] identityPublicKey() { return identityPublicKey.clone(); }

    private byte[] signedTranscript() throws IOException {
        return concat(unsigned(peerId, identityPublicKey, ephemeralPublicKey, nonce), signature);
    }

    private static byte[] unsigned(byte[] peerId, byte[] identity, byte[] ephemeral, byte[] nonce) {
        return concat("BLUELINK-HS1".getBytes(StandardCharsets.US_ASCII), peerId, identity, ephemeral, nonce);
    }

    private static int compare(byte[] left, byte[] right) {
        return Arrays.compareUnsigned(left, right);
    }

    private static int readInt(byte[] bytes, int offset) {
        return (bytes[offset] & 0xff) << 24 | (bytes[offset + 1] & 0xff) << 16
                | (bytes[offset + 2] & 0xff) << 8 | bytes[offset + 3] & 0xff;
    }

    private static byte[] concat(byte[]... values) {
        int length = 0;
        for (byte[] value : values) length += value.length;
        byte[] result = new byte[length];
        int offset = 0;
        for (byte[] value : values) {
            System.arraycopy(value, 0, result, offset, value.length);
            offset += value.length;
        }
        return result;
    }

    private static void writeField(DataOutputStream output, byte[] field) throws IOException {
        output.writeShort(field.length);
        output.write(field);
    }

    private static byte[] readField(DataInputStream input) throws IOException {
        int length = input.readUnsignedShort();
        if (length > MAX_FIELD) throw new IOException("Handshake field too large");
        return input.readNBytes(length);
    }
}
