package com.bluelink.core;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import java.security.GeneralSecurityException;
import java.util.Arrays;

final class Hkdf {
    private Hkdf() {}

    static byte[] derive(byte[] salt, byte[] inputKeyMaterial, byte[] info, int length)
            throws GeneralSecurityException {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(salt, "HmacSHA256"));
        byte[] prk = mac.doFinal(inputKeyMaterial);
        byte[] output = new byte[length];
        byte[] previous = new byte[0];
        int offset = 0;
        for (int counter = 1; offset < length; counter++) {
            mac.init(new SecretKeySpec(prk, "HmacSHA256"));
            mac.update(previous);
            mac.update(info);
            mac.update((byte) counter);
            previous = mac.doFinal();
            int copy = Math.min(previous.length, length - offset);
            System.arraycopy(previous, 0, output, offset, copy);
            offset += copy;
        }
        Arrays.fill(prk, (byte) 0);
        return output;
    }
}

