package com.bluelink.core;

import java.security.GeneralSecurityException;
import java.security.KeyPair;
import java.security.MessageDigest;
import java.security.Provider;
import java.util.Arrays;

public final class DeviceIdentity {
    private final KeyPair keyPair;
    private final Provider provider;
    private final byte[] peerId;

    private DeviceIdentity(KeyPair keyPair, Provider provider) throws GeneralSecurityException {
        this.keyPair = keyPair;
        this.provider = provider;
        this.peerId = Arrays.copyOf(MessageDigest.getInstance("SHA-256").digest(publicKey()), 16);
    }

    public static DeviceIdentity generate() throws GeneralSecurityException {
        CryptoProviders.GeneratedKeyPair generated = CryptoProviders.generateEd25519();
        return new DeviceIdentity(generated.keyPair(), generated.provider());
    }

    public static DeviceIdentity restore(byte[] privateKey, byte[] publicKey) throws GeneralSecurityException {
        CryptoProviders.GeneratedKeyPair restored = CryptoProviders.restoreEd25519(privateKey, publicKey);
        return new DeviceIdentity(restored.keyPair(), restored.provider());
    }

    public byte[] publicKey() { return keyPair.getPublic().getEncoded().clone(); }
    public byte[] privateKey() { return keyPair.getPrivate().getEncoded().clone(); }
    public byte[] peerId() { return peerId.clone(); }

    public byte[] sign(byte[] content) throws GeneralSecurityException {
        return CryptoProviders.signEd25519(keyPair.getPrivate(), provider, content);
    }

    public static void verifyPeer(byte[] publicKey, byte[] peerId, byte[] content, byte[] signed)
            throws GeneralSecurityException {
        byte[] calculated = Arrays.copyOf(MessageDigest.getInstance("SHA-256").digest(publicKey), 16);
        if (!MessageDigest.isEqual(calculated, peerId)) throw new GeneralSecurityException("Peer id mismatch");
        CryptoProviders.verifyEd25519(publicKey, content, signed);
    }
}
