package com.bluelink.core;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.charset.CodingErrorAction;

/** Optional display metadata; old peers continue decoding the unchanged first eight bytes. */
public final class DeviceNameGreeting {
    private DeviceNameGreeting() {}

    public static byte[] encode(String name) {
        String clean = normalize(name);
        byte[] greeting = ProtocolGreeting.current().encode();
        if (clean.isEmpty()) return greeting;
        byte[] text = clean.getBytes(StandardCharsets.UTF_8);
        return ByteBuffer.allocate(14 + text.length).put(greeting).put(new byte[]{'B','L','D','N'})
                .putShort((short) text.length).put(text).array();
    }

    public static String decode(byte[] value) {
        if (value.length < 14 || value[8] != 'B' || value[9] != 'L' || value[10] != 'D' || value[11] != 'N') return "";
        int length = Short.toUnsignedInt(ByteBuffer.wrap(value, 12, 2).getShort());
        if (length > 384 || value.length != 14 + length) return "";
        try {
            return normalize(StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(value, 14, length)).toString());
        } catch (java.nio.charset.CharacterCodingException ignored) { return ""; }
    }

    private static String normalize(String name) {
        String value = name == null ? "" : name.trim();
        return value.length() <= 128 && value.chars().noneMatch(Character::isISOControl)
                && value.getBytes(StandardCharsets.UTF_8).length <= 384 ? value : "";
    }
}
