package com.bluelink.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.Provider;
import java.util.Arrays;

/** Configures and verifies the cryptographic runtime used by the BTX protocol. */
public final class CryptoRuntime {
    private CryptoRuntime() {}

    public static void preferProvider(Provider provider) {
        CryptoProviders.prefer(provider);
    }

    public static String selfTest() throws Exception {
        DeviceIdentity firstIdentity = DeviceIdentity.generate();
        DeviceIdentity secondIdentity = DeviceIdentity.generate();
        HandshakeHello firstLocal = HandshakeHello.create(firstIdentity);
        HandshakeHello secondLocal = HandshakeHello.create(secondIdentity);
        HandshakeHello firstRemote = HandshakeHello.decode(firstLocal.encode());
        HandshakeHello secondRemote = HandshakeHello.decode(secondLocal.encode());
        SessionKeys firstKeys = firstLocal.derive(secondRemote);
        SessionKeys secondKeys = secondLocal.derive(firstRemote);
        if (!Arrays.equals(firstKeys.sendKey(), secondKeys.receiveKey()) ||
                !Arrays.equals(firstKeys.receiveKey(), secondKeys.sendKey()) ||
                firstKeys.safetyCode() != secondKeys.safetyCode()) {
            throw new GeneralSecurityException("X25519 session self-test produced mismatched keys");
        }

        byte[] payload = "BlueLink crypto self-test".getBytes(StandardCharsets.UTF_8);
        ByteArrayOutputStream wire = new ByteArrayOutputStream();
        BtxRecordCodec.write(wire, new BtxFrame(WireMessageType.CHAT, 0, 1, 0, payload),
                firstKeys.sendKey(), firstKeys.sendNoncePrefix());
        BtxFrame decoded = BtxRecordCodec.read(new ByteArrayInputStream(wire.toByteArray()),
                secondKeys.receiveKey(), secondKeys.receiveNoncePrefix(), new ReplayGuard(0));
        if (!Arrays.equals(payload, decoded.payload())) {
            throw new GeneralSecurityException("ChaCha20-Poly1305 record self-test failed");
        }
        return CryptoProviders.preferredDescription() + " · Ed25519/X25519/ChaCha20-Poly1305 自检通过";
    }
}
