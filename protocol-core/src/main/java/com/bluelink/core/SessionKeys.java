package com.bluelink.core;

public record SessionKeys(byte[] sendKey, byte[] receiveKey, int sendNoncePrefix,
                          int receiveNoncePrefix, int safetyCode, byte[] remotePeerId,
                          byte[] remoteIdentityPublicKey) {
    public SessionKeys {
        sendKey = sendKey.clone();
        receiveKey = receiveKey.clone();
        remotePeerId = remotePeerId.clone();
        remoteIdentityPublicKey = remoteIdentityPublicKey.clone();
    }

    public String formattedSafetyCode() { return "%03d %03d".formatted(safetyCode / 1000, safetyCode % 1000); }
}

